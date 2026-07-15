using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFReader.Services;
using AndroidUri = Android.Net.Uri;

namespace PDFReader;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Lets the app show up under "Open with" for PDF files in file managers, mail clients and browsers.
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataSchemes = ["content", "file"],
    DataMimeType = "application/pdf")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIncomingPdf(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        if (intent is not null)
            Intent = intent;

        HandleIncomingPdf(intent);
    }

    /// <summary>
    /// Copies a PDF handed over by another app into our cache and queues it for the library,
    /// which may not be on screen yet when the intent arrives.
    /// </summary>
    private void HandleIncomingPdf(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView || intent.Data is null)
            return;

        var uri = intent.Data;
        var services = IPlatformApplication.Current?.Services;
        var queue = services?.GetService<PendingDocumentQueue>();
        var logger = services?.GetService<ILogger<MainActivity>>();

        if (queue is null)
        {
            logger?.LogError("The document queue is not available; the incoming PDF was ignored.");
            return;
        }

        var displayName = ResolveDisplayName(uri, logger);
        var resolver = ContentResolver;
        var cacheFolder = Path.Combine(CacheDir?.AbsolutePath ?? FileSystem.CacheDirectory, "incoming");

        _ = Task.Run(() =>
        {
            try
            {
                using var source = resolver?.OpenInputStream(uri)
                    ?? throw new IOException($"Could not read {uri}.");

                Directory.CreateDirectory(cacheFolder);
                var temporaryPath = Path.Combine(cacheFolder, $"{Guid.NewGuid():N}.pdf");

                using (var destination = File.Create(temporaryPath))
                {
                    source.CopyTo(destination);
                }

                queue.Enqueue(new PendingDocument(temporaryPath, displayName));
            }
            catch (Exception ex)
            {
                // No page is on screen yet to hold an alert; the log is the honest record.
                logger?.LogError(ex, "Could not read the incoming PDF from {Uri}.", uri);
            }
        });
    }

    private string ResolveDisplayName(AndroidUri uri, ILogger? logger)
    {
        try
        {
            if (uri.Scheme == "content" && ContentResolver is not null)
            {
                using var cursor = ContentResolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
                if (cursor is not null && cursor.MoveToFirst())
                {
                    var name = cursor.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }

            var lastSegment = uri.LastPathSegment;
            if (!string.IsNullOrWhiteSpace(lastSegment))
                return Path.GetFileName(lastSegment);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not resolve the name of {Uri}.", uri);
        }

        return "document.pdf";
    }
}
