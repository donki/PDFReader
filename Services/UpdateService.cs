using System.Text.Json;
using System.Text.Json.Serialization;

namespace PDFReader.Services;

/// <summary>
/// Comprobacion de version al arrancar (constitucion, seccion 15): consulta un manifiesto en el
/// propio repositorio del proyecto (fuente de confianza) y, si hay una version mas reciente que la
/// instalada, avisa al usuario y le propone actualizar. Es silenciosa y no bloqueante: si no hay red
/// o ya se esta al dia, no molesta. Los textos se resuelven con el servicio de localizacion.
/// </summary>
public class UpdateService
{
    private const string AppcastUrl = "https://raw.githubusercontent.com/donki/PDFReader/main/appcast.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly ILocalizationService _localization;
    private bool _checkedThisSession;

    public UpdateService(ILocalizationService localization) => _localization = localization;

    public async Task CheckAndPromptAsync(Page page)
    {
        if (_checkedThisSession)
            return;
        _checkedThisSession = true;

        try
        {
            var json = await Http.GetStringAsync(AppcastUrl);
            var manifest = JsonSerializer.Deserialize<Appcast>(json);
            if (manifest?.Version is null)
                return;

            var current = AppInfo.Current.VersionString;
            if (CompareVersions(manifest.Version, current) <= 0)
                return; // ya se esta en la ultima version (o mas nueva)

            var wantsUpdate = await page.DisplayAlertAsync(
                _localization["update_available_title"],
                _localization.Format("update_available_message", manifest.Version, current),
                _localization["update_now"],
                _localization["update_later"]);

            if (wantsUpdate && !string.IsNullOrWhiteSpace(manifest.Url))
                await Browser.Default.OpenAsync(new Uri(manifest.Url), BrowserLaunchMode.SystemPreferred);
        }
        catch
        {
            // Sin red o manifiesto no disponible: la comprobacion no debe molestar ni bloquear.
        }
    }

    /// <summary>Compara versiones numericas por partes ("2026.07.19.0"). &gt;0 si a es mas nueva que b.</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }

    private static int[] Parts(string v) =>
        v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

    private sealed class Appcast
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
