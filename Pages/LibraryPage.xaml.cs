using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Entry point of the app: lists the documents already in the library and lets the user
/// bring in a new PDF from the device.
/// </summary>
public partial class LibraryPage : ContentPage
{
    private readonly ILibraryService _library;
    private readonly IPdfDocumentService _renderer;
    private readonly ILocalizationService _localization;
    private readonly PendingDocumentQueue _pendingDocuments;
    private readonly IServiceProvider _services;
    private readonly UpdateService _updateService;
    private readonly ILogger<LibraryPage> _logger;

    private readonly ObservableCollection<DocumentListItem> _documents = [];
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public LibraryPage(
        ILibraryService library,
        IPdfDocumentService renderer,
        ILocalizationService localization,
        PendingDocumentQueue pendingDocuments,
        IServiceProvider services,
        UpdateService updateService,
        ILogger<LibraryPage> logger)
    {
        InitializeComponent();

        _library = library;
        _renderer = renderer;
        _localization = localization;
        _pendingDocuments = pendingDocuments;
        _services = services;
        _updateService = updateService;
        _logger = logger;

        DocumentsView.ItemsSource = _documents;

        _localization.LanguageChanged += OnLanguageChanged;
        _pendingDocuments.DocumentQueued += OnDocumentQueued;

        ApplyTexts();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Comprobacion de version al arrancar (constitucion, seccion 15): avisa si hay una version
        // mas reciente y propone actualizar. No bloqueante y se hace una sola vez por sesion.
        _ = _updateService.CheckAndPromptAsync(this);

        await RefreshAsync();
        await ImportPendingDocumentsAsync();
    }

    private void ApplyTexts()
    {
        Title = _localization["library_title"];
        AppNameLabel.Text = _localization["app_name"];
        TaglineLabel.Text = _localization["app_tagline"];
        RecentLabel.Text = _localization["recent_documents"].ToUpper(CultureInfo.CurrentCulture);
        EmptyTitleLabel.Text = _localization["empty_title"];
        EmptyHintLabel.Text = _localization["empty_hint"];
        OpenButton.Text = _localization["open_pdf"];
        BusyLabel.Text = _localization["importing"];
    }

    private async void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyTexts();
        await RefreshAsync();
    }

    private void OnDocumentQueued(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(async () => await ImportPendingDocumentsAsync());

    private async Task RefreshAsync()
    {
        try
        {
            var entries = await _library.GetDocumentsAsync();

            _documents.Clear();
            foreach (var entry in entries)
                _documents.Add(ToListItem(entry));

            UpdateHeaderVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the library.");
            await ShowAlertAsync(_localization["error"], _localization.Format("error_library", ex.Message));
        }
    }

    /// <summary>The header would otherwise sit on top of the "no documents yet" placeholder.</summary>
    private void UpdateHeaderVisibility() => RecentLabel.IsVisible = _documents.Count > 0;

    private DocumentListItem ToListItem(PdfDocumentEntry entry) => new(
        entry,
        entry.DisplayName,
        FormatDetails(entry),
        FormatLastOpened(entry.LastOpenedUtc));

    private string FormatDetails(PdfDocumentEntry entry)
    {
        var size = FormatSize(entry.SizeBytes);

        if (entry.PageCount <= 0)
            return size;

        var pages = entry.PageCount == 1
            ? _localization["page_count_one"]
            : _localization.Format("page_count", entry.PageCount);

        return $"{pages} · {size}";
    }

    private static string FormatSize(long bytes)
    {
        const long megabyte = 1024 * 1024;

        if (bytes >= megabyte)
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", (double)bytes / megabyte);

        return string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Max(1, bytes / 1024d));
    }

    private string FormatLastOpened(DateTime lastOpenedUtc)
    {
        var local = lastOpenedUtc.ToLocalTime();
        var today = DateTime.Now.Date;

        if (local.Date == today)
            return $"{_localization["last_opened_today"]} {local:t}";

        if (local.Date == today.AddDays(-1))
            return $"{_localization["last_opened_yesterday"]} {local:t}";

        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    private async void OnOpenPdfClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = _localization["open_pdf"],
                FileTypes = FilePickerFileType.Pdf
            });

            if (result is null)
                return; // The user dismissed the picker.

            await using var stream = await result.OpenReadAsync();
            await ImportAndOpenAsync(stream, result.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not pick a document.");
            await ShowAlertAsync(_localization["error_open_title"], _localization.Format("error_import", ex.Message));
        }
    }

    private async Task ImportPendingDocumentsAsync()
    {
        while (_pendingDocuments.TryDequeue(out var pending) && pending is not null)
        {
            try
            {
                await using (var stream = File.OpenRead(pending.TemporaryFilePath))
                {
                    await ImportAndOpenAsync(stream, pending.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not import the incoming document.");
                await ShowAlertAsync(_localization["error_open_title"], _localization.Format("error_import", ex.Message));
            }
            finally
            {
                TryDeleteTemporaryFile(pending.TemporaryFilePath);
            }
        }
    }

    private void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // The cache directory is the OS's to reclaim; a leftover file is not worth bothering the user.
            _logger.LogWarning(ex, "Could not delete the temporary file {Path}.", path);
        }
    }

    /// <summary>Copies the PDF into the library, checks that it can be rendered and opens it.</summary>
    private async Task ImportAndOpenAsync(Stream content, string displayName)
    {
        if (!await _importGate.WaitAsync(TimeSpan.Zero))
            return; // An import is already running; ignore the double tap.

        PdfDocumentEntry? entry = null;
        string? password = null;
        try
        {
            SetBusy(true);

            entry = await _library.ImportAsync(content, displayName);

            // Open it once up front: an unreadable file must not reach the library. A protected
            // document is perfectly usable, so ask for the password instead of rejecting it.
            var opened = await OpenWithPasswordRetryAsync(entry);
            if (opened is null)
            {
                // Cancelled at the password prompt: the file never becomes a library entry.
                await _library.RemoveAsync(entry);
                entry = null;
                return;
            }

            password = opened.Value.Password;
            using (var document = opened.Value.Document)
            {
                await _library.SetPageCountAsync(entry, document.PageCount);
            }

            await RefreshAsync();
        }
        catch (PdfOpenException ex)
        {
            _logger.LogWarning(ex, "The imported file is not a usable PDF ({Failure}).", ex.Failure);

            if (entry is not null)
            {
                await _library.RemoveAsync(entry);
                entry = null;
            }

            await ShowAlertAsync(_localization["error_open_title"], DescribeFailure(ex.Failure));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import the document.");

            if (entry is not null)
            {
                await _library.RemoveAsync(entry);
                entry = null;
            }

            await ShowAlertAsync(_localization["error_open_title"], _localization.Format("error_import", ex.Message));
        }
        finally
        {
            SetBusy(false);
            _importGate.Release();
        }

        if (entry is not null)
            await OpenReaderAsync(entry, password);
    }

    /// <summary>
    /// Opens the imported document, asking for the password for as long as it stays protected.
    /// Returns null when the user cancels the prompt.
    /// </summary>
    private async Task<(IPdfDocument Document, string? Password)?> OpenWithPasswordRetryAsync(PdfDocumentEntry entry)
    {
        var path = _library.GetFilePath(entry);
        string? password = null;
        var wrongPassword = false;

        while (true)
        {
            try
            {
                return (await _renderer.OpenAsync(path, password), password);
            }
            catch (PdfOpenException ex) when (ex.Failure is PdfOpenFailure.PasswordProtected or PdfOpenFailure.WrongPassword)
            {
                _logger.LogWarning("{Document} needs a password ({Failure}).", entry.DisplayName, ex.Failure);

                SetBusy(false);
                password = await PasswordPromptPage.AskAsync(this, _localization, entry.DisplayName, wrongPassword);
                if (password is null)
                    return null;

                wrongPassword = true; // Any further failure means the password they typed was wrong.
                SetBusy(true);
            }
        }
    }

    private string DescribeFailure(PdfOpenFailure failure) => failure switch
    {
        PdfOpenFailure.PasswordProtected or PdfOpenFailure.WrongPassword => _localization["error_protected"],
        PdfOpenFailure.PasswordUnsupported => _localization["error_password_unsupported"],
        PdfOpenFailure.InvalidDocument => _localization["error_not_pdf"],
        _ => _localization["error_not_pdf"]
    };

    private async void OnDocumentTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: DocumentListItem item })
            return;

        await OpenReaderAsync(item.Entry);
    }

    /// <param name="password">
    /// Known password of a protected document, so a freshly imported one does not ask twice.
    /// Null when opening from the list: the reader asks for it again rather than the app keeping
    /// the password around between sessions.
    /// </param>
    private async Task OpenReaderAsync(PdfDocumentEntry entry, string? password = null)
    {
        if (!File.Exists(_library.GetFilePath(entry)))
        {
            await _library.RemoveAsync(entry);
            await RefreshAsync();
            await ShowAlertAsync(_localization["error_open_title"], _localization["error_missing_file"]);
            return;
        }

        var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_services, entry);
        reader.InitialPassword = password;
        await Navigation.PushAsync(reader);
    }

    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: DocumentListItem item })
            return;

        var confirmed = await DisplayAlertAsync(
            _localization["remove_title"],
            _localization.Format("remove_message", item.DisplayName),
            _localization["remove"],
            _localization["cancel"]);

        if (!confirmed)
            return;

        try
        {
            await _library.RemoveAsync(item.Entry);
            _documents.Remove(item);
            UpdateHeaderVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove the document.");
            await ShowAlertAsync(_localization["error"], _localization.Format("error_remove", ex.Message));
        }
    }

    private async void OnAboutClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<AboutPage>());

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        BusyIndicator.IsRunning = busy;
        OpenButton.IsEnabled = !busy;
    }

    private Task ShowAlertAsync(string title, string message) =>
        DisplayAlertAsync(title, message, _localization["ok"]);
}
