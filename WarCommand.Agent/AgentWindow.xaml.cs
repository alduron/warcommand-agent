using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Input.Devices;
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

    /// <summary>The HOTAS button, as the second row shows it. Never a key.</summary>
    public required string Hotas { get; init; }

    /// <summary>False dims the HOTAS pill, which is the shipped state for every action.</summary>
    public required bool HotasIsBound { get; init; }

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

    // The stick reader, handed over by the composition root once the agent is armed. Null before
    // then, and the HOTAS row says so rather than opening a capture nothing can ever answer.
    private IDeviceButtonSource? _devices;

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
        AnchorBox.ItemsSource = new[]
        {
            "Top left", "Top centre", "Top right",
            "Left centre", "Centre", "Right centre",
            "Bottom left", "Bottom centre", "Bottom right",
        };
        OverlayOpacityBox.ItemsSource = new[] { "Low", "Normal", "High" };
        WhenUnfocused.ItemsSource = new[] { "Hide", "Dim" };
        RecognizerName.Text = "Vosk small en-us";
    }

    /// <summary>
    /// The stick reader. Set once, by the composition root, when the agent arms. The HOTAS pills
    /// are dead without it, which is the same rule every other armed thing follows.
    /// </summary>
    public IDeviceButtonSource? DeviceButtons
    {
        get => _devices;
        set
        {
            if (_devices is { } previous)
            {
                previous.ButtonChanged -= OnDeviceButton;
            }

            _devices = value;
            LoadBindings();
        }
    }

    private void LoadBindings(BindingAction? capturing = null, BindingSlot slot = BindingSlot.Primary) =>
        Bindings.ItemsSource = BindingActions.All
            .Select(action => new BindingRow
            {
                Action = BindingActions.Display(action),
                ActionName = action.ToString(),
                Chord = capturing == action && slot == BindingSlot.Primary
                    ? "Press a key"
                    : _bindings[action].IsBound ? _bindings[action].ToString() : "Not set",
                IsBound = _bindings[action].IsBound,
                Hotas = capturing == action && slot == BindingSlot.Secondary
                    ? "Press a button"
                    : _bindings.Secondary(action).Display(_devices?.Devices),
                HotasIsBound = _bindings.Secondary(action).IsBound,
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
        AnchorBox.SelectedIndex = Math.Max(Array.IndexOf(AnchorOrder, settings.Anchor), 0);
        Slide.Value = settings.ClampedSlide;
        WidthFraction.Value = settings.ClampedWidthFraction;
        OverlayOpacityBox.SelectedIndex = (int)settings.Opacity;
        ColourblindSafe.IsChecked = settings.ColourblindSafe;
        WhenUnfocused.SelectedIndex = (int)settings.WhenUnfocused;
        AutoCopyOnClaim.IsChecked = settings.AutoCopyOnClaim;
        ScreenCapture.IsChecked = settings.ScreenCaptureEnabled;
        VerboseLogging.IsChecked = settings.VerboseLogging;
        RenderLogSize();

        _loading = false;
        RenderValues();
    }

    private void RenderValues()
    {
        MasterVolumeValue.Text = MasterVolume.Value.ToString("P0", CultureInfo.InvariantCulture);
        ConfidenceValue.Text = ConfidenceFloor.Value.ToString("0.00", CultureInfo.InvariantCulture);
        WidthValue.Text = WidthFraction.Value.ToString("P0", CultureInfo.InvariantCulture);

        // A corner has no free axis, so the slider is shown disabled rather than hidden: a control
        // that vanishes reads as a bug, and the caption says which way the live one travels.
        var anchor = SelectedAnchor();
        var free = FreeAxisOf(anchor);

        Slide.IsEnabled = free is not null;
        SlideCaption.Text = free switch
        {
            "vertical" => "+ is up, - is down",
            "horizontal" => "+ is right, - is left",
            _ => "a corner has nothing to slide along",
        };
        SlideValue.Text = free is null
            ? "-"
            : Slide.Value.ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);
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
        Anchor = SelectedAnchor(),
        Slide = Slide.Value,
        WidthFraction = WidthFraction.Value,
        Opacity = (OverlayOpacity)Math.Max(OverlayOpacityBox.SelectedIndex, 0),
        ColourblindSafe = ColourblindSafe.IsChecked is true,
        WhenUnfocused = (UnfocusedBehaviour)Math.Max(WhenUnfocused.SelectedIndex, 0),
        AutoCopyOnClaim = AutoCopyOnClaim.IsChecked is true,
        ScreenCaptureEnabled = ScreenCapture.IsChecked is true,
        VerboseLogging = VerboseLogging.IsChecked is true,
    };

    /// <summary>The nine anchors in reading order, which is the order the box lists them in.</summary>
    private static readonly OverlayAnchor[] AnchorOrder =
    [
        OverlayAnchor.TopLeft, OverlayAnchor.Top, OverlayAnchor.TopRight,
        OverlayAnchor.Left, OverlayAnchor.Centre, OverlayAnchor.Right,
        OverlayAnchor.BottomLeft, OverlayAnchor.Bottom, OverlayAnchor.BottomRight,
    ];

    private OverlayAnchor SelectedAnchor() =>
        AnchorOrder[Math.Clamp(AnchorBox.SelectedIndex, 0, AnchorOrder.Length - 1)];

    /// <summary>Which axis the offset travels along, or null for a corner.</summary>
    private static string? FreeAxisOf(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.Left or OverlayAnchor.Right or OverlayAnchor.Centre => "vertical",
        OverlayAnchor.Top or OverlayAnchor.Bottom => "horizontal",
        _ => null,
    };

    /// <summary>
    /// One zip on the desktop, then Explorer with it selected.
    /// </summary>
    /// <remarks>
    /// The desktop rather than a save dialog: the person doing this is mid-incident and describing
    /// it to somebody else, and a file picker is one more thing to explain. Selecting it in Explorer
    /// is what turns "it exported" into a file they can drag into a chat window.
    /// </remarks>
    private void OnExportLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var zip = LogBundle.Write(
                _store.Paths,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                App.DescribeForSupport(_store.Current));

            SavedNote.Text = "Exported to your desktop";
            RenderLogSize();
            Reveal(zip);
        }
        catch (IOException error)
        {
            SavedNote.Text = "Export failed: " + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            SavedNote.Text = "Export failed: " + error.Message;
        }
    }

    /// <summary>What the export would weigh, so nobody has to guess before clicking.</summary>
    private void RenderLogSize()
    {
        var directory = _store.Paths.LogDirectory;
        long bytes = 0;

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                bytes += new FileInfo(file).Length;
            }
        }

        LogSizeCaption.Text = bytes == 0
            ? "Nothing logged yet"
            : FormattableString.Invariant($"One zip on your desktop, about {bytes / 1024} KB");
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Paths.LogDirectory);
        Reveal(_store.Paths.LogDirectory);
    }

    /// <summary>Explorer, with the item selected rather than merely open beside it.</summary>
    private static void Reveal(string path)
    {
        using var explorer = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(path)
                    ? FormattableString.Invariant($"/select,\"{path}\"")
                    : FormattableString.Invariant($"\"{path}\""),
                UseShellExecute = true,
            },
        };

        explorer.Start();
    }

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

    /// <summary>
    /// Starts capturing the next HOTAS button for one action. Keyboard presses are ignored for the
    /// length of it: the row exists for people whose hands are not on the keyboard.
    /// </summary>
    private void OnRebindHotas(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string name }
            || !Enum.TryParse<BindingAction>(name, out var action))
        {
            return;
        }

        if (_devices is null)
        {
            SavedNote.Text = "No stick reader yet. Sign in first.";
            return;
        }

        EndCapture();
        _capture = new RebindSession(_bindings, action, DateTimeOffset.UtcNow, BindingSlot.Secondary);
        _devices.ButtonChanged += OnDeviceButton;
        LoadBindings(capturing: action, slot: BindingSlot.Secondary);
        SavedNote.Text = "Press a HOTAS button. Esc cancels.";
    }

    /// <summary>
    /// One button edge from the stick, marshalled onto the UI thread. Presses only: a release would
    /// bind the button the user let go of on their way to the one they meant.
    /// </summary>
    private void OnDeviceButton(object? sender, DeviceButtonEventArgs e)
    {
        if (!e.Down)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() => Offer(e.Button));
    }

    private void EndCapture()
    {
        if (_devices is { } devices)
        {
            devices.ButtonChanged -= OnDeviceButton;
        }

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

    /// <summary>Feeds one candidate HOTAS button to the open capture.</summary>
    private void Offer(DeviceButton button)
    {
        if (_capture is not { } session)
        {
            return;
        }

        switch (session.Offer(button, DateTimeOffset.UtcNow))
        {
            case RebindOutcome.Captured:
                EndCapture();
                SaveBindings();
                SavedNote.Text =
                    $"{BindingActions.Display(session.Action)} is {button.Display(_devices?.Devices)}";
                break;
            case RebindOutcome.RefusedConflict:
                SavedNote.Text =
                    $"{button.Display(_devices?.Devices)} is already {BindingActions.Display(session.ConflictsWith)}";
                break;
            default:
                break;
        }
    }

    /// <summary>Writes the chords through the store, the same way every other control does.</summary>
    private void SaveBindings()
    {
        _store.Save(_store.Current with
        {
            Bindings = App.StoredBindings(_bindings),
            SecondaryBindings = App.StoredSecondaryBindings(_bindings),
        });
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
