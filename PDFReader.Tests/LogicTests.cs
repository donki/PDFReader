using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Maui.Storage;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.Tests;

public class PdfPageMathTests
{
    [Theory]
    [InlineData(100, 200, 2.0)]
    [InlineData(200, 100, 0.5)]
    [InlineData(0, 100, PdfPageMath.FallbackAspectRatio)]
    [InlineData(-5, 100, PdfPageMath.FallbackAspectRatio)]
    public void AspectRatio(double width, double height, double expected) =>
        Assert.Equal(expected, PdfPageMath.AspectRatio(width, height), 6);

    [Fact]
    public void ScalePage_KeepsTheRequestedWidthAndThePageProportions()
    {
        // A4 en puntos (595 x 842) a 1080 px de ancho.
        Assert.Equal((1080, 1528), PdfPageMath.ScalePage(595, 842, 1080));
    }

    [Theory]
    [InlineData(10, PdfPageMath.MinWidthPixels)]
    [InlineData(0, PdfPageMath.MinWidthPixels)]
    [InlineData(-100, PdfPageMath.MinWidthPixels)]
    [InlineData(99999, PdfPageMath.MaxWidthPixels)]
    public void ScalePage_ClampsTheWidth(int requested, int expectedWidth)
    {
        var (width, height) = PdfPageMath.ScalePage(100, 100, requested);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedWidth, height);
    }

    [Fact]
    public void ScalePage_PageWithoutWidth_UsesA4Proportions() =>
        Assert.Equal((1000, 1414), PdfPageMath.ScalePage(0, 0, 1000));

    [Fact]
    public void ScalePage_HugeBitmap_IsShrunkBelowThePixelBudget_KeepingProportions()
    {
        // Pagina muy alargada (1:10) a 3000 px: 3000 x 30000 = 90 Mpx, muy por encima de 12 Mpx.
        var (width, height) = PdfPageMath.ScalePage(100, 1000, 3000);

        Assert.True((long)width * height <= PdfPageMath.MaxPixels);
        Assert.Equal(10.0, (double)height / width, 1);
        Assert.Equal(1095, width);
    }

    [Theory]
    [InlineData(1, 1000, 3000)]
    [InlineData(1, 5000, 1080)]
    [InlineData(3, 14400, 200)]
    public void ScalePage_ExtremelyElongatedPage_NeverExceedsThePixelBudget(int pageWidth, int pageHeight, int target)
    {
        // Con el ancho ya en el minimo, el alto seguia por encima del presupuesto (200 x 109544 px,
        // 88 MB en ARGB) y el mapa de bits podia agotar la memoria.
        var (width, height) = PdfPageMath.ScalePage(pageWidth, pageHeight, target);

        Assert.Equal(PdfPageMath.MinWidthPixels, width);
        Assert.True((long)width * height <= PdfPageMath.MaxPixels, $"{width} x {height}");
        Assert.Equal(PdfPageMath.MaxPixels / width, height);
    }

    [Fact]
    public void ScalePage_ExactlyAtTheBudget_IsNotShrunk() =>
        Assert.Equal((3000, 4000), PdfPageMath.ScalePage(3, 4, 3000));

    [Fact]
    public void ScalePage_VeryWidePage_KeepsAtLeastOnePixelHigh() =>
        Assert.Equal((200, 1), PdfPageMath.ScalePage(100000, 1, 200));

    [Fact]
    public void NormalizeMatch_SingleRect_IsDividedByThePageSize()
    {
        var match = PdfPageMath.NormalizeMatch(3, new[] { (10f, 20f, 60f, 40f) }, 100, 200);

        Assert.Equal(new PdfTextMatch(3, 0.1, 0.1, 0.6, 0.2), match);
    }

    [Fact]
    public void NormalizeMatch_WrappedHit_IsTheUnionOfItsRects()
    {
        var rects = new[] { (70f, 10f, 100f, 20f), (0f, 22f, 30f, 32f) };

        var match = PdfPageMath.NormalizeMatch(0, rects, 100, 100)!;

        Assert.Equal(0.0, match.Left, 6);
        Assert.Equal(0.10, match.Top, 6);
        Assert.Equal(1.0, match.Right, 6);
        Assert.Equal(0.32, match.Bottom, 6);
    }

    [Fact]
    public void NormalizeMatch_NoRects_IsNull() =>
        Assert.Null(PdfPageMath.NormalizeMatch(0, Array.Empty<(float, float, float, float)>(), 100, 100));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, -1)]
    public void NormalizeMatch_PageWithoutSize_IsNull(double width, double height) =>
        Assert.Null(PdfPageMath.NormalizeMatch(0, new[] { (1f, 1f, 2f, 2f) }, width, height));
}

public sealed class LocalizationServiceTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public LocalizationServiceTests() => Preferences.Default.Clear();

    public void Dispose()
    {
        Preferences.Default.Clear();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    private static Dictionary<string, string> Catalogue(string builder) =>
        (Dictionary<string, string>)typeof(LocalizationService)
            .GetMethod(builder, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

    [Fact]
    public void SpanishAndEnglish_HaveExactlyTheSameKeys()
    {
        var es = Catalogue("BuildSpanish").Keys.ToHashSet();
        var en = Catalogue("BuildEnglish").Keys.ToHashSet();

        Assert.Empty(es.Except(en));
        Assert.Empty(en.Except(es));
        Assert.True(es.Count > 40);
    }

    [Fact]
    public void Translations_AreNotEmpty_AndKeepTheSamePlaceholders()
    {
        var es = Catalogue("BuildSpanish");
        var en = Catalogue("BuildEnglish");
        var hole = new Regex(@"\{\d+(:[^}]*)?\}");

        foreach (var (key, english) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), $"en:{key}");
            Assert.False(string.IsNullOrWhiteSpace(es[key]), $"es:{key}");
            Assert.True(
                hole.Matches(english).Select(m => m.Value).Order().SequenceEqual(hole.Matches(es[key]).Select(m => m.Value).Order()),
                $"Placeholders differ for {key}");
        }
    }

    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    public void Initialize_WithoutPreference_FollowsTheDeviceOrFallsBackToEnglish(string device, string expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(device);
        var l = new LocalizationService();

        l.Initialize();

        Assert.Equal(expected, l.CurrentLanguage);
    }

    [Fact]
    public void Initialize_SavedPreferenceWins()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Preferences.Default.Set("app_language", "es");
        var l = new LocalizationService();

        l.Initialize();

        Assert.Equal("es", l.CurrentLanguage);
    }

    [Fact]
    public void Initialize_UnknownSavedPreference_IsIgnored()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
        Preferences.Default.Set("app_language", "de");
        var l = new LocalizationService();

        l.Initialize();

        Assert.Equal("es", l.CurrentLanguage);
    }

    [Fact]
    public void SetLanguage_SavesAndNotifies_OnlyForRealChanges()
    {
        var l = new LocalizationService();
        var raised = 0;
        l.LanguageChanged += (_, _) => raised++;

        l.SetLanguage("en"); // ya es el idioma
        l.SetLanguage("xx"); // no soportado
        l.SetLanguage("es");

        Assert.Equal(1, raised);
        Assert.Equal("es", l.CurrentLanguage);
        Assert.Equal("es", Preferences.Default.Get("app_language", ""));
    }

    [Fact]
    public void Get_TranslatesAndFallsBackToTheKey()
    {
        var l = new LocalizationService();
        l.SetLanguage("es");

        Assert.Equal(Catalogue("BuildSpanish")["library_title"], l["library_title"]);
        Assert.Equal(l.Get("library_title"), l["library_title"]);
        Assert.Equal("no_such_key", l.Get("no_such_key"));
    }

    [Fact]
    public void Get_MissingSpanishKey_FallsBackToEnglish()
    {
        var l = new LocalizationService();
        l.SetLanguage("es");
        var catalogue = (Dictionary<string, Dictionary<string, string>>)typeof(LocalizationService)
            .GetField("_catalogue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(l)!;
        catalogue["es"].Remove("library_title");

        Assert.Equal(catalogue["en"]["library_title"], l["library_title"]);
    }

    [Fact]
    public void Format_AppliesArgumentsWithTheCurrentCulture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        var l = new LocalizationService();

        Assert.Equal("3 / 10", l.Format("reader_page_of", 3, 10));
    }
}

public sealed class LibraryFormatterTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly LocalizationService _l = new();

    public LibraryFormatterTests()
    {
        Preferences.Default.Clear();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        Preferences.Default.Clear();
    }

    [Theory]
    [InlineData(0, "1 KB")]
    [InlineData(100, "1 KB")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(1048575, "1024 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(2621440, "2.5 MB")]
    public void Size(long bytes, string expected) =>
        Assert.Equal(expected, LibraryFormatter.Size(bytes));

    [Fact]
    public void Size_UsesTheCultureDecimalSeparator()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        Assert.Equal("2,5 MB", LibraryFormatter.Size(2621440));
    }

    [Fact]
    public void Details_UnknownPageCount_IsOnlyTheSize() =>
        Assert.Equal("2.5 MB", LibraryFormatter.Details(new PdfDocumentEntry { SizeBytes = 2621440 }, _l));

    [Fact]
    public void Details_OnePage_UsesTheSingular() =>
        Assert.Equal($"{_l["page_count_one"]} · 1 KB", LibraryFormatter.Details(new PdfDocumentEntry { PageCount = 1, SizeBytes = 10 }, _l));

    [Fact]
    public void Details_SeveralPages_UsesThePlural()
    {
        _l.SetLanguage("es");
        Assert.Equal("12 pág. · 1 KB", LibraryFormatter.Details(new PdfDocumentEntry { PageCount = 12, SizeBytes = 10 }, _l));
    }

    [Fact]
    public void LastOpened_TodayAndYesterday_ShowTheTime_OlderShowsTheDate()
    {
        var now = new DateTime(2026, 9, 29, 18, 0, 0, DateTimeKind.Local);
        var today = new DateTime(2026, 9, 29, 9, 5, 0, DateTimeKind.Local);
        var yesterday = new DateTime(2026, 9, 28, 23, 59, 0, DateTimeKind.Local);
        var older = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.Equal($"{_l["last_opened_today"]} 9:05 AM", LibraryFormatter.LastOpened(today.ToUniversalTime(), now, _l));
        Assert.Equal($"{_l["last_opened_yesterday"]} 11:59 PM", LibraryFormatter.LastOpened(yesterday.ToUniversalTime(), now, _l));
        Assert.Equal("9/1/2026", LibraryFormatter.LastOpened(older.ToUniversalTime(), now, _l));
    }
}

public class SmallTypesTests
{
    [Fact]
    public void PendingQueue_IsFirstInFirstOut_AndNotifies()
    {
        var queue = new PendingDocumentQueue();
        var notified = 0;
        queue.DocumentQueued += (_, _) => notified++;

        queue.Enqueue(new PendingDocument("/tmp/1", "one.pdf"));
        queue.Enqueue(new PendingDocument("/tmp/2", "two.pdf"));

        Assert.Equal(2, notified);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("one.pdf", first!.DisplayName);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("/tmp/2", second!.TemporaryFilePath);
        Assert.False(queue.TryDequeue(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void PendingQueue_WithoutSubscribers_DoesNotThrow()
    {
        var queue = new PendingDocumentQueue();
        queue.Enqueue(new PendingDocument("a", "b"));
        Assert.True(queue.TryDequeue(out _));
    }

    [Fact]
    public void PdfOpenException_CarriesTheReasonAndTheCause()
    {
        var inner = new IOException("disk");
        var ex = new PdfOpenException(PdfOpenFailure.WrongPassword, "bad password", inner);

        Assert.Equal(PdfOpenFailure.WrongPassword, ex.Failure);
        Assert.Equal("bad password", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Null(new PdfOpenException(PdfOpenFailure.Unreadable, "x").InnerException);
    }

    [Fact]
    public void Records_HaveValueEquality()
    {
        var entry = new PdfDocumentEntry { Id = "1" };
        Assert.Equal(new DocumentListItem(entry, "a", "b", "c"), new DocumentListItem(entry, "a", "b", "c"));
        Assert.Equal(new PdfTextMatch(1, 0.1, 0.2, 0.3, 0.4), new PdfTextMatch(1, 0.1, 0.2, 0.3, 0.4));
        Assert.NotEqual(new PdfTextMatch(1, 0.1, 0.2, 0.3, 0.4), new PdfTextMatch(2, 0.1, 0.2, 0.3, 0.4));
    }

    [Fact]
    public void PdfDocumentEntry_Defaults()
    {
        var entry = new PdfDocumentEntry();
        Assert.Equal(string.Empty, entry.Id);
        Assert.Equal(string.Empty, entry.DisplayName);
        Assert.Equal(0, entry.PageCount);
    }
}
