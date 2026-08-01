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
                // SecurityException; only the caller knows which of the two happened.
                descriptor?.Dispose();
                var failure = password is null ? PdfOpenFailure.PasswordProtected : PdfOpenFailure.WrongPassword;
                throw new PdfOpenException(failure, "The document is password protected.", ex);
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

    private sealed class AndroidPdfDocument : IPdfDocument
    {
        // PdfRenderer allows a single open page at a time, so every access is serialized.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly PdfRenderer _renderer;
        private readonly ParcelFileDescriptor _descriptor;
        private bool _disposed;

        // Guards against OutOfMemory on very large pages: an ARGB_8888 bitmap costs 4 bytes per pixel.
        private const int MinWidthPixels = 200;
        private const int MaxWidthPixels = 3000;
        private const long MaxPixels = 12_000_000;

        // A search that matched tens of thousands of times would stall the reader for no benefit:
        // nobody steps through that many hits, and every page has to be opened to find them.
        private const int MaxMatches = 500;

        public int PageCount { get; }

        public bool SupportsTextSearch => OperatingSystem.IsAndroidVersionAtLeast(35);

        public AndroidPdfDocument(PdfRenderer renderer, ParcelFileDescriptor descriptor)
        {
            _renderer = renderer;
            _descriptor = descriptor;
            PageCount = renderer.PageCount;
        }

        public async Task<double> GetPageAspectRatioAsync(int pageIndex)
        {
            ValidatePageIndex(pageIndex);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var page = OpenPage(pageIndex);
                try
                {
                    return page.Width > 0 ? (double)page.Height / page.Width : 1.414; // A4 portrait
                }
                finally
                {
                    ClosePage(page);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<byte[]> RenderPageAsync(
            int pageIndex,
            int targetWidthPixels,
            IReadOnlyList<PdfTextMatch>? highlights = null)
        {
            ValidatePageIndex(pageIndex);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var page = OpenPage(pageIndex);
                Bitmap? bitmap = null;
                try
                {
                    var (width, height) = ScalePage(page.Width, page.Height, targetWidthPixels);

                    bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)
                        ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} bitmap.");

                    // PDF pages are transparent where there is no ink; paint the paper white first.
                    bitmap.EraseColor(AndroidColor.White.ToArgb());
                    page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);

                    // The page must be closed before anything else touches the renderer.
                    ClosePage(page);
                    page = null;

                    if (highlights is { Count: > 0 })
                        DrawHighlights(bitmap, highlights, pageIndex);

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
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<PdfTextMatch>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            // The version check is repeated here rather than read from SupportsTextSearch so that the
            // platform analyser can see that SearchText is only reached on API 35 and later.
            if (string.IsNullOrWhiteSpace(query) || !OperatingSystem.IsAndroidVersionAtLeast(35))
                return [];

            var matches = new List<PdfTextMatch>();

            for (var pageIndex = 0; pageIndex < PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    var page = OpenPage(pageIndex);
                    try
                    {
                        CollectMatches(page, pageIndex, query, matches);
                    }
                    finally
                    {
                        ClosePage(page);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (matches.Count >= MaxMatches)
                    break;
            }

            return matches;
        }

        [SupportedOSPlatform("android35.0")]
        private static void CollectMatches(
            PdfRenderer.Page page,
            int pageIndex,
            string query,
            List<PdfTextMatch> matches)
        {
            var found = page.SearchText(query);
            if (found is null || found.Count == 0)
                return;

            double pageWidth = page.Width;
            double pageHeight = page.Height;
            if (pageWidth <= 0 || pageHeight <= 0)
                return;

            foreach (var match in found)
            {
                // One hit spans several rectangles when it wraps across lines. The union of them is
                // what the reader highlights, so a wrapped match stays a single result to step through.
                var bounds = match.Bounds;
                if (bounds is null || bounds.Count == 0)
                    continue;

                float left = float.MaxValue, top = float.MaxValue;
                float right = float.MinValue, bottom = float.MinValue;

                foreach (var rect in bounds)
                {
                    left = Math.Min(left, rect.Left);
                    top = Math.Min(top, rect.Top);
                    right = Math.Max(right, rect.Right);
                    bottom = Math.Max(bottom, rect.Bottom);
                }

                matches.Add(new PdfTextMatch(
                    pageIndex,
                    left / pageWidth,
                    top / pageHeight,
                    right / pageWidth,
                    bottom / pageHeight));

                if (matches.Count >= MaxMatches)
                    return;
            }
        }

        private PdfRenderer.Page OpenPage(int pageIndex) =>
            _renderer.OpenPage(pageIndex)
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

        /// <summary>Paints a translucent marker over each match that falls on this page.</summary>
        private static void DrawHighlights(Bitmap bitmap, IReadOnlyList<PdfTextMatch> highlights, int pageIndex)
        {
            using var canvas = new Canvas(bitmap);
            using var paint = new AndroidPaint { AntiAlias = true };
            paint.SetARGB(90, 255, 193, 7); // amber, translucent enough to read the text underneath

            foreach (var match in highlights)
            {
                if (match.PageIndex != pageIndex)
                    continue;

                canvas.DrawRect(
                    (float)(match.Left * bitmap.Width),
                    (float)(match.Top * bitmap.Height),
                    (float)(match.Right * bitmap.Width),
                    (float)(match.Bottom * bitmap.Height),
                    paint);
            }
        }

        private static (int Width, int Height) ScalePage(int pageWidth, int pageHeight, int targetWidthPixels)
        {
            var width = Math.Clamp(targetWidthPixels, MinWidthPixels, MaxWidthPixels);
            var aspect = pageWidth > 0 ? (double)pageHeight / pageWidth : 1.414;
            var height = Math.Max(1, (int)Math.Round(width * aspect));

            if ((long)width * height > MaxPixels)
            {
                var factor = Math.Sqrt((double)MaxPixels / ((long)width * height));
                width = Math.Max(MinWidthPixels, (int)(width * factor));
                height = Math.Max(1, (int)(height * factor));
            }

            return (width, height);
        }

        private void ValidatePageIndex(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The document has {PageCount} pages.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _gate.Wait();
            try
            {
                _renderer.Close();
                _renderer.Dispose();
                _descriptor.Close();
                _descriptor.Dispose();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
