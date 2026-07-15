using PDFReader.Models;

namespace PDFReader.Services;

/// <summary>
/// Keeps the imported documents and their reading state in the app's private storage.
/// </summary>
public interface ILibraryService
{
    /// <summary>Documents in the library, most recently opened first.</summary>
    Task<IReadOnlyList<PdfDocumentEntry>> GetDocumentsAsync();

    /// <summary>Copies <paramref name="content"/> into the library and returns its entry.</summary>
    Task<PdfDocumentEntry> ImportAsync(Stream content, string displayName);

    /// <summary>Absolute path of the stored file for <paramref name="entry"/>.</summary>
    string GetFilePath(PdfDocumentEntry entry);

    /// <summary>Stores the page count discovered when the document was first opened.</summary>
    Task SetPageCountAsync(PdfDocumentEntry entry, int pageCount);

    /// <summary>Marks the document as opened now, at <paramref name="pageIndex"/>.</summary>
    Task TouchAsync(PdfDocumentEntry entry, int pageIndex);

    /// <summary>Deletes the stored copy and removes the entry from the index.</summary>
    Task RemoveAsync(PdfDocumentEntry entry);
}
