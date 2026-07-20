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

    // The Shell (and the pages it hosts) is resolved here, not injected into the constructor: a
    // page built before InitializeComponent() runs would not find the styles in Application.Resources.
    // The Shell provides the flyout (menu hamburguesa, constitucion anexo A.9) and the navigation
    // stack; LibraryPage keeps pushing ReaderPage/AboutPage with Navigation.PushAsync as before.
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<AppShell>());
}
