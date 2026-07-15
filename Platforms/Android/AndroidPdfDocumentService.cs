using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using PDFReader.Services;
using AndroidColor = Android.Graphics.Color;

namespace PDFReader.Platforms.Android;

/// <summary>
/// Renders PDF pages with android.graphics.pdf.PdfRenderer, the renderer built into the
/// platform since API 21. It keeps the app free of third-party PDF dependencies, which is
/// what allows PDF Reader to ship under the MIT license with no further obligations.
/// </summary>
public class AndroidPdfDocumentService : IPdfDocumentService
{
    public Task<IPdfDocument> OpenAsync(string filePath)
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

                var renderer = new PdfRenderer(descriptor);
                return new AndroidPdfDocument(renderer, descriptor);
            }
            catch (Java.Lang.SecurityException ex)
            {
                // PdfRenderer throws SecurityException for password protected documents.
                descriptor?.Dispose();
                throw new PdfOpenException(PdfOpenFailure.PasswordProtected, "The document is password protected.", ex);
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

        public int PageCount { get; }

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

        public async Task<byte[]> RenderPageAsync(int pageIndex, int targetWidthPixels)
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
