using PdfSharp.Fonts;

namespace PDFReader.Services;

/// <summary>
/// Gives PDFsharp a font to write with. On Android there is no system font table PDFsharp can
/// read, so page numbers, watermarks and notes would fail; the app ships Open Sans anyway for its
/// own UI, and this resolver hands PDFsharp those same files from the app package.
/// </summary>
public sealed class PdfFontResolver : IFontResolver
{
    public const string FamilyName = "OpenSans";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static byte[]? _regular;
    private static byte[]? _bold;
    private static bool _installed;

    /// <summary>Loads the font files once and installs the resolver. Safe to call repeatedly.</summary>
    public static async Task EnsureInstalledAsync()
    {
        if (_installed)
            return;

        await Gate.WaitAsync();
        try
        {
            if (_installed)
                return;

            _regular = await ReadAssetAsync("fonts/OpenSans-Regular.ttf");
            _bold = await ReadAssetAsync("fonts/OpenSans-Semibold.ttf");

            // PDFsharp refuses a second assignment, hence the flag.
            GlobalFontSettings.FontResolver ??= new PdfFontResolver();
            _installed = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<byte[]> ReadAssetAsync(string name)
    {
        await using var stream = await FileSystem.OpenAppPackageFileAsync(name);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // Whatever family is asked for, Open Sans answers: it is the only font on board, and a
        // watermark in Open Sans beats an exception.
        return new FontResolverInfo(bold ? "OpenSans-Semibold" : "OpenSans-Regular", false, italic);
    }

    public byte[]? GetFont(string faceName) =>
        faceName == "OpenSans-Semibold" ? _bold : _regular;
}
