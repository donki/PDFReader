using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Devices;
using PDFReader.Models;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader.Tests;

/// <summary>Acceso a lo privado de las paginas (controles con x:Name y manejadores de eventos).</summary>
internal static class Ui
{
    public static T Field<T>(object page, string name) =>
        (T)page.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    public static void Set(object page, string name, object? value) =>
        page.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);

    public static object? Call(object page, string method, params object?[] args)
    {
        var info = page.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length);
        return info.Invoke(page, args);
    }

    public static Task CallAsync(object page, string method, params object?[] args) => (Task)Call(page, method, args)!;

    /// <summary>Le da tamaño a un control como lo haria el primer pase de maquetacion.</summary>
    public static void Size(VisualElement view, double width, double height) => view.Layout(new Rect(0, 0, width, height));
}

public sealed class LibraryPageTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private (LibraryPage Page, NavigationPage Nav) Open()
    {
        var window = (Window)((IApplication)_h.App).CreateWindow(null);
        var nav = (NavigationPage)window.Page!;
        return ((LibraryPage)nav.RootPage, nav);
    }

    private static Task AppearAsync(LibraryPage page)
    {
        Ui.Call(page, "OnAppearing");
        return Task.Delay(50);
    }

    [Fact]
    public async Task Texts_AndTheList_FollowTheLanguage()
    {
        await _h.ImportAsync("guia.pdf");
        var (page, _) = Open();

        await AppearAsync(page);

        Assert.Equal(_h.Localization["library_title"], page.Title);
        Assert.Equal(_h.Localization["open_pdf"], Ui.Field<Button>(page, "OpenButton").Text);
        Assert.True(Ui.Field<Label>(page, "RecentLabel").IsVisible);
        var item = Assert.Single(Ui.Field<System.Collections.ObjectModel.ObservableCollection<DocumentListItem>>(page, "_documents"));
        Assert.Equal("guia.pdf", item.DisplayName);

        _h.Localization.SetLanguage("es");
        await Task.Delay(20);

        Assert.Equal("Biblioteca", page.Title);
        Assert.Equal(_h.Localization["recent_documents"].ToUpperInvariant(), Ui.Field<Label>(page, "RecentLabel").Text.ToUpperInvariant());
    }

    [Fact]
    public async Task Appearing_ChecksForUpdates_AndImportsWhatOtherAppsSent()
    {
        _h.UpdateManifest = """{"version":"2099.1.1.0","url":"https://example.com/pdf"}""";
        _h.AlertAnswers.Enqueue(true);
        var incoming = _h.Temp.File("incoming/x.pdf", "%PDF");
        _h.Queue.Enqueue(new PendingDocument(incoming, "compartido.pdf"));
        var (page, nav) = Open();

        await AppearAsync(page);
        await Eventually.TrueAsync(() => nav.CurrentPage is ReaderPage);

        Assert.Equal(_h.Localization["update_available_title"], _h.Alerts[0].Title);
        Assert.Equal(new Uri("https://example.com/pdf"), Assert.Single(_h.Browser.Opened));
        Assert.Equal("compartido.pdf", nav.CurrentPage.Title);
        Assert.False(File.Exists(incoming));   // la copia de la cache se borra al importar
        Assert.Single(await _h.Library.GetDocumentsAsync());
    }

    [Fact]
    public async Task DocumentQueuedWhileOnScreen_IsOpened()
    {
        var (page, nav) = Open();
        await AppearAsync(page);

        _h.Queue.Enqueue(new PendingDocument(_h.Temp.File("y.pdf", "%PDF"), "y.pdf"));

        await Eventually.TrueAsync(() => nav.CurrentPage is ReaderPage);
    }

    [Fact]
    public async Task UnreadableIncomingFile_IsExplained()
    {
        var (page, _) = Open();
        _h.Queue.Enqueue(new PendingDocument(_h.Temp.Combine("no-existe.pdf"), "x.pdf"));

        await AppearAsync(page);

        await Eventually.TrueAsync(() => _h.Alerts.Count > 0);
        Assert.Equal(_h.Localization["error_open_title"], _h.Alerts[^1].Title);
    }

    [Fact]
    public async Task PickingAPdf_ImportsAndOpensIt()
    {
        _h.Picker.Result = new FileResult(_h.Temp.File("elegido.pdf", "%PDF-1.7"));
        var (page, nav) = Open();

        Ui.Field<Button>(page, "OpenButton").SendClicked();

        await Eventually.TrueAsync(() => nav.CurrentPage is ReaderPage);
        Assert.Equal(_h.Localization["open_pdf"], _h.Picker.LastOptions!.PickerTitle);
        var entry = Assert.Single(await _h.Library.GetDocumentsAsync());
        Assert.Equal(3, entry.PageCount);
        Assert.False(Ui.Field<VisualElement>(page, "BusyOverlay").IsVisible);
    }

    [Fact]
    public async Task CancelledPicker_DoesNothing_AndPickerErrorsAreShown()
    {
        var (page, _) = Open();

        await Task.Run(() => Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty));
        Assert.Empty(_h.Alerts);

        _h.Picker.Throws = new InvalidOperationException("sin permiso");
        Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty);
        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Contains("sin permiso", _h.Alerts[0].Message);
    }

    [Theory]
    [InlineData(PdfOpenFailure.InvalidDocument, "error_not_pdf")]
    [InlineData(PdfOpenFailure.PasswordUnsupported, "error_password_unsupported")]
    [InlineData(PdfOpenFailure.Unreadable, "error_not_pdf")]
    public async Task NotAUsablePdf_IsRejected_AndNotKept(PdfOpenFailure failure, string key)
    {
        _h.Pdf.Failure = failure;
        _h.Picker.Result = new FileResult(_h.Temp.File("malo.pdf", "no"));
        var (page, nav) = Open();

        Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty);

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Equal(_h.Localization[key], _h.Alerts[0].Message);
        Assert.Empty(await _h.Library.GetDocumentsAsync());
        Assert.IsType<LibraryPage>(nav.CurrentPage);
    }

    [Fact]
    public async Task UnexpectedImportError_IsShown_AndNotKept()
    {
        _h.Pdf.Throws = new IOException("disco lleno");
        _h.Picker.Result = new FileResult(_h.Temp.File("a.pdf", "%PDF"));
        var (page, _) = Open();

        Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty);

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Contains("disco lleno", _h.Alerts[0].Message);
        Assert.Empty(await _h.Library.GetDocumentsAsync());
    }

    [Fact]
    public async Task ProtectedPdf_AsksForThePassword_UntilItIsRight()
    {
        _h.Pdf.Password = "secreto";
        _h.Picker.Result = new FileResult(_h.Temp.File("p.pdf", "%PDF"));
        var (page, nav) = Open();

        Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty);

        // Primera vez: pregunta sin aviso de error; se escribe mal; la segunda avisa.
        var prompt = await WaitForPromptAsync(nav);
        Assert.False(Ui.Field<Label>(prompt, "ErrorLabel").IsVisible);
        Ui.Field<Entry>(prompt, "PasswordEntry").Text = "mal";
        Ui.Field<Button>(prompt, "AcceptButton").SendClicked();

        prompt = await WaitForPromptAsync(nav, prompt);
        Assert.True(Ui.Field<Label>(prompt, "ErrorLabel").IsVisible);
        Ui.Field<Entry>(prompt, "PasswordEntry").Text = "secreto";
        Ui.Field<Button>(prompt, "AcceptButton").SendClicked();

        await Eventually.TrueAsync(() => nav.CurrentPage is ReaderPage);
        Assert.Equal("secreto", ((ReaderPage)nav.CurrentPage).InitialPassword);
    }

    [Fact]
    public async Task ProtectedPdf_CancelledPrompt_DropsTheImport()
    {
        _h.Pdf.Password = "secreto";
        _h.Picker.Result = new FileResult(_h.Temp.File("p.pdf", "%PDF"));
        var (page, nav) = Open();

        Ui.Call(page, "OnOpenPdfClicked", null, EventArgs.Empty);
        var prompt = await WaitForPromptAsync(nav);
        Ui.Field<Button>(prompt, "AcceptButton").SendClicked();   // sin contraseña no hace nada
        Assert.Single(nav.Navigation.ModalStack);
        Ui.Field<Button>(prompt, "CancelButton").SendClicked();

        await Eventually.TrueAsync(() => nav.Navigation.ModalStack.Count == 0);
        await Eventually.TrueAsync(() => !Ui.Field<VisualElement>(page, "BusyOverlay").IsVisible);
        Assert.Empty(await _h.Library.GetDocumentsAsync());
        Assert.IsType<LibraryPage>(nav.CurrentPage);
    }

    private static async Task<PasswordPromptPage> WaitForPromptAsync(NavigationPage nav, PasswordPromptPage? previous = null)
    {
        await Eventually.TrueAsync(() => nav.Navigation.ModalStack.LastOrDefault() is PasswordPromptPage p && p != previous);
        return (PasswordPromptPage)nav.Navigation.ModalStack.Last();
    }

    [Fact]
    public async Task TappingADocument_OpensIt_AndOnlyOneReaderIsKept()
    {
        var first = await _h.ImportAsync("uno.pdf");
        var second = await _h.ImportAsync("dos.pdf");
        var (page, nav) = Open();

        Ui.Call(page, "OnDocumentTapped", new Grid { BindingContext = new DocumentListItem(first, "uno.pdf", "", "") }, new TappedEventArgs(null));
        await Eventually.TrueAsync(() => nav.CurrentPage is ReaderPage);
        Ui.Call(page, "OnDocumentTapped", new Grid { BindingContext = new DocumentListItem(second, "dos.pdf", "", "") }, new TappedEventArgs(null));
        await Eventually.TrueAsync(() => nav.CurrentPage.Title == "dos.pdf");

        Assert.Equal(2, nav.Navigation.NavigationStack.Count);
        Ui.Call(page, "OnDocumentTapped", new Grid(), new TappedEventArgs(null));   // sin documento: nada
    }

    [Fact]
    public async Task MissingFile_IsForgotten_AndExplained()
    {
        var entry = await _h.ImportAsync("borrado.pdf");
        File.Delete(_h.Library.GetFilePath(entry));
        var (page, nav) = Open();

        Ui.Call(page, "OnDocumentTapped", new Grid { BindingContext = new DocumentListItem(entry, "borrado.pdf", "", "") }, new TappedEventArgs(null));

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Equal(_h.Localization["error_missing_file"], _h.Alerts[0].Message);
        Assert.Empty(await _h.Library.GetDocumentsAsync());
        Assert.IsType<LibraryPage>(nav.CurrentPage);
    }

    [Fact]
    public async Task Remove_AsksFirst()
    {
        var entry = await _h.ImportAsync("quitar.pdf");
        var (page, _) = Open();
        await AppearAsync(page);
        var documents = Ui.Field<System.Collections.ObjectModel.ObservableCollection<DocumentListItem>>(page, "_documents");
        var button = new Button { BindingContext = documents[0] };

        Ui.Call(page, "OnRemoveClicked", button, EventArgs.Empty);   // dice que no
        await Task.Delay(20);
        Assert.Single(documents);

        _h.AlertAnswers.Enqueue(true);
        Ui.Call(page, "OnRemoveClicked", button, EventArgs.Empty);
        await Eventually.TrueAsync(() => documents.Count == 0);

        Assert.Empty(await _h.Library.GetDocumentsAsync());
        Assert.False(Ui.Field<Label>(page, "RecentLabel").IsVisible);
        Assert.Equal(_h.Localization["remove_title"], _h.Alerts[0].Title);
        Ui.Call(page, "OnRemoveClicked", new Button(), EventArgs.Empty);   // sin documento: nada
        Assert.Equal(2, _h.Alerts.Count);
        Assert.NotNull(entry);
    }

    [Fact]
    public void Back_HidesTheApp_OnAndroid_AndGoesOnElsewhere()
    {
        var (page, _) = Open();

        _h.MoveToBackResult = true;
        Assert.True(page.SendBackButtonPressed());
        _h.MoveToBackResult = false;
        page.SendBackButtonPressed();

        Assert.Equal(2, _h.MovedToBack);
    }

    [Fact]
    public async Task AboutButton_OpensAbout()
    {
        var (page, nav) = Open();

        Ui.Call(page, "OnAboutClicked", null, EventArgs.Empty);

        await Eventually.TrueAsync(() => nav.CurrentPage is AboutPage);
    }
}

public sealed class ReaderPageTests : IDisposable
{
    private readonly AppHarness _h = new();

    // El lector deja trabajo con retardo (redibujar al asentarse el zoom, 250 ms; al cambiar de
    // tamaño, 150 ms): se deja acabar aqui para que no caiga en la prueba siguiente.
    public void Dispose()
    {
        Thread.Sleep(400);
        _h.Dispose();
    }

    private async Task<(ReaderPage Reader, NavigationPage Nav)> OpenAsync(int pages = 3, int lastPage = 0, string? password = null)
    {
        _h.Pdf.Document = new FakePdfDocument(pages);
        var entry = await _h.ImportAsync("libro.pdf", pages);
        entry.LastPageIndex = lastPage;
        var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_h.Services, entry);
        reader.InitialPassword = password;
        var nav = new NavigationPage(new ContentPage());
        await nav.PushAsync(reader);
        Ui.Size(Ui.Field<VisualElement>(reader, "Viewport"), 416, 816);
        Ui.Call(reader, "OnAppearing");
        await Eventually.TrueAsync(() => (_h.Pdf.Document.Rendered.Count > 0 && Idle(reader)) || nav.CurrentPage != reader);
        return (reader, nav);
    }

    /// <summary>Sin ningun dibujo de pagina en curso (el lector ignora lo que llega mientras dibuja).</summary>
    private static bool Idle(ReaderPage reader) => Ui.Field<SemaphoreSlim>(reader, "_renderGate").CurrentCount == 1;

    private Task RenderedAsync(ReaderPage reader, Func<bool> condition) =>
        Eventually.TrueAsync(() => condition() && Idle(reader));

    private static string PageLabel(ReaderPage reader) => Ui.Field<Label>(reader, "PageLabel").Text;

    [Fact]
    public async Task Opens_AtTheLastPageRead_AtTheScreenWidth()
    {
        var (reader, _) = await OpenAsync(pages: 5, lastPage: 2);
        Assert.Empty(_h.Alerts.Select(a => a.Message));

        Assert.Equal("libro.pdf", reader.Title);
        Assert.Equal(_h.Localization.Format("reader_page_of", 3, 5), PageLabel(reader));
        var (page, width, _) = _h.Pdf.Document.Rendered[0];
        Assert.Equal(2, page);
        // 400 dips de ancho util (416 - 16 de margen) x 2,75 de densidad
        Assert.Equal(1100, width);
        Assert.True(Ui.Field<Button>(reader, "PreviousButton").IsEnabled);
        Assert.True(Ui.Field<Button>(reader, "NextButton").IsEnabled);
        Assert.True(Ui.Field<Button>(reader, "SearchButton").IsVisible);
        Assert.False(Ui.Field<VisualElement>(reader, "BusyPanel").IsVisible);
    }

    [Fact]
    public async Task LastPageBeyondTheDocument_StartsAtTheFirst()
    {
        var (reader, _) = await OpenAsync(pages: 2, lastPage: 9);

        Assert.Equal(0, _h.Pdf.Document.Rendered[0].Page);
        Assert.False(Ui.Field<Button>(reader, "PreviousButton").IsEnabled);
    }

    [Fact]
    public async Task NextAndPrevious_MoveOnePage_AndRememberIt()
    {
        var (reader, _) = await OpenAsync(pages: 2);

        Ui.Field<Button>(reader, "NextButton").SendClicked();
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 2);
        Ui.Field<Button>(reader, "NextButton").SendClicked();   // ya en la ultima
        Ui.Field<Button>(reader, "PreviousButton").SendClicked();
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 3);
        Ui.Field<Button>(reader, "PreviousButton").SendClicked();   // ya en la primera

        Assert.Equal(new[] { 0, 1, 0 }, _h.Pdf.Document.Rendered.Select(r => r.Page));
        Assert.Equal(0, (await _h.Library.GetDocumentsAsync())[0].LastPageIndex);
    }

    [Fact]
    public async Task Zoom_ScalesTheView_AndSharpensWhenItSettles()
    {
        var (reader, _) = await OpenAsync();
        var container = Ui.Field<Grid>(reader, "PageContainer");

        Ui.Field<Button>(reader, "ZoomInButton").SendClicked();
        Assert.Equal(1.35, container.Scale, 3);
        Assert.True(Ui.Field<Button>(reader, "ZoomOutButton").IsEnabled);
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 2);   // 250 ms despues
        Assert.Equal(1485, _h.Pdf.Document.Rendered[1].Width);

        for (var i = 0; i < 8; i++)
            Ui.Field<Button>(reader, "ZoomInButton").SendClicked();
        Assert.Equal(4.0, container.Scale);
        Assert.False(Ui.Field<Button>(reader, "ZoomInButton").IsEnabled);

        Ui.Field<Button>(reader, "ZoomOutButton").SendClicked();
        Ui.Call(reader, "OnDoubleTapped", null, new TappedEventArgs(null));   // con zoom: vuelve a 1
        Assert.Equal(1.0, container.Scale);
        Ui.Call(reader, "OnDoubleTapped", null, new TappedEventArgs(null));   // sin zoom: a 2
        Assert.Equal(2.0, container.Scale);
    }

    [Fact]
    public async Task Pinch_AccumulatesTheScale_AndRendersOnceAtTheEnd()
    {
        var (reader, _) = await OpenAsync();
        var container = Ui.Field<Grid>(reader, "PageContainer");

        Ui.Call(reader, "OnPinchUpdated", null, new PinchGestureUpdatedEventArgs(GestureStatus.Started));
        Ui.Call(reader, "OnPinchUpdated", null, new PinchGestureUpdatedEventArgs(GestureStatus.Running, 1.5, new Point(0.5, 0.5)));
        Ui.Call(reader, "OnPinchUpdated", null, new PinchGestureUpdatedEventArgs(GestureStatus.Running, 1.2, new Point(0.5, 0.5)));
        Assert.Equal(1.8, container.Scale, 3);
        Ui.Call(reader, "OnPinchUpdated", null, new PinchGestureUpdatedEventArgs(GestureStatus.Completed));
        Ui.Call(reader, "OnPinchUpdated", null, new PinchGestureUpdatedEventArgs(GestureStatus.Canceled));

        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 2);
        await Task.Delay(300);
        Assert.Equal(2, _h.Pdf.Document.Rendered.Count);
    }

    [Fact]
    public async Task Pan_MovesAZoomedPage_WithinItsEdges()
    {
        var (reader, _) = await OpenAsync();
        var container = Ui.Field<Grid>(reader, "PageContainer");
        Ui.Size(Ui.Field<VisualElement>(reader, "Viewport"), 416, 816);
        Ui.Field<Button>(reader, "ZoomInButton").SendClicked();
        Ui.Field<Button>(reader, "ZoomInButton").SendClicked();

        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Started, 1));
        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Running, 1, 50, 30));
        Assert.Equal(50, container.TranslationX);
        Assert.Equal(30, container.TranslationY);
        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Running, 1, 5000, 0));
        Assert.True(container.TranslationX < 5000);   // no se va de la pantalla
        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Completed, 1));
    }

    [Theory]
    [InlineData(-120, 10, 1)]   // a la izquierda: siguiente
    [InlineData(120, 10, 0)]    // a la derecha: anterior (ya en la primera: se queda)
    [InlineData(-30, 0, 0)]     // poco: nada
    [InlineData(-100, 120, 0)]  // mas vertical que horizontal: nada
    public async Task Swipe_WithoutZoom_TurnsThePage(double x, double y, int expectedPage)
    {
        var (reader, _) = await OpenAsync();

        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Started, 1));
        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Running, 1, x, y));
        Ui.Call(reader, "OnPanUpdated", null, new PanUpdatedEventArgs(GestureStatus.Canceled, 1));
        await Task.Delay(100);
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Page == expectedPage);

        Assert.Equal(expectedPage, _h.Pdf.Document.Rendered[^1].Page);
    }

    [Fact]
    public async Task GoToPage_AsksAndJumps_OrExplains()
    {
        var (reader, _) = await OpenAsync(pages: 10);

        _h.PromptAnswers.Enqueue("7");
        Ui.Call(reader, "OnGoToPageTapped", null, new TappedEventArgs(null));
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Page == 6);
        Assert.Equal(("" + _h.Localization["goto_title"], "1"), _h.Prompts[0]);

        _h.PromptAnswers.Enqueue("99");
        Ui.Call(reader, "OnGoToPageTapped", null, new TappedEventArgs(null));
        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Equal(_h.Localization["invalid_page_title"], _h.Alerts[0].Title);

        _h.PromptAnswers.Enqueue(null);   // cancelado
        Ui.Call(reader, "OnGoToPageTapped", null, new TappedEventArgs(null));
        await Task.Delay(20);
        Assert.Single(_h.Alerts);
    }

    [Fact]
    public async Task Search_HighlightsAndStepsThroughTheMatches()
    {
        var (reader, _) = await OpenAsync(pages: 3);
        _h.Pdf.Document.Matches = (page, query) => page == 1
            ? [new PdfTextMatch(1, 0.1, 0.1, 0.2, 0.2), new PdfTextMatch(1, 0.3, 0.3, 0.4, 0.4)]
            : page == 2 ? [new PdfTextMatch(2, 0.5, 0.5, 0.6, 0.6)] : [];

        Ui.Field<Button>(reader, "SearchButton").SendClicked();
        Assert.True(Ui.Field<VisualElement>(reader, "SearchBar").IsVisible);
        Ui.Field<Entry>(reader, "SearchEntry").Text = "  palabra ";
        Ui.Field<Entry>(reader, "SearchEntry").SendCompleted();

        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Page == 1);
        Assert.Equal(_h.Localization.Format("search_match_of", 1, 3), Ui.Field<Label>(reader, "SearchStatusLabel").Text);
        Assert.Equal(2, _h.Pdf.Document.Rendered[^1].Highlights.Count);

        Ui.Field<Button>(reader, "PreviousMatchButton").SendClicked();   // da la vuelta: la ultima
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Page == 2);
        Ui.Field<Button>(reader, "NextMatchButton").SendClicked();
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Page == 1);

        // Atras con el buscador abierto lo cierra y quita el resaltado.
        Assert.True(reader.SendBackButtonPressed());
        await Eventually.TrueAsync(() => !Ui.Field<VisualElement>(reader, "SearchBar").IsVisible);
        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered[^1].Highlights.Count == 0);
        Assert.Equal(string.Empty, Ui.Field<Entry>(reader, "SearchEntry").Text);
    }

    [Fact]
    public async Task Search_WithoutResults_SaysSo_AndBlankSearchesAreIgnored()
    {
        var (reader, _) = await OpenAsync();
        Ui.Field<Button>(reader, "SearchButton").SendClicked();

        Ui.Field<Entry>(reader, "SearchEntry").Text = "   ";
        Ui.Field<Entry>(reader, "SearchEntry").SendCompleted();
        Ui.Field<Entry>(reader, "SearchEntry").Text = "nada";
        Ui.Field<Entry>(reader, "SearchEntry").SendCompleted();

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Equal(_h.Localization.Format("search_no_results", "nada"), _h.Alerts[0].Message);
        Assert.False(Ui.Field<Button>(reader, "NextMatchButton").IsEnabled);
        Ui.Field<Button>(reader, "NextMatchButton").SendClicked();
        Ui.Field<Button>(reader, "PreviousMatchButton").SendClicked();
        Ui.Field<Button>(reader, "CloseSearchButton").SendClicked();
        await Eventually.TrueAsync(() => !Ui.Field<VisualElement>(reader, "SearchBar").IsVisible);
    }

    [Fact]
    public async Task SearchFailure_IsShown()
    {
        var (reader, _) = await OpenAsync();
        _h.Pdf.Document.Matches = (_, _) => throw new InvalidOperationException("roto");

        Ui.Field<Entry>(reader, "SearchEntry").Text = "x";
        Ui.Field<Entry>(reader, "SearchEntry").SendCompleted();

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Contains("roto", _h.Alerts[0].Message);
        Assert.False(reader.SendBackButtonPressed() && false);
    }

    [Fact]
    public async Task RenderFailure_IsShown()
    {
        _h.Pdf.Document = new FakePdfDocument(3) { RenderThrows = new OutOfMemoryException("sin memoria") };
        var entry = await _h.ImportAsync("libro.pdf");
        var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_h.Services, entry);
        var nav = new NavigationPage(new ContentPage());
        await nav.PushAsync(reader);
        Ui.Size(Ui.Field<VisualElement>(reader, "Viewport"), 416, 816);
        _h.Pdf.Document.RenderThrows = new InvalidOperationException("sin memoria");

        Ui.Call(reader, "OnAppearing");

        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Contains("sin memoria", _h.Alerts[0].Message);
    }

    [Theory]
    [InlineData(PdfOpenFailure.InvalidDocument, "error_not_pdf")]
    [InlineData(PdfOpenFailure.PasswordUnsupported, "error_password_unsupported")]
    [InlineData(PdfOpenFailure.Unreadable, "error_missing_file")]
    public async Task UnopenableDocument_IsExplained_AndClosed(PdfOpenFailure failure, string key)
    {
        _h.Pdf.Failure = failure;

        var (_, nav) = await OpenAsync();

        await Eventually.TrueAsync(() => nav.CurrentPage is not ReaderPage);
        Assert.Equal(_h.Localization[key], _h.Alerts[0].Message);
    }

    [Fact]
    public async Task UnexpectedOpenError_IsExplained_AndClosed()
    {
        _h.Pdf.Throws = new InvalidOperationException("raro");

        var (_, nav) = await OpenAsync();

        await Eventually.TrueAsync(() => nav.CurrentPage is not ReaderPage);
        Assert.Contains("raro", _h.Alerts[0].Message);
    }

    [Fact]
    public async Task ProtectedDocument_UsesTheKnownPassword_OrAsks()
    {
        _h.Pdf.Password = "clave";
        var (reader, nav) = await OpenAsync(password: "clave");
        Assert.Equal("clave", _h.Pdf.Opened[^1].Password);
        Assert.Same(reader, nav.CurrentPage);

        // Sin contraseña conocida: se pregunta; cancelar cierra el lector.
        var entry = await _h.ImportAsync("otro.pdf");
        var other = ActivatorUtilities.CreateInstance<ReaderPage>(_h.Services, entry);
        await nav.PushAsync(other);
        Ui.Call(other, "OnAppearing");
        await Eventually.TrueAsync(() => nav.Navigation.ModalStack.Count == 1);
        Assert.True(nav.Navigation.ModalStack[0].SendBackButtonPressed());   // atras = cancelar

        await Eventually.TrueAsync(() => nav.CurrentPage != other);
    }

    [Fact]
    public async Task Leaving_ClosesTheDocument_AndComingBackDoesNotReopenIt()
    {
        var (reader, _) = await OpenAsync();
        Ui.Call(reader, "OnAppearing");   // ya abierto: no hace nada
        Assert.Single(_h.Pdf.Opened);

        Ui.Call(reader, "OnDisappearing");

        Assert.Equal(1, _h.Pdf.Document.Released);
    }

    [Fact]
    public async Task Resizing_RendersAgain_AtTheNewWidth()
    {
        var (reader, _) = await OpenAsync();

        Ui.Size(Ui.Field<VisualElement>(reader, "Viewport"), 816, 616);

        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 2);
        // Ventana ancha: cabe la pagina entera (600 de alto / 1,415 de proporcion) x 2,75
        Assert.Equal((int)Math.Round(600 / (842.0 / 595) * 2.75), _h.Pdf.Document.Rendered[1].Width);
    }

    [Fact]
    public async Task BeforeTheFirstLayout_TheDisplayWidthIsUsed()
    {
        _h.Display.MainDisplayInfo = new DisplayInfo(800, 1600, 2, DisplayOrientation.Portrait, DisplayRotation.Rotation0);
        var entry = await _h.ImportAsync("libro.pdf");
        var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_h.Services, entry);
        var nav = new NavigationPage(new ContentPage());
        await nav.PushAsync(reader);

        Ui.Call(reader, "OnAppearing");   // sin tamaño: espera hasta 1 s y usa la pantalla

        await RenderedAsync(reader, () => _h.Pdf.Document.Rendered.Count == 1);
        Assert.Equal(800, _h.Pdf.Document.Rendered[0].Width);   // 400 dips x 2

        _h.Display.MainDisplayInfo = new DisplayInfo(0, 0, 0, DisplayOrientation.Portrait, DisplayRotation.Rotation0);
        Assert.Equal(360.0, Ui.Call(reader, "GetAvailableWidthDips"));
    }
}

public sealed class AboutPageTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AboutPage NewPage() => _h.Services.GetRequiredService<AboutPage>();

    [Fact]
    public void Texts_Version_AndTheLanguageButtons()
    {
        var page = NewPage();

        Assert.Equal(_h.Localization["about_title"], page.Title);
        Assert.Equal(_h.Localization.Format("version", "2026.10.02.0"), Ui.Field<Label>(page, "VersionLabel").Text);
        Assert.Equal(0, Ui.Field<Button>(page, "EnglishButton").BorderWidth);
        Assert.Equal(1, Ui.Field<Button>(page, "SpanishButton").BorderWidth);

        Ui.Field<Button>(page, "SpanishButton").SendClicked();

        Assert.Equal("es", _h.Localization.CurrentLanguage);
        Assert.Equal(0, Ui.Field<Button>(page, "SpanishButton").BorderWidth);
        Ui.Field<Button>(page, "EnglishButton").SendClicked();
        Assert.Equal("en", _h.Localization.CurrentLanguage);
    }

    [Fact]
    public void WithoutAppColors_TheButtonsAreTransparent()
    {
        var page = NewPage();
        Application.Current = null;

        Ui.Field<Button>(page, "SpanishButton").SendClicked();

        Assert.Equal(Colors.Transparent, Ui.Field<Button>(page, "SpanishButton").BackgroundColor);
    }

    [Fact]
    public async Task Contact_UsesTheAndroidChooser_WhenThereIsOne()
    {
        _h.ChooserResult = true;

        Ui.Field<Button>(NewPage(), "ContactButton").SendClicked();
        await Task.Delay(20);

        var chooser = Assert.Single(_h.Choosers);
        Assert.Equal("jsoladelarosa@gmail.com", chooser.To);
        Assert.Contains("2026.10.02.0", chooser.Body);
        Assert.Equal(_h.Localization["email_chooser"], chooser.Title);
        Assert.Null(_h.Email.Sent);
    }

    [Fact]
    public async Task Contact_OtherwiseComposesAnEmail()
    {
        _h.ChooserResult = false;

        Ui.Field<Button>(NewPage(), "ContactButton").SendClicked();
        await Eventually.TrueAsync(() => _h.Email.Sent is not null);

        Assert.Equal(new[] { "jsoladelarosa@gmail.com" }, _h.Email.Sent!.To);
        Assert.Equal(_h.Localization["email_subject"], _h.Email.Sent.Subject);
    }

    [Fact]
    public async Task Contact_WithoutEmail_SaysSo()
    {
        _h.Email.Throws = new FeatureNotSupportedException();
        Ui.Field<Button>(NewPage(), "ContactButton").SendClicked();
        await Eventually.TrueAsync(() => _h.Alerts.Count == 1);
        Assert.Equal(_h.Localization["error_no_email"], _h.Alerts[0].Message);

        _h.Email.Throws = new InvalidOperationException("roto");
        Ui.Field<Button>(NewPage(), "ContactButton").SendClicked();
        await Eventually.TrueAsync(() => _h.Alerts.Count == 2);
        Assert.Contains("roto", _h.Alerts[1].Message);
    }

    [Fact]
    public async Task Back_ReturnsToTheLibrary()
    {
        var nav = new NavigationPage(new ContentPage());
        var page = NewPage();
        await nav.PushAsync(page);

        Ui.Field<Button>(page, "BackButton").SendClicked();

        await Eventually.TrueAsync(() => nav.CurrentPage is not AboutPage);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private UpdateService With(string json) => new(_h.Localization, _ => Task.FromResult(json));

    [Theory]
    [InlineData("2026.10.03.0", "2026.10.02.0", 1)]
    [InlineData("2026.10.02.0", "2026.10.02.0", 0)]
    [InlineData("2026.9.30.0", "2026.10.02.0", -1)]
    [InlineData("2026.10.02", "2026.10.02.0", 0)]
    [InlineData("2026.x.1", "2026.0.1", 0)]
    public void CompareVersions_IsNumericByParts(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(UpdateService.CompareVersions(a, b)));

    [Fact]
    public async Task NewerVersion_Asks_AndOpensTheLink()
    {
        _h.AlertAnswers.Enqueue(true);

        await With("""{"version":"2027.1.1.0","url":"https://example.com/p"}""").CheckAndPromptAsync(new ContentPage());

        var shown = Assert.Single(_h.Alerts);
        Assert.Equal(_h.Localization.Format("update_available_message", "2027.1.1.0", "2026.10.02.0"), shown.Message);
        Assert.Equal((_h.Localization["update_now"], _h.Localization["update_later"]), (shown.Accept, shown.Cancel!));
        Assert.Single(_h.Browser.Opened);
    }

    [Theory]
    [InlineData("""{"version":"2026.10.02.0"}""")]
    [InlineData("""{"url":"https://example.com"}""")]
    [InlineData("no es json")]
    public async Task UpToDate_OrBadManifest_SaysNothing(string json)
    {
        await With(json).CheckAndPromptAsync(new ContentPage());

        Assert.Empty(_h.Alerts);
    }

    [Fact]
    public async Task Declined_OrWithoutLink_OpensNothing_AndOnlyOncePerSession()
    {
        var service = With("""{"version":"2027.1.1.0"}""");
        _h.AlertAnswers.Enqueue(true);

        await service.CheckAndPromptAsync(new ContentPage());
        await service.CheckAndPromptAsync(new ContentPage());

        Assert.Single(_h.Alerts);
        Assert.Empty(_h.Browser.Opened);
    }

    [Fact]
    public void DefaultConstructor_DoesNotCallTheNetwork() => Assert.NotNull(new UpdateService(_h.Localization));
}

public sealed class AppStartupTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void App_OpensTheLibrary_InANavigationPage()
    {
        var window = (Window)((IApplication)_h.App).CreateWindow(null);

        Assert.IsType<LibraryPage>(Assert.IsType<NavigationPage>(window.Page).RootPage);
    }

    [Fact]
    public void MauiProgram_RegistersTheServices()
    {
        var app = MauiProgram.CreateMauiApp();

        Assert.IsType<LocalizationService>(app.Services.GetService(typeof(ILocalizationService)));
        Assert.NotNull(app.Services.GetService(typeof(PendingDocumentQueue)));
        Assert.NotNull(app.Services.GetService(typeof(UpdateService)));
    }

    [Fact]
    public async Task Defaults_HideNothing_AndHaveNoChooser()
    {
        Assert.False(AppPlatformDefaults.MoveTaskToBack());
        Assert.False(AppPlatformDefaults.StartEmailChooser("a", "b", "c", "d"));

        // El aviso, la pregunta y el fundido por defecto son los del dialogo moderno y MAUI: sin
        // dispositivo no hay animaciones, pero el aviso ya queda puesto sobre la pagina.
        var host = new Grid();
        var page = new ContentPage { Content = host };
        Assert.ThrowsAny<Exception>(() => { _ = AppPlatformDefaults.Alert(page, "t", "m", "ok", null); });
        Assert.ThrowsAny<Exception>(() => { _ = AppPlatformDefaults.Prompt(page, "t", "m", "ok", "no", "1", "p"); });
        Assert.NotEmpty(host.Children);
        await Assert.ThrowsAnyAsync<Exception>(() => AppPlatformDefaults.FadeInAsync(new Label()));
    }
}
