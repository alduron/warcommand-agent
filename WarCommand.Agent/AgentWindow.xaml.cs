using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Speech.Capture;

namespace WarCommand.Agent;

/// <summary>One row of the keybinds tab.</summary>
public sealed record BindingRow
{
    public required string Action { get; init; }

    public required string Chord { get; init; }

    /// <summary>False dims the pill: an unbound action reads as absent, not as a key named "Not set".</summary>
    public required bool IsBound { get; init; }

    public string? Note { get; init; }

    public Visibility NoteVisibility => Note is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One entry in a device list. Null id is the system default.</summary>
public sealed record DeviceChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The agent's one window. Board first, then the four settings tabs from
/// docs/design/mocks/TraySettings.dc.html: Audio, Keybinds, Speech, Overlay.
/// </summary>
/// <remarks>
/// One window, not two. The tray's double-click and its Settings row both land here and differ
/// only in which tab they select.
///
/// Every settings control writes straight through to <see cref="SettingsStore"/>, so there is no
/// Apply and nothing to lose by closing the window. A live preview needs no commit step.
/// </remarks>
public partial class AgentWindow : Window
{
    private readonly SettingsStore _store;
    private readonly BindingSet _bindings;
    private bool _loading;

    public AgentWindow(SettingsStore store, IAudioDeviceCatalog? devices, BindingSet? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _bindings = bindings ?? BindingSet.Defaults();

        InitializeComponent();
        LoadDevices(devices);
        LoadChoices();
        LoadBindings();
        LoadFrom(store.Current);
    }

    private void LoadDevices(IAudioDeviceCatalog? devices)
    {
        var inputs = new List<DeviceChoice> { new(null, "Default") };
        if (devices is not null)
        {
            inputs.AddRange(devices.Inputs
                .Where(d => !d.IsDefault)
                .Select(d => new DeviceChoice(d.Id, d.FriendlyName)));
        }
        else
        {
            AudioNotice.Text = "Capture not running. Only Default can be chosen.";
            AudioNoticeBox.Visibility = Visibility.Visible;
        }

        InputDevice.ItemsSource = inputs;

        // Render endpoints are not enumerated yet: nothing plays a sound, so a list of outputs
        // would be a list of choices that change nothing.
        OutputDevice.ItemsSource = new List<DeviceChoice> { new(null, "Default") };
    }

    private void LoadChoices()
    {
        AnchorBox.ItemsSource = new[] { "Left", "Right", "Top right", "Bottom right" };
        OverlayOpacityBox.ItemsSource = new[] { "Low", "Normal", "High" };
        WhenUnfocused.ItemsSource = new[] { "Hide", "Dim" };
        RecognizerName.Text = "Vosk small en-us";
    }

    private void LoadBindings() =>
        Bindings.ItemsSource = BindingActions.All
            .Select(action => new BindingRow
            {
                Action = BindingActions.Display(action),
                Chord = _bindings[action].IsBound ? _bindings[action].ToString() : "Not set",
                IsBound = _bindings[action].IsBound,
                Note = action == BindingAction.Panic ? "Rebindable, cannot be unbound" : null,
            })
            .ToList();

    private void LoadFrom(AgentSettings settings)
    {
        _loading = true;

        InputDevice.SelectedItem = ((IEnumerable<DeviceChoice>)InputDevice.ItemsSource)
            .FirstOrDefault(d => d.Id == settings.InputDeviceId)
            ?? ((IEnumerable<DeviceChoice>)InputDevice.ItemsSource).First();
        OutputDevice.SelectedIndex = 0;

        MasterVolume.Value = settings.MasterVolume;
        SoundBoardEmpty.IsChecked = settings.Sounds.BoardWentFromEmpty;
        SoundNewUrgent.IsChecked = settings.Sounds.NewUrgent;
        SoundYourClaimed.IsChecked = settings.Sounds.YourRequestClaimed;
        SoundClaimOk.IsChecked = settings.Sounds.ClaimSucceeded;
        SoundClaimLost.IsChecked = settings.Sounds.ClaimLostTheRace;
        SoundAll.IsChecked = !settings.Sounds.AllSound;

        ConfidenceFloor.Value = settings.ConfidenceFloor;
        ShowRecognizedText.IsChecked = settings.ShowRecognizedText;

        OverlayEnabled.IsChecked = settings.OverlayEnabled;
        AnchorBox.SelectedIndex = (int)settings.Anchor;
        WidthPx.Value = settings.ClampedWidth;
        OverlayOpacityBox.SelectedIndex = (int)settings.Opacity;
        ColourblindSafe.IsChecked = settings.ColourblindSafe;
        SecondScreen.IsChecked = settings.SecondScreenMode;
        WhenUnfocused.SelectedIndex = (int)settings.WhenUnfocused;
        AutoCopyOnClaim.IsChecked = settings.AutoCopyOnClaim;
        ScreenCapture.IsChecked = settings.ScreenCaptureEnabled;

        _loading = false;
        RenderValues();
    }

    private void RenderValues()
    {
        MasterVolumeValue.Text = MasterVolume.Value.ToString("P0", CultureInfo.InvariantCulture);
        ConfidenceValue.Text = ConfidenceFloor.Value.ToString("0.00", CultureInfo.InvariantCulture);
        WidthValue.Text = FormattableString.Invariant($"{(int)WidthPx.Value} px");
    }

    /// <summary>Every control lands here. There is no Apply: a setting takes effect when set.</summary>
    private void OnDirty(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        RenderValues();
        _store.Save(Collect());
        SavedNote.Text = "Saved";
    }

    private AgentSettings Collect() => _store.Current with
    {
        InputDeviceId = (InputDevice.SelectedItem as DeviceChoice)?.Id,
        OutputDeviceId = (OutputDevice.SelectedItem as DeviceChoice)?.Id,
        MasterVolume = MasterVolume.Value,
        Sounds = new SoundMutes
        {
            BoardWentFromEmpty = SoundBoardEmpty.IsChecked is true,
            NewUrgent = SoundNewUrgent.IsChecked is true,
            YourRequestClaimed = SoundYourClaimed.IsChecked is true,
            ClaimSucceeded = SoundClaimOk.IsChecked is true,
            ClaimLostTheRace = SoundClaimLost.IsChecked is true,
            AllSound = SoundAll.IsChecked is not true,
        },
        ConfidenceFloor = ConfidenceFloor.Value,
        ShowRecognizedText = ShowRecognizedText.IsChecked is true,
        OverlayEnabled = OverlayEnabled.IsChecked is true,
        Anchor = (OverlayAnchor)Math.Max(AnchorBox.SelectedIndex, 0),
        WidthPx = (int)WidthPx.Value,
        Opacity = (OverlayOpacity)Math.Max(OverlayOpacityBox.SelectedIndex, 0),
        ColourblindSafe = ColourblindSafe.IsChecked is true,
        SecondScreenMode = SecondScreen.IsChecked is true,
        WhenUnfocused = (UnfocusedBehaviour)Math.Max(WhenUnfocused.SelectedIndex, 0),
        AutoCopyOnClaim = AutoCopyOnClaim.IsChecked is true,
        ScreenCaptureEnabled = ScreenCapture.IsChecked is true,
    };

    private void OnResetBindings(object sender, RoutedEventArgs e)
    {
        _bindings.ResetToDefaults();
        LoadBindings();
        SavedNote.Text = "Keybinds reset";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>The board, for the composition root to render into.</summary>
    public BoardView BoardView => Board;

    /// <summary>Second-screen mode. The tray's double-click lands here.</summary>
    public void ShowBoardTab() => Tabs.SelectedIndex = 0;

    /// <summary>The tray's Settings row lands on Audio, the first settings tab.</summary>
    public void ShowSettingsTab() => Tabs.SelectedIndex = 1;
}
