using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PDFReader.WinUI;

/// <summary>Arranque WinUI de la misma aplicacion MAUI que corre en Android.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        HandleCommandLine();
    }

    /// <summary>
    /// «Abrir con» desde el Explorador o desde la consola: el PDF llega como argumento. Se copia a
    /// la cache y se encola, igual que hace MainActivity en Android con el intent ACTION_VIEW.
    /// </summary>
    private static void HandleCommandLine()
    {
        var paths = PDFReader.Services.IncomingDocuments.PdfArguments(Environment.GetCommandLineArgs());
        var queue = IPlatformApplication.Current?.Services.GetService<PDFReader.Services.PendingDocumentQueue>();
        if (paths.Count == 0 || queue is null)
            return;

        var cacheFolder = PDFReader.Services.IncomingDocuments.CacheFolder(FileSystem.CacheDirectory);
        _ = Task.Run(() =>
        {
            foreach (var path in paths)
                PDFReader.Services.IncomingDocuments.CopyAndQueue(() => File.OpenRead(path), Path.GetFileName(path), cacheFolder, queue);
        });
    }
}
