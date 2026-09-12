using Android.Content;
using PDFReader.Services;

namespace PDFReader.Platforms.Android;

/// <summary>
/// ACTION_CREATE_DOCUMENT: el usuario elige donde guardar (Descargas, Drive, una tarjeta…) y el
/// sistema devuelve un URI escribible. Sin permisos de almacenamiento, como todo lo demas.
/// </summary>
public sealed class AndroidFileExportService : IFileExportService
{
    public async Task<bool> SaveAsAsync(string sourcePath, string suggestedName, string mimeType)
    {
        var activity = Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException("No activity is available.");

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraTitle, suggestedName);

        var uri = await activity.StartForUriResultAsync(intent);
        if (uri is null)
            return false;

        await using var source = File.OpenRead(sourcePath);
        using var destination = activity.ContentResolver?.OpenOutputStream(uri, "wt")
            ?? throw new IOException("Could not open the destination for writing.");
        await source.CopyToAsync(destination);
        return true;
    }
}
