using System.Globalization;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Renders one page of a document at a time, so memory stays bounded no matter how long the
/// document is.
///
/// Zooming is a view transform, not a re-render: the page keeps its layout size and only the
/// image's Scale changes, which is instant. The bitmap behind it is rasterized again only once
/// the zoom settles, and only when it has become visibly too coarse.
/// </summary>
public partial class ReaderPage : ContentPage
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.35;
    private const double DoubleTapZoom = 2.0;
    private const double FallbackWidthDips = 360;

    /// <summary>
    /// Rasterizing beyond this factor buys detail the screen cannot show while the bitmap, and the
    /// cost of encoding it, keep growing with the square of the factor.
    /// </summary>
    private const double MaxRenderScale = 3.0;

    /// <summary>A re-render only pays for itself once the zoom has moved enough to be noticeable.</summary>
    private const double RenderScaleTolerance = 0.25;

    private static readonly TimeSpan RenderSettleDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>A render that finishes quickly should not make the spinner flash on screen.</summary>
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(150);

    private readonly PdfDocumentEntry _entry;
    private readonly ILibraryService _library;
    private readonly IPdfDocumentService _renderer;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ReaderPage> _logger;

    private readonly SemaphoreSlim _renderGate = new(1, 1);

    // The aspect ratio of a page never changes, and asking for it opens the page: ask once.
    private readonly Dictionary<int, double> _aspectRatios = [];

    private IPdfDocument? _document;
    private int _pageIndex;
    private double _zoom = MinZoom;

    /// <summary>Factor the bitmap currently on screen was rasterized at.</summary>
    private double _renderedScale = MinZoom;

    private double _pageWidthDips;
    private double _pageHeightDips;

    private double _pinchStartZoom = MinZoom;
    private double _pinchScale = 1.0;
    private double _panStartX;
    private double _panStartY;

    private CancellationTokenSource? _renderSettle;

    private IReadOnlyList<PdfTextMatch> _matches = [];
    private int _matchIndex = -1;
    private CancellationTokenSource? _searchCancellation;

    /// <summary>
    /// Password of a protected document, when the caller already asked for it during import.
    /// It is kept in memory for this reading session only: storing it would put the key to the
    /// user's document on disk (constitucion, seccion 5).
    /// </summary>
    public string? InitialPassword { get; set; }

    public ReaderPage(
        PdfDocumentEntry entry,
        ILibraryService library,
        IPdfDocumentService renderer,
        ILocalizationService localization,
        ILogger<ReaderPage> logger)
    {
        InitializeComponent();

        _entry = entry;
        _library = library;
        _renderer = renderer;
        _localization = localization;
        _logger = logger;

        Title = entry.DisplayName;
        BusyLabel.Text = _localization["loading_page"];
        _pageIndex = Math.Max(0, entry.LastPageIndex);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_document is not null)
            return;

        try
        {
            SetBusy(true);
            _document = await OpenWithPasswordRetryAsync();

            if (_document is null)
            {
                // The user cancelled the password prompt, or the document could not be opened
                // at all; either way the failure has already been explained.
                SetBusy(false);
                await Navigation.PopAsync();
                return;
            }

            if (_document.PageCount != _entry.PageCount)
                await _library.SetPageCountAsync(_entry, _document.PageCount);

            if (_pageIndex >= _document.PageCount)
                _pageIndex = 0;

            SearchButton.IsVisible = _document.SupportsTextSearch;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Document}.", _entry.DisplayName);
            SetBusy(false);
            await ShowAlertAsync(_localization["error_open_title"], _localization.Format("error_render", ex.Message));
            await Navigation.PopAsync();
            return;
        }

        await ShowPageAsync(_pageIndex);
    }

    /// <summary>
    /// Opens the document, asking for the password as many times as the user is willing to try.
    /// Returns null when they cancel or the document cannot be opened for any other reason.
    /// </summary>
    private async Task<IPdfDocument?> OpenWithPasswordRetryAsync()
    {
        var path = _library.GetFilePath(_entry);
        var password = InitialPassword;
        var wrongPassword = false;

        while (true)
        {
            try
            {
                return await _renderer.OpenAsync(path, password);
            }
            catch (PdfOpenException ex) when (ex.Failure is PdfOpenFailure.PasswordProtected or PdfOpenFailure.WrongPassword)
            {
                _logger.LogWarning("{Document} needs a password ({Failure}).", _entry.DisplayName, ex.Failure);

                SetBusy(false);
                password = await PasswordPromptPage.AskAsync(this, _localization, _entry.DisplayName, wrongPassword);
                if (password is null)
                    return null; // Cancelled: nothing to explain, the user knows.

                wrongPassword = true; // Any further failure means the password they typed was wrong.
                SetBusy(true);
            }
            catch (PdfOpenException ex)
            {
                _logger.LogWarning(ex, "Could not open {Document} ({Failure}).", _entry.DisplayName, ex.Failure);
                SetBusy(false);
                await ShowAlertAsync(_localization["error_open_title"], DescribeFailure(ex.Failure));
                return null;
            }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // The password prompt is modal, so it does not tear the reader down; only a real
        // departure should dispose the document a pending search may still be walking.
        if (Navigation.ModalStack.Count > 0)
            return;

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;

        CancelSettleRender();

        // The reader never pushes another page, so leaving means the document is no longer needed.
        _document?.Dispose();
        _document = null;
    }

    /// <param name="resetView">True when moving to another page, so the pan starts from the top.</param>
    private async Task ShowPageAsync(int pageIndex, bool resetView = false)
    {
        if (_document is null)
            return;

        if (!await _renderGate.WaitAsync(TimeSpan.Zero))
            return; // A render is already in flight; the user's next tap will still be honoured.

        var busy = new CancellationTokenSource();
        _ = ShowBusyAfterDelayAsync(busy.Token);

        try
        {
            var fitWidthDips = GetAvailableWidthDips();
            var density = DeviceDisplay.Current.MainDisplayInfo.Density;
            if (density <= 0)
                density = 1;

            // The page keeps its layout size at every zoom level; only the resolution of the
            // bitmap behind it follows the zoom, and only up to MaxRenderScale.
            var renderScale = Math.Clamp(_zoom, MinZoom, MaxRenderScale);
            var targetPixels = (int)Math.Round(fitWidthDips * density * renderScale);

            var aspectRatio = await GetAspectRatioAsync(pageIndex);
            var png = await _document.RenderPageAsync(pageIndex, targetPixels, _matches);

            PageImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
            PageImage.WidthRequest = fitWidthDips;
            PageImage.HeightRequest = fitWidthDips * aspectRatio;

            _pageWidthDips = fitWidthDips;
            _pageHeightDips = fitWidthDips * aspectRatio;
            _renderedScale = renderScale;
            _pageIndex = pageIndex;

            if (resetView)
            {
                PageImage.TranslationX = 0;
                PageImage.TranslationY = 0;
            }

            ApplyTransform();
            UpdateToolbar();

            await _library.TouchAsync(_entry, _pageIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not render page {Page} of {Document}.", pageIndex + 1, _entry.DisplayName);
            await ShowAlertAsync(_localization["error"], _localization.Format("error_render", ex.Message));
        }
        finally
        {
            busy.Cancel();
            busy.Dispose();
            SetBusy(false);
            _renderGate.Release();
        }
    }

    /// <summary>Shows the spinner only if the work outlasts <see cref="BusyDelay"/>.</summary>
    private async Task ShowBusyAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(BusyDelay, token);
        }
        catch (OperationCanceledException)
        {
            return; // The render beat the delay: no spinner at all.
        }

        if (!token.IsCancellationRequested)
            MainThread.BeginInvokeOnMainThread(() => SetBusy(true));
    }

    private async Task<double> GetAspectRatioAsync(int pageIndex)
    {
        if (_aspectRatios.TryGetValue(pageIndex, out var cached))
            return cached;

        var aspectRatio = await _document!.GetPageAspectRatioAsync(pageIndex);
        _aspectRatios[pageIndex] = aspectRatio;
        return aspectRatio;
    }

    /// <summary>Applies the current zoom as a view transform and keeps the page within reach.</summary>
    private void ApplyTransform()
    {
        PageImage.Scale = _zoom;
        ClampTranslation();
    }

    /// <summary>
    /// Stops the page being dragged out of sight. Scaling happens around the centre, so the image
    /// overflows the viewport by half of the excess on each side, and that is how far it may move.
    /// </summary>
    private void ClampTranslation()
    {
        var viewportWidth = Viewport.Width - 16;   // minus Viewport padding
        var viewportHeight = Viewport.Height - 16;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        var maxX = Math.Max(0, (_pageWidthDips * _zoom - viewportWidth) / 2);
        var maxY = Math.Max(0, (_pageHeightDips * _zoom - viewportHeight) / 2);

        PageImage.TranslationX = Math.Clamp(PageImage.TranslationX, -maxX, maxX);
        PageImage.TranslationY = Math.Clamp(PageImage.TranslationY, -maxY, maxY);
    }

    private double GetAvailableWidthDips()
    {
        // Before the first layout pass the viewport has no width yet; fall back to the display.
        if (Viewport.Width > 0)
            return Math.Max(100, Viewport.Width - 16); // minus Viewport padding

        var displayWidth = DeviceDisplay.Current.MainDisplayInfo.Width;
        var density = DeviceDisplay.Current.MainDisplayInfo.Density;

        return displayWidth > 0 && density > 0 ? displayWidth / density : FallbackWidthDips;
    }

    private void UpdateToolbar()
    {
        var pageCount = _document?.PageCount ?? 0;

        PageLabel.Text = _localization.Format("reader_page_of", _pageIndex + 1, pageCount);
        PreviousButton.IsEnabled = _pageIndex > 0;
        NextButton.IsEnabled = _pageIndex < pageCount - 1;
        ZoomOutButton.IsEnabled = _zoom > MinZoom;
        ZoomInButton.IsEnabled = _zoom < MaxZoom;
    }

    private async void OnPreviousClicked(object? sender, EventArgs e)
    {
        if (_pageIndex > 0)
            await ShowPageAsync(_pageIndex - 1, resetView: true);
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_document is not null && _pageIndex < _document.PageCount - 1)
            await ShowPageAsync(_pageIndex + 1, resetView: true);
    }

    private void OnZoomInClicked(object? sender, EventArgs e) => SetZoom(_zoom * ZoomStep);

    private void OnZoomOutClicked(object? sender, EventArgs e) => SetZoom(_zoom / ZoomStep);

    private void OnDoubleTapped(object? sender, TappedEventArgs e) =>
        SetZoom(_zoom > MinZoom + 0.01 ? MinZoom : DoubleTapZoom);

    /// <summary>
    /// Moves the zoom without touching the bitmap: the change lands on screen immediately and a
    /// sharper render is scheduled for when the user stops.
    /// </summary>
    private void SetZoom(double zoom)
    {
        var clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.01)
            return;

        _zoom = clamped;

        if (_zoom <= MinZoom + 0.01)
        {
            // Back to fit: the page is fully visible again, so any pan is stale.
            PageImage.TranslationX = 0;
            PageImage.TranslationY = 0;
        }

        ApplyTransform();
        UpdateToolbar();
        ScheduleSettleRender();
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartZoom = _zoom;
                _pinchScale = 1.0;
                CancelSettleRender();
                break;

            case GestureStatus.Running:
                // e.Scale is the change since the PREVIOUS update, not since the gesture started,
                // so it has to be accumulated. Multiplying the starting zoom by it directly left
                // the preview pinned at roughly 1x and made the gesture look like it did nothing.
                _pinchScale *= e.Scale;
                _zoom = Math.Clamp(_pinchStartZoom * _pinchScale, MinZoom, MaxZoom);
                ApplyTransform();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                UpdateToolbar();
                ScheduleSettleRender();
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = PageImage.TranslationX;
                _panStartY = PageImage.TranslationY;
                break;

            case GestureStatus.Running:
                // TotalX/TotalY are measured from where the gesture started, not from the last update.
                PageImage.TranslationX = _panStartX + e.TotalX;
                PageImage.TranslationY = _panStartY + e.TotalY;
                ClampTranslation();
                break;
        }
    }

    /// <summary>
    /// Rasterizes the page again once the zoom has settled, and only when the bitmap on screen has
    /// become visibly coarser than the zoom now demands.
    /// </summary>
    private void ScheduleSettleRender()
    {
        CancelSettleRender();

        var wanted = Math.Clamp(_zoom, MinZoom, MaxRenderScale);
        if (Math.Abs(wanted - _renderedScale) < RenderScaleTolerance)
            return;

        var cancellation = new CancellationTokenSource();
        _renderSettle = cancellation;
        _ = SettleRenderAsync(cancellation.Token);
    }

    private async Task SettleRenderAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(RenderSettleDelay, token);
        }
        catch (OperationCanceledException)
        {
            return; // The user kept zooming; a newer schedule owns the render.
        }

        if (!token.IsCancellationRequested)
            await ShowPageAsync(_pageIndex);
    }

    private void CancelSettleRender()
    {
        _renderSettle?.Cancel();
        _renderSettle?.Dispose();
        _renderSettle = null;
    }

    private async void OnGoToPageTapped(object? sender, TappedEventArgs e)
    {
        if (_document is null)
            return;

        var pageCount = _document.PageCount;

        var answer = await DisplayPromptAsync(
            _localization["goto_title"],
            _localization.Format("goto_message", pageCount),
            _localization["go"],
            _localization["cancel"],
            _localization["goto_placeholder"],
            maxLength: 6,
            keyboard: Keyboard.Numeric,
            initialValue: (_pageIndex + 1).ToString(CultureInfo.CurrentCulture));

        if (string.IsNullOrWhiteSpace(answer))
            return; // Cancelled.

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.CurrentCulture, out var page)
            || page < 1 || page > pageCount)
        {
            await ShowAlertAsync(
                _localization["invalid_page_title"],
                _localization.Format("invalid_page_message", pageCount));
            return;
        }

        await ShowPageAsync(page - 1, resetView: true);
    }

    private void OnSearchClicked(object? sender, EventArgs e)
    {
        SearchBar.IsVisible = true;
        SearchEntry.Placeholder = _localization["search_placeholder"];
        SearchEntry.Focus();
        UpdateSearchStatus();
    }

    private async void OnCloseSearchClicked(object? sender, EventArgs e)
    {
        await CancelSearchAsync();

        SearchBar.IsVisible = false;
        SearchEntry.Text = string.Empty;

        if (_matches.Count > 0)
        {
            // Drop the highlights: leaving them painted after closing the search would be a lie.
            _matches = [];
            _matchIndex = -1;
            await ShowPageAsync(_pageIndex);
        }
    }

    private async void OnSearchSubmitted(object? sender, EventArgs e)
    {
        if (_document is null)
            return;

        var query = SearchEntry.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        await CancelSearchAsync();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        try
        {
            SearchStatusLabel.Text = _localization["searching"];
            SetSearchNavigationEnabled(false);

            _matches = await _document.SearchAsync(query, token);
            _matchIndex = _matches.Count > 0 ? 0 : -1;

            if (_matches.Count == 0)
            {
                UpdateSearchStatus();
                await ShowAlertAsync(_localization["search"], _localization.Format("search_no_results", query));
                return;
            }

            await GoToMatchAsync(0);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; the newer one owns the UI now.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not search {Document}.", _entry.DisplayName);
            await ShowAlertAsync(_localization["error"], _localization.Format("error_render", ex.Message));
        }
    }

    private async void OnPreviousMatchClicked(object? sender, EventArgs e)
    {
        if (_matches.Count > 0)
            await GoToMatchAsync((_matchIndex - 1 + _matches.Count) % _matches.Count);
    }

    private async void OnNextMatchClicked(object? sender, EventArgs e)
    {
        if (_matches.Count > 0)
            await GoToMatchAsync((_matchIndex + 1) % _matches.Count);
    }

    private async Task GoToMatchAsync(int matchIndex)
    {
        _matchIndex = matchIndex;
        var match = _matches[matchIndex];

        UpdateSearchStatus();

        // Re-render even when the match is on the current page: the highlight is painted into
        // the bitmap, so moving between matches on one page still needs a new render.
        await ShowPageAsync(match.PageIndex, resetView: true);
    }

    private void UpdateSearchStatus()
    {
        SearchStatusLabel.Text = _matches.Count > 0
            ? _localization.Format("search_match_of", _matchIndex + 1, _matches.Count)
            : string.Empty;

        SetSearchNavigationEnabled(_matches.Count > 0);
    }

    private void SetSearchNavigationEnabled(bool enabled)
    {
        PreviousMatchButton.IsEnabled = enabled;
        NextMatchButton.IsEnabled = enabled;
    }

    private async Task CancelSearchAsync()
    {
        if (_searchCancellation is null)
            return;

        await _searchCancellation.CancelAsync();
        _searchCancellation.Dispose();
        _searchCancellation = null;
    }

    private string DescribeFailure(PdfOpenFailure failure) => failure switch
    {
        PdfOpenFailure.PasswordProtected or PdfOpenFailure.WrongPassword => _localization["error_protected"],
        PdfOpenFailure.PasswordUnsupported => _localization["error_password_unsupported"],
        PdfOpenFailure.InvalidDocument => _localization["error_not_pdf"],
        _ => _localization["error_missing_file"]
    };

    private void SetBusy(bool busy)
    {
        BusyPanel.IsVisible = busy;
        BusyIndicator.IsRunning = busy;
    }

    private Task ShowAlertAsync(string title, string message) =>
        DisplayAlertAsync(title, message, _localization["ok"]);
}
