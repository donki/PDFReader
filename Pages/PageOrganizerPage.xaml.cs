using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>A page in the organizer grid: which original page it is, how it is turned, whether it is picked.</summary>
public sealed class PageThumbnail(int sourceIndex) : INotifyPropertyChanged
{
    private ImageSource? _image;
    private int _rotation;
    private bool _isSelected;
    private int _position;

    public int SourceIndex { get; } = sourceIndex;

    public ImageSource? Image
    {
        get => _image;
        set { _image = value; OnPropertyChanged(); }
    }

    /// <summary>Extra rotation applied by the user, in degrees clockwise (0, 90, 180, 270).</summary>
    public int Rotation
    {
        get => _rotation;
        set { _rotation = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(Outline)); OnPropertyChanged(nameof(OutlineThickness)); }
    }

    /// <summary>1-based position in the output, shown under the thumbnail.</summary>
    public int Position
    {
        get => _position;
        set { _position = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
    }

    public string Label => Rotation == 0 ? $"{Position}" : $"{Position} · {Rotation}°";

    public Color Outline => IsSelected ? Color.FromArgb("#3525CD") : Colors.Transparent;

    public double OutlineThickness => IsSelected ? 3 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => OnPropertyChanged(nameof(Label));

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Rotate, reorder, delete and extract pages on a grid of thumbnails, then save the result as a
/// new document in the library. Selection is by tap; the buttons act on the selected pages (or
/// on all of them when nothing is selected, for rotation).
/// </summary>
public partial class PageOrganizerPage : ContentPage
{
    private readonly PdfDocumentEntry _entry;
    private readonly PdfInput _input;
    private readonly ILibraryService _library;
    private readonly ILocalizationService _localization;
    private readonly IPdfToolsService _tools;
    private readonly IPdfDocumentService _renderer;
    private readonly IServiceProvider _services;
    private readonly ILogger<PageOrganizerPage> _logger;

    private readonly ObservableCollection<PageThumbnail> _pages = [];
    private CancellationTokenSource? _thumbnails;

    public PageOrganizerPage(
        PdfDocumentEntry entry,
        PdfInput input,
        ILibraryService library,
        ILocalizationService localization,
        IPdfToolsService tools,
        IPdfDocumentService renderer,
        IServiceProvider services,
        ILogger<PageOrganizerPage> logger)
    {
        InitializeComponent();
        _entry = entry;
        _input = input;
        _library = library;
        _localization = localization;
        _tools = tools;
        _renderer = renderer;
        _services = services;
        _logger = logger;

        Title = _localization["tool_organize"];
        TitleLabel.Text = entry.DisplayName;
        PagesView.ItemsSource = _pages;
        UpdateHint();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_pages.Count > 0)
            return;

        _thumbnails = new CancellationTokenSource();
        try
        {
            await LoadThumbnailsAsync(_thumbnails.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not render the thumbnails of {Document}.", _entry.DisplayName);
            await SocShared.ModernDialog.AlertAsync(this, _localization["error"], _localization.Format("error_render", ex.Message), _localization["ok"]);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _thumbnails?.Cancel();
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        using var document = await _renderer.OpenAsync(_input.Path, _input.Password);
        for (var i = 0; i < document.PageCount; i++)
            _pages.Add(new PageThumbnail(i) { Position = i + 1 });
        UpdateHint();

        // Small and progressive: the grid fills in as the pages come, instead of waiting for all.
        for (var i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var png = await document.RenderPageAsync(i, 240);
            _pages[i].Image = ImageSource.FromStream(() => new MemoryStream(png));
        }
    }

    private void UpdateHint()
    {
        var selected = _pages.Count(p => p.IsSelected);
        HintLabel.Text = selected == 0
            ? _localization.Format("organizer_hint_none", _pages.Count)
            : _localization.Format("organizer_hint_selected", selected, _pages.Count);
    }

    private void Renumber()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Position = i + 1;
            _pages[i].Refresh();
        }

        UpdateHint();
    }

    private IReadOnlyList<PageThumbnail> Targets()
    {
        var selected = _pages.Where(p => p.IsSelected).ToList();
        return selected.Count > 0 ? selected : _pages.ToList();
    }

    private void OnPageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: PageThumbnail page })
            return;

        page.IsSelected = !page.IsSelected;
        UpdateHint();
    }

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        var all = _pages.All(p => p.IsSelected);
        foreach (var page in _pages)
            page.IsSelected = !all;
        UpdateHint();
    }

    private void OnRotateLeftClicked(object? sender, EventArgs e) => Rotate(-90);

    private void OnRotateRightClicked(object? sender, EventArgs e) => Rotate(90);

    private void Rotate(int degrees)
    {
        foreach (var page in Targets())
        {
            page.Rotation = ((page.Rotation + degrees) % 360 + 360) % 360;
            page.Refresh();
        }
    }

    private void OnMoveUpClicked(object? sender, EventArgs e) => Move(-1);

    private void OnMoveDownClicked(object? sender, EventArgs e) => Move(1);

    /// <summary>Moves the selected pages one slot, keeping them together in order.</summary>
    private void Move(int delta)
    {
        var indexes = _pages.Select((p, i) => (p, i)).Where(t => t.p.IsSelected).Select(t => t.i).ToList();
        if (indexes.Count == 0)
            return;

        if (delta < 0 && indexes[0] == 0)
            return;
        if (delta > 0 && indexes[^1] == _pages.Count - 1)
            return;

        var ordered = delta < 0 ? indexes : Enumerable.Reverse(indexes).ToList();
        foreach (var index in ordered)
            _pages.Move(index, index + delta);

        Renumber();
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        var selected = _pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        if (selected.Count == _pages.Count)
        {
            await SocShared.ModernDialog.AlertAsync(this, _localization["error"], _localization["organizer_keep_one"], _localization["ok"]);
            return;
        }

        foreach (var page in selected)
            _pages.Remove(page);

        Renumber();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_pages.Count == 0)
            return;

        // Something selected: offer to keep only that (extract). Nothing selected: save everything.
        var selected = _pages.Where(p => p.IsSelected).ToList();
        IReadOnlyList<PageThumbnail> pagesToSave = _pages;
        if (selected.Count > 0 && selected.Count < _pages.Count)
        {
            var choice = await SocShared.ModernDialog.ActionSheetAsync(this, _localization["organizer_save_prompt"], _localization["cancel"],
                _localization.Format("organizer_save_selected", selected.Count), _localization.Format("organizer_save_all", _pages.Count));
            if (choice is null)
                return;
            if (choice == _localization.Format("organizer_save_selected", selected.Count))
                pagesToSave = selected;
        }

        try
        {
            BusyLabel.Text = _localization["busy_working"];
            BusyOverlay.IsVisible = true;

            var output = Path.Combine(FileSystem.CacheDirectory, "tools", $"organized-{Guid.NewGuid():N}.pdf");
            await _tools.RearrangeAsync(_input, pagesToSave.Select(p => new PageEdit(p.SourceIndex, p.Rotation)).ToList(), output);

            PdfDocumentEntry entry;
            await using (var stream = File.OpenRead(output))
            {
                entry = await _library.ImportAsync(stream, $"{Path.GetFileNameWithoutExtension(_entry.DisplayName)}-pages.pdf");
            }

            await _library.SetPageCountAsync(entry, pagesToSave.Count);
            File.Delete(output);

            BusyOverlay.IsVisible = false;
            await SocShared.ModernDialog.AlertAsync(this, _localization["done"], _localization.Format("result_organized", pagesToSave.Count), _localization["ok"]);

            var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_services, entry);
            reader.InitialPassword = null; // the rearranged copy is written unencrypted
            Navigation.InsertPageBefore(reader, this);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            BusyOverlay.IsVisible = false;
            _logger.LogError(ex, "Could not save the rearranged pages of {Document}.", _entry.DisplayName);
            await SocShared.ModernDialog.AlertAsync(this, _localization["error"], _localization.Format("error_tool", ex.Message), _localization["ok"]);
        }
    }
}
