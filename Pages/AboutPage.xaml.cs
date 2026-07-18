using Microsoft.Extensions.Logging;
using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Application information, contact, support link, language selector and legal notice.
/// </summary>
public partial class AboutPage : ContentPage
{
    private const string ContactEmail = "jsoladelarosa@gmail.com";
    private const string DonationUrl = "https://ko-fi.com/josepsola";

    private static readonly Color ActiveLanguage = Color.FromArgb("#B3121E");

    private readonly ILocalizationService _localization;
    private readonly ILogger<AboutPage> _logger;

    public AboutPage(ILocalizationService localization, ILogger<AboutPage> logger)
    {
        InitializeComponent();

        _localization = localization;
        _logger = logger;

        ApplyTexts();
        UpdateLanguageButtons();
    }

    private void ApplyTexts()
    {
        Title = _localization["about_title"];

        AppNameLabel.Text = _localization["app_name"];
        VersionLabel.Text = _localization.Format("version", AppInfo.Current.VersionString);
        TaglineLabel.Text = _localization["app_tagline"];

        ContactTitleLabel.Text = _localization["contact_title"];
        ContactButton.Text = ContactEmail;
        ContactHintLabel.Text = _localization["contact_hint"];

        SupportTitleLabel.Text = _localization["support_title"];
        SupportButton.Text = _localization["support_button"];
        SupportHintLabel.Text = _localization["support_hint"];

        LanguageTitleLabel.Text = _localization["language_title"];
        LanguageHintLabel.Text = _localization["language_hint"];

        PrivacyTitleLabel.Text = _localization["privacy_title"];
        PrivacyTextLabel.Text = _localization["privacy_text"];

        LicenseTitleLabel.Text = _localization["license_title"];
        LicenseTextLabel.Text = _localization["license_text"];
        LicenseCopyrightLabel.Text = _localization["license_copyright"];

        LegalTitleLabel.Text = _localization["legal_title"];
        LegalText1Label.Text = _localization["legal_text_1"];
        LegalText2Label.Text = _localization["legal_text_2"];
        LegalWarningLabel.Text = _localization["legal_warning"];

        BackButton.Text = _localization["back"];
    }

    private void UpdateLanguageButtons()
    {
        var spanishActive = _localization.CurrentLanguage == "es";

        StyleLanguageButton(SpanishButton, spanishActive);
        StyleLanguageButton(EnglishButton, !spanishActive);
    }

    private static void StyleLanguageButton(Button button, bool active)
    {
        button.BackgroundColor = active ? ActiveLanguage : Colors.Transparent;
        button.TextColor = active ? Colors.White : ActiveLanguage;
        button.BorderColor = ActiveLanguage;
        button.BorderWidth = active ? 0 : 1;
    }

    private void OnSpanishClicked(object? sender, EventArgs e) => SetLanguage("es");

    private void OnEnglishClicked(object? sender, EventArgs e) => SetLanguage("en");

    private void SetLanguage(string languageCode)
    {
        _localization.SetLanguage(languageCode);
        ApplyTexts();
        UpdateLanguageButtons();
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    private async void OnContactEmailClicked(object? sender, EventArgs e)
    {
        var appName = _localization["app_name"];
        var subject = _localization["email_subject"];
        var body = _localization.Format("email_body", appName, AppInfo.Current.VersionString);

        try
        {
#if ANDROID
            if (TryStartEmailIntent(subject, body))
                return;
#endif
            await Email.Default.ComposeAsync(new EmailMessage
            {
                Subject = subject,
                Body = body,
                To = [ContactEmail]
            });
        }
        catch (FeatureNotSupportedException ex)
        {
            _logger.LogWarning(ex, "No email client is available.");
            await ShowAlertAsync(_localization["error"], _localization["error_no_email"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the email client.");
            await ShowAlertAsync(_localization["error"], _localization.Format("error_email", ex.Message));
        }
    }

#if ANDROID
    /// <summary>
    /// Opens the system chooser with every installed email app, which is more reliable than
    /// letting the platform pick one for us.
    /// </summary>
    private bool TryStartEmailIntent(string subject, string body)
    {
        var context = Platform.CurrentActivity ?? (Android.Content.Context)Android.App.Application.Context;

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionSendto);
        intent.SetData(Android.Net.Uri.Parse($"mailto:{ContactEmail}"));
        intent.PutExtra(Android.Content.Intent.ExtraSubject, subject);
        intent.PutExtra(Android.Content.Intent.ExtraText, body);

        var chooser = Android.Content.Intent.CreateChooser(intent, _localization["email_chooser"]);
        if (chooser is null)
            return false;

        if (context is not Android.App.Activity)
            chooser.AddFlags(Android.Content.ActivityFlags.NewTask);

        context.StartActivity(chooser);
        return true;
    }
#endif

    private async void OnDonationClicked(object? sender, EventArgs e)
    {
        try
        {
            await Browser.Default.OpenAsync(new Uri(DonationUrl), new BrowserLaunchOptions
            {
                LaunchMode = BrowserLaunchMode.SystemPreferred,
                TitleMode = BrowserTitleMode.Show,
                PreferredToolbarColor = Color.FromArgb("#E67E22"),
                PreferredControlColor = Colors.White
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the donation link.");
            await CopyDonationLinkAsync(ex);
        }
    }

    private async Task CopyDonationLinkAsync(Exception originalException)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(DonationUrl);
            await ShowAlertAsync(
                _localization["browser_unavailable_title"],
                _localization.Format("browser_copied", DonationUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not copy the donation link to the clipboard.");
            await ShowAlertAsync(_localization["error"], _localization.Format("error_browser", originalException.Message));
        }
    }

    private Task ShowAlertAsync(string title, string message) =>
        DisplayAlertAsync(title, message, _localization["ok"]);
}
