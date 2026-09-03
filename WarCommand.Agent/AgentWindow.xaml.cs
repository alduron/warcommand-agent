using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Input;
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

    /// <summary>The enum name, carried on the pill so a click knows which action it is rebinding.</summary>
    public required string ActionName { get; init; }

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
    private RebindSession? _capture;
    private bool _loading;

    /// <summary>
    /// A chord was rebound or reset. The composition root re-arms the hook and redraws the hint;
    /// the window holds the live BindingSet and cannot do either itself.
    /// </summary>
    public event EventHandler? BindingsChanged;

    public AgentWindow(SettingsStore store, IAudioDeviceCatalog? devices, BindingSet? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _bindings = bindings ?? BindingSet.Defaults();

        InitializeComponent();
        LoadDevices(devices);
        LoadChoices();
        LoadBindings();
        LoadCommands();
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

    /// <summary>
    /// The command reference, read from the menu's own tables so it cannot drift from what the
    /// digits actually do.
    /// </summary>
    private void LoadCommands()
    {
        var menu = _bindings[BindingAction.Menu];
        var ptt = _bindings[BindingAction.Ptt];
        var up = _bindings[BindingAction.NavUp];
        var down = _bindings[BindingAction.NavDown];
        var select = _bindings[BindingAction.NavSelect];
        var back = _bindings[BindingAction.NavBack];
        MenuOpenLine.Text = menu.IsBound
            ? $"Hold {menu.Label}. {up.Label} and {down.Label} move, {select.Label} takes the highlighted line, {back.Label} goes back. Release and nothing is listening. Hold {(ptt.IsBound ? ptt.Label : "the push to talk key")} instead to speak."
            : "No overlay menu key is bound. Click its chord above and press any key.";

        // No digits here. A row offers only the verbs it can honour and numbers them from one, so
        // the number beside DONE depends on the row you are standing on.
        RowVerbs.ItemsSource = MenuStateMachine.BoardVerbList.ToList();

        MorePages.ItemsSource = MenuStateMachine.MoreList
            .Select(e => $"{e.Digit.ToString(CultureInfo.InvariantCulture)}  {e.Label}")
            .ToList();
    }

    private void LoadBindings(BindingAction? capturing = null) =>
        Bindings.ItemsSource = BindingActions.All
            .Select(action => new BindingRow
            {
                Action = BindingActions.Display(action),
                ActionName = action.ToString(),
                Chord = capturing == action
                    ? "Press a key"
                    : _bindings[action].IsBound ? _bindings[action].ToString() : "Not set",
                IsBound = _bindings[action].IsBound,
                Note = action switch
                {
                    BindingAction.Panic => "Rebindable, cannot be unbound",
                    BindingAction.Ptt => "Hold to speak",
                    BindingAction.Menu => "Hold to work the overlay. Released, nothing is listening.",
                    _ => null,
                },
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
        EndCapture();
        _bindings.ResetToDefaults();
        LoadBindings();
        SaveBindings();
        SavedNote.Text = "Keybinds reset";
    }

    /// <summary>
    /// Starts capturing the next key or mouse button for one action.
    /// </summary>
    /// <remarks>
    /// The window takes the press itself rather than the global hook: rebinding happens with this
    /// window focused, and routing it through the hook would mean the key being bound is also
    /// dispatched as whatever it currently is.
    /// </remarks>
    private void OnRebind(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string name } || !Enum.TryParse<BindingAction>(name, out var action))
        {
            return;
        }

        _capture = new RebindSession(_bindings, action, DateTimeOffset.UtcNow);
        LoadBindings(capturing: action);
        SavedNote.Text = "Press a key or mouse button. Esc cancels.";
    }

    private void EndCapture()
    {
        _capture = null;
        LoadBindings();
    }

    /// <summary>Feeds one candidate chord to the open capture, and reports what happened.</summary>
    private void Offer(Chord chord)
    {
        if (_capture is not { } session)
        {
            return;
        }

        switch (session.Offer(chord, DateTimeOffset.UtcNow))
        {
            case RebindOutcome.Captured:
                _capture = null;
                LoadBindings();
                SaveBindings();
                SavedNote.Text = $"{BindingActions.Display(session.Action)} is {chord.Label}";
                break;
            case RebindOutcome.RefusedConflict:
                SavedNote.Text =
                    $"{chord.Label} is already {BindingActions.Display(session.ConflictsWith)}";
                break;
            default:
                break;
        }
    }

    /// <summary>Writes the chords through the store, the same way every other control does.</summary>
    private void SaveBindings()
    {
        _store.Save(_store.Current with { Bindings = App.StoredBindings(_bindings) });
        LoadCommands();
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);

        if (_capture is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            EndCapture();
            SavedNote.Text = "Rebind cancelled";
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.IsKeyDown(Key.RightAlt) ? BindingModifiers.RightAlt : BindingModifiers.None;
        if (key is Key.RightAlt or Key.LeftAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (BindingKey.TryFromVirtualKey(KeyInterop.VirtualKeyFromKey(key), out var bindingKey))
        {
            Offer(new Chord(modifiers, bindingKey));
        }
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseDown(e);

        if (_capture is null)
        {
            return;
        }

        // Only the extra buttons. Left and right belong to the window while it is open, and a
        // rebind that swallowed the click that started it could never be finished with a mouse.
        var button = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.XButton1 => (Input.Bindings.MouseButton?)Input.Bindings.MouseButton.Button4,
            System.Windows.Input.MouseButton.XButton2 => Input.Bindings.MouseButton.Button5,
            System.Windows.Input.MouseButton.Middle => Input.Bindings.MouseButton.Middle,
            _ => null,
        };

        if (button is { } chosen
            && BindingKey.TryFromMouseButton(chosen, out var bindingKey))
        {
            e.Handled = true;
            Offer(Chord.Of(bindingKey));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The window is settings and nothing else. The queue lives on the web board and, in a fight,
    /// on the overlay; a third copy of it in a desktop tab was the same list a worse way.
    /// </summary>
    public void ShowSettingsTab() => Tabs.SelectedIndex = 0;
}
