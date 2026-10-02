namespace PDFReader.Services;

/// <summary>
/// PDF que llegan de fuera de la app: «Abrir con» desde otra app en Android (intent ACTION_VIEW)
/// o desde el Explorador en Windows (argumento de la linea de ordenes). Se copian a la cache y se
/// encolan para la biblioteca, que puede no estar aun en pantalla. MainActivity y la App de
/// Windows solo sacan de su plataforma el contenido y el nombre, y llaman aqui.
/// </summary>
public static class IncomingDocuments
{
    public const string FallbackName = "document.pdf";

    /// <summary>Carpeta de la cache donde se dejan las copias.</summary>
    public static string CacheFolder(string cacheRoot) => Path.Combine(cacheRoot, "incoming");

    /// <summary>
    /// Nombre que se ve en la biblioteca: el que da el proveedor; si no, el ultimo trozo de la
    /// URI; si tampoco, «document.pdf».
    /// </summary>
    public static string DisplayName(string? providerName, string? lastPathSegment)
    {
        if (!string.IsNullOrWhiteSpace(providerName))
            return providerName;
        if (!string.IsNullOrWhiteSpace(lastPathSegment))
            return Path.GetFileName(lastPathSegment);
        return FallbackName;
    }

    /// <summary>Los PDF que existen entre los argumentos de la linea de ordenes (sin el programa).</summary>
    public static IReadOnlyList<string> PdfArguments(IEnumerable<string> commandLine) =>
        commandLine.Skip(1)
            .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            .ToList();

    /// <summary>
    /// Copia el contenido a un fichero nuevo de la cache y lo encola. Devuelve la copia, o null si
    /// no se pudo leer (el error se cuenta a <paramref name="onError"/>: aun no hay pagina en la
    /// que avisar).
    /// </summary>
    public static string? CopyAndQueue(Func<Stream?> open, string displayName, string cacheFolder,
        PendingDocumentQueue queue, Action<Exception>? onError = null)
    {
        try
        {
            using var source = open() ?? throw new IOException($"Could not read {displayName}.");

            Directory.CreateDirectory(cacheFolder);
            var temporaryPath = Path.Combine(cacheFolder, $"{Guid.NewGuid():N}.pdf");
            using (var destination = File.Create(temporaryPath))
            {
                source.CopyTo(destination);
            }

            queue.Enqueue(new PendingDocument(temporaryPath, displayName));
            return temporaryPath;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return null;
        }
    }
}
