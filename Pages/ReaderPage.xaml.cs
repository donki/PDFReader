using System.Globalization;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Renders one page of a document at a time. Pages are rasterized on demand at the current
/// zoom level, so memory stays bounded no matter how long the document is.
/// </summary>
public partial class ReaderPage : ContentPage
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.35;
    private const double FallbackWidthDips = 360;

    private readonly PdfDocumentEntry _entry;
    private readonly ILibraryService _library;
    private readonly IPdfDocumentService _renderer;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ReaderPage> _logger;

    private readonly SemaphoreSlim _renderGate = new(1, 1);

    private IPdfDocument? _document;
    private int _pageIndex;
    private double _zoom = MinZoom;
    private double _pinchStartZoom = MinZoom;

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
            _document = await _renderer.OpenAsync(_library.GetFilePath(_entry));

            if (_document.PageCount != _entry.PageCount)
                await _library.SetPageCountAsync(_entry, _document.PageCount);

            if (_pageIndex >= _document.PageCount)
                _pageIndex = 0;
        }
        catch (PdfOpenException ex)
        {
            _logger.LogWarning(ex, "Could not open {Document} ({Failure}).", _entry.DisplayName, ex.Failure);
            SetBusy(false);
            await ShowAlertAsync(_localization["error_open_title"], DescribeFailure(ex.Failure));
            await Navigation.PopAsync();
            return;
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

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // The reader never pushes another page, so leaving means the document is no longer needed.
        _document?.Dispose();
        _document = null;
    }

    private async Task ShowPageAsync(int pageIndex)
    {
        if (_document is null)
            return;

        if (!await _renderGate.WaitAsync(TimeSpan.Zero))
            return; // A render is already in flight; the user's next tap will still be honoured.

        try
        {
            SetBusy(true);

            var widthDips = GetAvailableWidthDips() * _zoom;
            var density = DeviceDisplay.Current.MainDisplayInfo.Density;
            if (density <= 0)
                density = 1;

            var targetPixels = (int)Math.Round(widthDips * density);

            var aspectRatio = await _document.GetPageAspectRatioAsync(pageIndex);
            var png = await _document.RenderPageAsync(pageIndex, targetPixels);

            PageImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
            PageImage.WidthRequest = widthDips;
            PageImage.HeightRequest = widthDips * aspectRatio;
            PageImage.Scale = 1;

            _pageIndex = pageIndex;
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
            SetBusy(false);
            _renderGate.Release();
        }
    }

    private double GetAvailableWidthDips()
    {
        // Before the first layout pass the ScrollView has no width yet; fall back to the display.
        if (PageScroll.Width > 0)
            return Math.Max(100, PageScroll.Width - 16); // minus PageHost padding

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
            await ShowPageAsync(_pageIndex - 1);
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_document is not null && _pageIndex < _document.PageCount - 1)
            await ShowPageAsync(_pageIndex + 1);
    }

    private async void OnZoomInClicked(object? sender, EventArgs e) => await ApplyZoomAsync(_zoom * ZoomStep);

    private async void OnZoomOutClicked(object? sender, EventArgs e) => await ApplyZoomAsync(_zoom / ZoomStep);

    private async Task ApplyZoomAsync(double zoom)
    {
        var clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.01)
            return;

        _zoom = clamped;
        await ShowPageAsync(_pageIndex);
    }

    private async void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartZoom = _zoom;
                break;

            case GestureStatus.Running:
                // Scale the current bitmap for immediate feedback; the sharp render comes on release.
                var preview = Math.Clamp(_pinchStartZoom * e.Scale, MinZoom, MaxZoom) / _zoom;
                PageImage.Scale = preview;
                break;

            case GestureStatus.Completed:
                var target = PageImage.Scale * _zoom;
                PageImage.Scale = 1;
                await ApplyZoomAsync(target);
                break;

            case GestureStatus.Canceled:
                PageImage.Scale = 1;
                break;
        }
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

        await ShowPageAsync(page - 1);
    }

    private string DescribeFailure(PdfOpenFailure failure) => failure switch
    {
        PdfOpenFailure.PasswordProtected => _localization["error_protected"],
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
