using System.ComponentModel;
using System.Runtime.CompilerServices;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>A library document as shown in the picker: tapping toggles it, and the badge shows the pick order.</summary>
public sealed class PickableDocument(PdfDocumentEntry entry, string details) : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _order;

    public PdfDocumentEntry Entry { get; } = entry;
    public string DisplayName => Entry.DisplayName;
    public string Details { get; } = details;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public int Order
    {
        get => _order;
        set { _order = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Lets the user pick one or several library documents for a tool, in order; «+» imports another
/// file into the library first. Modal, resolved through <see cref="PickAsync"/>.
/// </summary>
public partial class DocumentPickerPage : ContentPage
{
    private readonly ILibraryService _library;
    private readonly ILocalizationService _localization;
    private readonly bool _multiple;
    private readonly List<PickableDocument> _items = [];
    private readonly List<PickableDocument> _picked = [];
    private readonly TaskCompletionSource<IReadOnlyList<PdfDocumentEntry>?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DocumentPickerPage(ILibraryService library, ILocalizationService localization, string title, string hint, bool multiple)
    {
        InitializeComponent();
        _library = library;
        _localization = localization;
        _multiple = multiple;

        TitleLabel.Text = title;
        HintLabel.Text = hint;
        DocumentsView.ItemsSource = _items;
    }

    /// <summary>Shows the picker and returns the chosen documents in pick order; null when cancelled.</summary>
    public static async Task<IReadOnlyList<PdfDocumentEntry>?> PickAsync(Page host, ILibraryService library, ILocalizationService localization, string title, string hint, bool multiple)
    {
        var page = new DocumentPickerPage(library, localization, title, hint, multiple);
        await page.LoadAsync();
        await host.Navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private async Task LoadAsync()
    {
        _items.Clear();
        foreach (var entry in await _library.GetDocumentsAsync())
            _items.Add(new PickableDocument(entry, Describe(entry)));
        DocumentsView.ItemsSource = null;
        DocumentsView.ItemsSource = _items;
    }

    private string Describe(PdfDocumentEntry entry)
    {
        var size = entry.SizeBytes >= 1024 * 1024
            ? $"{entry.SizeBytes / (1024.0 * 1024.0):0.0} MB"
            : $"{Math.Max(1, entry.SizeBytes / 1024)} KB";
        if (entry.PageCount <= 0)
            return size;

        var pages = entry.PageCount == 1 ? _localization["page_count_one"] : _localization.Format("page_count", entry.PageCount);
        return $"{pages} · {size}";
    }

    private void OnDocumentTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: PickableDocument item })
            return;

        if (item.IsSelected)
        {
            item.IsSelected = false;
            _picked.Remove(item);
        }
        else
        {
            if (!_multiple)
            {
                foreach (var other in _picked)
                    other.IsSelected = false;
                _picked.Clear();
            }

            item.IsSelected = true;
            _picked.Add(item);
        }

        for (var i = 0; i < _picked.Count; i++)
            _picked[i].Order = i + 1;
    }

    private async void OnAddFileClicked(object? sender, EventArgs e)
    {
        try
        {
            var files = _multiple
                ? await FilePicker.Default.PickMultipleAsync(new PickOptions { FileTypes = FilePickerFileType.Pdf })
                : [await FilePicker.Default.PickAsync(new PickOptions { FileTypes = FilePickerFileType.Pdf })];

            BusyOverlay.IsVisible = true;
            foreach (var file in files)
            {
                if (file is null)
                    continue;

                await using var stream = await file.OpenReadAsync();
                var entry = await _library.ImportAsync(stream, file.FileName);
                var item = new PickableDocument(entry, Describe(entry)) { IsSelected = true };
                if (!_multiple)
                {
                    foreach (var other in _picked)
                        other.IsSelected = false;
                    _picked.Clear();
                }

                _items.Insert(0, item);
                _picked.Add(item);
            }

            for (var i = 0; i < _picked.Count; i++)
                _picked[i].Order = i + 1;

            DocumentsView.ItemsSource = null;
            DocumentsView.ItemsSource = _items;
        }
        catch (Exception ex)
        {
            await SocShared.ModernDialog.AlertAsync(this, _localization["error"], _localization.Format("error_import", ex.Message), _localization["ok"]);
        }
        finally
        {
            BusyOverlay.IsVisible = false;
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async void OnAcceptClicked(object? sender, EventArgs e)
    {
        if (_picked.Count == 0)
            return;

        await CloseAsync(_picked.Select(p => p.Entry).ToList());
    }

    private async Task CloseAsync(IReadOnlyList<PdfDocumentEntry>? result)
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
