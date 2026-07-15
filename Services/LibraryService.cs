using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using PDFReader.Models;

namespace PDFReader.Services;

/// <summary>Source generated serializer, so the index keeps working under trimming (Release builds).</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<PdfDocumentEntry>))]
internal partial class LibraryJsonContext : JsonSerializerContext;

/// <summary>
/// Stores documents under the app's private data directory. Nothing leaves the device
/// and no storage permission is needed, which keeps the app at zero permissions
/// (constitucion, secciones 3 y 16).
/// </summary>
public class LibraryService : ILibraryService
{
    private const string DocumentsFolderName = "documents";
    private const string IndexFileName = "library.json";

    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _documentsFolder;
    private readonly string _indexPath;

    private List<PdfDocumentEntry>? _entries;

    public LibraryService(ILogger<LibraryService> logger)
    {
        _logger = logger;
        _documentsFolder = Path.Combine(FileSystem.AppDataDirectory, DocumentsFolderName);
        _indexPath = Path.Combine(FileSystem.AppDataDirectory, IndexFileName);
    }

    public string GetFilePath(PdfDocumentEntry entry) => Path.Combine(_documentsFolder, $"{entry.Id}.pdf");

    public async Task<IReadOnlyList<PdfDocumentEntry>> GetDocumentsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync().ConfigureAwait(false);
            return entries.OrderByDescending(e => e.LastOpenedUtc).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PdfDocumentEntry> ImportAsync(Stream content, string displayName)
    {
        var entry = new PdfDocumentEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "document.pdf" : displayName,
            LastOpenedUtc = DateTime.UtcNow,
            LastPageIndex = 0
        };

        Directory.CreateDirectory(_documentsFolder);
        var path = GetFilePath(entry);

        try
        {
            await using (var file = File.Create(path))
            {
                await content.CopyToAsync(file).ConfigureAwait(false);
            }

            entry.SizeBytes = new FileInfo(path).Length;
        }
        catch
        {
            // Never leave a half written document behind for the library to trip over later.
            TryDelete(path);
            throw;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync().ConfigureAwait(false);
            entries.Add(entry);
            await SaveAsync(entries).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        return entry;
    }

    public Task SetPageCountAsync(PdfDocumentEntry entry, int pageCount) =>
        UpdateAsync(entry.Id, stored =>
        {
            stored.PageCount = pageCount;
            entry.PageCount = pageCount;
        });

    public Task TouchAsync(PdfDocumentEntry entry, int pageIndex) =>
        UpdateAsync(entry.Id, stored =>
        {
            stored.LastOpenedUtc = DateTime.UtcNow;
            stored.LastPageIndex = pageIndex;
            entry.LastOpenedUtc = stored.LastOpenedUtc;
            entry.LastPageIndex = pageIndex;
        });

    public async Task RemoveAsync(PdfDocumentEntry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync().ConfigureAwait(false);
            entries.RemoveAll(e => e.Id == entry.Id);
            await SaveAsync(entries).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        TryDelete(GetFilePath(entry));
    }

    private async Task UpdateAsync(string id, Action<PdfDocumentEntry> update)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync().ConfigureAwait(false);
            var stored = entries.FirstOrDefault(e => e.Id == id);
            if (stored is null)
                return;

            update(stored);
            await SaveAsync(entries).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the index once and keeps it in memory. Caller must hold <see cref="_gate"/>.</summary>
    private async Task<List<PdfDocumentEntry>> LoadAsync()
    {
        if (_entries is not null)
            return _entries;

        if (!File.Exists(_indexPath))
        {
            _entries = [];
            return _entries;
        }

        try
        {
            await using var stream = File.OpenRead(_indexPath);
            var loaded = await JsonSerializer.DeserializeAsync(stream, LibraryJsonContext.Default.ListPdfDocumentEntry)
                .ConfigureAwait(false);

            _entries = loaded ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt index must not brick the app: start clean and say so in the log
            // rather than failing silently (constitucion, secciones 16 y 17).
            _logger.LogError(ex, "The library index is corrupt and was reset.");
            _entries = [];
        }

        // Drop entries whose file is gone (app data cleared, restore from backup, ...).
        var missing = _entries.RemoveAll(e => !File.Exists(GetFilePath(e)));
        if (missing > 0)
        {
            _logger.LogWarning("Removed {Count} library entries with no file on disk.", missing);
            await SaveAsync(_entries).ConfigureAwait(false);
        }

        return _entries;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private async Task SaveAsync(List<PdfDocumentEntry> entries)
    {
        _entries = entries;

        var temporaryPath = _indexPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, LibraryJsonContext.Default.ListPdfDocumentEntry)
                .ConfigureAwait(false);
        }

        // Write then move, so an interrupted save cannot truncate a good index.
        File.Move(temporaryPath, _indexPath, overwrite: true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
    }
}
