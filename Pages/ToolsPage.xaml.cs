using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>A row of the tools list.</summary>
public sealed record ToolItem(ToolKind Kind, ImageSource Icon, string Title, string Subtitle);

public enum ToolKind
{
    Merge,
    Split,
    Organize,
    ImagesToPdf,
    PdfToImages,
    Protect,
    Unprotect,
    PageNumbers,
    Watermark
}

/// <summary>
/// The document tools. Each one asks for its inputs (library picker, file picker, a prompt),
/// runs in the background with a progress overlay and drops the result in the library, where it
/// opens right away; the reader's export button gets it out of the app. The originals are never
/// modified.
/// </summary>
public partial class ToolsPage : ContentPage
{
    private readonly ILibraryService _library;
    private readonly ILocalizationService _localization;
    private readonly IPdfToolsService _tools;
    private readonly IPdfDocumentService _renderer;
    private readonly IFileExportService _export;
    private readonly IServiceProvider _services;
    private readonly ILogger<ToolsPage> _logger;

    private bool _running;

    public ToolsPage(
        ILibraryService library,
        ILocalizationService localization,
        IPdfToolsService tools,
        IPdfDocumentService renderer,
        IFileExportService export,
        IServiceProvider services,
        ILogger<ToolsPage> logger)
    {
        InitializeComponent();
        _library = library;
        _localization = localization;
        _tools = tools;
        _renderer = renderer;
        _export = export;
        _services = services;
        _logger = logger;

        Title = _localization["tools_title"];
        TitleLabel.Text = _localization["tools_title"];
        HintLabel.Text = _localization["tools_hint"];

        ToolsView.ItemsSource = new List<ToolItem>
        {
            Tool(ToolKind.Merge, "ic_merge", "tool_merge"),
            Tool(ToolKind.Split, "ic_split", "tool_split"),
            Tool(ToolKind.Organize, "ic_pages", "tool_organize"),
            Tool(ToolKind.ImagesToPdf, "ic_image", "tool_images_to_pdf"),
            Tool(ToolKind.PdfToImages, "ic_export_image", "tool_pdf_to_images"),
            Tool(ToolKind.Protect, "ic_lock", "tool_protect"),
            Tool(ToolKind.Unprotect, "ic_unlock", "tool_unprotect"),
            Tool(ToolKind.PageNumbers, "ic_number", "tool_page_numbers"),
            Tool(ToolKind.Watermark, "ic_watermark", "tool_watermark"),
        };
    }

    private ToolItem Tool(ToolKind kind, string icon, string key)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return new ToolItem(kind, ImageSource.FromFile(dark ? $"{icon}_dark.png" : $"{icon}.png"),
            _localization[key], _localization[key + "_hint"]);
    }

    private async void OnToolTapped(object? sender, TappedEventArgs e)
    {
        if (_running || sender is not BindableObject { BindingContext: ToolItem item })
            return;

        _running = true;
        try
        {
            switch (item.Kind)
            {
                case ToolKind.Merge: await MergeAsync(); break;
                case ToolKind.Split: await SplitAsync(); break;
                case ToolKind.Organize: await OrganizeAsync(); break;
                case ToolKind.ImagesToPdf: await ImagesToPdfAsync(); break;
                case ToolKind.PdfToImages: await PdfToImagesAsync(); break;
                case ToolKind.Protect: await ProtectAsync(); break;
                case ToolKind.Unprotect: await UnprotectAsync(); break;
                case ToolKind.PageNumbers: await PageNumbersAsync(); break;
                case ToolKind.Watermark: await WatermarkAsync(); break;
            }
        }
        catch (OperationCanceledException)
        {
            // The user backed out; nothing to say.
        }
        catch (PdfOpenException ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} could not open its input ({Failure}).", item.Kind, ex.Failure);
            await AlertAsync(_localization["error_open_title"], ex.Failure == PdfOpenFailure.InvalidDocument
                ? _localization["error_not_pdf"] : _localization["error_protected"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} failed.", item.Kind);
            await AlertAsync(_localization["error"], _localization.Format("error_tool", ex.Message));
        }
        finally
        {
            SetBusy(false);
            _running = false;
        }
    }

    // ---------------------------------------------------------------------
    //  Herramientas
    // ---------------------------------------------------------------------

    private async Task MergeAsync()
    {
        var picked = await PickAsync("tool_merge", "pick_merge_hint", multiple: true);
        if (picked is null || picked.Count == 0)
            return;

        var inputs = new List<PdfInput>();
        foreach (var entry in picked)
        {
            var input = await InputWithPasswordAsync(entry);
            if (input is null)
                return;
            inputs.Add(input);
        }

        SetBusy(_localization["busy_merging"]);
        var output = OutputPath("merged");
        await _tools.MergeAsync(inputs, output, Progress());
        await FinishAsync(output, _localization.Format("result_merged", picked.Count));
    }

    private async Task SplitAsync()
    {
        var entry = await PickOneAsync("tool_split", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        var pageCount = await _tools.GetPageCountAsync(input);
        var answer = await SocShared.ModernDialog.PromptAsync(this, _localization["tool_split"],
            _localization.Format("split_prompt", pageCount), _localization["ok"], _localization["cancel"],
            placeholder: "1-3, 4-6");
        if (answer is null)
            return;

        var ranges = ParseRanges(answer, pageCount);
        if (ranges.Count == 0)
        {
            await AlertAsync(_localization["error"], _localization["split_invalid"]);
            return;
        }

        SetBusy(_localization["busy_splitting"]);
        var baseName = Path.GetFileNameWithoutExtension(entry.DisplayName);
        PdfDocumentEntry? first = null;
        for (var i = 0; i < ranges.Count; i++)
        {
            var output = OutputPath($"{baseName}-{i + 1}");
            await _tools.RearrangeAsync(input, ranges[i].Select(p => new PageEdit(p)).ToList(), output);
            var imported = await ImportResultAsync(output, $"{baseName} ({i + 1} de {ranges.Count}).pdf");
            first ??= imported;
            BusyProgress.Progress = (i + 1) / (double)ranges.Count;
        }

        SetBusy(false);
        await AlertAsync(_localization["done"], _localization.Format("result_split", ranges.Count));
        await Navigation.PopAsync();
    }

    /// <summary>«1-3, 5, 8-» → page index lists. Empty text means one document per page.</summary>
    private static List<List<int>> ParseRanges(string text, int pageCount)
    {
        var result = new List<List<int>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            for (var i = 0; i < pageCount; i++)
                result.Add([i]);
            return result;
        }

        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var from))
                continue;

            var to = from;
            if (bounds.Length > 1)
            {
                if (bounds[1].Length == 0)
                    to = pageCount;
                else if (!int.TryParse(bounds[1], out to))
                    continue;
            }

            from = Math.Clamp(from, 1, pageCount);
            to = Math.Clamp(to, 1, pageCount);
            if (to < from)
                (from, to) = (to, from);

            result.Add(Enumerable.Range(from - 1, to - from + 1).ToList());
        }

        return result;
    }

    private async Task OrganizeAsync()
    {
        var entry = await PickOneAsync("tool_organize", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        var organizer = ActivatorUtilities.CreateInstance<PageOrganizerPage>(_services, entry, input);
        await Navigation.PushAsync(organizer);
    }

    private async Task ImagesToPdfAsync()
    {
        var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = _localization["tool_images_to_pdf"],
            FileTypes = FilePickerFileType.Images,
        });
        var list = files.ToList();
        if (list.Count == 0)
            return;

        var fitChoice = await SocShared.ModernDialog.ActionSheetAsync(this, _localization["images_fit_prompt"], _localization["cancel"],
            _localization["images_fit_a4"], _localization["images_fit_image"]);
        if (fitChoice is null)
            return;
        var fit = fitChoice == _localization["images_fit_a4"] ? ImagePageFit.A4 : ImagePageFit.ImageSize;

        SetBusy(_localization["busy_converting"]);
        var folder = Path.Combine(FileSystem.CacheDirectory, "tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var file in list)
        {
            var path = Path.Combine(folder, file.FileName);
            await using var source = await file.OpenReadAsync();
            await using var target = File.Create(path);
            await source.CopyToAsync(target);
            paths.Add(path);
        }

        var output = OutputPath("images");
        await _tools.ImagesToPdfAsync(paths, fit, output, Progress());
        var name = list.Count == 1 ? Path.GetFileNameWithoutExtension(list[0].FileName) : _localization["images_document_name"];
        await FinishAsync(output, _localization.Format("result_images", list.Count), $"{name}.pdf");
    }

    private async Task PdfToImagesAsync()
    {
        var entry = await PickOneAsync("tool_pdf_to_images", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        SetBusy(_localization["busy_rendering"]);
        var folder = Path.Combine(FileSystem.CacheDirectory, "tools", Guid.NewGuid().ToString("N"));
        var baseName = Path.GetFileNameWithoutExtension(entry.DisplayName);
        using (var document = await _renderer.OpenAsync(input.Path, input.Password))
        {
            await _tools.PdfToImagesAsync(document, folder, baseName, 1654, Progress());
        }

        var zip = Path.Combine(FileSystem.CacheDirectory, "tools", $"{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        SetBusy(false);

        await _export.SaveAsAsync(zip, $"{baseName}.zip", "application/zip");
    }

    private async Task ProtectAsync()
    {
        var entry = await PickOneAsync("tool_protect", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        var password = await SocShared.ModernDialog.PromptAsync(this, _localization["tool_protect"], _localization["protect_prompt"],
            _localization["ok"], _localization["cancel"]);
        if (string.IsNullOrEmpty(password))
            return;

        SetBusy(_localization["busy_working"]);
        var output = OutputPath("protected");
        await _tools.ProtectAsync(input, password, null, output);
        await FinishAsync(output, _localization["result_protected"], Suffix(entry, "protected"), password);
    }

    private async Task UnprotectAsync()
    {
        var entry = await PickOneAsync("tool_unprotect", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        SetBusy(_localization["busy_working"]);
        var output = OutputPath("unprotected");
        await _tools.UnprotectAsync(input, output);
        await FinishAsync(output, _localization["result_unprotected"], Suffix(entry, "unprotected"));
    }

    private async Task PageNumbersAsync()
    {
        var entry = await PickOneAsync("tool_page_numbers", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        var choice = await SocShared.ModernDialog.ActionSheetAsync(this, _localization["numbers_position_prompt"], _localization["cancel"],
            _localization["numbers_bottom_center"], _localization["numbers_bottom_right"], _localization["numbers_top_right"]);
        if (choice is null)
            return;

        var position = choice == _localization["numbers_bottom_right"] ? NumberPosition.BottomRight
            : choice == _localization["numbers_top_right"] ? NumberPosition.TopRight
            : NumberPosition.BottomCenter;

        SetBusy(_localization["busy_working"]);
        var output = OutputPath("numbered");
        await _tools.AddPageNumbersAsync(input, position, output);
        await FinishAsync(output, _localization["result_numbered"], Suffix(entry, "numbered"));
    }

    private async Task WatermarkAsync()
    {
        var entry = await PickOneAsync("tool_watermark", "pick_one_hint");
        if (entry is null)
            return;

        var input = await InputWithPasswordAsync(entry);
        if (input is null)
            return;

        var text = await SocShared.ModernDialog.PromptAsync(this, _localization["tool_watermark"], _localization["watermark_prompt"],
            _localization["ok"], _localization["cancel"], placeholder: _localization["watermark_placeholder"]);
        if (string.IsNullOrWhiteSpace(text))
            return;

        SetBusy(_localization["busy_working"]);
        var output = OutputPath("watermarked");
        await _tools.WatermarkAsync(input, text.Trim(), 0.25, output);
        await FinishAsync(output, _localization["result_watermarked"], Suffix(entry, "watermark"));
    }

    // ---------------------------------------------------------------------
    //  Piezas comunes
    // ---------------------------------------------------------------------

    private Task<IReadOnlyList<PdfDocumentEntry>?> PickAsync(string titleKey, string hintKey, bool multiple) =>
        DocumentPickerPage.PickAsync(this, _library, _localization, _localization[titleKey], _localization[hintKey], multiple);

    private async Task<PdfDocumentEntry?> PickOneAsync(string titleKey, string hintKey)
    {
        var picked = await PickAsync(titleKey, hintKey, multiple: false);
        return picked is { Count: > 0 } ? picked[0] : null;
    }

    /// <summary>The input for a tool, asking for the password until PDFsharp accepts it; null when the user gives up.</summary>
    private async Task<PdfInput?> InputWithPasswordAsync(PdfDocumentEntry entry)
    {
        var path = _library.GetFilePath(entry);
        if (!File.Exists(path))
            throw new FileNotFoundException(_localization["error_missing_file"], path);

        string? password = null;
        var wrong = false;
        while (true)
        {
            var input = new PdfInput(path, password);
            try
            {
                await _tools.GetPageCountAsync(input);
                return input;
            }
            catch (PdfOpenException ex) when (ex.Failure is PdfOpenFailure.PasswordProtected or PdfOpenFailure.WrongPassword)
            {
                password = await PasswordPromptPage.AskAsync(this, _localization, entry.DisplayName, wrong);
                if (password is null)
                    return null;
                wrong = true;
            }
        }
    }

    private static string OutputPath(string stem) =>
        Path.Combine(FileSystem.CacheDirectory, "tools", $"{stem}-{Guid.NewGuid():N}.pdf");

    private static string Suffix(PdfDocumentEntry entry, string suffix) =>
        $"{Path.GetFileNameWithoutExtension(entry.DisplayName)}-{suffix}.pdf";

    private async Task<PdfDocumentEntry> ImportResultAsync(string outputPath, string displayName)
    {
        PdfDocumentEntry entry;
        await using (var stream = File.OpenRead(outputPath))
        {
            entry = await _library.ImportAsync(stream, displayName);
        }

        try
        {
            var pageCount = await _tools.GetPageCountAsync(new PdfInput(outputPath));
            await _library.SetPageCountAsync(entry, pageCount);
        }
        catch (PdfOpenException)
        {
            // A protected result: the count is filled in when the reader opens it.
        }

        File.Delete(outputPath);
        return entry;
    }

    /// <summary>Puts the result in the library, tells the user and opens it in the reader.</summary>
    private async Task FinishAsync(string outputPath, string message, string? displayName = null, string? password = null)
    {
        var entry = await ImportResultAsync(outputPath, displayName ?? $"{Path.GetFileNameWithoutExtension(outputPath).Split('-')[0]}.pdf");
        SetBusy(false);
        await AlertAsync(_localization["done"], message);

        var reader = ActivatorUtilities.CreateInstance<ReaderPage>(_services, entry);
        reader.InitialPassword = password;
        Navigation.InsertPageBefore(reader, this);
        await Navigation.PopAsync();
        DropOtherReaders(reader);
    }

    private IProgress<double> Progress() => new Progress<double>(value => BusyProgress.Progress = value);

    private void SetBusy(string label)
    {
        BusyLabel.Text = label;
        BusyProgress.Progress = 0;
        BusyOverlay.IsVisible = true;
    }

    private void SetBusy(bool busy) => BusyOverlay.IsVisible = busy;

    private Task AlertAsync(string title, string message) =>
        SocShared.ModernDialog.AlertAsync(this, title, message, _localization["ok"]);

    /// <summary>One reader on the stack at a time: each keeps its page bitmaps, and a chain of them eats the memory.</summary>
    private void DropOtherReaders(ReaderPage keep)
    {
        foreach (var previous in Navigation.NavigationStack.OfType<ReaderPage>().Where(page => !ReferenceEquals(page, keep)).ToList())
            Navigation.RemovePage(previous);
    }
}
