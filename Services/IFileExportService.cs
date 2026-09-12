namespace PDFReader.Services;

/// <summary>
/// Gets a file out of the app's private storage: a «save as» dialog on Windows, the system
/// document creator (Storage Access Framework) on Android. Returns false when the user cancels.
/// </summary>
public interface IFileExportService
{
    /// <param name="sourcePath">File inside the app storage.</param>
    /// <param name="suggestedName">File name proposed in the dialog, extension included.</param>
    /// <param name="mimeType">«application/pdf», «image/png», «application/zip»…</param>
    Task<bool> SaveAsAsync(string sourcePath, string suggestedName, string mimeType);
}
