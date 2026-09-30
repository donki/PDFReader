using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Storage;
using PDFReader.Models;
using PDFReader.Services;

// FileSystem.AppDataDirectory y Preferences son estaticos: las pruebas van en serie.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PDFReader.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly TempFolder _tmp = new();

    public LibraryServiceTests() => FileSystem.AppDataDirectory = _tmp.Path;

    public void Dispose() => _tmp.Dispose();

    private static LibraryService NewService() => new(NullLogger<LibraryService>.Instance);

    private static MemoryStream Pdf(string text = "%PDF-1.7 test") => new(Encoding.ASCII.GetBytes(text));

    private string IndexPath => _tmp.Combine("library.json");

    [Fact]
    public async Task EmptyLibrary_HasNoDocuments_AndWritesNothing()
    {
        Assert.Empty(await NewService().GetDocumentsAsync());
        Assert.False(File.Exists(IndexPath));
    }

    [Fact]
    public async Task Import_StoresTheFile_AndTheEntry()
    {
        var library = NewService();
        var before = DateTime.UtcNow;

        var entry = await library.ImportAsync(Pdf("%PDF-12345"), "manual.pdf");

        Assert.Equal(32, entry.Id.Length);
        Assert.Equal("manual.pdf", entry.DisplayName);
        Assert.Equal(10, entry.SizeBytes);
        Assert.Equal(0, entry.LastPageIndex);
        Assert.InRange(entry.LastOpenedUtc, before, DateTime.UtcNow);

        var path = library.GetFilePath(entry);
        Assert.Equal(_tmp.Combine("documents", entry.Id + ".pdf"), path);
        Assert.Equal("%PDF-12345", File.ReadAllText(path));
        Assert.Single(await library.GetDocumentsAsync());
        Assert.False(File.Exists(IndexPath + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Import_WithoutName_UsesDefaultName(string name) =>
        Assert.Equal("document.pdf", (await NewService().ImportAsync(Pdf(), name)).DisplayName);

    [Fact]
    public async Task Import_PersistsAcrossInstances()
    {
        var entry = await NewService().ImportAsync(Pdf(), "a.pdf");

        var reloaded = await NewService().GetDocumentsAsync();

        var stored = Assert.Single(reloaded);
        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal("a.pdf", stored.DisplayName);
        Assert.Equal(entry.SizeBytes, stored.SizeBytes);
    }

    [Fact]
    public async Task Documents_AreSortedMostRecentlyOpenedFirst()
    {
        var library = NewService();
        var first = await library.ImportAsync(Pdf(), "first.pdf");
        await Task.Delay(20);
        await library.ImportAsync(Pdf(), "second.pdf");
        await Task.Delay(20);

        await library.TouchAsync(first, pageIndex: 7);

        var docs = await library.GetDocumentsAsync();
        Assert.Equal(new[] { "first.pdf", "second.pdf" }, docs.Select(d => d.DisplayName));
        Assert.Equal(7, docs[0].LastPageIndex);
    }

    [Fact]
    public async Task Touch_UpdatesStoredAndCallerEntry_AndPersists()
    {
        var library = NewService();
        var entry = await library.ImportAsync(Pdf(), "a.pdf");
        var caller = new PdfDocumentEntry { Id = entry.Id, DisplayName = "a.pdf" };

        await library.TouchAsync(caller, 3);

        Assert.Equal(3, caller.LastPageIndex);
        Assert.NotEqual(default, caller.LastOpenedUtc);
        var stored = Assert.Single(await NewService().GetDocumentsAsync());
        Assert.Equal(3, stored.LastPageIndex);
        Assert.Equal(caller.LastOpenedUtc, stored.LastOpenedUtc);
    }

    [Fact]
    public async Task SetPageCount_UpdatesStoredAndCallerEntry_AndPersists()
    {
        var library = NewService();
        var entry = await library.ImportAsync(Pdf(), "a.pdf");

        await library.SetPageCountAsync(entry, 42);

        Assert.Equal(42, entry.PageCount);
        Assert.Equal(42, Assert.Single(await NewService().GetDocumentsAsync()).PageCount);
    }

    [Fact]
    public async Task Updates_OnUnknownEntry_DoNothing()
    {
        var library = NewService();
        await library.ImportAsync(Pdf(), "a.pdf");
        var ghost = new PdfDocumentEntry { Id = "ghost", PageCount = 1 };

        await library.SetPageCountAsync(ghost, 99);
        await library.TouchAsync(ghost, 5);

        Assert.Equal(1, ghost.PageCount);
        Assert.Equal(0, ghost.LastPageIndex);
        Assert.Single(await library.GetDocumentsAsync());
    }

    [Fact]
    public async Task Remove_DeletesFileAndEntry()
    {
        var library = NewService();
        var keep = await library.ImportAsync(Pdf(), "keep.pdf");
        var drop = await library.ImportAsync(Pdf(), "drop.pdf");

        await library.RemoveAsync(drop);

        Assert.False(File.Exists(library.GetFilePath(drop)));
        Assert.True(File.Exists(library.GetFilePath(keep)));
        Assert.Equal(new[] { "keep.pdf" }, (await NewService().GetDocumentsAsync()).Select(d => d.DisplayName));
    }

    [Fact]
    public async Task Remove_UnknownEntry_IsHarmless()
    {
        var library = NewService();
        await library.ImportAsync(Pdf(), "a.pdf");

        await library.RemoveAsync(new PdfDocumentEntry { Id = "ghost" });

        Assert.Single(await library.GetDocumentsAsync());
    }

    [Fact]
    public async Task Remove_FileInUse_StillRemovesTheEntry()
    {
        // Windows no deja borrar un fichero abierto: el borrado falla, se registra y la entrada sale igual.
        var library = NewService();
        var entry = await library.ImportAsync(Pdf(), "busy.pdf");

        using (File.Open(library.GetFilePath(entry), FileMode.Open, FileAccess.Read, FileShare.Read))
            await library.RemoveAsync(entry);

        Assert.Empty(await library.GetDocumentsAsync());
    }

    [Fact]
    public async Task CorruptIndex_IsResetInsteadOfCrashing()
    {
        File.WriteAllText(IndexPath, "{ this is not json");

        var library = NewService();

        Assert.Empty(await library.GetDocumentsAsync());
        var entry = await library.ImportAsync(Pdf(), "after.pdf");
        Assert.Equal(entry.Id, Assert.Single(await NewService().GetDocumentsAsync()).Id);
    }

    [Fact]
    public async Task NullIndex_IsTreatedAsEmpty()
    {
        File.WriteAllText(IndexPath, "null");
        Assert.Empty(await NewService().GetDocumentsAsync());
    }

    [Fact]
    public async Task EntriesWhoseFileIsGone_AreDroppedAndTheIndexRewritten()
    {
        var library = NewService();
        var kept = await library.ImportAsync(Pdf(), "kept.pdf");
        var lost = await library.ImportAsync(Pdf(), "lost.pdf");
        File.Delete(library.GetFilePath(lost));

        var docs = await NewService().GetDocumentsAsync();

        Assert.Equal(kept.Id, Assert.Single(docs).Id);
        Assert.DoesNotContain(lost.Id, File.ReadAllText(IndexPath));
    }

    [Fact]
    public async Task Import_StreamFailure_LeavesNoFileAndNoEntry()
    {
        var library = NewService();

        await Assert.ThrowsAsync<IOException>(() => library.ImportAsync(new FailingStream(), "broken.pdf"));

        Assert.Empty(await library.GetDocumentsAsync());
        Assert.Empty(Directory.EnumerateFiles(_tmp.Combine("documents")));
    }

    [Fact]
    public async Task Import_IndexCannotBeSaved_RemovesTheCopiedFile()
    {
        // Una carpeta con el nombre del indice hace fallar el guardado.
        Directory.CreateDirectory(IndexPath);
        var library = NewService();

        await Assert.ThrowsAnyAsync<Exception>(() => library.ImportAsync(Pdf(), "a.pdf"));

        Assert.Empty(Directory.EnumerateFiles(_tmp.Combine("documents")));
    }

    [Fact]
    public async Task ConcurrentImports_AllEndUpInTheIndex()
    {
        var library = NewService();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => library.ImportAsync(Pdf(), $"doc{i}.pdf")));

        Assert.Equal(20, (await NewService().GetDocumentsAsync()).Count);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
