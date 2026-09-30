// Dobles en memoria de lo que la app usa de Microsoft.Maui.Storage, con las mismas firmas, para
// probar LibraryService y LocalizationService sin MAUI ni dispositivo.
namespace Microsoft.Maui.Storage;

public static class FileSystem
{
    /// <summary>Cada prueba apunta la carpeta de datos a su propia carpeta temporal.</summary>
    public static string AppDataDirectory { get; set; } = Path.GetTempPath();
}

public sealed class InMemoryPreferences
{
    private readonly Dictionary<string, object?> _store = new();

    public void Clear() => _store.Clear();

    public T Get<T>(string key, T defaultValue) =>
        _store.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

    public void Set<T>(string key, T value) => _store[key] = value;
}

public static class Preferences
{
    public static InMemoryPreferences Default { get; } = new();
}
