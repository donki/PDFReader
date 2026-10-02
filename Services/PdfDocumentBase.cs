namespace PDFReader.Services;

/// <summary>
/// What every platform document shares, so the Android and Windows renderers only do the native
/// part (measure, rasterize and search one page): pages are accessed one at a time (PdfRenderer
/// allows a single open page), indexes are checked, a disposed document refuses work, a search
/// stops at <see cref="MaxMatches"/> and highlights only reach the page they belong to.
/// Kept apart from the platform code so it can be tested without a device (constitucion, General 8.6).
/// </summary>
public abstract class PdfDocumentBase : IPdfDocument
{
    /// <summary>
    /// A search that matched tens of thousands of times would stall the reader for no benefit:
    /// nobody steps through that many hits, and every page has to be opened to find them.
    /// </summary>
    public const int MaxMatches = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    protected PdfDocumentBase(int pageCount) => PageCount = pageCount;

    public int PageCount { get; }

    public abstract bool SupportsTextSearch { get; }

    /// <summary>Size of a page in its own units (points on both platforms).</summary>
    protected abstract (double Width, double Height) MeasurePage(int pageIndex);

    /// <summary>Rasterizes a page as PNG about <paramref name="targetWidthPixels"/> wide, painting the highlights given.</summary>
    protected abstract Task<byte[]> RenderPngAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch> highlights);

    /// <summary>Matches of <paramref name="query"/> on one page, normalised to the page (0..1).</summary>
    protected virtual IEnumerable<PdfTextMatch> SearchPage(int pageIndex, string query) => [];

    /// <summary>Closes the native renderer and its file.</summary>
    protected abstract void ReleaseNative();

    public async Task<double> GetPageAspectRatioAsync(int pageIndex)
    {
        ValidatePageIndex(pageIndex);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (width, height) = MeasurePage(pageIndex);
            return PdfPageMath.AspectRatio(width, height);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> RenderPageAsync(int pageIndex, int targetWidthPixels, IReadOnlyList<PdfTextMatch>? highlights = null)
    {
        ValidatePageIndex(pageIndex);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var onThisPage = highlights?.Where(match => match.PageIndex == pageIndex).ToList() ?? [];
            return await RenderPngAsync(pageIndex, targetWidthPixels, onThisPage).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PdfTextMatch>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !SupportsTextSearch)
            return [];

        var matches = new List<PdfTextMatch>();
        for (var pageIndex = 0; pageIndex < PageCount && matches.Count < MaxMatches; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                matches.AddRange(SearchPage(pageIndex, query).Take(MaxMatches - matches.Count));
            }
            finally
            {
                _gate.Release();
            }
        }

        return matches;
    }

    /// <summary>A highlight in bitmap pixels (left, top, right, bottom).</summary>
    public static (float Left, float Top, float Right, float Bottom) ToPixels(PdfTextMatch match, int bitmapWidth, int bitmapHeight) =>
        ((float)(match.Left * bitmapWidth), (float)(match.Top * bitmapHeight),
         (float)(match.Right * bitmapWidth), (float)(match.Bottom * bitmapHeight));

    private void ValidatePageIndex(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The document has {PageCount} pages.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _gate.Wait();
        try
        {
            ReleaseNative();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
