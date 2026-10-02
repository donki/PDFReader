namespace PDFReader.Services;

/// <summary>
/// Lo que las paginas usan del dispositivo (selector de ficheros, correo, navegador, version,
/// pantalla, dialogos, hilo principal...) pasa por aqui. En la app cada puerta es la de MAUI; las
/// pruebas (constitucion General 8.6) ponen dobles para recorrer las paginas sin dispositivo.
/// </summary>
public static class AppPlatform
{
    private static IFilePicker? _filePicker;
    private static IEmail? _email;
    private static IBrowser? _browser;
    private static IAppInfo? _appInfo;
    private static IDeviceDisplay? _deviceDisplay;

    public static IFilePicker FilePicker { get => _filePicker ??= Microsoft.Maui.Storage.FilePicker.Default; set => _filePicker = value; }

    public static IEmail Email { get => _email ??= Microsoft.Maui.ApplicationModel.Communication.Email.Default; set => _email = value; }

    public static IBrowser Browser { get => _browser ??= Microsoft.Maui.ApplicationModel.Browser.Default; set => _browser = value; }

    public static IAppInfo AppInfo { get => _appInfo ??= Microsoft.Maui.ApplicationModel.AppInfo.Current; set => _appInfo = value; }

    public static IDeviceDisplay DeviceDisplay { get => _deviceDisplay ??= Microsoft.Maui.Devices.DeviceDisplay.Current; set => _deviceDisplay = value; }

    /// <summary>Tipo de fichero PDF para el selector (el de MAUI depende de la plataforma).</summary>
    public static Func<FilePickerFileType?> PdfFileType { get; set; } = () => FilePickerFileType.Pdf;

    /// <summary>Abre lo elegido en el selector.</summary>
    public static Func<FileResult, Task<Stream>> OpenPickedFile { get; set; } = result => result.OpenReadAsync();

    /// <summary>Aviso o confirmacion con el dialogo moderno comun (pagina, titulo, mensaje, aceptar, cancelar).</summary>
    public static Func<Page, string, string, string, string?, Task<bool>> Alert { get; set; } =
        (page, title, message, accept, cancel) => SocShared.ModernDialog.AlertAsync(page, title, message, accept, cancel);

    /// <summary>Pregunta de texto con el dialogo moderno (pagina, titulo, mensaje, aceptar, cancelar, valor, pista).</summary>
    public static Func<Page, string, string, string, string, string, string, Task<string?>> Prompt { get; set; } =
        (page, title, message, accept, cancel, initial, placeholder) =>
            SocShared.ModernDialog.PromptAsync(page, title, message, accept, cancel, initial, placeholder);

    /// <summary>Fundido de entrada de la pagina recien dibujada (las animaciones necesitan dispositivo).</summary>
    public static Func<VisualElement, Task> FadeInAsync { get; set; } = view => view.FadeToAsync(1, 60, Easing.CubicOut);

    /// <summary>Ejecuta en el hilo principal sin esperar.</summary>
    public static Action<Action> BeginInvokeOnMainThread { get; set; } = MainThread.BeginInvokeOnMainThread;

    /// <summary>Ejecuta en el hilo principal.</summary>
    public static Func<Func<Task>, Task> RunOnMainThreadAsync { get; set; } = MainThread.InvokeOnMainThreadAsync;

    /// <summary>
    /// Oculta la app sin cerrarla (atras en la biblioteca, Mobile 7). La pone MainActivity en
    /// Android; en Windows no hace nada y atras sigue su curso.
    /// </summary>
    public static Func<bool> MoveTaskToBack { get; set; } = () => false;

    /// <summary>
    /// Abre el selector del sistema con las apps de correo (para, asunto, cuerpo, titulo del
    /// selector). Lo pone MainActivity en Android; si devuelve false se usa el correo de MAUI.
    /// </summary>
    public static Func<string, string, string, string, bool> StartEmailChooser { get; set; } = (_, _, _, _) => false;
}
