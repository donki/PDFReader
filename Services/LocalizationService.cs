using System.Globalization;
using Microsoft.Maui.Storage;

namespace PDFReader.Services;

/// <summary>
/// In-memory catalogue of the Spanish and English strings. English is the fallback language
/// for any device whose OS language is not Spanish (constitucion, seccion 15).
/// </summary>
public class LocalizationService : ILocalizationService
{
    private const string PreferenceKey = "app_language";
    private const string Spanish = "es";
    private const string English = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _catalogue;
    private string _currentLanguage = English;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    public string this[string key] => Get(key);

    public LocalizationService()
    {
        _catalogue = new Dictionary<string, Dictionary<string, string>>
        {
            [Spanish] = BuildSpanish(),
            [English] = BuildEnglish()
        };
    }

    public void Initialize()
    {
        var saved = Preferences.Default.Get(PreferenceKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(saved) && _catalogue.ContainsKey(saved))
        {
            _currentLanguage = saved;
            return;
        }

        var deviceLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        _currentLanguage = deviceLanguage == Spanish ? Spanish : English;
    }

    public void SetLanguage(string languageCode)
    {
        if (!_catalogue.ContainsKey(languageCode) || _currentLanguage == languageCode)
            return;

        _currentLanguage = languageCode;
        Preferences.Default.Set(PreferenceKey, languageCode);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        if (_catalogue[_currentLanguage].TryGetValue(key, out var value))
            return value;

        // Fall back to English before surfacing the raw key, so a missing translation
        // degrades into readable text instead of a silent blank (constitucion, seccion 17).
        if (_catalogue[English].TryGetValue(key, out var fallback))
            return fallback;

        return key;
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static Dictionary<string, string> BuildSpanish() => new()
    {
        ["app_name"] = "PDF Reader",
        ["app_tagline"] = "Tus documentos, solo en tu dispositivo",

        // Biblioteca
        ["library_title"] = "Biblioteca",
        ["open_pdf"] = "Abrir un PDF",
        ["empty_title"] = "Todavía no hay documentos",
        ["empty_hint"] = "Abre un PDF de tu dispositivo y aparecerá aquí para volver a leerlo cuando quieras.",
        ["recent_documents"] = "Documentos recientes",
        ["about"] = "Acerca de",
        ["importing"] = "Abriendo documento…",
        ["remove_title"] = "Quitar documento",
        ["remove_message"] = "Se eliminará «{0}» de la biblioteca y del almacenamiento de la aplicación. El archivo original de tu dispositivo no se toca.",
        ["remove"] = "Quitar",
        ["cancel"] = "Cancelar",
        ["page_count"] = "{0} pág.",
        ["page_count_one"] = "1 pág.",
        ["last_opened_today"] = "Hoy",
        ["last_opened_yesterday"] = "Ayer",

        // Lector
        ["reader_page_of"] = "{0} / {1}",
        ["loading_page"] = "Cargando página…",
        ["goto_title"] = "Ir a la página",
        ["goto_message"] = "Introduce un número entre 1 y {0}",
        ["goto_placeholder"] = "Número de página",
        ["go"] = "Ir",
        ["invalid_page_title"] = "Página no válida",
        ["invalid_page_message"] = "Introduce un número entre 1 y {0}.",
        ["previous_page"] = "Anterior",
        ["next_page"] = "Siguiente",
        ["zoom_in"] = "Acercar",
        ["zoom_out"] = "Alejar",
        ["zoom_reset"] = "Ajustar",

        // Contraseña
        ["password_title"] = "PDF protegido",
        ["password_message"] = "Introduce la contraseña de «{0}».",
        ["password_wrong"] = "La contraseña no es correcta. Inténtalo de nuevo.",
        ["open"] = "Abrir",

        // Búsqueda
        ["search"] = "Buscar",
        ["search_placeholder"] = "Buscar en el documento",
        ["searching"] = "Buscando…",
        ["search_no_results"] = "Sin resultados para «{0}»",
        ["search_match_of"] = "{0} de {1}",
        ["search_close"] = "Cerrar la búsqueda",
        ["search_truncated"] = "Se muestran los primeros {0} resultados.",

        // Errores
        ["error"] = "Error",
        ["ok"] = "OK",
        ["error_open_title"] = "No se pudo abrir el PDF",
        ["error_not_pdf"] = "El archivo seleccionado no es un PDF válido o está dañado.",
        ["error_protected"] = "Este PDF está protegido con contraseña.",
        ["error_password_unsupported"] = "Abrir un PDF protegido con contraseña requiere Android 15 o posterior.",
        ["error_search_unsupported"] = "La búsqueda de texto requiere Android 15 o posterior.",
        ["error_render"] = "No se pudo mostrar esta página: {0}",
        ["error_import"] = "No se pudo copiar el documento: {0}",
        ["error_library"] = "No se pudo leer la biblioteca: {0}",
        ["error_remove"] = "No se pudo quitar el documento: {0}",
        ["error_missing_file"] = "El documento ya no está disponible y se ha quitado de la biblioteca.",

        // Acerca de
        ["about_title"] = "Acerca de",
        ["version"] = "Versión {0}",
        ["contact_title"] = "Contacto",
        ["contact_hint"] = "Toca para enviar un correo electrónico",
        ["support_title"] = "Apoya el Desarrollo",
        ["support_button"] = "Ko-fi.com - Invítame un café",
        ["support_hint"] = "Tu apoyo ayuda a mantener y mejorar la aplicación",
        ["language_title"] = "Idioma",
        ["language_hint"] = "Selecciona tu idioma preferido",
        ["privacy_title"] = "Privacidad",
        ["privacy_text"] = "Esta aplicación no recopila tus datos personales ni los envía a los desarrolladores. La información se procesa en tu dispositivo para la función propia de la app.",
        ["license_title"] = "Licencia",
        ["license_text"] = "Esta aplicación es software libre distribuido bajo licencia MIT.",
        ["license_copyright"] = "MIT License · Copyright © 2026 Socratic",
        ["legal_title"] = "Aviso Legal",
        ["legal_text_1"] = "Este software se proporciona «tal cual», sin garantías de ningún tipo. El usuario es responsable del uso adecuado de la aplicación y del cumplimiento de las leyes locales.",
        ["legal_text_2"] = "En ningún caso los autores serán responsables de daños directos, indirectos, incidentales o consecuentes que resulten del uso de este software.",
        ["legal_warning"] = "⚠️ Uso bajo su propio riesgo",
        ["back"] = "← Volver",
        ["email_subject"] = "Contacto desde PDF Reader",
        ["email_body"] = "Hola,\n\nMe pongo en contacto desde la aplicación {0} (versión {1}).\n\n[Escribe tu mensaje aquí]\n\nSaludos.",
        ["email_chooser"] = "Enviar email con:",
        ["error_no_email"] = "No hay ninguna aplicación de correo disponible en este dispositivo.",
        ["error_email"] = "No se pudo abrir el cliente de correo: {0}",
        ["browser_unavailable_title"] = "Navegador no disponible",
        ["browser_copied"] = "Enlace copiado al portapapeles:\n{0}",
        ["error_browser"] = "No se pudo abrir el navegador: {0}"
    };

    private static Dictionary<string, string> BuildEnglish() => new()
    {
        ["app_name"] = "PDF Reader",
        ["app_tagline"] = "Your documents, only on your device",

        // Library
        ["library_title"] = "Library",
        ["open_pdf"] = "Open a PDF",
        ["empty_title"] = "No documents yet",
        ["empty_hint"] = "Open a PDF from your device and it will show up here, ready to read again any time.",
        ["recent_documents"] = "Recent documents",
        ["about"] = "About",
        ["importing"] = "Opening document…",
        ["remove_title"] = "Remove document",
        ["remove_message"] = "\"{0}\" will be removed from the library and from the app storage. The original file on your device is left untouched.",
        ["remove"] = "Remove",
        ["cancel"] = "Cancel",
        ["page_count"] = "{0} pages",
        ["page_count_one"] = "1 page",
        ["last_opened_today"] = "Today",
        ["last_opened_yesterday"] = "Yesterday",

        // Reader
        ["reader_page_of"] = "{0} / {1}",
        ["loading_page"] = "Loading page…",
        ["goto_title"] = "Go to page",
        ["goto_message"] = "Enter a number between 1 and {0}",
        ["goto_placeholder"] = "Page number",
        ["go"] = "Go",
        ["invalid_page_title"] = "Invalid page",
        ["invalid_page_message"] = "Enter a number between 1 and {0}.",
        ["previous_page"] = "Previous",
        ["next_page"] = "Next",
        ["zoom_in"] = "Zoom in",
        ["zoom_out"] = "Zoom out",
        ["zoom_reset"] = "Fit",

        // Password
        ["password_title"] = "Protected PDF",
        ["password_message"] = "Enter the password for \"{0}\".",
        ["password_wrong"] = "That password is not correct. Try again.",
        ["open"] = "Open",

        // Search
        ["search"] = "Search",
        ["search_placeholder"] = "Search in the document",
        ["searching"] = "Searching…",
        ["search_no_results"] = "No results for \"{0}\"",
        ["search_match_of"] = "{0} of {1}",
        ["search_close"] = "Close search",
        ["search_truncated"] = "Showing the first {0} results.",

        // Errors
        ["error"] = "Error",
        ["ok"] = "OK",
        ["error_open_title"] = "Could not open the PDF",
        ["error_not_pdf"] = "The selected file is not a valid PDF, or it is damaged.",
        ["error_protected"] = "This PDF is password protected.",
        ["error_password_unsupported"] = "Opening a password protected PDF requires Android 15 or later.",
        ["error_search_unsupported"] = "Text search requires Android 15 or later.",
        ["error_render"] = "Could not display this page: {0}",
        ["error_import"] = "Could not copy the document: {0}",
        ["error_library"] = "Could not read the library: {0}",
        ["error_remove"] = "Could not remove the document: {0}",
        ["error_missing_file"] = "The document is no longer available and was removed from the library.",

        // About
        ["about_title"] = "About",
        ["version"] = "Version {0}",
        ["contact_title"] = "Contact",
        ["contact_hint"] = "Tap to send an email",
        ["support_title"] = "Support Development",
        ["support_button"] = "Ko-fi.com - Buy me a coffee",
        ["support_hint"] = "Your support helps maintain and improve the app",
        ["language_title"] = "Language",
        ["language_hint"] = "Select your preferred language",
        ["privacy_title"] = "Privacy",
        ["privacy_text"] = "This app does not collect your personal data or send it to the developers. Information is processed on your device for the app's own purpose.",
        ["license_title"] = "License",
        ["license_text"] = "This app is free software distributed under the MIT license.",
        ["license_copyright"] = "MIT License · Copyright © 2026 Socratic",
        ["legal_title"] = "Legal Notice",
        ["legal_text_1"] = "This software is provided 'as is', without warranty of any kind. The user is responsible for proper use of the app and compliance with local laws.",
        ["legal_text_2"] = "In no event shall the authors be liable for any direct, indirect, incidental or consequential damages arising from the use of this software.",
        ["legal_warning"] = "⚠️ Use at your own risk",
        ["back"] = "← Back",
        ["email_subject"] = "Contact from PDF Reader",
        ["email_body"] = "Hello,\n\nI'm contacting you from the {0} app (version {1}).\n\n[Write your message here]\n\nBest regards.",
        ["email_chooser"] = "Send email with:",
        ["error_no_email"] = "No email app is available on this device.",
        ["error_email"] = "Could not open the email client: {0}",
        ["browser_unavailable_title"] = "Browser not available",
        ["browser_copied"] = "Link copied to clipboard:\n{0}",
        ["error_browser"] = "Could not open the browser: {0}"
    };
}
