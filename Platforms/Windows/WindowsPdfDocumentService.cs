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
        // ERROR_LOGON_FAILURE ("wrong password") and E_ACCESSDENIED (also used for encrypted PDFs).
        catch (Exception ex) when ((uint)ex.HResult is 0x8007052E or 0x80070005)
        {
            throw PdfOpenException.Protected(password, ex);
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

    /// <summary>The native part only: the shared rules live in <see cref="PdfDocumentBase"/>.</summary>
    private sealed class WindowsPdfDocument(PdfDocument document) : PdfDocumentBase((int)document.PageCount)
    {
        public override bool SupportsTextSearch => false;

        protected override (double Width, double Height) MeasurePage(int pageIndex)
        {
            using var page = document.GetPage((uint)pageIndex);
            return (page.Size.Width, page.Size.Height);
        }

        protected override async Task<byte[]> RenderPngAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch> highlights)
        {
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

        protected override void ReleaseNative() { }
    }
}
