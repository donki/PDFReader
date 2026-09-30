namespace PDFReader.Services;

/// <summary>
/// Page geometry shared by the platform renderers. Pure arithmetic, kept apart from the Android
/// and Windows code so it can be tested without a device (constitucion, General 8.6).
/// </summary>
public static class PdfPageMath
{
    /// <summary>Aspect ratio (height / width) assumed when a page reports no width: A4 portrait.</summary>
    public const double FallbackAspectRatio = 1.414;

    // Guards against OutOfMemory on very large pages: an ARGB_8888 bitmap costs 4 bytes per pixel.
    public const int MinWidthPixels = 200;
    public const int MaxWidthPixels = 3000;
    public const long MaxPixels = 12_000_000;

    /// <summary>Height divided by width, or <see cref="FallbackAspectRatio"/> for a page without width.</summary>
    public static double AspectRatio(double pageWidth, double pageHeight) =>
        pageWidth > 0 ? pageHeight / pageWidth : FallbackAspectRatio;

    /// <summary>
    /// Bitmap size for rendering a <paramref name="pageWidth"/> x <paramref name="pageHeight"/> page
    /// about <paramref name="targetWidthPixels"/> wide, never narrower than
    /// <see cref="MinWidthPixels"/>, wider than <see cref="MaxWidthPixels"/> or bigger than
    /// <see cref="MaxPixels"/> in total.
    /// </summary>
    public static (int Width, int Height) ScalePage(int pageWidth, int pageHeight, int targetWidthPixels)
    {
        var width = Math.Clamp(targetWidthPixels, MinWidthPixels, MaxWidthPixels);
        var aspect = AspectRatio(pageWidth, pageHeight);
        var height = Math.Max(1, (int)Math.Round(width * aspect));

        if ((long)width * height > MaxPixels)
        {
            var factor = Math.Sqrt((double)MaxPixels / ((long)width * height));
            width = Math.Max(MinWidthPixels, (int)(width * factor));
            // With the width held at its minimum a very elongated page (a till receipt, a scroll)
            // would still blow the budget: its height is capped instead.
            height = (int)Math.Clamp((long)(height * factor), 1, MaxPixels / width);
        }

        return (width, height);
    }

    /// <summary>
    /// One search hit as the smallest box around all of its <paramref name="rects"/> (a hit that
    /// wraps onto a second line has two), normalised to the page so it holds at any zoom.
    /// Returns null when there is nothing to box or the page has no size.
    /// </summary>
    public static PdfTextMatch? NormalizeMatch(
        int pageIndex,
        IEnumerable<(float Left, float Top, float Right, float Bottom)> rects,
        double pageWidth,
        double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
            return null;

        float left = float.MaxValue, top = float.MaxValue;
        float right = float.MinValue, bottom = float.MinValue;
        var any = false;

        foreach (var rect in rects)
        {
            any = true;
            left = Math.Min(left, rect.Left);
            top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        if (!any)
            return null;

        return new PdfTextMatch(
            pageIndex,
            left / pageWidth,
            top / pageHeight,
            right / pageWidth,
            bottom / pageHeight);
    }
}
