using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace WarCommand.Agent.Tray;

/// <summary>
/// Takes a web-issued pairing code and redeems it. The path for anybody whose browser and agent are
/// on different machines, and the one that does not need the web to see this device at all.
/// </summary>
/// <remarks>
/// The code is redeemed here rather than by the caller so the dialog can name the failure on the
/// spot. It never logs the code: a live pairing code is a credential.
/// </remarks>
public partial class PairingCodeWindow : Window
{
    private readonly Func<string, CancellationToken, Task> _redeem;

    public PairingCodeWindow(Func<string, CancellationToken, Task> redeem)
    {
        ArgumentNullException.ThrowIfNull(redeem);
        _redeem = redeem;
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // WinForms is referenced for the tray's NotifyIcon, so KeyEventArgs is ambiguous here.
    private async void OnCodeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await PairAsync().ConfigureAwait(true);
        }
    }

    private async void OnPair(object sender, RoutedEventArgs e) => await PairAsync().ConfigureAwait(true);

    private async Task PairAsync()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
        {
            return;
        }

        PairButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            await _redeem(code, CancellationToken.None).ConfigureAwait(true);
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ErrorText.Text = "that code did not pair";
            ErrorText.Visibility = Visibility.Visible;
            PairButton.IsEnabled = true;
        }
    }
}
