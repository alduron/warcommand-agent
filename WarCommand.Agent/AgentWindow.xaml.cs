using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Game;
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
        InputDevice.ItemsSource = Choices(devices?.Inputs, devices?.DefaultInput);
        OutputDevice.ItemsSource = Choices(devices?.Outputs, devices?.DefaultOutput);

        if (devices is null)
        {
            AudioNotice.Text = "No audio device list. Only Default can be chosen.";
            AudioNoticeBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// One device list: Default first, naming the device it currently resolves to, then every
    /// other active endpoint.
    /// </summary>
    /// <remarks>
    /// Default is a real choice and not a placeholder: it follows the user changing their default
    /// in Windows, which a pinned endpoint id does not. Naming what it resolves to is what makes
    /// the difference visible, so nobody picks their headset twice.
    /// </remarks>
    private static List<DeviceChoice> Choices(
        IReadOnlyList<AudioDevice>? devices, AudioDevice? fallback)
    {
        var label = fallback is { } current ? $"Default ({current.FriendlyName})" : "Default";
        var choices = new List<DeviceChoice> { new(null, label) };

        if (devices is not null)
        {
            choices.AddRange(devices
                .Where(d => !d.IsDefault)
                .Select(d => new DeviceChoice(d.Id, d.FriendlyName)));
        }

        return choices;
    }

    private void LoadChoices()
    {
        // Device name is what is persisted; nobody recognises \.\DISPLAY2, and every monitor
        // reports itself as Generic PnP Monitor, so the label is the index and the resolution.
        var screens = System.Windows.Forms.Screen.AllScreens;
        DisplayBox.ItemsSource = screens
            .Select((screen, i) => new DeviceChoice(screen.DeviceName, OverlayController.DisplayName(screen, i)))
            .ToList();

        OverlayModeBox.ItemsSource = new[] { "Always on", "Mirror Wardogs", "Hidden" };
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
        OutputDevice.SelectedItem = ((IEnumerable<DeviceChoice>)OutputDevice.ItemsSource)
            .FirstOrDefault(d => d.Id == settings.OutputDeviceId)
            ?? ((IEnumerable<DeviceChoice>)OutputDevice.ItemsSource).First();

        MasterVolume.Value = settings.MasterVolume;
        SoundBoardEmpty.IsChecked = settings.Sounds.BoardWentFromEmpty;
        SoundNewUrgent.IsChecked = settings.Sounds.NewUrgent;
        SoundYourClaimed.IsChecked = settings.Sounds.YourRequestClaimed;
        SoundClaimOk.IsChecked = settings.Sounds.ClaimSucceeded;
        SoundClaimLost.IsChecked = settings.Sounds.ClaimLostTheRace;
        SoundAll.IsChecked = !settings.Sounds.AllSound;

        ConfidenceFloor.Value = settings.ConfidenceFloor;
        ShowRecognizedText.IsChecked = settings.ShowRecognizedText;

        OverlayModeBox.SelectedIndex = (int)settings.OverlayMode;
        DisplayBox.SelectedItem = ((IEnumerable<DeviceChoice>)DisplayBox.ItemsSource)
            .FirstOrDefault(d => d.Id == settings.DisplayDeviceName)
            ?? ((IEnumerable<DeviceChoice>)DisplayBox.ItemsSource).FirstOrDefault();
        AnchorBox.SelectedIndex = (int)settings.Anchor;
        WidthPx.Value = settings.ClampedWidth;
        OverlayOpacityBox.SelectedIndex = (int)settings.Opacity;
        ColourblindSafe.IsChecked = settings.ColourblindSafe;
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
        OverlayMode = (OverlayMode)Math.Max(OverlayModeBox.SelectedIndex, 0),
        DisplayDeviceName = (DisplayBox.SelectedItem as DeviceChoice)?.Id,
        Anchor = (OverlayAnchor)Math.Max(AnchorBox.SelectedIndex, 0),
        WidthPx = (int)WidthPx.Value,
        Opacity = (OverlayOpacity)Math.Max(OverlayOpacityBox.SelectedIndex, 0),
        ColourblindSafe = ColourblindSafe.IsChecked is true,
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

    /// <summary>
    /// The window is settings and nothing else. The queue lives on the web board and, in a fight,
    /// on the overlay; a third copy of it in a desktop tab was the same list a worse way.
    /// </summary>
    public void ShowSettingsTab() => Tabs.SelectedIndex = 0;
}
