using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFReader.Services;

/// <summary>
/// Document tools on top of PDFsharp (MIT). Every operation opens the input in import mode, builds
/// a new document and saves it to the output path: the original is never touched, which is also
/// what makes «undo» trivial (delete the copy).
/// </summary>
/// <remarks>
/// PDFsharp reports a missing or wrong password as a <see cref="PdfReaderException"/> with no
/// distinguishing code; it is mapped to <see cref="PdfOpenException"/> so the pages can reuse the
/// same password prompt they already have for the renderer.
/// </remarks>
public sealed class PdfToolsService(ILogger<PdfToolsService> logger) : IPdfToolsService
{
    private const double A4WidthPoints = 595.276;
    private const double A4HeightPoints = 841.890;
    private const double ImageDpi = 96;

    public Task MergeAsync(IReadOnlyList<PdfInput> inputs, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var output = new PdfDocument();
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = Open(inputs[i], PdfDocumentOpenMode.Import);
                foreach (var page in source.Pages)
                    output.AddPage(page);

                progress?.Report((i + 1) / (double)inputs.Count);
            }

            Save(output, outputPath);
        }, cancellationToken);
    }

    public Task RearrangeAsync(PdfInput input, IReadOnlyList<PageEdit> pages, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var source = Open(input, PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            foreach (var edit in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edit.PageIndex < 0 || edit.PageIndex >= source.PageCount)
                    continue;

                var page = output.AddPage(source.Pages[edit.PageIndex]);
                if (edit.RotationDegrees != 0)
                    page.Rotate = ((page.Rotate + edit.RotationDegrees) % 360 + 360) % 360;
            }

            if (output.PageCount == 0)
                throw new InvalidOperationException("No pages selected.");

            Save(output, outputPath);
        }, cancellationToken);
    }

    public Task<int> GetPageCountAsync(PdfInput input) => Task.Run(() =>
    {
        using var source = Open(input, PdfDocumentOpenMode.Import);
        return source.PageCount;
    });

    public Task ImagesToPdfAsync(IReadOnlyList<string> imagePaths, ImagePageFit fit, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var output = new PdfDocument();
            for (var i = 0; i < imagePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = XImage.FromFile(imagePaths[i]);

                // Pixel size at 96 dpi, the usual screen assumption; a 1920x1080 photo makes a
                // 20x11 inch page, which is what every other converter does too.
                var imageWidth = image.PixelWidth * 72.0 / ImageDpi;
                var imageHeight = image.PixelHeight * 72.0 / ImageDpi;

                var page = output.AddPage();
                double pageWidth, pageHeight;
                if (fit == ImagePageFit.A4)
                {
                    var landscape = imageWidth > imageHeight;
                    pageWidth = landscape ? A4HeightPoints : A4WidthPoints;
                    pageHeight = landscape ? A4WidthPoints : A4HeightPoints;
                }
                else
                {
                    pageWidth = imageWidth;
                    pageHeight = imageHeight;
                }

                page.Width = XUnit.FromPoint(pageWidth);
                page.Height = XUnit.FromPoint(pageHeight);

                // Fit inside the page keeping the aspect ratio, centred.
                var margin = fit == ImagePageFit.A4 ? 28.35 : 0; // 1 cm
                var scale = Math.Min((pageWidth - 2 * margin) / imageWidth, (pageHeight - 2 * margin) / imageHeight);
                var drawWidth = imageWidth * scale;
                var drawHeight = imageHeight * scale;

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, (pageWidth - drawWidth) / 2, (pageHeight - drawHeight) / 2, drawWidth, drawHeight);

                progress?.Report((i + 1) / (double)imagePaths.Count);
            }

            Save(output, outputPath);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PdfToImagesAsync(IPdfDocument document, string outputFolder, string baseName, int widthPixels, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        var digits = Math.Max(2, document.PageCount.ToString().Length);
        var paths = new List<string>(document.PageCount);

        for (var i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var png = await document.RenderPageAsync(i, widthPixels);
            var path = Path.Combine(outputFolder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}.png");
            await File.WriteAllBytesAsync(path, png, cancellationToken);
            paths.Add(path);
            progress?.Report((i + 1) / (double)document.PageCount);
        }

        return paths;
    }

    public Task ProtectAsync(PdfInput input, string userPassword, string? ownerPassword, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var source = Open(input, PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            foreach (var page in source.Pages)
                output.AddPage(page);

            var security = output.SecuritySettings;
            security.UserPassword = userPassword;
            security.OwnerPassword = string.IsNullOrEmpty(ownerPassword) ? userPassword : ownerPassword;
            Save(output, outputPath);
        }, cancellationToken);
    }

    public Task UnprotectAsync(PdfInput input, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            // Importing the pages into a fresh document drops the encryption dictionary with it.
            using var source = Open(input, PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            foreach (var page in source.Pages)
                output.AddPage(page);

            Save(output, outputPath);
        }, cancellationToken);
    }

    public async Task AddPageNumbersAsync(PdfInput input, NumberPosition position, string outputPath, CancellationToken cancellationToken = default)
    {
        await PdfFontResolver.EnsureInstalledAsync();
        await Task.Run(() =>
        {
            using var document = Open(input, PdfDocumentOpenMode.Modify);
            var font = new XFont(PdfFontResolver.FamilyName, 10);
            var total = document.PageCount;

            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var width = gfx.PageSize.Width;
                var height = gfx.PageSize.Height;
                const double margin = 24;

                var (rect, format) = position switch
                {
                    NumberPosition.BottomRight => (new XRect(width - 120 - margin, height - margin - 14, 120, 14), XStringFormats.CenterRight),
                    NumberPosition.TopRight => (new XRect(width - 120 - margin, margin, 120, 14), XStringFormats.CenterRight),
                    _ => (new XRect(0, height - margin - 14, width, 14), XStringFormats.Center),
                };

                gfx.DrawString($"{i + 1} / {total}", font, XBrushes.DimGray, rect, format);
            }

            Save(document, outputPath);
        }, cancellationToken);
    }

    public async Task WatermarkAsync(PdfInput input, string text, double opacity, string outputPath, CancellationToken cancellationToken = default)
    {
        await PdfFontResolver.EnsureInstalledAsync();
        await Task.Run(() =>
        {
            using var document = Open(input, PdfDocumentOpenMode.Modify);
            var alpha = (int)Math.Clamp(opacity * 255, 0, 255);
            var brush = new XSolidBrush(XColor.FromArgb(alpha, 120, 120, 120));

            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var width = gfx.PageSize.Width;
                var height = gfx.PageSize.Height;

                // Size the text to the diagonal so a short word and a long sentence both span the page.
                var diagonal = Math.Sqrt(width * width + height * height);
                var fontSize = 60.0;
                var font = new XFont(PdfFontResolver.FamilyName, fontSize, XFontStyleEx.Bold);
                var measured = gfx.MeasureString(text, font).Width;
                if (measured > 0)
                    fontSize = Math.Clamp(fontSize * (diagonal * 0.7) / measured, 12, 200);
                font = new XFont(PdfFontResolver.FamilyName, fontSize, XFontStyleEx.Bold);

                gfx.TranslateTransform(width / 2, height / 2);
                gfx.RotateTransform(-Math.Atan2(height, width) * 180 / Math.PI);
                gfx.DrawString(text, font, brush, new XPoint(0, 0), XStringFormats.Center);
            }

            Save(document, outputPath);
        }, cancellationToken);
    }

    public async Task FlattenAnnotationsAsync(PdfInput input, IReadOnlyList<Models.Annotation> annotations, string outputPath, CancellationToken cancellationToken = default)
    {
        await PdfFontResolver.EnsureInstalledAsync();
        await Task.Run(() =>
        {
            using var document = Open(input, PdfDocumentOpenMode.Modify);
            foreach (var group in annotations.GroupBy(a => a.PageIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Key < 0 || group.Key >= document.PageCount)
                    continue;

                var page = document.Pages[group.Key];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                // XGraphics draws in the page's unrotated space, but the annotations were made
                // on the page as displayed (/Rotate applied). A rotation of the graphics maps the
                // displayed coordinates back onto the stored page.
                var w0 = gfx.PageSize.Width;
                var h0 = gfx.PageSize.Height;
                var rotate = ((page.Rotate % 360) + 360) % 360;
                double w = w0, h = h0;
                switch (rotate)
                {
                    case 90:
                        (w, h) = (h0, w0);
                        gfx.TranslateTransform(0, w);
                        gfx.RotateTransform(-90);
                        break;
                    case 180:
                        gfx.TranslateTransform(w, h);
                        gfx.RotateTransform(180);
                        break;
                    case 270:
                        (w, h) = (h0, w0);
                        gfx.TranslateTransform(h, 0);
                        gfx.RotateTransform(90);
                        break;
                }

                foreach (var annotation in group)
                    AnnotationPainter.Paint(gfx, annotation, w, h);
            }

            Save(document, outputPath);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------------

    private PdfDocument Open(PdfInput input, PdfDocumentOpenMode mode)
    {
        try
        {
            return input.Password is null
                ? PdfReader.Open(input.Path, mode)
                : PdfReader.Open(input.Path, input.Password, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            var failure = input.Password is null ? PdfOpenFailure.PasswordProtected : PdfOpenFailure.WrongPassword;
            throw new PdfOpenException(failure, "The document is password protected.", ex);
        }
        catch (PdfReaderException ex)
        {
            logger.LogWarning(ex, "PDFsharp could not open {Path}.", input.Path);
            throw new PdfOpenException(PdfOpenFailure.InvalidDocument, "The document is not a valid PDF.", ex);
        }
    }

    private static void Save(PdfDocument document, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        document.Save(outputPath);
    }
}
