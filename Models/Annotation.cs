namespace PDFReader.Models;

/// <summary>
/// Marks the user draws on a page. Every coordinate is normalised to the page (0..1 of its width
/// and height, origin top-left), so the same annotation is valid on screen at any zoom and on the
/// PDF page in points when it gets flattened.
/// </summary>
public abstract class Annotation
{
    public int PageIndex { get; set; }

    /// <summary>Colour as #RRGGBB.</summary>
    public string Color { get; set; } = "#3525CD";

    /// <summary>Bounding box, normalised. Used for hit tests, moving and resizing.</summary>
    public abstract RectF Bounds { get; }

    /// <summary>Moves the annotation by a normalised offset.</summary>
    public abstract void Translate(float dx, float dy);
}

/// <summary>A freehand line: pen or highlighter.</summary>
public sealed class StrokeAnnotation : Annotation
{
    public List<PointF> Points { get; set; } = [];

    /// <summary>Line width as a fraction of the page width.</summary>
    public float Width { get; set; } = 0.004f;

    /// <summary>0..1; a highlighter is translucent, a pen is opaque.</summary>
    public float Alpha { get; set; } = 1f;

    public override RectF Bounds
    {
        get
        {
            if (Points.Count == 0)
                return RectF.Zero;
            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            var pad = Width;
            return new RectF(minX - pad, minY - pad, maxX - minX + 2 * pad, maxY - minY + 2 * pad);
        }
    }

    public override void Translate(float dx, float dy)
    {
        for (var i = 0; i < Points.Count; i++)
            Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
    }
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,

    /// <summary>A filled white box: covers what is under it («tapar»).</summary>
    Whiteout
}

/// <summary>A rectangle, an ellipse or a white cover.</summary>
public sealed class ShapeAnnotation : Annotation
{
    public ShapeKind Kind { get; set; }

    public RectF Rect { get; set; }

    /// <summary>Line width as a fraction of the page width (ignored by the whiteout).</summary>
    public float Width { get; set; } = 0.004f;

    public override RectF Bounds => Rect;

    public override void Translate(float dx, float dy) => Rect = Rect.Offset(dx, dy);
}

/// <summary>A box of text, wrapped inside its rectangle.</summary>
public sealed class TextAnnotation : Annotation
{
    public RectF Rect { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Font size as a fraction of the page height (0.02 ≈ 12 pt on A4).</summary>
    public float FontSize { get; set; } = 0.018f;

    public bool Bold { get; set; }

    /// <summary>One of <see cref="Services.PdfFontResolver.Families"/> by name.</summary>
    public string FontFamily { get; set; } = "OpenSans";

    /// <summary>0 left, 1 centre, 2 right.</summary>
    public int Alignment { get; set; }

    /// <summary>0 horizontal; 90 reads top to bottom; 270 reads bottom to top. The box stays as laid out.</summary>
    public int Rotation { get; set; }

    public override RectF Bounds => Rect;

    public override void Translate(float dx, float dy) => Rect = Rect.Offset(dx, dy);
}

/// <summary>A handwritten signature: strokes in a unit box, placed and scaled into <see cref="Rect"/>.</summary>
public sealed class SignatureAnnotation : Annotation
{
    public RectF Rect { get; set; }

    /// <summary>Strokes with points in 0..1 of the signature's own box.</summary>
    public List<List<PointF>> Strokes { get; set; } = [];

    /// <summary>Aspect ratio (height / width) of the signature as drawn, so scaling keeps its shape.</summary>
    public float AspectRatio { get; set; } = 0.4f;

    public override RectF Bounds => Rect;

    public override void Translate(float dx, float dy) => Rect = Rect.Offset(dx, dy);
}

/// <summary>A signature saved for reuse (Preferences), as strokes in a unit box.</summary>
public sealed class SavedSignature
{
    public List<List<PointF>> Strokes { get; set; } = [];
    public float AspectRatio { get; set; } = 0.4f;
}
