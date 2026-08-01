using PDFReader.Services;

namespace PDFReader.Pages;

/// <summary>
/// Asks for the password of a protected document. Pushed as a modal and awaited through
/// <see cref="AskAsync"/>, which returns the password, or null when the user gives up.
/// </summary>
public partial class PasswordPromptPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PasswordPromptPage(ILocalizationService localization, string documentName, bool retryAfterWrongPassword)
    {
        InitializeComponent();

        TitleLabel.Text = localization["password_title"];
        MessageLabel.Text = localization.Format("password_message", documentName);
        AcceptButton.Text = localization["open"];
        CancelButton.Text = localization["cancel"];

        if (retryAfterWrongPassword)
        {
            ErrorLabel.Text = localization["password_wrong"];
            ErrorLabel.IsVisible = true;
        }
    }

    /// <summary>
    /// Shows the prompt over <paramref name="host"/> and waits for an answer.
    /// Returns null when the user cancels or dismisses it with the back button.
    /// </summary>
    public static async Task<string?> AskAsync(
        Page host,
        ILocalizationService localization,
        string documentName,
        bool retryAfterWrongPassword = false)
    {
        var prompt = new PasswordPromptPage(localization, documentName, retryAfterWrongPassword);
        await host.Navigation.PushModalAsync(prompt, animated: false);

        return await prompt._result.Task;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        PasswordEntry.Focus();
    }

    /// <summary>The hardware back button dismisses the prompt, same as cancelling.</summary>
    protected override bool OnBackButtonPressed()
    {
        CloseAsync(null).ContinueWith(
            t => { /* observed to keep the task from faulting unobserved */ _ = t.Exception; },
            TaskScheduler.Default);

        return true;
    }

    private async void OnAcceptClicked(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text;
        if (string.IsNullOrEmpty(password))
            return; // An empty password never opens anything; keep the prompt up.

        await CloseAsync(password);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(string? password)
    {
        if (_result.Task.IsCompleted)
            return;

        await Navigation.PopModalAsync(animated: false);
        _result.TrySetResult(password);
    }
}
