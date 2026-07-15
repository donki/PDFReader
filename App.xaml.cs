using Microsoft.Extensions.DependencyInjection;
using PDFReader.Pages;
using PDFReader.Services;

namespace PDFReader;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services, ILocalizationService localization)
    {
        InitializeComponent();

        _services = services;

        // Resolve the language before the first page builds its texts.
        localization.Initialize();
    }

    // Pages are resolved here, not injected into the constructor: a page built before
    // InitializeComponent() runs would not find the styles in Application.Resources.
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(_services.GetRequiredService<LibraryPage>()));
}
