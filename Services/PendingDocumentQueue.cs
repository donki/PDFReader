using System.Collections.Concurrent;

namespace PDFReader.Services;

/// <summary>A PDF handed to the app by another app, already copied to a temporary file.</summary>
/// <param name="TemporaryFilePath">Cache file holding the content; the consumer deletes it after importing.</param>
/// <param name="DisplayName">File name to show in the library.</param>
public record PendingDocument(string TemporaryFilePath, string DisplayName);

/// <summary>
/// Holds documents opened from outside the app (share sheet, file manager) until the
/// library page is ready to import them, since the intent can arrive before the UI exists.
/// </summary>
public class PendingDocumentQueue
{
    private readonly ConcurrentQueue<PendingDocument> _queue = new();

    /// <summary>Raised when a document is queued, so a page already on screen can pick it up.</summary>
    public event EventHandler? DocumentQueued;

    public void Enqueue(PendingDocument document)
    {
        _queue.Enqueue(document);
        DocumentQueued?.Invoke(this, EventArgs.Empty);
    }

    public bool TryDequeue(out PendingDocument? document) => _queue.TryDequeue(out document);
}
