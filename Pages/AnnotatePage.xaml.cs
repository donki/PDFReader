using System.Text.Json;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Annotate and sign: draw over the rendered page with a pen, highlighter, shapes, white covers,
/// text boxes and a handwritten signature, then save everything flattened into a new PDF. The
/// annotations live in normalised page coordinates, so the overlay and the saved page match.
/// </summary>
/// <remarks>
/// «Corregir un dato» is a white cover plus a text box on top: the closest thing to editing text
/// that a PDF allows without re-flowing the original (see README, «por qué no se edita el texto»).
/// </remarks>
public partial class AnnotatePage : ContentPage, IDrawable
{
    private enum Tool { Select, Pen, Highlight, Rectangle, Ellipse, Whiteout, Text, Signature }

    private static readonly string[] Palette = ["#3525CD", "#1F2430", "#D32F2F", "#2E7D32", "#1565C0", "#F9A825"];
    private static readonly float[] StrokeWidths = [0.002f, 0.004f, 0.008f, 0.014f];
    private static readonly float[] TextSizes = [0.014f, 0.018f, 0.024f, 0.032f];
    private const string SignaturePreference = "saved_signature";

    private readonly PdfDocumentEntry _entry;
    private readonly PdfInput _input;
    private readonly ILibraryService _library;
    private readonly ILocalizationService _localization;
    private readonly IPdfToolsService _tools;
    private readonly IPdfDocumentService _renderer;
    private readonly IServiceProvider _services;
    private readonly ILogger<AnnotatePage> _logger;

    private IPdfDocument? _document;
    private int _pageIndex;
    private double _aspectRatio = 1.4142;
    private byte[]? _pagePng;

    private readonly List<Annotation> _annotations = [];
    private Annotation? _selected;
    private Tool _tool = Tool.Pen;
    private int _colorIndex;
    private int _sizeIndex = 1;
    private bool _bold;
    private int _alignment;
    private int _rotation;
    private int _familyIndex;

    // Gesture in progress.
    private StrokeAnnotation? _currentStroke;
    private ShapeAnnotation? _currentShape;
    private PointF _dragStart;
    private RectF _dragStartRect;
    private bool _resizing;
    private bool _moved;

    public AnnotatePage(
        PdfDocumentEntry entry,
        PdfInput input,
        int pageIndex,
        ILibraryService library,
        ILocalizationService localization,
        IPdfToolsService tools,
        IPdfDocumentService renderer,
        IServiceProvider services,
        ILogger<AnnotatePage> logger)
    {
        InitializeComponent();
        _entry = entry;
        _input = input;
        _pageIndex = pageIndex;
        _library = library;
        _localization = localization;
        _tools = tools;
        _renderer = renderer;
        _services = services;
        _logger = logger;

        Title = _localization["annotate_title"];
        TitleLabel.Text = entry.DisplayName;
        Overlay.Drawable = this;
        Viewport.SizeChanged += (_, _) => Refit();

        SetTool(Tool.Pen);
        UpdateColorButton();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_document is not null)
            return;

        try
        {
            _document = await _renderer.OpenAsync(_input.Path, _input.Password);
            if (_pageIndex >= _document.PageCount)
                _pageIndex = 0;
            await ShowPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Document} for annotation.", _entry.DisplayName);
            await AlertAsync(_localization["error_open_title"], _localization.Format("error_render", ex.Message));
            await Navigation.PopAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Navigation.NavigationStack.Contains(this))
            return; // pushed something on top (the signature pad): keep the document open

        _document?.Dispose();
        _document = null;
    }

    // ---------------------------------------------------------------------
    //  Pagina
    // ---------------------------------------------------------------------

    private async Task ShowPageAsync()
    {
        if (_document is null)
            return;

        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;
        try
        {
            _aspectRatio = await _document.GetPageAspectRatioAsync(_pageIndex);
            Refit();

            var density = DeviceDisplay.Current.MainDisplayInfo.Density;
            var width = (int)Math.Round(Math.Max(300, PageContainer.WidthRequest) * (density > 0 ? density : 1) * 1.5);
            _pagePng = await _document.RenderPageAsync(_pageIndex, width);
            PageImage.Source = ImageSource.FromStream(() => new MemoryStream(_pagePng));

            _selected = null;
            Overlay.Invalidate();
            UpdatePageLabel();
        }
        finally
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
        }
    }

    /// <summary>Whole page inside the viewport, keeping its shape.</summary>
    private void Refit()
    {
        var availableWidth = Math.Max(100, Viewport.Width - 16);
        var availableHeight = Math.Max(100, Viewport.Height - 16);
        if (Viewport.Width <= 0 || Viewport.Height <= 0)
            return;

        var width = Math.Min(availableWidth, availableHeight / _aspectRatio);
        PageContainer.WidthRequest = width;
        PageContainer.HeightRequest = width * _aspectRatio;
        Overlay.Invalidate();
    }

    private void UpdatePageLabel()
    {
        var count = _document?.PageCount ?? 0;
        PageLabel.Text = $"{_pageIndex + 1} / {count}";
        PreviousButton.IsEnabled = _pageIndex > 0;
        NextButton.IsEnabled = _pageIndex < count - 1;
    }

    private async void OnPreviousClicked(object? sender, EventArgs e)
    {
        if (_pageIndex <= 0)
            return;
        _pageIndex--;
        await ShowPageAsync();
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_document is null || _pageIndex >= _document.PageCount - 1)
            return;
        _pageIndex++;
        await ShowPageAsync();
    }

    // ---------------------------------------------------------------------
    //  Dibujo del lienzo
    // ---------------------------------------------------------------------

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var w = dirtyRect.Width;
        var h = dirtyRect.Height;
        foreach (var annotation in _annotations.Where(a => a.PageIndex == _pageIndex))
            AnnotationPainter.Draw(canvas, annotation, w, h, ReferenceEquals(annotation, _selected));
    }

    private PointF Normalise(PointF p) =>
        new((float)Math.Clamp(p.X / Math.Max(1, Overlay.Width), 0, 1), (float)Math.Clamp(p.Y / Math.Max(1, Overlay.Height), 0, 1));

    private void OnStartInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
            return;

        var p = Normalise(e.Touches[0]);
        _dragStart = p;
        _moved = false;
        _resizing = false;

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Highlight:
                _currentStroke = new StrokeAnnotation
                {
                    PageIndex = _pageIndex,
                    Color = _tool == Tool.Highlight ? "#F9A825" : Palette[_colorIndex],
                    Width = _tool == Tool.Highlight ? StrokeWidths[_sizeIndex] * 4 : StrokeWidths[_sizeIndex],
                    Alpha = _tool == Tool.Highlight ? 0.35f : 1f,
                    Points = [p],
                };
                _annotations.Add(_currentStroke);
                break;

            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.Whiteout:
                _currentShape = new ShapeAnnotation
                {
                    PageIndex = _pageIndex,
                    Kind = _tool == Tool.Rectangle ? ShapeKind.Rectangle : _tool == Tool.Ellipse ? ShapeKind.Ellipse : ShapeKind.Whiteout,
                    Color = Palette[_colorIndex],
                    Width = StrokeWidths[_sizeIndex],
                    Rect = new RectF(p.X, p.Y, 0, 0),
                };
                _annotations.Add(_currentShape);
                break;

            case Tool.Select:
                // Handle of the selected annotation first, then whatever is under the finger.
                if (_selected is not null && NearHandle(_selected, p))
                {
                    _resizing = true;
                    _dragStartRect = _selected.Bounds;
                }
                else
                {
                    _selected = HitTest(p);
                    _dragStartRect = _selected?.Bounds ?? RectF.Zero;
                }

                if (_selected is TextAnnotation picked)
                {
                    // The bar shows the picked box's own settings and edits it from here on.
                    _alignment = picked.Alignment;
                    _rotation = picked.Rotation;
                    _bold = picked.Bold;
                    _familyIndex = Math.Max(0, PdfFontResolver.Families.ToList().FindIndex(f => f.Name == picked.FontFamily));
                    BoldButton.BackgroundColor = _bold ? Color.FromArgb("#3525CD").WithAlpha(0.25f) : Colors.Transparent;
                }

                UpdateTextOptions();
                break;
        }

        Overlay.Invalidate();
    }

    private void OnDragInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
            return;

        var p = Normalise(e.Touches[0]);
        _moved = true;

        if (_currentStroke is not null)
        {
            _currentStroke.Points.Add(p);
        }
        else if (_currentShape is not null)
        {
            _currentShape.Rect = RectF.FromLTRB(Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y), Math.Max(_dragStart.X, p.X), Math.Max(_dragStart.Y, p.Y));
        }
        else if (_tool == Tool.Select && _selected is not null)
        {
            if (_resizing)
                Resize(_selected, p);
            else
                MoveTo(_selected, p);
        }

        Overlay.Invalidate();
    }

    private async void OnEndInteraction(object? sender, TouchEventArgs e)
    {
        var p = e.Touches.Length > 0 ? Normalise(e.Touches[0]) : _dragStart;

        if (_currentShape is not null && (_currentShape.Rect.Width < 0.005f || _currentShape.Rect.Height < 0.005f))
            _annotations.Remove(_currentShape); // a tap, not a box

        _currentStroke = null;
        _currentShape = null;

        try
        {
            switch (_tool)
            {
                case Tool.Text when !_moved:
                    await AddTextAsync(p);
                    break;

                case Tool.Signature when !_moved:
                    await AddSignatureAsync(p);
                    break;

                case Tool.Select when !_moved && _selected is TextAnnotation text:
                    await EditTextAsync(text);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Annotation gesture failed.");
        }

        Overlay.Invalidate();
    }

    private Annotation? HitTest(PointF p)
    {
        // Last drawn wins, the way it is stacked on screen.
        for (var i = _annotations.Count - 1; i >= 0; i--)
        {
            var a = _annotations[i];
            if (a.PageIndex != _pageIndex)
                continue;

            var b = a.Bounds;
            b = new RectF(b.X - 0.01f, b.Y - 0.01f, b.Width + 0.02f, b.Height + 0.02f);
            if (b.Contains(p))
                return a;
        }

        return null;
    }

    private bool NearHandle(Annotation a, PointF p)
    {
        var b = a.Bounds;
        var tolerance = 14 / (float)Math.Max(1, Overlay.Width);
        return Math.Abs(p.X - b.Right) < tolerance && Math.Abs(p.Y - b.Bottom) < tolerance * (float)(Overlay.Width / Math.Max(1, Overlay.Height));
    }

    private void MoveTo(Annotation a, PointF p)
    {
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        var current = a.Bounds;
        a.Translate(_dragStartRect.X + dx - current.X, _dragStartRect.Y + dy - current.Y);
    }

    private void Resize(Annotation a, PointF p)
    {
        var width = Math.Max(0.02f, p.X - _dragStartRect.X);
        var height = Math.Max(0.02f, p.Y - _dragStartRect.Y);
        switch (a)
        {
            case ShapeAnnotation shape:
                shape.Rect = new RectF(_dragStartRect.X, _dragStartRect.Y, width, height);
                break;
            case TextAnnotation text:
                text.Rect = new RectF(_dragStartRect.X, _dragStartRect.Y, width, height);
                break;
            case SignatureAnnotation signature:
            {
                // Keep the signature's shape: width rules, height follows.
                var ratio = signature.AspectRatio * (float)(Overlay.Width / Math.Max(1, Overlay.Height));
                signature.Rect = new RectF(_dragStartRect.X, _dragStartRect.Y, width, width * ratio);
                break;
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Texto y firma
    // ---------------------------------------------------------------------

    private async Task AddTextAsync(PointF at)
    {
        var text = await SocShared.ModernDialog.PromptAsync(this, _localization["text_prompt_title"], null,
            _localization["ok"], _localization["cancel"], placeholder: _localization["text_placeholder"]);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var fontSize = TextSizes[_sizeIndex];
        var lines = text.Split('\n').Length;
        var width = Math.Min(0.5f, 1 - at.X);
        var height = Math.Max(fontSize * 1.5f, fontSize * 1.35f * (lines + 1));
        var annotation = new TextAnnotation
        {
            PageIndex = _pageIndex,
            Color = Palette[_colorIndex],
            Text = text,
            FontSize = fontSize,
            Bold = _bold,
            FontFamily = PdfFontResolver.Families[_familyIndex].Name,
            Alignment = _alignment,
            Rotation = _rotation,
            Rect = _rotation == 0
                ? new RectF(at.X, at.Y, width, Math.Min(height, 1 - at.Y))
                : new RectF(at.X, at.Y, Math.Min(height, 1 - at.X), Math.Min(width, 1 - at.Y)),
        };
        _annotations.Add(annotation);
        _selected = annotation;
        SetTool(Tool.Select);
        UpdateTextOptions();
    }

    private async Task EditTextAsync(TextAnnotation text)
    {
        var edited = await SocShared.ModernDialog.PromptAsync(this, _localization["text_prompt_title"], null,
            _localization["ok"], _localization["cancel"], initialValue: text.Text);
        if (edited is null)
            return;

        if (string.IsNullOrWhiteSpace(edited))
        {
            _annotations.Remove(text);
            _selected = null;
            return;
        }

        text.Text = edited;
    }

    private async Task AddSignatureAsync(PointF at)
    {
        var saved = LoadSavedSignature();
        SavedSignature? signature = null;

        if (saved is not null)
        {
            var choice = await SocShared.ModernDialog.ActionSheetAsync(this, _localization["signature_prompt"], _localization["cancel"],
                _localization["signature_use_saved"], _localization["signature_draw_new"]);
            if (choice is null)
                return;
            if (choice == _localization["signature_use_saved"])
                signature = saved;
        }

        signature ??= await SignaturePadPage.CaptureAsync(this, _localization);
        if (signature is null)
            return;

        Preferences.Default.Set(SignaturePreference, JsonSerializer.Serialize(signature));

        var width = 0.3f;
        var ratio = signature.AspectRatio * (float)(Overlay.Width / Math.Max(1, Overlay.Height));
        var annotation = new SignatureAnnotation
        {
            PageIndex = _pageIndex,
            Color = Palette[_colorIndex] == "#F9A825" ? "#1F2430" : Palette[_colorIndex],
            Strokes = signature.Strokes,
            AspectRatio = signature.AspectRatio,
            Rect = new RectF(Math.Clamp(at.X - width / 2, 0, 1 - width), Math.Clamp(at.Y - width * ratio / 2, 0, 1 - width * ratio), width, width * ratio),
        };
        _annotations.Add(annotation);
        _selected = annotation;
        SetTool(Tool.Select);
    }

    private static SavedSignature? LoadSavedSignature()
    {
        try
        {
            var json = Preferences.Default.Get(SignaturePreference, string.Empty);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<SavedSignature>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    //  Barra de herramientas
    // ---------------------------------------------------------------------

    private void OnToolClicked(object? sender, EventArgs e)
    {
        var tool = sender switch
        {
            _ when ReferenceEquals(sender, SelectButton) => Tool.Select,
            _ when ReferenceEquals(sender, PenButton) => Tool.Pen,
            _ when ReferenceEquals(sender, HighlightButton) => Tool.Highlight,
            _ when ReferenceEquals(sender, RectButton) => Tool.Rectangle,
            _ when ReferenceEquals(sender, EllipseButton) => Tool.Ellipse,
            _ when ReferenceEquals(sender, WhiteoutButton) => Tool.Whiteout,
            _ when ReferenceEquals(sender, TextButton) => Tool.Text,
            _ => Tool.Signature,
        };
        SetTool(tool);
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        if (tool != Tool.Select)
            _selected = null;

        var accent = Color.FromArgb("#3525CD");
        foreach (var (button, t) in new (Button, Tool)[]
                 {
                     (SelectButton, Tool.Select), (PenButton, Tool.Pen), (HighlightButton, Tool.Highlight), (RectButton, Tool.Rectangle),
                     (EllipseButton, Tool.Ellipse), (WhiteoutButton, Tool.Whiteout), (TextButton, Tool.Text), (SignatureButton, Tool.Signature),
                 })
        {
            button.BackgroundColor = t == tool ? accent.WithAlpha(0.25f) : Colors.Transparent;
        }

        UpdateTextOptions();
        HintLabel.Text = _localization[tool switch
        {
            Tool.Select => "hint_select",
            Tool.Pen => "hint_pen",
            Tool.Highlight => "hint_highlight",
            Tool.Rectangle => "hint_rect",
            Tool.Ellipse => "hint_ellipse",
            Tool.Whiteout => "hint_whiteout",
            Tool.Text => "hint_text",
            _ => "hint_signature",
        }];
        Overlay.Invalidate();
    }

    private void UpdateTextOptions()
    {
        var text = _selected as TextAnnotation;
        TextOptions.IsVisible = _tool == Tool.Text || text is not null;
        if (!TextOptions.IsVisible)
            return;

        var alignment = text?.Alignment ?? _alignment;
        var rotation = text?.Rotation ?? _rotation;
        var family = text is null ? PdfFontResolver.Families[_familyIndex] : PdfFontResolver.Find(text.FontFamily);
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark ? "_dark" : string.Empty;

        AlignButton.ImageSource = ImageSource.FromFile(alignment switch { 1 => $"ic_align_center{dark}.png", 2 => $"ic_align_right{dark}.png", _ => $"ic_align_left{dark}.png" });
        VerticalButton.BackgroundColor = rotation != 0 ? Color.FromArgb("#3525CD").WithAlpha(0.25f) : Colors.Transparent;
        FontButton.Text = "Aa · " + _localization["font_" + family.Name];
        FontButton.FontFamily = family.MauiRegular;
        TextOptionsHint.Text = _localization["hint_text_options"];
    }

    private void OnAlignClicked(object? sender, EventArgs e)
    {
        _alignment = (_alignment + 1) % 3;
        if (_selected is TextAnnotation text)
        {
            text.Alignment = _alignment;
            Overlay.Invalidate();
        }

        UpdateTextOptions();
    }

    /// <summary>Horizontal → top-to-bottom → bottom-to-top → horizontal. The box is turned with the text.</summary>
    private void OnVerticalClicked(object? sender, EventArgs e)
    {
        _rotation = _rotation switch { 0 => 90, 90 => 270, _ => 0 };
        if (_selected is TextAnnotation text)
        {
            var wasVertical = text.Rotation != 0;
            var isVertical = _rotation != 0;
            text.Rotation = _rotation;
            if (wasVertical != isVertical)
            {
                // Swap the box so the text keeps its line length; the centre stays put.
                var r = text.Rect;
                var pageRatio = (float)(Overlay.Width / Math.Max(1, Overlay.Height));
                var newWidth = r.Height / pageRatio;
                var newHeight = r.Width * pageRatio;
                text.Rect = new RectF(r.Center.X - newWidth / 2, r.Center.Y - newHeight / 2, newWidth, newHeight);
            }

            Overlay.Invalidate();
        }

        UpdateTextOptions();
    }

    private void OnFontClicked(object? sender, EventArgs e)
    {
        _familyIndex = (_familyIndex + 1) % PdfFontResolver.Families.Count;
        if (_selected is TextAnnotation text)
        {
            text.FontFamily = PdfFontResolver.Families[_familyIndex].Name;
            Overlay.Invalidate();
        }

        UpdateTextOptions();
    }

    private void OnColorClicked(object? sender, EventArgs e)
    {
        _colorIndex = (_colorIndex + 1) % Palette.Length;
        UpdateColorButton();
        if (_selected is not null && _selected is not StrokeAnnotation { Alpha: < 1 })
        {
            _selected.Color = Palette[_colorIndex];
            Overlay.Invalidate();
        }
    }

    private void UpdateColorButton()
    {
        ColorButton.BackgroundColor = Color.FromArgb(Palette[_colorIndex]);
        ColorButton.BorderColor = Colors.White;
        ColorButton.BorderWidth = 2;
    }

    private void OnSizeClicked(object? sender, EventArgs e)
    {
        _sizeIndex = (_sizeIndex + 1) % StrokeWidths.Length;
        switch (_selected)
        {
            case TextAnnotation text:
                text.FontSize = TextSizes[_sizeIndex];
                break;
            case StrokeAnnotation stroke:
                stroke.Width = stroke.Alpha < 1 ? StrokeWidths[_sizeIndex] * 4 : StrokeWidths[_sizeIndex];
                break;
            case ShapeAnnotation shape:
                shape.Width = StrokeWidths[_sizeIndex];
                break;
        }

        HintLabel.Text = _localization.Format("hint_size", _sizeIndex + 1, StrokeWidths.Length);
        Overlay.Invalidate();
    }

    private void OnBoldClicked(object? sender, EventArgs e)
    {
        _bold = !_bold;
        BoldButton.BackgroundColor = _bold ? Color.FromArgb("#3525CD").WithAlpha(0.25f) : Colors.Transparent;
        if (_selected is TextAnnotation text)
        {
            text.Bold = _bold;
            Overlay.Invalidate();
        }
    }

    private void OnUndoClicked(object? sender, EventArgs e)
    {
        var last = _annotations.LastOrDefault(a => a.PageIndex == _pageIndex);
        if (last is null)
            return;

        _annotations.Remove(last);
        if (ReferenceEquals(_selected, last))
            _selected = null;
        Overlay.Invalidate();
    }

    private void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (_selected is null)
            return;

        _annotations.Remove(_selected);
        _selected = null;
        Overlay.Invalidate();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_annotations.Count == 0)
        {
            await AlertAsync(_localization["annotate_title"], _localization["annotate_nothing"]);
            return;
        }

        try
        {
            BusyLabel.Text = _localization["busy_working"];
            BusyOverlay.IsVisible = true;

            var output = Path.Combine(FileSystem.CacheDirectory, "tools", $"annotated-{Guid.NewGuid():N}.pdf");
            await _tools.FlattenAnnotationsAsync(_input, _annotations, output);

            PdfDocumentEntry entry;
            await using (var stream = File.OpenRead(output))
            {
                entry = await _library.ImportAsync(stream, $"{Path.GetFileNameWithoutExtension(_entry.DisplayName)}-{_localization["annotated_suffix"]}.pdf");
            }

            await _library.SetPageCountAsync(entry, _document?.PageCount ?? 0);
            File.Delete(output);

            BusyOverlay.IsVisible = false;
            await AlertAsync(_localization["done"], _localization["result_annotated"]);

            var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_services, entry);
            reader.InitialPassword = _input.Password;
            Navigation.InsertPageBefore(reader, this);
            await Navigation.PopAsync();
            DropOtherReaders(reader);
        }
        catch (Exception ex)
        {
            BusyOverlay.IsVisible = false;
            _logger.LogError(ex, "Could not save the annotations of {Document}.", _entry.DisplayName);
            await AlertAsync(_localization["error"], _localization.Format("error_tool", ex.Message));
        }
    }

    private Task AlertAsync(string title, string message) =>
        SocShared.ModernDialog.AlertAsync(this, title, message, _localization["ok"]);

    /// <summary>One reader on the stack at a time: each keeps its page bitmaps, and a chain of them eats the memory.</summary>
    private void DropOtherReaders(ReaderPage keep)
    {
        foreach (var previous in Navigation.NavigationStack.OfType<ReaderPage>().Where(page => !ReferenceEquals(page, keep)).ToList())
            Navigation.RemovePage(previous);
    }
}
