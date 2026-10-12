namespace PDFReader.Services;

/// <summary>
/// Compartir el documento abierto con otra app. La biblioteca guarda cada PDF con su id como
/// nombre; quien lo reciba debe ver el nombre de verdad, asi que se comparte una copia con ese
/// nombre en una carpeta de la cache que se vacia en cada envio.
/// </summary>
public static class DocumentSharing
{
    public const string FallbackName = "document.pdf";

    public const string ContentType = "application/pdf";

    /// <summary>Carpeta de la cache donde se dejan las copias para compartir.</summary>
    public static string CacheFolder(string cacheRoot) => Path.Combine(cacheRoot, "share");

    /// <summary>Nombre del fichero compartido: el que ve el usuario, valido como fichero y acabado en .pdf.</summary>
    public static string FileName(string? displayName)
    {
        // El ultimo trozo a mano: Path.GetFileName en Windows toma «a:» por una unidad.
        var name = (displayName ?? string.Empty).Split('/', '\\')[^1];
        foreach (var invalid in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(invalid, '_');

        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name))
            return FallbackName;

        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
    }

    /// <summary>Copia el PDF a la carpeta de compartir con su nombre y devuelve la ruta de la copia.</summary>
    public static async Task<string> PrepareAsync(string sourcePath, string? displayName, string cacheRoot)
    {
        var folder = CacheFolder(cacheRoot);

        // Lo enviado antes ya no hace falta: la app que lo recibio se lleva su copia.
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, FileName(displayName));
        await using (var source = File.OpenRead(sourcePath))
        await using (var copy = File.Create(target))
            await source.CopyToAsync(copy);

        return target;
    }
}
