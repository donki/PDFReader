using Microsoft.Extensions.DependencyInjection;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader;

/// <summary>
/// Application shell (constitucion, anexo A.9): provides the hamburger flyout accessible from the
/// top bar, with the minimum options «Inicio» (LibraryPage) and «Acerca de» (AboutPage). Only the
/// first-level flyout lives here; navigation to ReaderPage/PasswordPromptPage keeps happening with
/// Navigation.PushAsync from inside the pages, as before.
/// </summary>
public partial class AppShell : Shell
{
    private readonly IServiceProvider _services;
    private readonly ILocalizationService _localization;

    public AppShell(IServiceProvider services, ILocalizationService localization)
    {
        InitializeComponent();

        _services = services;
        _localization = localization;

        // The library is a singleton and is the app's home screen. It is resolved from DI here
        // rather than through a DataTemplate so its constructor injection keeps working.
        HomeContent.Content = services.GetRequiredService<LibraryPage>();

        ApplyTexts();
        _localization.LanguageChanged += (_, _) => ApplyTexts();
    }

    private void ApplyTexts()
    {
        HeaderTitleLabel.Text = _localization["app_name"];
        HeaderTaglineLabel.Text = _localization["app_tagline"];
        HomeFlyoutItem.Title = _localization["menu_home"];
        AboutMenuItem.Text = _localization["about"];
    }

    private async void OnAboutMenuClicked(object? sender, EventArgs e)
    {
        FlyoutIsPresented = false;
        await Navigation.PushAsync(_services.GetRequiredService<AboutPage>());
    }
}
