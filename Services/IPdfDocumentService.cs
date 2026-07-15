namespace PDFReader.Services;

/// <summary>Reason a PDF file could not be opened, so the UI can explain it to the user.</summary>
public enum PdfOpenFailure
{
    /// <summary>The file is not a PDF, or its structure is damaged.</summary>
    InvalidDocument,

    /// <summary>The document is encrypted and needs a password.</summary>
    PasswordProtected,

    /// <summary>The file could not be read from storage.</summary>
    Unreadable
}

/// <summary>Thrown by <see cref="IPdfDocumentService.OpenAsync"/> when a document cannot be opened.</summary>
public class PdfOpenException(PdfOpenFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public PdfOpenFailure Failure { get; } = failure;
}

/// <summary>Opens PDF files for rendering. Implemented per platform.</summary>
public interface IPdfDocumentService
{
    /// <summary>Opens the PDF at <paramref name="filePath"/>.</summary>
    /// <exception cref="PdfOpenException">The file is not a readable, unencrypted PDF.</exception>
    Task<IPdfDocument> OpenAsync(string filePath);
}

/// <summary>An open PDF document. Rendering is serialized, so a single instance is safe to share.</summary>
public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>Height divided by width for the given page, used to size the view before rendering.</summary>
    Task<double> GetPageAspectRatioAsync(int pageIndex);

    /// <summary>Renders a page as a PNG image scaled to <paramref name="targetWidthPixels"/> wide.</summary>
    Task<byte[]> RenderPageAsync(int pageIndex, int targetWidthPixels);
}
