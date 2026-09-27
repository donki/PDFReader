using Microsoft.Extensions.Logging;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Gestor global de excepciones (constitucion General 6.12): lo primero, antes de crear nada.
        // Registra en AppDataDirectory/crash.log y avisa sin cerrar la app, en el idioma elegido en
        // la app (que no toca CurrentUICulture, por eso se le pasa).
        SocShared.CrashGuard.Install("PDF Reader", language: () =>
            IPlatformApplication.Current?.Services.GetService<ILocalizationService>()?.CurrentLanguage);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Servicios (constitucion, seccion 4: inyeccion de dependencias para todos los servicios)
        builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
        builder.Services.AddSingleton<ILibraryService, LibraryService>();
        builder.Services.AddSingleton<PendingDocumentQueue>();
        // Sin esta linea la aplicacion abortaba al arrancar: LibraryPage lo pide por constructor
        // y el contenedor no sabia construirlo (CannotResolveService). Mismo fallo que tuvo
        // File Manager el 2026-08-01.
        builder.Services.AddSingleton<UpdateService>();

#if ANDROID
        builder.Services.AddSingleton<IPdfDocumentService, Platforms.Android.AndroidPdfDocumentService>();
#elif WINDOWS
        builder.Services.AddSingleton<IPdfDocumentService, Platforms.Windows.WindowsPdfDocumentService>();
#endif

        // Paginas
        builder.Services.AddSingleton<LibraryPage>();
        builder.Services.AddTransient<AboutPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
