using System.Windows;
using System.Windows.Interop;
using PcHub.Native;

namespace PcHub.Views;

public partial class ApiKeyWindow : Window
{
    public ApiKeyWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Win32.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => KeyBox.Focus();
    }

    /// <summary>The entered key; empty means "remove the saved key".</summary>
    public string Key { get; private set; } = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string k = KeyBox.Password.Trim();
        if (!k.StartsWith("sk-ant-", StringComparison.Ordinal) || k.Length < 20 || k.Any(char.IsWhiteSpace))
        {
            ErrorText.Text = "That doesn't look like an Anthropic API key (they start with \"sk-ant-\").";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Key = k;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Key = "";
        DialogResult = true;
    }

    /// <summary>Only on the user's click.</summary>
    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = "Couldn't open the browser. Go to console.anthropic.com → Settings → API keys.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
