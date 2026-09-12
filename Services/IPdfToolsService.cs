namespace PDFReader.Services;

/// <summary>A PDF file handed to a tool, with the password that opens it when it is protected.</summary>
public sealed record PdfInput(string Path, string? Password = null);

/// <summary>One page of the output, taken from the input at <paramref name="PageIndex"/> and turned by <paramref name="RotationDegrees"/>.</summary>
public sealed record PageEdit(int PageIndex, int RotationDegrees = 0);

/// <summary>Where the page number goes.</summary>
public enum NumberPosition
{
    BottomCenter,
    BottomRight,
    TopRight
}

/// <summary>How an image fills its PDF page.</summary>
public enum ImagePageFit
{
    /// <summary>The page takes the size of the image (at 96 dpi): nothing is cropped or padded.</summary>
    ImageSize,

    /// <summary>A4 portrait or landscape following the image, with the image fitted inside.</summary>
    A4
}

/// <summary>
/// Document tools: everything that writes a new PDF (merge, split, rearrange, images, passwords,
/// stamps). The original file is never modified; every tool writes to a new path.
/// </summary>
public interface IPdfToolsService
{
    /// <summary>Joins <paramref name="inputs"/> in order into one document.</summary>
    Task MergeAsync(IReadOnlyList<PdfInput> inputs, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Writes the pages of <paramref name="input"/> listed in <paramref name="pages"/>, in that order and rotation. Covers extract, reorder, delete and rotate.</summary>
    Task RearrangeAsync(PdfInput input, IReadOnlyList<PageEdit> pages, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Number of pages, opening the file with PDFsharp (so the same password rules apply as the other tools).</summary>
    Task<int> GetPageCountAsync(PdfInput input);

    /// <summary>One page per image, in order. JPEG, PNG and BMP.</summary>
    Task ImagesToPdfAsync(IReadOnlyList<string> imagePaths, ImagePageFit fit, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Renders every page to a PNG in <paramref name="outputFolder"/> and returns the file paths, in page order.</summary>
    Task<IReadOnlyList<string>> PdfToImagesAsync(IPdfDocument document, string outputFolder, string baseName, int widthPixels, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Encrypts with <paramref name="userPassword"/> (needed to open) and <paramref name="ownerPassword"/> (needed to change permissions; same as user when null).</summary>
    Task ProtectAsync(PdfInput input, string userPassword, string? ownerPassword, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Writes an unencrypted copy. The input must carry the password.</summary>
    Task UnprotectAsync(PdfInput input, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Stamps «n / total» on every page.</summary>
    Task AddPageNumbersAsync(PdfInput input, NumberPosition position, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Draws <paramref name="text"/> diagonally across every page, translucent.</summary>
    Task WatermarkAsync(PdfInput input, string text, double opacity, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Paints the annotations onto their pages (flattened: part of the page content, visible in any viewer).</summary>
    Task FlattenAnnotationsAsync(PdfInput input, IReadOnlyList<Models.Annotation> annotations, string outputPath, CancellationToken cancellationToken = default);
}
