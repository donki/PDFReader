using PDFReader.Services;

namespace PDFReader.Tests;

/// <summary>Las reglas comunes de los documentos (lo que antes estaba repetido en Android y Windows).</summary>
public sealed class PdfDocumentBaseTests
{
    [Fact]
    public async Task AspectRatio_ComesFromThePageSize_WithA4WhenItHasNoWidth()
    {
        var doc = new FakePdfDocument(2) { Size = page => page == 0 ? (100, 200) : (0, 50) };

        Assert.Equal(2.0, await doc.GetPageAspectRatioAsync(0));
        Assert.Equal(PdfPageMath.FallbackAspectRatio, await doc.GetPageAspectRatioAsync(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task PagesOutsideTheDocument_AreRejected(int page)
    {
        var doc = new FakePdfDocument(3);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.GetPageAspectRatioAsync(page));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.RenderPageAsync(page, 500));
    }

    [Fact]
    public async Task Render_PassesOnlyTheHighlightsOfThatPage()
    {
        var doc = new FakePdfDocument(3);
        PdfTextMatch[] matches = [new(0, 0, 0, 1, 1), new(1, 0.1, 0.1, 0.2, 0.2), new(1, 0.3, 0.3, 0.4, 0.4)];

        await doc.RenderPageAsync(1, 800, matches);
        await doc.RenderPageAsync(2, 800);

        Assert.Equal(2, doc.Rendered[0].Highlights.Count);
        Assert.All(doc.Rendered[0].Highlights, m => Assert.Equal(1, m.PageIndex));
        Assert.Empty(doc.Rendered[1].Highlights);
        Assert.Equal(800, doc.Rendered[0].Width);
    }

    [Fact]
    public async Task Search_GoesThroughEveryPage_InOrder()
    {
        var doc = new FakePdfDocument(3) { Matches = (page, query) => page == 1 ? [] : [new PdfTextMatch(page, 0, 0, 1, 1)] };

        var found = await doc.SearchAsync("hola");

        Assert.Equal(new[] { 0, 2 }, found.Select(m => m.PageIndex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSearch_FindsNothing(string query)
    {
        var asked = 0;
        var doc = new FakePdfDocument(2) { Matches = (_, _) => { asked++; return []; } };

        Assert.Empty(await doc.SearchAsync(query));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task WithoutTextSearch_FindsNothing()
    {
        var doc = new FakePdfDocument(2, supportsSearch: false) { Matches = (p, _) => [new PdfTextMatch(p, 0, 0, 1, 1)] };

        Assert.Empty(await doc.SearchAsync("x"));
    }

    [Fact]
    public async Task Search_StopsAtTheMaximum()
    {
        var pagesSearched = 0;
        var doc = new FakePdfDocument(10)
        {
            Matches = (page, _) => { pagesSearched++; return Enumerable.Range(0, 300).Select(_ => new PdfTextMatch(page, 0, 0, 1, 1)); }
        };

        var found = await doc.SearchAsync("x");

        Assert.Equal(PdfDocumentBase.MaxMatches, found.Count);
        Assert.Equal(2, pagesSearched);
    }

    [Fact]
    public async Task Search_CanBeCancelled()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FakePdfDocument(2).SearchAsync("x", cancel.Token));
    }

    [Fact]
    public async Task Disposed_RefusesWork_AndReleasesOnce()
    {
        var doc = new FakePdfDocument(2);

        doc.Dispose();
        doc.Dispose();

        Assert.Equal(1, doc.Released);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.GetPageAspectRatioAsync(0));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.RenderPageAsync(0, 100));
        await Assert.ThrowsAnyAsync<Exception>(() => doc.SearchAsync("x"));
    }

    [Fact]
    public void ToPixels_ScalesTheNormalisedBox()
    {
        var (left, top, right, bottom) = PdfDocumentBase.ToPixels(new PdfTextMatch(0, 0.1, 0.25, 0.5, 1), 1000, 2000);

        Assert.Equal((100f, 500f, 500f, 2000f), (left, top, right, bottom));
    }

    [Theory]
    [InlineData(null, PdfOpenFailure.PasswordProtected)]
    [InlineData("mala", PdfOpenFailure.WrongPassword)]
    public void Protected_TellsMissingFromWrongPassword(string? password, PdfOpenFailure expected)
    {
        var inner = new InvalidOperationException();

        var ex = PdfOpenException.Protected(password, inner);

        Assert.Equal(expected, ex.Failure);
        Assert.Same(inner, ex.InnerException);
    }
}

/// <summary>PDF que llegan de otras apps (Android) o del Explorador (Windows).</summary>
public sealed class IncomingDocumentsTests : IDisposable
{
    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("Factura.pdf", "x/y.pdf", "Factura.pdf")]
    [InlineData(null, "primary:Download/informe.pdf", "informe.pdf")]
    [InlineData(" ", "dir/otro.pdf", "otro.pdf")]
    [InlineData(null, null, "document.pdf")]
    [InlineData("", "  ", "document.pdf")]
    public void DisplayName_ProviderThenUriThenFallback(string? provider, string? segment, string expected) =>
        Assert.Equal(expected, IncomingDocuments.DisplayName(provider, segment));

    [Fact]
    public void CommandLine_KeepsOnlyExistingPdfs_AndSkipsTheProgram()
    {
        var pdf = _tmp.File("a.PDF", "%PDF");
        var txt = _tmp.File("b.txt", "x");

        var found = IncomingDocuments.PdfArguments([pdf, pdf, txt, _tmp.Combine("no.pdf"), "--flag"]);

        Assert.Equal(new[] { pdf }, found);
    }

    [Fact]
    public void Copy_GoesToTheCache_AndIsQueued()
    {
        var queue = new PendingDocumentQueue();
        var queued = 0;
        queue.DocumentQueued += (_, _) => queued++;
        var folder = IncomingDocuments.CacheFolder(_tmp.Path);

        var copy = IncomingDocuments.CopyAndQueue(() => new MemoryStream("%PDF-1.4"u8.ToArray()), "x.pdf", folder, queue);

        Assert.Equal(_tmp.Combine("incoming"), Path.GetDirectoryName(copy));
        Assert.Equal("%PDF-1.4", File.ReadAllText(copy!));
        Assert.True(queue.TryDequeue(out var pending));
        Assert.Equal(new PendingDocument(copy!, "x.pdf"), pending);
        Assert.Equal(1, queued);
    }

    [Fact]
    public void UnreadableSource_IsReported_AndNothingIsQueued()
    {
        var queue = new PendingDocumentQueue();
        var errors = new List<Exception>();

        Assert.Null(IncomingDocuments.CopyAndQueue(() => null, "x.pdf", _tmp.Path, queue, errors.Add));
        Assert.Null(IncomingDocuments.CopyAndQueue(() => throw new UnauthorizedAccessException(), "x.pdf", _tmp.Path, queue));

        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.False(queue.TryDequeue(out _));
    }
}
