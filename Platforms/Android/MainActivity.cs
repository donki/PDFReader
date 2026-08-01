using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFReader.Services;
using AndroidX.Core.View;
using AndroidUri = Android.Net.Uri;
using AndroidView = Android.Views.View;

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
// Plenty of file managers hand a PDF over as a generic binary instead of application/pdf, which
// the filter above would miss. Match those by extension, and only by extension, so the app does
// not offer itself for every unknown file on the device.
// DataHost is required: Android ignores pathPattern unless the filter also declares a scheme AND
// a host, and without it this filter would match every octet-stream file on the device.
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataSchemes = ["content", "file"],
    DataHost = "*",
    DataMimeTypes = ["application/octet-stream", "application/x-pdf", "binary/octet-stream"],
    DataPathPatterns = [".*\\\\.pdf", ".*\\\\.PDF"])]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplySystemBarInsets();
        HandleIncomingPdf(Intent);
    }

    // Android 15 dibuja de borde a borde: separa el contenido del reloj y de la barra inferior.
    private void ApplySystemBarInsets()
    {
        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content is null) return;
        content.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#2A1CB8")); // indigo de marca
        ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());
        var controller = Window is not null ? WindowCompat.GetInsetsController(Window, Window.DecorView) : null;
        if (controller is not null)
        {
            controller.AppearanceLightStatusBars = false;
            controller.AppearanceLightNavigationBars = false;
        }
    }

    private class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(AndroidView? view, WindowInsetsCompat? insets)
        {
            var consumed = WindowInsetsCompat.Consumed!;
            if (view is null || insets is null) return consumed;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (bars is not null) view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return consumed;
        }
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
