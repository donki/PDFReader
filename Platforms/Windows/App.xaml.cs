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

        // Un fallo no capturado en WinUI muere como 0xc000027b sin decir que paso; aqui queda escrito.
        UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(FileSystem.CacheDirectory, "crash.txt");
                File.AppendAllText(path, $"{DateTime.Now:O} {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Nada mas que hacer.
            }
        };
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
        var paths = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            .ToList();
        if (paths.Count == 0)
            return;

        var queue = IPlatformApplication.Current?.Services.GetService<PDFReader.Services.PendingDocumentQueue>();
        if (queue is null)
            return;

        var cacheFolder = Path.Combine(FileSystem.CacheDirectory, "incoming");
        _ = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    Directory.CreateDirectory(cacheFolder);
                    var temporaryPath = Path.Combine(cacheFolder, $"{Guid.NewGuid():N}.pdf");
                    File.Copy(path, temporaryPath, overwrite: true);
                    queue.Enqueue(new PDFReader.Services.PendingDocument(temporaryPath, Path.GetFileName(path)));
                }
                catch (Exception)
                {
                    // Sin pagina en pantalla todavia no hay donde avisar; se sigue con el siguiente.
                }
            }
        });
    }
}
