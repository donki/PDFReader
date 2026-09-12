using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Draw a signature with the finger or the mouse. The strokes come back normalised to their own
/// bounding box, so the signature can be placed and scaled anywhere and reused later.
/// </summary>
public partial class SignaturePadPage : ContentPage, IDrawable
{
    private readonly ILocalizationService _localization;
    private readonly List<List<PointF>> _strokes = [];
    private List<PointF>? _current;
    private readonly TaskCompletionSource<SavedSignature?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SignaturePadPage(ILocalizationService localization)
    {
        InitializeComponent();
        _localization = localization;
        TitleLabel.Text = _localization["signature_title"];
        HintLabel.Text = _localization["signature_hint"];
        Pad.Drawable = this;
    }

    /// <summary>Shows the pad and returns the signature, or null if cancelled or left blank.</summary>
    public static async Task<SavedSignature?> CaptureAsync(Page host, ILocalizationService localization)
    {
        var page = new SignaturePadPage(localization);
        await host.Navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var ink = Color.FromArgb("#1F2430");
        var size = Math.Max(2.5f, dirtyRect.Width * 0.006f);
        canvas.StrokeColor = ink;
        canvas.StrokeSize = size;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 1)
            {
                canvas.FillColor = ink;
                canvas.FillCircle(stroke[0], size / 2);
                continue;
            }

            var path = new PathF();
            path.MoveTo(stroke[0]);
            for (var i = 1; i < stroke.Count; i++)
                path.LineTo(stroke[i]);
            canvas.DrawPath(path);
        }
    }

    private void OnStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
            return;
        _current = [e.Touches[0]];
        _strokes.Add(_current);
        Pad.Invalidate();
    }

    private void OnDrag(object? sender, TouchEventArgs e)
    {
        if (_current is null || e.Touches.Length == 0)
            return;
        _current.Add(e.Touches[0]);
        Pad.Invalidate();
    }

    private void OnEnd(object? sender, TouchEventArgs e) => _current = null;

    private void OnClearClicked(object? sender, EventArgs e)
    {
        _strokes.Clear();
        Pad.Invalidate();
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async void OnAcceptClicked(object? sender, EventArgs e)
    {
        var points = _strokes.SelectMany(s => s).ToList();
        if (points.Count == 0)
        {
            await CloseAsync(null);
            return;
        }

        // Normalise to the bounding box of the signature itself (with a hair of padding so the
        // pen width does not get clipped), not to the pad: the pad's empty space is not the signature.
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var pad = Math.Max(width, height) * 0.03f;
        minX -= pad; minY -= pad; width += 2 * pad; height += 2 * pad;

        var signature = new SavedSignature
        {
            AspectRatio = height / width,
            Strokes = _strokes.Select(s => s.Select(p => new PointF((p.X - minX) / width, (p.Y - minY) / height)).ToList()).ToList(),
        };
        await CloseAsync(signature);
    }

    private async Task CloseAsync(SavedSignature? result)
    {
        await Navigation.PopModalAsync();
        _result.TrySetResult(result);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }
}
