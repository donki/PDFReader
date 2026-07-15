namespace PDFReader.Models;

/// <summary>A document stored in the app library, as persisted in the library index.</summary>
public class PdfDocumentEntry
{
    /// <summary>Stable identifier, also the name of the stored file.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>File name shown to the user, taken from the file the user picked.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public int PageCount { get; set; }

    public long SizeBytes { get; set; }

    public DateTime LastOpenedUtc { get; set; }

    /// <summary>Zero based index of the page the user was last reading.</summary>
    public int LastPageIndex { get; set; }
}
