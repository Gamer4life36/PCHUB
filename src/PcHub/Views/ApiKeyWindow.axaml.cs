using Avalonia.Controls;
using Avalonia.Interactivity;
using PcHub.Platform;

namespace PcHub.Views;

public partial class ApiKeyWindow : Window
{
    public ApiKeyWindow()
    {
        InitializeComponent();
        Opened += (_, _) => KeyBox.Focus();
    }

    /// <summary>The entered key; empty means "remove the saved key".</summary>
    public string Key { get; private set; } = "";

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        string k = (KeyBox.Text ?? "").Trim();
        if (!k.StartsWith("sk-ant-", StringComparison.Ordinal) || k.Length < 20 || k.Any(char.IsWhiteSpace))
        {
            ErrorText.Text = "That doesn't look like an Anthropic API key (they start with \"sk-ant-\").";
            ErrorText.IsVisible = true;
            return;
        }

        Key = k;
        Close(true);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        Key = "";
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Only on the user's click.</summary>
    private void GetKey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Os.Current.OpenWithShell("https://console.anthropic.com/settings/keys");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = "Couldn't open the browser. Go to console.anthropic.com → Settings → API keys.";
            ErrorText.IsVisible = true;
        }
    }
}
