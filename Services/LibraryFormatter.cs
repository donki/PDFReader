using System.Globalization;
using PDFReader.Models;

namespace PDFReader.Services;

/// <summary>
/// Texts of a library row (pages, size, when it was last opened), apart from the page so they can
/// be tested without the interface (constitucion, General 8.6). Formats follow the current culture.
/// </summary>
public static class LibraryFormatter
{
    /// <summary>Page count and size, for example "12 pág. · 2,4 MB"; only the size while the page count is unknown.</summary>
    public static string Details(PdfDocumentEntry entry, ILocalizationService localization)
    {
        var size = Size(entry.SizeBytes);

        if (entry.PageCount <= 0)
            return size;

        var pages = entry.PageCount == 1
            ? localization["page_count_one"]
            : localization.Format("page_count", entry.PageCount);

        return $"{pages} · {size}";
    }

    /// <summary>Size in MB with one decimal from 1 MB up, otherwise in whole KB (never below 1 KB).</summary>
    public static string Size(long bytes)
    {
        const long megabyte = 1024 * 1024;

        if (bytes >= megabyte)
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", (double)bytes / megabyte);

        return string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Max(1, bytes / 1024d));
    }

    /// <summary>"Today 10:30", "Yesterday 18:05" or the short date, relative to <paramref name="nowLocal"/>.</summary>
    public static string LastOpened(DateTime lastOpenedUtc, DateTime nowLocal, ILocalizationService localization)
    {
        var local = lastOpenedUtc.ToLocalTime();
        var today = nowLocal.Date;

        if (local.Date == today)
            return $"{localization["last_opened_today"]} {local:t}";

        if (local.Date == today.AddDays(-1))
            return $"{localization["last_opened_yesterday"]} {local:t}";

        return local.ToString("d", CultureInfo.CurrentCulture);
    }
}
