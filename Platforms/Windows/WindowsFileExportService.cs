using PDFReader.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PDFReader.Platforms.Windows;

/// <summary>«Guardar como» de Windows. El selector necesita el HWND de la ventana para colgar de ella.</summary>
public sealed class WindowsFileExportService : IFileExportService
{
    public async Task<bool> SaveAsAsync(string sourcePath, string suggestedName, string mimeType)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };

        var extension = Path.GetExtension(suggestedName);
        if (string.IsNullOrEmpty(extension))
            extension = ".pdf";
        picker.FileTypeChoices.Add(DescribeType(mimeType), [extension]);

        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var target = await picker.PickSaveFileAsync();
        if (target is null)
            return false;

        await using var source = File.OpenRead(sourcePath);
        await using var destination = await target.OpenStreamForWriteAsync();
        destination.SetLength(0);
        await source.CopyToAsync(destination);
        return true;
    }

    private static string DescribeType(string mimeType) => mimeType switch
    {
        "application/pdf" => "PDF",
        "image/png" => "PNG",
        "application/zip" => "ZIP",
        _ => "File",
    };
}
