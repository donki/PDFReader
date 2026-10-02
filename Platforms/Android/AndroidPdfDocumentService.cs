using System.Runtime.Versioning;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using PDFReader.Services;
using AndroidColor = Android.Graphics.Color;
using AndroidPaint = Android.Graphics.Paint;

namespace PDFReader.Platforms.Android;

/// <summary>
/// Renders PDF pages with android.graphics.pdf.PdfRenderer, the renderer built into the
/// platform since API 21. It keeps the app free of third-party PDF dependencies, which is
/// what allows PDF Reader to ship under the MIT license with no further obligations.
///
/// Decrypting protected documents and searching text are platform features of Android 15
/// (API 35). Below that the renderer offers no way to do either, so both degrade instead of
/// pulling in an external library.
/// </summary>
public class AndroidPdfDocumentService : IPdfDocumentService
{
    // The API level checks below are written as the literal 35 -Android 15, the release that added
    // LoadParams and Page.searchText- because the platform-compatibility analyser only recognises a
    // literal argument to IsAndroidVersionAtLeast and would flag the guarded calls otherwise.

    public Task<IPdfDocument> OpenAsync(string filePath, string? password = null)
    {
        return Task.Run<IPdfDocument>(() =>
        {
            var file = new Java.IO.File(filePath);
            if (!file.Exists())
                throw new PdfOpenException(PdfOpenFailure.Unreadable, $"File not found: {filePath}");

            ParcelFileDescriptor? descriptor = null;
            try
            {
                descriptor = ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly)
                    ?? throw new PdfOpenException(PdfOpenFailure.Unreadable, $"Could not open a descriptor for {filePath}");

                var renderer = CreateRenderer(descriptor, password);
                return new AndroidPdfDocument(renderer, descriptor);
            }
            catch (Java.Lang.SecurityException ex)
            {
                // PdfRenderer reports both "needs a password" and "that password is wrong" as a
                // SecurityException.
                descriptor?.Dispose();
                throw PdfOpenException.Protected(password, ex);
            }
            catch (Java.IO.IOException ex)
            {
                descriptor?.Dispose();
                throw new PdfOpenException(PdfOpenFailure.InvalidDocument, "The document is not a valid PDF.", ex);
            }
            catch (PdfOpenException)
            {
                descriptor?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                descriptor?.Dispose();
                throw new PdfOpenException(PdfOpenFailure.Unreadable, ex.Message, ex);
            }
        });
    }

    private static PdfRenderer CreateRenderer(ParcelFileDescriptor descriptor, string? password)
    {
        if (password is null)
            return new PdfRenderer(descriptor);

        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            throw new PdfOpenException(
                PdfOpenFailure.PasswordUnsupported,
                "Opening a protected document needs Android 15 or later.");
        }

        return CreateRendererWithPassword(descriptor, password);
    }

    [SupportedOSPlatform("android35.0")]
    private static PdfRenderer CreateRendererWithPassword(ParcelFileDescriptor descriptor, string password)
    {
        using var loadParams = new LoadParams.Builder()
            .SetPassword(password)!
            .Build();

        return new PdfRenderer(descriptor, loadParams);
    }

    /// <summary>The native part only: the shared rules live in <see cref="PdfDocumentBase"/>.</summary>
    private sealed class AndroidPdfDocument(PdfRenderer renderer, ParcelFileDescriptor descriptor)
        : PdfDocumentBase(renderer.PageCount)
    {
        public override bool SupportsTextSearch => OperatingSystem.IsAndroidVersionAtLeast(35);

        protected override (double Width, double Height) MeasurePage(int pageIndex)
        {
            var page = OpenPage(pageIndex);
            try
            {
                return (page.Width, page.Height);
            }
            finally
            {
                ClosePage(page);
            }
        }

        protected override async Task<byte[]> RenderPngAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch> highlights)
        {
            PdfRenderer.Page? page = OpenPage(pageIndex);
            Bitmap? bitmap = null;
            try
            {
                var (width, height) = PdfPageMath.ScalePage(page.Width, page.Height, targetWidthPixels);

                bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)
                    ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} bitmap.");

                // PDF pages are transparent where there is no ink; paint the paper white first.
                bitmap.EraseColor(AndroidColor.White.ToArgb());
                page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);

                // The page must be closed before anything else touches the renderer.
                ClosePage(page);
                page = null;

                if (highlights.Count > 0)
                    DrawHighlights(bitmap, highlights);

                using var stream = new MemoryStream();
                await bitmap.CompressAsync(Bitmap.CompressFormat.Png!, 100, stream).ConfigureAwait(false);
                return stream.ToArray();
            }
            finally
            {
                if (page is not null)
                    ClosePage(page);

                if (bitmap is not null)
                {
                    bitmap.Recycle();
                    bitmap.Dispose();
                }
            }
        }

        protected override IEnumerable<PdfTextMatch> SearchPage(int pageIndex, string query)
        {
            // Repeated here so that the platform analyser can see that SearchText is only reached on API 35.
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
                return [];

            var page = OpenPage(pageIndex);
            try
            {
                return CollectMatches(page, pageIndex, query);
            }
            finally
            {
                ClosePage(page);
            }
        }

        // One hit spans several rectangles when it wraps across lines. The union of them is what
        // the reader highlights, so a wrapped match stays a single result to step through.
        [SupportedOSPlatform("android35.0")]
        private static List<PdfTextMatch> CollectMatches(PdfRenderer.Page page, int pageIndex, string query) =>
            (page.SearchText(query) ?? [])
                .Where(match => match.Bounds is { Count: > 0 })
                .Select(match => PdfPageMath.NormalizeMatch(pageIndex,
                    match.Bounds!.Select(r => (r.Left, r.Top, r.Right, r.Bottom)), page.Width, page.Height))
                .OfType<PdfTextMatch>()
                .ToList();

        private PdfRenderer.Page OpenPage(int pageIndex) =>
            renderer.OpenPage(pageIndex)
                ?? throw new InvalidOperationException($"Page {pageIndex} could not be opened.");

        /// <summary>
        /// Closes a page explicitly. Disposing the managed wrapper does not close the native page,
        /// and PdfRenderer refuses to open another one until the current page is closed.
        /// </summary>
        private static void ClosePage(PdfRenderer.Page page)
        {
            page.Close();
            page.Dispose();
        }

        /// <summary>Paints a translucent marker over each match.</summary>
        private static void DrawHighlights(Bitmap bitmap, IReadOnlyList<PdfTextMatch> highlights)
        {
            using var canvas = new Canvas(bitmap);
            using var paint = new AndroidPaint { AntiAlias = true };
            paint.SetARGB(90, 255, 193, 7); // amber, translucent enough to read the text underneath

            foreach (var match in highlights)
            {
                var (left, top, right, bottom) = ToPixels(match, bitmap.Width, bitmap.Height);
                canvas.DrawRect(left, top, right, bottom, paint);
            }
        }

        protected override void ReleaseNative()
        {
            renderer.Close();
            renderer.Dispose();
            descriptor.Close();
            descriptor.Dispose();
        }
    }
}
