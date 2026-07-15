namespace PDFReader.Models;

/// <summary>
/// A library entry with its texts already localized and formatted, so the list can bind
/// straight to it without converters.
/// </summary>
/// <param name="Entry">The document this row represents.</param>
/// <param name="DisplayName">File name.</param>
/// <param name="Details">Page count and size, for example "12 pág. · 2,4 MB".</param>
/// <param name="LastOpened">When the document was last opened, in words.</param>
public record DocumentListItem(PdfDocumentEntry Entry, string DisplayName, string Details, string LastOpened);
