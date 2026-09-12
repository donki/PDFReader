using Microsoft.Maui.Graphics;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PDFReader.Models;

namespace PDFReader.Services;

/// <summary>
/// Draws annotations twice with the same geometry: on the screen (<see cref="ICanvas"/>, while
/// editing) and on the PDF page (<see cref="XGraphics"/>, when saving). Keeping both in one place
/// is what guarantees the saved page looks like the preview.
/// </summary>
public static class AnnotationPainter
{
    // ---------------------------------------------------------------------
    //  Pantalla
    // ---------------------------------------------------------------------

    /// <summary>Draws one annotation on a canvas whose (0,0)-(width,height) is the page.</summary>
    public static void Draw(ICanvas canvas, Annotation annotation, float width, float height, bool selected)
    {
        var color = Color.FromArgb(annotation.Color);
        switch (annotation)
        {
            case StrokeAnnotation stroke:
                DrawStroke(canvas, stroke.Points, color.WithAlpha(stroke.Alpha), stroke.Width * width, width, height, 0, 0, 1, 1);
                break;

            case ShapeAnnotation shape:
            {
                var r = Scale(shape.Rect, width, height);
                if (shape.Kind == ShapeKind.Whiteout)
                {
                    canvas.FillColor = Colors.White;
                    canvas.FillRectangle(r);
                    canvas.StrokeColor = Colors.LightGray;
                    canvas.StrokeSize = 1;
                    canvas.StrokeDashPattern = [4, 4];
                    canvas.DrawRectangle(r);
                    canvas.StrokeDashPattern = null;
                }
                else
                {
                    canvas.StrokeColor = color;
                    canvas.StrokeSize = Math.Max(1, shape.Width * width);
                    if (shape.Kind == ShapeKind.Rectangle)
                        canvas.DrawRectangle(r);
                    else
                        canvas.DrawEllipse(r);
                }

                break;
            }

            case TextAnnotation text:
            {
                var r = Scale(text.Rect, width, height);
                var family = PdfFontResolver.Find(text.FontFamily);
                canvas.FontColor = color;
                canvas.FontSize = text.FontSize * height;
                canvas.Font = new Microsoft.Maui.Graphics.Font(PdfFontResolver.CanvasFont(family, text.Bold));
                var alignment = text.Alignment switch { 1 => HorizontalAlignment.Center, 2 => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };

                if (text.Rotation == 0)
                {
                    canvas.DrawString(text.Text, r.X, r.Y, r.Width, r.Height, alignment, VerticalAlignment.Top, TextFlow.ClipBounds);
                }
                else
                {
                    // Vertical: lay the text out in a box of swapped size and turn it around the centre.
                    canvas.SaveState();
                    canvas.Translate(r.Center.X, r.Center.Y);
                    canvas.Rotate(text.Rotation);
                    canvas.DrawString(text.Text, -r.Height / 2, -r.Width / 2, r.Height, r.Width, alignment, VerticalAlignment.Top, TextFlow.ClipBounds);
                    canvas.RestoreState();
                }

                break;
            }

            case SignatureAnnotation signature:
            {
                var r = Scale(signature.Rect, width, height);
                var pen = Math.Max(1.2f, r.Width * 0.012f);
                foreach (var strokePoints in signature.Strokes)
                    DrawStroke(canvas, strokePoints, color, pen, r.Width, r.Height, r.X, r.Y, 1, 1);
                break;
            }
        }

        if (selected)
        {
            var b = Scale(annotation.Bounds, width, height);
            canvas.StrokeColor = Color.FromArgb("#3525CD");
            canvas.StrokeSize = 1.5f;
            canvas.StrokeDashPattern = [5, 3];
            canvas.DrawRectangle(b);
            canvas.StrokeDashPattern = null;

            // Resize handle, bottom-right.
            canvas.FillColor = Color.FromArgb("#3525CD");
            canvas.FillCircle(b.Right, b.Bottom, 6);
        }
    }

    private static void DrawStroke(ICanvas canvas, List<PointF> points, Color color, float size, float scaleX, float scaleY, float offsetX, float offsetY, float sx, float sy)
    {
        if (points.Count == 0)
            return;

        canvas.StrokeColor = color;
        canvas.StrokeSize = Math.Max(1, size);
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        if (points.Count == 1)
        {
            canvas.FillColor = color;
            canvas.FillCircle(offsetX + points[0].X * scaleX, offsetY + points[0].Y * scaleY, size / 2);
            return;
        }

        var path = new PathF();
        path.MoveTo(offsetX + points[0].X * scaleX, offsetY + points[0].Y * scaleY);
        for (var i = 1; i < points.Count; i++)
            path.LineTo(offsetX + points[i].X * scaleX, offsetY + points[i].Y * scaleY);
        canvas.DrawPath(path);
    }

    private static RectF Scale(RectF r, float width, float height) =>
        new(r.X * width, r.Y * height, r.Width * width, r.Height * height);

    // ---------------------------------------------------------------------
    //  PDF
    // ---------------------------------------------------------------------

    /// <summary>Paints one annotation on a page of <paramref name="w"/> × <paramref name="h"/> points.</summary>
    public static void Paint(XGraphics gfx, Annotation annotation, double w, double h)
    {
        var color = Color.FromArgb(annotation.Color);
        switch (annotation)
        {
            case StrokeAnnotation stroke:
            {
                var xcolor = XColor.FromArgb((int)(stroke.Alpha * 255), (int)(color.Red * 255), (int)(color.Green * 255), (int)(color.Blue * 255));
                PaintStroke(gfx, stroke.Points, xcolor, stroke.Width * w, w, h, 0, 0);
                break;
            }

            case ShapeAnnotation shape:
            {
                var r = new XRect(shape.Rect.X * w, shape.Rect.Y * h, shape.Rect.Width * w, shape.Rect.Height * h);
                if (shape.Kind == ShapeKind.Whiteout)
                {
                    gfx.DrawRectangle(XBrushes.White, r);
                }
                else
                {
                    var pen = new XPen(ToX(color), Math.Max(0.5, shape.Width * w)) { LineJoin = XLineJoin.Round };
                    if (shape.Kind == ShapeKind.Rectangle)
                        gfx.DrawRectangle(pen, r);
                    else
                        gfx.DrawEllipse(pen, r);
                }

                break;
            }

            case TextAnnotation text:
            {
                var r = new XRect(text.Rect.X * w, text.Rect.Y * h, text.Rect.Width * w, text.Rect.Height * h);
                var family = PdfFontResolver.Find(text.FontFamily);
                var font = new XFont(family.Name, text.FontSize * h, text.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                var formatter = new XTextFormatter(gfx)
                {
                    Alignment = text.Alignment switch { 1 => XParagraphAlignment.Center, 2 => XParagraphAlignment.Right, _ => XParagraphAlignment.Left },
                };
                var brush = new XSolidBrush(ToX(color));

                if (text.Rotation == 0)
                {
                    formatter.DrawString(text.Text, font, brush, r, XStringFormats.TopLeft);
                }
                else
                {
                    var state = gfx.Save();
                    gfx.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
                    gfx.RotateTransform(text.Rotation);
                    formatter.DrawString(text.Text, font, brush, new XRect(-r.Height / 2, -r.Width / 2, r.Height, r.Width), XStringFormats.TopLeft);
                    gfx.Restore(state);
                }

                break;
            }

            case SignatureAnnotation signature:
            {
                var rw = signature.Rect.Width * w;
                var rh = signature.Rect.Height * h;
                var pen = Math.Max(0.8, rw * 0.012);
                foreach (var strokePoints in signature.Strokes)
                    PaintStroke(gfx, strokePoints, ToX(color), pen, rw, rh, signature.Rect.X * w, signature.Rect.Y * h);
                break;
            }
        }
    }

    private static void PaintStroke(XGraphics gfx, List<PointF> points, XColor color, double size, double scaleX, double scaleY, double offsetX, double offsetY)
    {
        if (points.Count == 0)
            return;

        if (points.Count == 1)
        {
            var p = points[0];
            gfx.DrawEllipse(new XSolidBrush(color), offsetX + p.X * scaleX - size / 2, offsetY + p.Y * scaleY - size / 2, size, size);
            return;
        }

        var pen = new XPen(color, size) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        var xpoints = points.Select(p => new XPoint(offsetX + p.X * scaleX, offsetY + p.Y * scaleY)).ToArray();
        gfx.DrawLines(pen, xpoints);
    }

    private static XColor ToX(Color c) => XColor.FromArgb((int)(c.Alpha * 255), (int)(c.Red * 255), (int)(c.Green * 255), (int)(c.Blue * 255));
}
