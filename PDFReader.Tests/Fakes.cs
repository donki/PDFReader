using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader.Tests;

// Dobles de lo que la app usa del dispositivo (Services/AppPlatform.cs) y del renderizado nativo.
// Registran lo que se les pide y responden lo que diga la prueba; ninguno abre nada de verdad.

public sealed class FakeFilePicker : IFilePicker
{
    public FileResult? Result { get; set; }
    public Exception? Throws { get; set; }
    public PickOptions? LastOptions { get; private set; }

    public Task<FileResult?> PickAsync(PickOptions? options = null)
    {
        LastOptions = options;
        if (Throws is not null) throw Throws;
        return Task.FromResult(Result);
    }

    public Task<IEnumerable<FileResult?>> PickMultipleAsync(PickOptions? options = null) => throw new NotSupportedException();
}

public sealed class FakeEmail : IEmail
{
    public Exception? Throws { get; set; }
    public EmailMessage? Sent { get; private set; }
    public bool IsComposeSupported => true;

    public Task ComposeAsync(EmailMessage? message)
    {
        if (Throws is not null) throw Throws;
        Sent = message;
        return Task.CompletedTask;
    }
}

public sealed class FakeBrowser : IBrowser
{
    public List<Uri> Opened { get; } = new();

    public Task<bool> OpenAsync(Uri uri, BrowserLaunchOptions options)
    {
        Opened.Add(uri);
        return Task.FromResult(true);
    }
}

public sealed class FakeAppInfo : IAppInfo
{
    public string PackageName => "com.socratic.pdfreader";
    public string Name => "PDF Reader";
    public string VersionString { get; set; } = "2026.10.02.0";
    public Version Version => new(2026, 10, 2, 0);
    public string BuildString => "2026100200";
    public void ShowSettingsUI() { }
    public AppTheme RequestedTheme => AppTheme.Light;
    public AppPackagingModel PackagingModel => AppPackagingModel.Packaged;
    public LayoutDirection RequestedLayoutDirection => LayoutDirection.LeftToRight;
}

public sealed class FakeDeviceDisplay : IDeviceDisplay
{
    public bool KeepScreenOn { get; set; }
    public DisplayInfo MainDisplayInfo { get; set; } = new(1080, 2400, 2.75, DisplayOrientation.Portrait, DisplayRotation.Rotation0);
    public event EventHandler<DisplayInfoChangedEventArgs>? MainDisplayInfoChanged { add { } remove { } }
}

/// <summary>Documento de mentira sobre las reglas comunes de <see cref="PdfDocumentBase"/>.</summary>
public sealed class FakePdfDocument(int pageCount, bool supportsSearch = true) : PdfDocumentBase(pageCount)
{
    public override bool SupportsTextSearch => supportsSearch;

    /// <summary>Tamaño de cada pagina (ancho, alto); por defecto A4 en puntos.</summary>
    public Func<int, (double, double)> Size { get; set; } = _ => (595, 842);

    /// <summary>Coincidencias por pagina para la busqueda.</summary>
    public Func<int, string, IEnumerable<PdfTextMatch>> Matches { get; set; } = (_, _) => [];

    public Exception? RenderThrows { get; set; }

    public List<(int Page, int Width, IReadOnlyList<PdfTextMatch> Highlights)> Rendered { get; } = new();

    public int Released { get; private set; }

    protected override (double Width, double Height) MeasurePage(int pageIndex) => Size(pageIndex);

    protected override Task<byte[]> RenderPngAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch> highlights)
    {
        if (RenderThrows is not null) throw RenderThrows;
        Rendered.Add((pageIndex, targetWidthPixels, highlights));
        return Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)pageIndex });
    }

    protected override IEnumerable<PdfTextMatch> SearchPage(int pageIndex, string query) => Matches(pageIndex, query);

    protected override void ReleaseNative() => Released++;
}

/// <summary>Abre siempre el mismo documento de mentira, o falla como diga la prueba (con o sin contraseña).</summary>
public sealed class FakePdfService : IPdfDocumentService
{
    public FakePdfDocument Document { get; set; } = new(3);

    /// <summary>Contraseña del documento (null = sin proteger).</summary>
    public string? Password { get; set; }

    public PdfOpenFailure? Failure { get; set; }

    public Exception? Throws { get; set; }

    public List<(string Path, string? Password)> Opened { get; } = new();

    public Task<IPdfDocument> OpenAsync(string filePath, string? password = null)
    {
        Opened.Add((filePath, password));
        if (Throws is not null) throw Throws;
        if (Failure is { } failure) throw new PdfOpenException(failure, failure.ToString());
        if (Password is not null && password != Password) throw PdfOpenException.Protected(password, new UnauthorizedAccessException());
        return Task.FromResult<IPdfDocument>(Document);
    }
}

/// <summary>Un aviso que la app quiso enseñar.</summary>
public sealed record Shown(Page Page, string Title, string Message, string Accept, string? Cancel);

/// <summary>
/// Estado comun de cada prueba que toca la app entera: datos en una carpeta temporal, preferencias
/// vacias, dobles nuevos, contenedor de servicios como el de MauiProgram y la App de verdad (con sus
/// estilos) como Application.Current. Las pruebas van en serie (LibraryServiceTests.cs).
/// </summary>
public sealed class AppHarness : IDisposable
{
    public FakeFilePicker Picker { get; } = new();
    public FakeEmail Email { get; } = new();
    public FakeBrowser Browser { get; } = new();
    public FakeAppInfo AppInfo { get; } = new();
    public FakeDeviceDisplay Display { get; } = new();
    public FakePdfService Pdf { get; } = new();
    public List<Shown> Alerts { get; } = new();
    public Queue<bool> AlertAnswers { get; } = new();
    public Queue<string?> PromptAnswers { get; } = new();
    public List<(string Title, string Initial)> Prompts { get; } = new();
    public List<(string To, string Subject, string Body, string Title)> Choosers { get; } = new();
    public TempFolder Temp { get; } = new();

    /// <summary>Lo que devuelve el selector de correo de Android (null = no hay, como fuera de Android).</summary>
    public bool? ChooserResult { get; set; }

    public bool MoveToBackResult { get; set; }
    public int MovedToBack { get; private set; }

    public ServiceProvider Services { get; }
    public ILocalizationService Localization { get; }
    public ILibraryService Library { get; }
    public PendingDocumentQueue Queue { get; }
    public App App { get; }

    public string UpdateManifest { get; set; } = "{}";

    public AppHarness(string language = "en")
    {
        FileSystem.AppDataDirectory = Temp.Path;
        Preferences.Default.Clear();
        Preferences.Default.Set("app_language", language);

        AppPlatform.FilePicker = Picker;
        AppPlatform.Email = Email;
        AppPlatform.Browser = Browser;
        AppPlatform.AppInfo = AppInfo;
        AppPlatform.DeviceDisplay = Display;
        AppPlatform.Alert = (page, title, message, accept, cancel) =>
        {
            Alerts.Add(new Shown(page, title, message, accept, cancel));
            return Task.FromResult(AlertAnswers.Count > 0 && AlertAnswers.Dequeue());
        };
        AppPlatform.Prompt = (page, title, message, accept, cancel, initial, placeholder) =>
        {
            Prompts.Add((title, initial));
            return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null);
        };
        AppPlatform.PdfFileType = () => null;
        AppPlatform.OpenPickedFile = result => Task.FromResult<Stream>(File.OpenRead(result.FullPath));
        AppPlatform.FadeInAsync = view => { view.Opacity = 1; return Task.CompletedTask; };
        AppPlatform.BeginInvokeOnMainThread = action => action();
        AppPlatform.RunOnMainThreadAsync = action => action();
        AppPlatform.MoveTaskToBack = () => { MovedToBack++; return MoveToBackResult; };
        AppPlatform.StartEmailChooser = (to, subject, body, title) =>
        {
            if (ChooserResult is null) return false;
            Choosers.Add((to, subject, body, title));
            return ChooserResult.Value;
        };

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IPdfDocumentService>(Pdf);
        services.AddSingleton<PendingDocumentQueue>();
        services.AddSingleton(sp => new UpdateService(sp.GetRequiredService<ILocalizationService>(), _ => Task.FromResult(UpdateManifest)));
        services.AddSingleton<LibraryPage>();
        services.AddTransient<AboutPage>();
        Services = services.BuildServiceProvider();

        Localization = Services.GetRequiredService<ILocalizationService>();
        Library = Services.GetRequiredService<ILibraryService>();
        Queue = Services.GetRequiredService<PendingDocumentQueue>();
        App = new App(Services, Localization);
        Application.Current = App;
    }

    /// <summary>Un PDF de la biblioteca (el contenido da igual: el renderizado es de mentira).</summary>
    public async Task<Models.PdfDocumentEntry> ImportAsync(string name = "manual.pdf", int pages = 3)
    {
        var entry = await Library.ImportAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), name);
        await Library.SetPageCountAsync(entry, pages);
        return entry;
    }

    public void Dispose()
    {
        Application.Current = null;
        // Lo que aun este en marcha (un dibujo pendiente, un temporizador) no debe tocar el
        // dispositivo de verdad cuando la prueba ya acabo: puertas inertes hasta la siguiente.
        AppPlatform.Alert = (_, _, _, _, _) => Task.FromResult(false);
        AppPlatform.Prompt = (_, _, _, _, _, _, _) => Task.FromResult<string?>(null);
        Services.Dispose();
        Temp.Dispose();
    }
}

/// <summary>Espera a que se cumpla algo que la app hace sin esperar (manejadores async void).</summary>
public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("La condicion no se cumplio a tiempo.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Las puertas de AppPlatform tal como las deja la app, para volver a ellas tras cada prueba.</summary>
public static class AppPlatformDefaults
{
    public static Func<Page, string, string, string, string?, Task<bool>> Alert { get; private set; } = null!;
    public static Func<Page, string, string, string, string, string, string, Task<string?>> Prompt { get; private set; } = null!;
    public static Func<VisualElement, Task> FadeInAsync { get; private set; } = null!;
    public static Func<FilePickerFileType?> PdfFileType { get; private set; } = null!;
    public static Func<FileResult, Task<Stream>> OpenPickedFile { get; private set; } = null!;
    public static Action<Action> BeginInvokeOnMainThread { get; private set; } = null!;
    public static Func<Func<Task>, Task> RunOnMainThreadAsync { get; private set; } = null!;
    public static Func<bool> MoveTaskToBack { get; private set; } = null!;
    public static Func<string, string, string, string, bool> StartEmailChooser { get; private set; } = null!;

    [ModuleInitializer]
    internal static void Capture()
    {
        Alert = AppPlatform.Alert;
        Prompt = AppPlatform.Prompt;
        FadeInAsync = AppPlatform.FadeInAsync;
        PdfFileType = AppPlatform.PdfFileType;
        OpenPickedFile = AppPlatform.OpenPickedFile;
        BeginInvokeOnMainThread = AppPlatform.BeginInvokeOnMainThread;
        RunOnMainThreadAsync = AppPlatform.RunOnMainThreadAsync;
        MoveTaskToBack = AppPlatform.MoveTaskToBack;
        StartEmailChooser = AppPlatform.StartEmailChooser;
    }

    public static void Restore()
    {
        AppPlatform.Alert = Alert;
        AppPlatform.Prompt = Prompt;
        AppPlatform.FadeInAsync = FadeInAsync;
        AppPlatform.PdfFileType = PdfFileType;
        AppPlatform.OpenPickedFile = OpenPickedFile;
        AppPlatform.BeginInvokeOnMainThread = BeginInvokeOnMainThread;
        AppPlatform.RunOnMainThreadAsync = RunOnMainThreadAsync;
        AppPlatform.MoveTaskToBack = MoveTaskToBack;
        AppPlatform.StartEmailChooser = StartEmailChooser;
    }
}

/// <summary>
/// Hilo principal de mentira: lo que se le manda se ejecuta en el acto. MAUI lo necesita para los
/// enlaces y la navegacion cuando no hay dispositivo.
/// </summary>
public sealed class ImmediateDispatcher : IDispatcher, IDispatcherProvider
{
    [ModuleInitializer]
    internal static void Install() => DispatcherProvider.SetCurrent(new ImmediateDispatcher());

    public IDispatcher? GetForCurrentThread() => this;

    public bool IsDispatchRequired => false;

    public bool Dispatch(Action action)
    {
        action();
        return true;
    }

    public bool DispatchDelayed(TimeSpan delay, Action action) => Dispatch(action);

    public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
}
