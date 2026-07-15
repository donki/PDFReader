namespace PDFReader.Services;

/// <summary>
/// Provides the user-visible strings of the application in the supported languages.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Two letter code of the language in use ("es" or "en").</summary>
    string CurrentLanguage { get; }

    /// <summary>Raised after <see cref="SetLanguage"/> changes the language.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Resolves the language from the saved preference, falling back to the OS language and then to English.</summary>
    void Initialize();

    /// <summary>Persists <paramref name="languageCode"/> as the language preference.</summary>
    void SetLanguage(string languageCode);

    /// <summary>Returns the string for <paramref name="key"/> in the current language.</summary>
    string Get(string key);

    /// <summary>Returns the string for <paramref name="key"/> with <paramref name="args"/> applied.</summary>
    string Format(string key, params object[] args);

    /// <summary>Indexer shorthand for <see cref="Get"/>.</summary>
    string this[string key] { get; }
}
