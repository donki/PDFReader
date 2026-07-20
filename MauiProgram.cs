using Microsoft.Extensions.Logging;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        // Tipografia del sistema (constitucion, anexo A.9): no se embeben familias propias.
        builder.UseMauiApp<App>();

        // Servicios (constitucion, seccion 4: inyeccion de dependencias para todos los servicios)
        builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
        builder.Services.AddSingleton<ILibraryService, LibraryService>();
        builder.Services.AddSingleton<PendingDocumentQueue>();
        builder.Services.AddSingleton<UpdateService>();

#if ANDROID
        builder.Services.AddSingleton<IPdfDocumentService, Platforms.Android.AndroidPdfDocumentService>();
#endif

        // Shell (constitucion, anexo A.9: menu hamburguesa de primer nivel)
        builder.Services.AddSingleton<AppShell>();

        // Paginas
        builder.Services.AddSingleton<LibraryPage>();
        builder.Services.AddTransient<AboutPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
