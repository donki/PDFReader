using PdfSharp.Fonts;

namespace PDFReader.Services;

/// <summary>
/// Gives PDFsharp the fonts to write with. On Android there is no system font table PDFsharp
/// can read, so page numbers, watermarks and text boxes would fail; the app ships its fonts in the
/// package anyway (for the UI and the text boxes) and this resolver hands PDFsharp those files.
/// </summary>
public sealed class PdfFontResolver : IFontResolver
{
    /// <summary>Default family; also what everything unknown falls back to.</summary>
    public const string FamilyName = "OpenSans";

    /// <summary>A family the text boxes can use: PDFsharp name, MAUI aliases and the font files.</summary>
    /// <param name="TtfRegular">Family name inside the regular file, as Windows (Win2D) wants it after the «#».</param>
    public sealed record Family(string Name, string MauiRegular, string MauiBold, string FileRegular, string FileBold, string TtfRegular, string TtfBold);

    public static readonly IReadOnlyList<Family> Families =
    [
        new("OpenSans", "OpenSansRegular", "OpenSansSemibold", "OpenSans-Regular.ttf", "OpenSans-Semibold.ttf", "Open Sans", "Open Sans SemiBold"),
        new("Lora", "Lora", "LoraBold", "Lora-Regular.ttf", "Lora-Bold.ttf", "Lora", "Lora"),
        new("RobotoMono", "RobotoMono", "RobotoMonoBold", "RobotoMono-Regular.ttf", "RobotoMono-Bold.ttf", "Roboto Mono", "Roboto Mono"),
        new("Caveat", "Caveat", "CaveatBold", "Caveat-Regular.ttf", "Caveat-Bold.ttf", "Caveat", "Caveat"),
    ];

    /// <summary>
    /// What the drawing canvas needs to find the font. Windows' Win2D cannot load the packaged
    /// files of an unpackaged app (ms-appx URIs throw «element not found» and kill the app), so
    /// the preview there uses an installed look-alike of the same style; the PDF always gets the
    /// real font. Elsewhere the MAUI alias.
    /// </summary>
    public static string CanvasFont(Family family, bool bold)
    {
#if WINDOWS
        return family.Name switch
        {
            "Lora" => "Georgia",
            "RobotoMono" => "Consolas",
            "Caveat" => "Segoe Script",
            _ => "Segoe UI",
        };
#else
        return bold ? family.MauiBold : family.MauiRegular;
#endif
    }

    public static Family Find(string? name) => Families.FirstOrDefault(f => f.Name == name) ?? Families[0];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, byte[]> Faces = new(StringComparer.Ordinal);
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

            foreach (var family in Families)
            {
                Faces[family.FileRegular] = await ReadAssetAsync("fonts/" + family.FileRegular);
                Faces[family.FileBold] = await ReadAssetAsync("fonts/" + family.FileBold);
            }

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
        // An unknown family gets Open Sans: a text box in the wrong font beats an exception.
        var family = Find(familyName);
        return new FontResolverInfo(bold ? family.FileBold : family.FileRegular, false, italic);
    }

    public byte[]? GetFont(string faceName) => Faces.GetValueOrDefault(faceName);
}
