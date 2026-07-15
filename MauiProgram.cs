using Microsoft.Extensions.Logging;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
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

#if ANDROID
        builder.Services.AddSingleton<IPdfDocumentService, Platforms.Android.AndroidPdfDocumentService>();
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
