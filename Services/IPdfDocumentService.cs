namespace PDFReader.Services;

/// <summary>Reason a PDF file could not be opened, so the UI can explain it to the user.</summary>
public enum PdfOpenFailure
{
    /// <summary>The file is not a PDF, or its structure is damaged.</summary>
    InvalidDocument,

    /// <summary>The document is encrypted and no password was supplied.</summary>
    PasswordProtected,

    /// <summary>The document is encrypted and the supplied password did not open it.</summary>
    WrongPassword,

    /// <summary>The document is encrypted and this Android version cannot decrypt it.</summary>
    PasswordUnsupported,

    /// <summary>The file could not be read from storage.</summary>
    Unreadable
}

/// <summary>Thrown by <see cref="IPdfDocumentService.OpenAsync"/> when a document cannot be opened.</summary>
public class PdfOpenException(PdfOpenFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public PdfOpenFailure Failure { get; } = failure;
}

/// <summary>
/// One occurrence of a search term. Bounds are normalised to the page (0..1) instead of pixels,
/// so a match stays valid no matter which zoom level the page is later rendered at.
/// </summary>
public sealed record PdfTextMatch(int PageIndex, double Left, double Top, double Right, double Bottom);

/// <summary>Opens PDF files for rendering. Implemented per platform.</summary>
public interface IPdfDocumentService
{
    /// <summary>Opens the PDF at <paramref name="filePath"/>, decrypting it with
    /// <paramref name="password"/> when the document is protected.</summary>
    /// <exception cref="PdfOpenException">The file is not a readable PDF, or the password is missing or wrong.</exception>
    Task<IPdfDocument> OpenAsync(string filePath, string? password = null);
}

/// <summary>An open PDF document. Rendering is serialized, so a single instance is safe to share.</summary>
public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>
    /// Whether <see cref="SearchAsync"/> can return matches. Text search is a platform feature of
    /// Android 15 and later; below that the app has no way to extract text from a page.
    /// </summary>
    bool SupportsTextSearch { get; }

    /// <summary>Height divided by width for the given page, used to size the view before rendering.</summary>
    Task<double> GetPageAspectRatioAsync(int pageIndex);

    /// <summary>
    /// Renders a page as a PNG image scaled to <paramref name="targetWidthPixels"/> wide, painting
    /// <paramref name="highlights"/> over it. Highlighting during the render keeps the caller free of
    /// any page-to-view coordinate maths.
    /// </summary>
    Task<byte[]> RenderPageAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch>? highlights = null);

    /// <summary>
    /// Finds every occurrence of <paramref name="query"/> across the document, in page order.
    /// Returns an empty list when <see cref="SupportsTextSearch"/> is false.
    /// </summary>
    Task<IReadOnlyList<PdfTextMatch>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
