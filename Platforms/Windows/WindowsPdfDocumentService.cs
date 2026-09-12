using PDFReader.Services;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PDFReader.Platforms.Windows;

/// <summary>
/// Renders PDF pages with <see cref="PdfDocument"/> from <c>Windows.Data.Pdf</c>, the renderer that
/// ships with Windows 10 and later. Like the Android implementation it needs no third-party PDF
/// library, so PDF Reader stays MIT-clean on both platforms.
///
/// The Windows renderer decrypts protected documents but exposes no text, so search degrades the
/// same way it does on Android before API 35.
/// </summary>
public class WindowsPdfDocumentService : IPdfDocumentService
{
    public async Task<IPdfDocument> OpenAsync(string filePath, string? password = null)
    {
        if (!File.Exists(filePath))
            throw new PdfOpenException(PdfOpenFailure.Unreadable, $"File not found: {filePath}");

        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(filePath);
        }
        catch (Exception ex)
        {
            throw new PdfOpenException(PdfOpenFailure.Unreadable, $"Could not open {filePath}", ex);
        }

        try
        {
            var document = password is null
                ? await PdfDocument.LoadFromFileAsync(file)
                : await PdfDocument.LoadFromFileAsync(file, password);
            return new WindowsPdfDocument(document);
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x8007052E) // ERROR_LOGON_FAILURE: "wrong password"
        {
            var failure = password is null ? PdfOpenFailure.PasswordProtected : PdfOpenFailure.WrongPassword;
            throw new PdfOpenException(failure, "The document is password protected.", ex);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005)) // E_ACCESSDENIED: also used for encrypted PDFs
        {
            var failure = password is null ? PdfOpenFailure.PasswordProtected : PdfOpenFailure.WrongPassword;
            throw new PdfOpenException(failure, "The document is password protected.", ex);
        }
        catch (PdfOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfOpenException(PdfOpenFailure.InvalidDocument, "The document is not a valid PDF.", ex);
        }
    }

    private sealed class WindowsPdfDocument(PdfDocument document) : IPdfDocument
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public int PageCount => (int)document.PageCount;

        public bool SupportsTextSearch => false;

        public async Task<double> GetPageAspectRatioAsync(int pageIndex)
        {
            await _gate.WaitAsync();
            try
            {
                ThrowIfDisposed();
                using var page = document.GetPage((uint)pageIndex);
                var size = page.Size;
                return size.Width <= 0 ? 1.4142 : size.Height / size.Width;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<byte[]> RenderPageAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch>? highlights = null)
        {
            await _gate.WaitAsync();
            try
            {
                ThrowIfDisposed();
                using var page = document.GetPage((uint)pageIndex);
                using var stream = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, targetWidthPixels),
                    BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                };
                await page.RenderToStreamAsync(stream, options);

                var bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
                return bytes;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<IReadOnlyList<PdfTextMatch>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PdfTextMatch>>([]);

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            _disposed = true;
            _gate.Dispose();
        }
    }
}
