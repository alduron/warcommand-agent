using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Link;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Client.Tokens;
using WarCommand.Agent.Client.Updates;
using WarCommand.Agent.Core;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Dev;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Core.Tray;
using WarCommand.Agent.Core.Updates;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Game;
using WarCommand.Agent.Input;
using WarCommand.Agent.Speech.Capture;
using WarCommand.Agent.Startup;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tray;
using Application = System.Windows.Application;

namespace WarCommand.Agent;

/// <summary>
/// The composition root. Owns the tray icon and, for now, second-screen mode: the layered overlay,
/// hotkeys, speech and the rest of the startup sequence in 10-agent-spec.md are not wired up yet.
/// </summary>
/// <remarks>
/// The composition root that owns the realtime client and the PanicSwitch calls
/// <see cref="TrayIconController.SetConnectionState"/> and registers the tray as
/// <see cref="WarCommand.Agent.Input.PanicSubsystem.TrayIndicator"/> once it exists; this class does
/// not wait for that to show second-screen mode and reach the API on its own.
/// </remarks>
public partial class App : Application, IDisposable
{
    /// <summary>
    /// Session-scoped, so it covers every profile at once. A dev launch and a tray-only launch are
    /// still two agents: two tray icons, two device registrations racing the same tokens.dat, and
    /// two sets of global hooks once those land. There is only ever one.
    /// </summary>
    private const string SingleInstanceMutexName = @"Local\WarCommand.Agent.SingleInstance";

    /// <summary>
    /// Set by a second launch to ask the running agent to show its window. Session-scoped for the
    /// same reason the mutex is: two desktop sessions are two agents, not one.
    /// </summary>
    private const string ShowWindowEventName = @"Local\WarCommand.Agent.ShowWindow";

    /// <summary>
    /// The running build, read from the assembly the release workflow stamps from the git tag.
    /// A constant here would have to be edited in lockstep with every tag, and the first time it
    /// was not, the agent would either offer an update it already has or refuse the one it needs.
    /// </summary>
    private static string AgentVersion { get; } = CleanVersion(
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0");

    /// <summary>
    /// The version without SemVer build metadata, capped at what the API accepts.
    /// </summary>
    /// <remarks>
    /// Belt and braces with IncludeSourceRevisionInInformationalVersion in Directory.Build.props.
    /// A "+&lt;40 char sha&gt;" suffix put the string at 46 characters against a 32 character
    /// limit on POST /v1/devices/register, so registration failed with a 422 and the agent could
    /// never pair with anything. A build that reintroduces the suffix must not break pairing again.
    /// </remarks>
    internal static string CleanVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var trimmed = plus >= 0 ? version[..plus] : version;

        return trimmed.Length <= AgentVersionMaxLength
            ? trimmed
            : trimmed[..AgentVersionMaxLength];
    }

    /// <summary>The API's cap on agent_version. Mirrored, not guessed: see RegisterDeviceIn.</summary>
    private const int AgentVersionMaxLength = 32;

    /// <summary>Startup, then every six hours. From 10-agent-spec.md "Updates".</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// True when this process is running from an install rather than from a build output folder.
    /// </summary>
    /// <remarks>
    /// The installer lays the agent down under <c>%LOCALAPPDATA%\Programs\WarCommand</c>, and the
    /// self-update replaces that. A build tree has a <c>bin</c> segment and no install beside it;
    /// updating one downloads a release over the top of the thing being worked on.
    /// </remarks>
    private static bool IsInstalledBuild()
    {
        var directory = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "WarCommand");

        return directory.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
    }

    private readonly CancellationTokenSource _shutdown = new();

    private Mutex? _instanceLock;
    private string? _deviceToken;
    private IReadOnlyList<string> _webOrigins = [];
    private WarCommandApiClient? _client;
    private LocalPairingListener? _localLink;
    private string? _currentUserId;
    private TokenStore? _tokenStore;
    private SettingsStore? _settings;
    private TrayIconController? _tray;
    private DispatcherTimer? _pollTimer;
    private CoordinateSourceRegistry? _devCoordinateSources;
    private AgentWindow? _window;
    private BoardPresenter? _presenter;
    private OverlayController? _overlay;
    private GameWindowWatcher? _gameWatcher;
    private TrayMenuState _menuState = new();
    private WindowsStartup? _startup;
    private UpdateDownloader? _updates;
    private UpdateOffer? _offer;
    private DispatcherTimer? _updateTimer;
    private FileClientLog? _updateLog;
    private WasapiAudioCapture? _audioDevices;
    private DispatcherTimer? _configTimer;

    /// <summary>The deployment the current render is for, or null when standing on none.</summary>
    private Guid? _standingOn;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showRegistration;

    /// <summary>
    /// Starts the tray grey (unpaired/no session yet), then resolves the profile, ensures device
    /// credentials, and shows second-screen mode against whatever API the profile names.
    /// </summary>
    /// <remarks>
    /// The tray is built first and unconditionally: it is the only always-visible signal, so it has
    /// to survive an API that is down. <c>WARCOMMAND_TRAY_ONLY</c> stops here on purpose.
    /// </remarks>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray app outlives its windows. Without this, hiding second-screen mode would quit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var profile = AgentProfile.Resolve();
        var paths = profile.ResolvePaths();
        paths.EnsureCreated();

        if (!TryTakeSingleInstanceLock())
        {
            // Before the tray, before any registration: a second instance must leave no trace.
            // It does raise the running agent's window first: somebody who clicked the Start menu
            // shortcut asked to see WarCommand, and silently exiting answers that with nothing.
            new FileClientLog(paths).Warn("Another agent is already running. This launch is exiting.");
            AskRunningAgentToShowItself();
            Shutdown();
            return;
        }

        ListenForShowRequests();

        _webOrigins = profile.WebOrigins;
        _settings = new SettingsStore(paths);
        _menuState = _menuState with
        {
            IsDev = profile.IsDev,
            SettingsAvailable = true,
            ScreenCaptureEnabled = _settings.Current.ScreenCaptureEnabled,
            SoundsEnabled = _settings.Current.Sounds.AllSound,
        };
        // The registry is the source of truth for autostart, so it is read here rather than
        // mirrored: a user who switched it off in Task Manager must see "off" in the tray.
        // Absent in a dev launch, which must never register a developer's machine for startup.
        if (!profile.IsDev)
        {
            _startup = new WindowsStartup(new FileClientLog(paths));
            _startup.Reconcile();
            _menuState = _menuState with { StartWithWindows = _startup.IsEnabled };
        }

        _tray = new TrayIconController { StateProvider = () => _menuState };
        _tray.CommandInvoked += OnTrayCommand;
        _tray.SetTooltip(profile.IsTrayOnly ? "WarCommand (tray only)" : "WarCommand");
        _tray.ShowLocationHint();

        var log = new FileClientLog(paths);

        if (profile.IsTrayOnly)
        {
            // No API, no device registration, no window. The tray's own iteration loop.
            log.Info("Tray-only launch: the startup sequence stops after the icon.");
            return;
        }

        if (profile.IsOverlayDemo)
        {
            ShowOverlayDemo(log);
            return;
        }

        // No window is built here any more, and none is shown. The agent is a tray app: the queue
        // is the web board, the glance is the overlay, and the status is the tray's own rows. The
        // settings window is built on demand by EnsureWindow when somebody asks for it.
        //
        // The overlay is the only surface the presenter starts with. It joins the moment the
        // controller builds it, and is replayed up to date rather than waiting a poll.
        var presenter = new BoardPresenter();
        _presenter = presenter;

        presenter.SetHeader(new BoardHeader { Title = "WarCommand", Hint = "RightAlt+H ?" });
        presenter.SetStatus(FormattableString.Invariant(
            $"WARCOMMAND {AgentVersion}  /  {(profile.IsDev ? "DEV" : "PROD")}  /  {profile.ApiBaseAddress.Host}"));

        StartOverlay(presenter, log);

        if (profile.IsDev)
        {
            var fakeSource = new FakeCoordinateSource();
            _devCoordinateSources = new CoordinateSourceRegistry([fakeSource], DevCoordinateSources.FakeOnly());
        }

        try
        {
            await RunAgentLoopAsync(profile, paths, log, presenter).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            log.Error("Startup failed.", ex);
            presenter.ShowEmptyState("API unreachable", profile.ApiBaseAddress.Host);
        }
    }

    /// <summary>
    /// Draws the overlay on the primary monitor with the board from 06-overlay-ux.md and stops
    /// there. No API, no device registration, no window, no game.
    /// </summary>
    /// <remarks>
    /// Wardogs is not out. Without this loop the surface could only be looked at by somebody who
    /// has the game, which is nobody, and it would ship unseen. The demo plays the game window
    /// watcher's part by hand: it hands the controller a Show and no client rect, which is the
    /// same path a second-monitor user in Dim takes.
    /// </remarks>
    private void ShowOverlayDemo(FileClientLog log)
    {
        if (_settings is not { } settings)
        {
            return;
        }

        var surface = new OverlayWindow();
        var presenter = new BoardPresenter(surface.BoardView);
        _presenter = presenter;

        var controller = new OverlayController(
            Dispatcher,
            settings.Current with { OverlayMode = OverlayMode.AlwaysOn },
            factory: () => surface,
            notify: (title, body) => _tray?.ShowNotice(title, body));
        _overlay = controller;

        controller.OverlayVisibilityChanged(OverlayVisibility.Show);

        presenter.SetHeader(OverlayDemo.Header);
        presenter.RenderBoard(
            OverlayDemo.Rows,
            OverlayDemo.SecondaryStrip,
            OverlayDemo.OverflowCount,
            OverlayDemo.OverflowUrgentCount);

        _menuState = _menuState with
        {
            OverlayMode = controller.Mode.ToString(),
            OverlayHint = controller.Hint,
            OverlayDisplayDeviceName = settings.Current.DisplayDeviceName,
            Displays = OverlayController.Displays(),
        };

        log.Info("Overlay demo: the surface is on the primary monitor. Nothing else is running.");
    }

    /// <summary>
    /// Brings up the in-game surface and the watcher that decides when it draws. Both are built
    /// unconditionally: the overlay's own master switch decides whether anything appears, and the
    /// watcher has to run either way so the tray can say why it does not.
    /// </summary>
    /// <remarks>
    /// The watcher polls out of process and by window handle only. Nothing here opens the game, and
    /// nothing draws inside it: see 06-overlay-ux.md "Window" and binding rule 1.
    /// </remarks>
    private void StartOverlay(BoardPresenter presenter, FileClientLog log)
    {
        if (_settings is not { } settings)
        {
            return;
        }

        var controller = new OverlayController(
            Dispatcher,
            settings.Current,
            factory: () =>
            {
                var surface = new OverlayWindow();
                presenter.Add(surface.BoardView);
                return surface;
            },
            notify: (title, body) => _tray?.ShowNotice(title, body));

        controller.StateChanged += (_, _) => _menuState = _menuState with
        {
            OverlayMode = controller.Mode.ToString(),
            OverlayHint = controller.Hint,
        };
        _overlay = controller;

        _menuState = _menuState with
        {
            OverlayMode = settings.Current.OverlayMode.ToString(),
            OverlayDisplayDeviceName = settings.Current.DisplayDeviceName,
            Displays = OverlayController.Displays(),
        };

        // Always Hide. The tracker's Dim existed to keep drawing while unfocused, which is now
        // what OverlayMode.AlwaysOn means, and the controller reads the mode rather than this.
        var watcher = new GameWindowWatcher(
            BundledContracts.GameProfile().Current,
            controller,
            OverlayFocusBehavior.Hide);
        watcher.Start();
        _gameWatcher = watcher;

        // One subscription covers both ways settings move: the Overlay tab and the tray toggle
        // both go through Save, so neither can change the overlay without the other seeing it.
        settings.Changed += (_, saved) => controller.ApplySettings(saved);

        log.Info("Overlay armed. Watching for a game window.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// One case per <see cref="TrayCommand"/> the agent can honour today. A command whose subsystem
    /// is not wired up cannot arrive: <see cref="TrayMenu.Build"/> does not render its row until the
    /// matching <see cref="TrayMenuState"/> field is filled in.
    /// </summary>
    private void OnTrayCommand(object? sender, TrayCommandInvoked invoked)
    {
        ArgumentNullException.ThrowIfNull(invoked);

        switch (invoked.Command)
        {
            case TrayCommand.CopyPairingCode:
                CopyPairingCode();
                break;
            case TrayCommand.EnterPairingCode:
                ShowPairingCodeDialog();
                break;
            case TrayCommand.OpenWebBoard:
                OpenInBrowser(invoked.Argument);
                break;
            case TrayCommand.OpenSettings:
                ShowSettings();
                break;
            case TrayCommand.ToggleScreenCapture:
                ToggleSetting(s => s with { ScreenCaptureEnabled = !s.ScreenCaptureEnabled });
                break;
            case TrayCommand.ToggleSounds:
                ToggleSetting(s => s with { Sounds = s.Sounds with { AllSound = !s.Sounds.AllSound } });
                break;
            case TrayCommand.ToggleStartWithWindows:
                ToggleStartWithWindows();
                break;
            case TrayCommand.SelectOverlayMode:
                if (Enum.TryParse<OverlayMode>(invoked.Argument, out var mode))
                {
                    ToggleSetting(s => s with { OverlayMode = mode });
                }

                break;
            case TrayCommand.SelectOverlayDisplay:
                ToggleSetting(s => s with { DisplayDeviceName = invoked.Argument });
                break;
            case TrayCommand.CheckForUpdates:
                CheckForUpdatesNow();
                break;
            case TrayCommand.InstallUpdate:
                InstallUpdate();
                break;
            case TrayCommand.Quit:
                Shutdown();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Hands a URL to the default browser. Absolute https only, and one of the profile's own web
    /// origins: the argument comes from a menu row this process built, and checking it anyway is
    /// what stops a future row shipping a shell execute of something else.
    /// </summary>
    private void OpenInBrowser(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps
            || !_webOrigins.Contains(target.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser, or the shell refused. Nothing to say in a tray menu.
        }
    }

    /// <summary>
    /// This deployment's board on the web, or null when there is nothing to open. The browser
    /// addresses a group and a deployment by slug and never by uuid, so a membership that arrived
    /// without slugs yields no row rather than a link that 404s.
    /// </summary>
    private string? WebBoardUrl(ConfigMembership? membership)
    {
        if (_webOrigins.Count == 0
            || membership?.GroupSlug is not { Length: > 0 } group
            || membership.Deployment?.Slug is not { Length: > 0 } deployment)
        {
            return null;
        }

        return $"{_webOrigins[0]}/g/{Uri.EscapeDataString(group)}/d/{Uri.EscapeDataString(deployment)}";
    }

    /// <summary>
    /// The tray's Settings row and its double-click. One window, settings only: the queue is the
    /// web board and the glance is the overlay, so a third copy in a desktop tab was the same list
    /// a worse way.
    /// </summary>
    private void ShowSettings()
    {
        if (EnsureWindow() is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.ShowSettingsTab();
        _ = window.Activate();
    }

    /// <summary>
    /// The audio endpoint list, built once and held. Constructing it opens the shell's device
    /// enumerator and nothing else: no endpoint is opened, and no audio moves until capture is
    /// started, which this does not do.
    /// </summary>
    /// <remarks>
    /// Never constructed before it is needed, which today is the settings window. A machine with a
    /// broken audio stack must still get a tray icon, so a failure here is a settings window with
    /// Default only rather than an agent that does not start.
    /// </remarks>
    private WasapiAudioCapture? EnsureAudioDevices()
    {
        if (_audioDevices is not null)
        {
            return _audioDevices;
        }

        try
        {
            var capture = new WasapiAudioCapture();
            _audioDevices = capture;
            return capture;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one window, built on demand. Every launch that reaches the tray can open Settings,
    /// including the overlay demo, which previously showed a Settings row that did nothing because
    /// it returned before the window existed.
    /// </summary>
    private AgentWindow? EnsureWindow()
    {
        if (_window is { } existing)
        {
            return existing;
        }

        if (_settings is not { } settings)
        {
            return null;
        }

        var window = new AgentWindow(settings, EnsureAudioDevices());
        window.Closing += OnWindowClosing;
        _window = window;
        MainWindow = window;
        return window;
    }

    /// <summary>A tray toggle writes through the same store the settings window does.</summary>
    private void ToggleSetting(Func<AgentSettings, AgentSettings> change)
    {
        if (_settings is not { } store)
        {
            return;
        }

        store.Save(change(store.Current));
        _menuState = _menuState with
        {
            ScreenCaptureEnabled = store.Current.ScreenCaptureEnabled,
            SoundsEnabled = store.Current.Sounds.AllSound,
            OverlayMode = store.Current.OverlayMode.ToString(),
            OverlayHint = _overlay?.Hint,
            OverlayDisplayDeviceName = store.Current.DisplayDeviceName,
        };
    }

    // --- updates --------------------------------------------------------------------------------

    /// <summary>
    /// Checks now, then every six hours. A failed check is not an error the user sees: the tray
    /// simply keeps showing no update, which is what it showed a moment ago.
    /// </summary>
    private void StartUpdateChecks(AgentPaths paths, FileClientLog log)
    {
        if (!IsInstalledBuild())
        {
            // A build running out of bin/ or a dotnet run reports the Directory.Build.props default
            // of 0.0.0, which is below every release on purpose. So the tray offered the last
            // release, and one click REPLACED the newer working build with an older published one
            // and relaunched into it. An update is for an installed agent; this is not one.
            log.Info("Not an installed build: update checks are off.");
            return;
        }

        // HttpClient's 100-second default covers the whole operation, streamed body included, so a
        // 59 MB installer needs a sustained 5 Mbps to beat it. Below that the transfer is cancelled
        // mid-stream, the cancellation is swallowed as "try again later", and the agent retries for
        // ever and never updates. The shutdown token is what bounds this transfer.
        _updates = new UpdateDownloader(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            paths,
            log);
        _updateLog = log;

        var timer = new DispatcherTimer { Interval = UpdateCheckInterval };
        timer.Tick += async (_, _) => await CheckForUpdateAsync(log).ConfigureAwait(true);
        timer.Start();
        _updateTimer = timer;

        // The row exists from here on, whether or not anything is on offer: six hours is a long
        // time to have no way of asking, and "am I on the latest build" is the first question
        // anybody asks when something looks wrong.
        _menuState = _menuState with { UpdateCheckAvailable = true, RunningVersion = AgentVersion };

        _ = CheckForUpdateAsync(log);
    }

    /// <summary>
    /// The tray's Check for updates row. Same call as the six-hourly one, with the row disabled
    /// while it is in flight so a second click cannot start a second check.
    /// </summary>
    private async void CheckForUpdatesNow()
    {
        if (_updateLog is not { } log || _menuState.UpdateCheckInProgress)
        {
            return;
        }

        _menuState = _menuState with { UpdateCheckInProgress = true };

        try
        {
            await CheckForUpdateAsync(log).ConfigureAwait(true);
        }
        finally
        {
            _menuState = _menuState with { UpdateCheckInProgress = false };
        }
    }

    /// <summary>
    /// Asks the API what is published and lets <see cref="UpdateDecision"/> rule on it. Every
    /// refusal lives there, so this method only maps the wire shape and stores the outcome.
    /// </summary>
    private async Task CheckForUpdateAsync(FileClientLog log)
    {
        if (_client is not { } client)
        {
            return;
        }

        PublishedRelease? published = null;
        try
        {
            var latest = await client.GetLatestAgentAsync(_shutdown.Token).ConfigureAwait(true);
            published = new PublishedRelease
            {
                Version = latest.Version,
                Notes = latest.Notes,
                Url = latest.Url?.ToString(),
                Sha256 = latest.Sha256,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WarCommandApiException)
        {
            // 404 until the first release is tagged, which is the normal state, not a fault.
            log.Info($"Update check did not complete: {ex.GetType().Name}");
            return;
        }

        _ = SemVersion.TryParse(AgentVersion, out var running);
        var availability = UpdateDecision.Evaluate(running, published, GameIsRunning(), out var offer);
        _offer = offer;

        _menuState = _menuState with
        {
            UpdateVersion = offer?.Version.ToString(),
            UpdateWaitingForGameToClose = availability == UpdateAvailability.WaitingForGameToClose,
        };

        if (offer is not null)
        {
            log.Info($"Update {offer.Version} is published; running {AgentVersion}.");
        }
    }

    /// <summary>
    /// True when any process named in <c>game.process_names</c> is up. Out of process and by name
    /// only: this opens no handle into the game and reads nothing from it.
    /// </summary>
    private static bool GameIsRunning()
    {
        try
        {
            foreach (var name in BundledContracts.GameProfile().Current.Game.ProcessNames)
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Enumerating processes can race one exiting. Treat that as "cannot tell", and the
            // safe answer to "cannot tell" is that the game is up, so nothing installs over it.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Downloads the offer, verifies its digest, runs the installer and quits so it can replace
    /// this exe. Refuses while the game is running, which the menu row already says.
    /// </summary>
    private async void InstallUpdate()
    {
        if (_offer is not { } offer || _updates is not { } downloader || _menuState.UpdateInProgress)
        {
            return;
        }

        if (GameIsRunning())
        {
            _menuState = _menuState with { UpdateWaitingForGameToClose = true };
            return;
        }

        _menuState = _menuState with { UpdateInProgress = true };

        try
        {
            var installer = await downloader.FetchAsync(offer, _shutdown.Token).ConfigureAwait(true);
            downloader.Prune(installer);

            if (downloader.Launch(installer))
            {
                Shutdown();
                return;
            }
        }
        catch (UpdateVerificationException)
        {
            // Deliberately not retried and deliberately not surfaced as an installable offer any
            // more: bytes that do not match the published digest are not our build.
            _offer = null;
            _menuState = _menuState with { UpdateVersion = null };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // The offer stands; the next click or the next six-hourly check tries again.
        }

        _menuState = _menuState with { UpdateInProgress = false };
    }

    /// <summary>
    /// Flips the HKCU Run value and re-reads it. The menu shows what the registry says afterwards,
    /// not what was asked for, so a write that silently failed cannot leave the row lying.
    /// </summary>
    private void ToggleStartWithWindows()
    {
        if (_startup is not { } startup)
        {
            return;
        }

        _ = startup.Set(!startup.IsEnabled);
        _menuState = _menuState with { StartWithWindows = startup.IsEnabled };
    }

    /// <summary>Puts the agent's own pairing code on the clipboard, for the web to take.</summary>
    private void CopyPairingCode()
    {
        if (_menuState.PairingCode is { } code)
        {
            System.Windows.Clipboard.SetText(code);
        }
    }

    /// <summary>
    /// The other direction: a code the web issued, typed in here. The claim binds this device to
    /// whoever minted the code, which is whoever is signed in on the web, guest account included.
    /// </summary>
    private void ShowPairingCodeDialog()
    {
        if (_client is not { } client || _deviceToken is not { } deviceToken)
        {
            return;
        }

        var dialog = new PairingCodeWindow(async (code, ct) =>
        {
            var claim = await client.ClaimPairingByPairCodeAsync(deviceToken, code, pttBinding: null, ct)
                .ConfigureAwait(true);

            // Saved here, not by the caller: the pairing loop watches the store and stops the
            // moment tokens land, whichever direction they came from.
            _tokenStore?.SaveIssued(ToAgentTokens(claim.Tokens));
        });

        if (_window is { IsVisible: true })
        {
            dialog.Owner = _window;
        }

        _ = dialog.ShowDialog();
    }

    /// <summary>
    /// The close button hides the settings window rather than destroying it. A closed WPF window
    /// cannot be shown again, so closing it would make the tray's Settings row a dead click.
    /// </summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (sender is not AgentWindow window)
        {
            return;
        }

        e.Cancel = true;
        window.Hide();
    }

    public void Dispose()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _shutdown.Dispose();
        _localLink?.Dispose();
        _localLink = null;
        _pollTimer?.Stop();
        _pollTimer = null;
        _configTimer?.Stop();
        _configTimer = null;
        _updateTimer?.Stop();
        _updateTimer = null;
        _ = _showRegistration?.Unregister(null);
        _showRegistration = null;
        _showRequest?.Dispose();
        _showRequest = null;
        _audioDevices?.Dispose();
        _audioDevices = null;
        _gameWatcher?.Dispose();
        _gameWatcher = null;
        _overlay?.Dispose();
        _overlay = null;
        _tray?.Dispose();
        _tray = null;
        ReleaseSingleInstanceLock();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// True when this process is the only agent. A mutex rather than a process-name scan: the scan
    /// races two launches that start together, and it cannot tell a crashed process from a live
    /// one. An abandoned mutex, which is what a crash leaves behind, is acquired here rather than
    /// treated as a conflict, so a hard kill never locks the next launch out.
    /// </summary>
    private bool TryTakeSingleInstanceLock()
    {
        _instanceLock = new Mutex(initiallyOwned: false, SingleInstanceMutexName);

        try
        {
            return _instanceLock.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// Listens for a second launch asking to be seen. The agent lives in the tray with no window
    /// of its own, so the Start menu shortcut is how most people will reach it, and clicking it
    /// while the agent is already running has to do something. It shows the window.
    /// </summary>
    private void ListenForShowRequests()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showRequest = signal;
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            signal,
            (_, _) => Dispatcher.BeginInvoke(ShowSettings),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Asks the running agent to show itself. Called by the launch that lost the mutex, just
    /// before it exits: without it a second click of the shortcut does nothing at all, which is
    /// indistinguishable from an agent that failed to start.
    /// </summary>
    private static void AskRunningAgentToShowItself()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                using (signal)
                {
                    _ = signal.Set();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is on its way down. Nothing to show.
        }
        catch (UnauthorizedAccessException)
        {
            // Another desktop session owns it. Not our agent to raise.
        }
    }

    private void ReleaseSingleInstanceLock()
    {
        if (_instanceLock is null)
        {
            return;
        }

        try
        {
            _instanceLock.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner: this is the second instance exiting, which never took the lock.
        }

        _instanceLock.Dispose();
        _instanceLock = null;
    }

    /// <summary>
    /// Runs the dev-profile coordinate sweep through the same <see cref="CoordinateSourceRegistry"/>
    /// the real PTT path would use, so this proves the fake source's wiring rather than just its
    /// constructor. There is no game, no capture and no microphone anywhere in this call.
    /// </summary>
    private async Task OnSimulatePttAsync(BoardView window)
    {
        if (_devCoordinateSources is null)
        {
            return;
        }

        var point = await _devCoordinateSources.ReadAsync(CancellationToken.None).ConfigureAwait(true);
        window.ShowSimulatedPoint(point is null
            ? "no coordinate source answered"
            : FormattableString.Invariant($"{point.Source}: x{point.X:0.00} y{point.Y:0.00}"));
    }

    private async Task RunAgentLoopAsync(AgentProfile profile, AgentPaths paths, FileClientLog log, BoardPresenter presenter)
    {
        var tokenStore = new TokenStore(paths, log: log);
        _tokenStore = tokenStore;
        var apiOptions = new ApiClientOptions
        {
            BaseAddress = profile.ApiBaseAddress,
            AgentVersion = AgentVersion,
        };

        // The token source needs the client to refresh through, and the client needs the token
        // source to authenticate with. RefreshingAgentTokenSource takes the refresh call as a
        // delegate for exactly this reason: the composition root closes over the client it builds
        // on the very next line.
        WarCommandApiClient? client = null;
        var tokenSource = new RefreshingAgentTokenSource(
            tokenStore,
            (refreshToken, ct) => client!.RefreshTokensAsync(refreshToken, ct));
        client = WarCommandApiClient.Create(apiOptions, tokenSource, log);
        _client = client;

        // Unauthenticated, so it runs before sign-in and keeps running whatever happens below.
        StartUpdateChecks(paths, log);

        var me = await AuthenticateAsync(client, tokenStore, paths, profile, log).ConfigureAwait(true);
        AdoptAccount(me);
        await RenderForAsync(client, me, presenter, log).ConfigureAwait(true);
        StartConfigWatch(client, presenter, log);
    }

    /// <summary>
    /// Records which account the agent now holds. The loopback hello reports this id, which is how
    /// a page tells that its own account and the agent's have diverged.
    /// </summary>
    private void AdoptAccount(MeResponse me)
    {
        _currentUserId = me.User.Id.ToString();
        _menuState = _menuState with
        {
            IsPaired = true,
            PairingCode = null,
            Callsign = me.User.Callsign,
        };
        _tray?.SetTooltip($"WarCommand ({me.User.Callsign})");
    }

    /// <summary>
    /// Renders everything that depends on which account this is: the header, the board and the
    /// tray's counts. Called at startup and again whenever the linked account changes.
    /// </summary>
    private async Task RenderForAsync(
        WarCommandApiClient client, MeResponse me, BoardPresenter presenter, FileClientLog log)
    {
        // Reaching /v1/me is the connection, deployment or not. An agent standing on no deployment
        // is idle, not offline, and a grey dot would say the opposite.
        _tray?.SetConnectionState(RealtimeConnectionState.Connected);

        var membership = me.Memberships.FirstOrDefault(m => m.Deployment is not null);

        if (membership?.Deployment is null)
        {
            // Signed in, standing nowhere. The account goes in the header either way: an agent that
            // shows nothing about who it is reads as signed out, whatever it is holding.
            var group = me.Memberships.Count > 0 ? me.Memberships[0] : null;
            presenter.SetHeader(new BoardHeader
            {
                Title = group is null ? "WarCommand" : $"WarCommand / {group.GroupName}",
                Right = me.User.Callsign,
                Hint = "RightAlt+H ?",
            });
            presenter.ShowEmptyState(
                group is null ? "No group" : "No live deployment",
                group is null
                    ? $"signed in as {me.User.Callsign}, join from the web"
                    : $"signed in as {me.User.Callsign}, start one from the web");
            _menuState = _menuState with
            {
                GroupName = group?.GroupName,
                MatchName = null,
                OpenRequestCount = null,
                WebBoardUrl = null,
            };
            _standingOn = null;
            log.Info("No deployment: showing the cold-start empty state.");
            return;
        }

        var catalog = BundledContracts.Catalog().Current;
        var board = new BoardState(membership.MembershipId, catalog.GrammarRules);
        var deploymentId = membership.Deployment.Id;
        board.EnterDeployment(deploymentId, DateTimeOffset.UtcNow, draft: null);

        // Only the rows this build can honour are filled in. The group, match, map, microphone and
        // push-to-talk fields stay null until their subsystem lands, and TrayMenu.Build leaves the
        // rows out, so the menu can never offer a click that does nothing.
        _standingOn = deploymentId;
        _menuState = _menuState with
        {
            GroupName = membership.GroupName,
            GroupMemberCount = membership.Deployment.MemberCount,
            MatchName = membership.Deployment.Label,
            MatchPeopleCount = membership.Deployment.MemberCount,
            OpenRequestCount = 0,
            MyRequestCount = 0,
            WebBoardUrl = WebBoardUrl(membership),
        };

        presenter.SetHeader(new BoardHeader
        {
            Title = $"{membership.GroupName} / {membership.Deployment.Label}",
            PeopleCount = membership.Deployment.MemberCount,
            Where = membership.ParticipantKind == "visitor" ? "visitor" : null,
            Right = membership.Deployment.InviteCode is { } invite
                ? $"invite {invite}"
                : me.User.Callsign,
            Roles = string.Join(' ', membership.SubscribedRoleIds),
            Hint = "RightAlt+H ?",
        });

        await RefreshBoardAsync(client, catalog, board, deploymentId, membership.MembershipId, presenter, log)
            .ConfigureAwait(true);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                await RefreshBoardAsync(client, catalog, board, deploymentId, membership.MembershipId, presenter, log)
                    .ConfigureAwait(true);
            }
            catch (WarCommandApiException ex)
            {
                _tray?.SetConnectionState(RealtimeConnectionState.Reconnecting);
                log.Warn($"Board poll failed: {ex.Code}");
            }
            catch (HttpRequestException ex)
            {
                _tray?.SetConnectionState(RealtimeConnectionState.Reconnecting);
                log.Warn($"Board poll failed: {ex.Message}");
            }
        };
        timer.Start();
        _pollTimer = timer;
    }

    /// <summary>
    /// Re-reads <c>/v1/me</c> and re-renders when the deployment underneath the agent changes.
    /// </summary>
    /// <remarks>
    /// The realtime socket is what is supposed to deliver this, and it is not wired up. Until it
    /// is, <c>/v1/me</c> was read exactly twice: at startup and when the linked account changed.
    /// So joining a deployment from the web after the agent started reached it never: it kept
    /// polling the board of wherever it happened to be at launch, kept the old deployment in the
    /// tray, and offered a web board link to the wrong match. Standing on none at launch was worse,
    /// because that branch starts no timer at all and the agent sat on the cold-start state for the
    /// rest of the session.
    /// <para>
    /// Fifteen seconds rather than five: this is a whole config payload, and a deployment hop is
    /// something a person does between rounds, not mid-fight.
    /// </para>
    /// </remarks>
    private void StartConfigWatch(WarCommandApiClient client, BoardPresenter presenter, FileClientLog log)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                var me = await client.GetMeAsync(_shutdown.Token).ConfigureAwait(true);
                var deployment = me.Memberships
                    .FirstOrDefault(m => m.Deployment is not null)?.Deployment?.Id;

                if (deployment == _standingOn)
                {
                    return;
                }

                log.Info($"Deployment changed to {deployment?.ToString() ?? "none"}. Re-rendering.");
                _pollTimer?.Stop();
                _pollTimer = null;
                await RenderForAsync(client, me, presenter, log).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is WarCommandApiException or HttpRequestException or TaskCanceledException)
            {
                _tray?.SetConnectionState(RealtimeConnectionState.Reconnecting);
                log.Warn($"Config poll failed: {ex.GetType().Name}");
            }
        };
        timer.Start();
        _configTimer = timer;
    }

    /// <summary>
    /// Ensures credentials and reads <c>/v1/me</c>, re-registering once if what is on disk is
    /// rejected. A local API whose database has been reset leaves a device id and a refresh token
    /// that no longer exist server-side, and without this the agent would fail every launch until
    /// somebody deleted tokens.dat by hand.
    /// </summary>
    private async Task<MeResponse> AuthenticateAsync(
        WarCommandApiClient client, TokenStore tokenStore, AgentPaths paths, AgentProfile profile, FileClientLog log)
    {
        try
        {
            await EnsureCredentialsAsync(client, tokenStore, paths, profile, log).ConfigureAwait(true);
            _menuState = _menuState with { IsPaired = tokenStore.Current is not null };
            return await client.GetMeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (WarCommandApiException ex) when (ex.Code == ErrorCodes.Unauthenticated)
        {
            log.Warn("Stored credentials were rejected. Clearing them and registering again.");
            tokenStore.Clear("the API rejected the stored credentials");
        }

        await EnsureCredentialsAsync(client, tokenStore, paths, profile, log).ConfigureAwait(true);
        _menuState = _menuState with { IsPaired = tokenStore.Current is not null };
        return await client.GetMeAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Registers the device, then gets it a token. The token is somebody's: the account the agent
    /// ends up holding is whoever is signed in on the web when they claim this device, and a guest
    /// account is a signed-in account.
    /// </summary>
    /// <remarks>
    /// Cold-start activation mints a brand new guest user of the agent's own, so it is never the
    /// default. Doing it automatically signs the agent in as somebody other than the person sitting
    /// at the browser, and the two accounts can never be merged afterwards.
    /// </remarks>
    private async Task EnsureCredentialsAsync(
        WarCommandApiClient client, TokenStore tokenStore, AgentPaths paths, AgentProfile profile, FileClientLog log)
    {
        if (tokenStore.DeviceId is null)
        {
            var installId = InstallId.LoadOrCreate(paths);
            var registration = await client.RegisterDeviceAsync(
                new DeviceRegisterRequest
                {
                    InstallId = installId,
                    MachineLabel = Environment.MachineName,
                    AgentVersion = AgentVersion,
                    PttBinding = null,
                },
                CancellationToken.None).ConfigureAwait(true);
            tokenStore.SaveDeviceRegistration(registration.DeviceId, registration.DeviceToken);
            log.Info("Device registered with the API.");
        }
        else
        {
            log.Info("Reusing a device registration from a previous run.");
        }

        var deviceId = tokenStore.DeviceId!.Value;
        var deviceToken = tokenStore.DeviceToken!;
        _deviceToken = deviceToken;

        StartLocalLink(client, tokenStore, deviceToken, log);

        if (tokenStore.Current is not null)
        {
            log.Info("Reusing tokens from a previous run: no pairing needed.");
            return;
        }

        if (profile.PairCode is { } pairCode)
        {
            var claim = await client.ClaimPairingByPairCodeAsync(deviceToken, pairCode, pttBinding: null, CancellationToken.None)
                .ConfigureAwait(true);
            tokenStore.SaveIssued(ToAgentTokens(claim.Tokens));
            log.Info("Device paired with a web-issued pairing code.");
            return;
        }

        if (profile.IsColdStart)
        {
            var activation = await client.ActivateDeviceAsync(deviceId, deviceToken, callsignHint: null, CancellationToken.None)
                .ConfigureAwait(true);
            tokenStore.SaveIssued(ToAgentTokens(activation.Tokens));
            log.Info("Device activated cold-start: a guest user of the agent's own, with no membership.");
            return;
        }

        await WaitForPairingAsync(client, tokenStore, deviceId, deviceToken, log).ConfigureAwait(true);
    }

    /// <summary>
    /// Unpaired mode: startup step 5. Shows a pairing code and polls until a live web session
    /// claims this device, then keeps the tokens that claim issued.
    /// </summary>
    /// <remarks>
    /// The poll runs on the bare device token and reads no group and no request. It never times
    /// out: an agent installed before its owner has finished signing in is the ordinary case, and
    /// giving up would leave a tray icon that has quietly stopped trying.
    /// </remarks>
    private async Task WaitForPairingAsync(
        WarCommandApiClient client, TokenStore tokenStore, Guid deviceId, string deviceToken, FileClientLog log)
    {
        await ShowPairingCodeAsync(client, deviceId, deviceToken, log).ConfigureAwait(true);
        _presenter?.ShowEmptyState("Not set up", _menuState.PairingCode is { } shown
            ? $"pairing code {shown}, or enter one from the web"
            : "pair from the web");

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var poll = await client.PollPairingAsync(deviceToken, _shutdown.Token).ConfigureAwait(true);
                if (poll.Claim is { } claim)
                {
                    tokenStore.SaveIssued(ToAgentTokens(claim.Tokens));
                    _menuState = _menuState with { IsPaired = true, PairingCode = null };
                    log.Info("Device paired: holding the account that claimed it.");
                    return;
                }
            }
            catch (WarCommandApiException ex)
            {
                log.Warn($"Pairing poll failed: {ex.Code}");
            }
            catch (HttpRequestException ex)
            {
                log.Warn($"Pairing poll failed: {ex.Message}");
            }

            // A code redeemed from the tray lands here rather than through the poll.
            if (tokenStore.Current is not null)
            {
                _menuState = _menuState with { IsPaired = true, PairingCode = null };
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Opens the loopback link and leaves it open for the life of the process, paired or not.
    /// </summary>
    /// <remarks>
    /// Closing it once paired stranded anybody who signed into a different account afterwards: the
    /// agent kept the old one and the web had no way to reach it. A re-link swaps the account.
    /// </remarks>
    private void StartLocalLink(
        WarCommandApiClient client, TokenStore tokenStore, string deviceToken, FileClientLog log)
    {
        _localLink?.Dispose();
        _localLink = new LocalPairingListener(
            new LocalPairingOptions { AllowedOrigins = _webOrigins, AgentVersion = AgentVersion },
            async (ticket, ct) =>
            {
                var claim = await client.ClaimPairingByTicketAsync(deviceToken, ticket, pttBinding: null, ct)
                    .ConfigureAwait(false);
                tokenStore.SaveIssued(ToAgentTokens(claim.Tokens));
            },
            () => _currentUserId,
            log,
            () => tokenStore.DeviceId?.ToString());

        // Raised on the listener's thread, so the reload is marshalled back onto the dispatcher.
        _localLink.Paired += (_, _) => Dispatcher.InvokeAsync(async () =>
            await ReloadAfterLinkAsync(client, log).ConfigureAwait(true));

        _ = _localLink.Start();
    }

    /// <summary>
    /// Re-reads the account after a link and re-renders. The board, the header and the tray all
    /// come from <c>/v1/me</c>, so this is the whole of what changes when the account does.
    /// </summary>
    private async Task ReloadAfterLinkAsync(WarCommandApiClient client, FileClientLog log)
    {
        if (_presenter is not { } presenter)
        {
            return;
        }

        _pollTimer?.Stop();
        _pollTimer = null;

        try
        {
            var me = await client.GetMeAsync(_shutdown.Token).ConfigureAwait(true);
            AdoptAccount(me);
            log.Info("Linked to a different account. Reloading.");
            await RenderForAsync(client, me, presenter, log).ConfigureAwait(true);
        }
        catch (WarCommandApiException ex)
        {
            log.Warn($"Reload after linking failed: {ex.Code}");
        }
        catch (HttpRequestException ex)
        {
            log.Warn($"Reload after linking failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mints the code the web takes. Losing it is not fatal: the other direction, a web-issued code
    /// typed into the tray, still pairs the device.
    /// </summary>
    private async Task ShowPairingCodeAsync(
        WarCommandApiClient client, Guid deviceId, string deviceToken, FileClientLog log)
    {
        try
        {
            var code = await client.CreatePairingCodeAsync(deviceId, deviceToken, _shutdown.Token).ConfigureAwait(true);
            _menuState = _menuState with { PairingCode = code.Code, IsPaired = false };
        }
        catch (WarCommandApiException ex)
        {
            log.Warn($"Could not mint a pairing code: {ex.Code}");
        }
        catch (HttpRequestException ex)
        {
            log.Warn($"Could not mint a pairing code: {ex.Message}");
        }
    }

    /// <summary>TokenPair is the wire shape; AgentTokens is what TokenStore persists, with an
    /// absolute expiry instead of a relative one. Never logs either.</summary>
    private static AgentTokens ToAgentTokens(TokenPair pair)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTokens
        {
            AgentToken = pair.AgentToken,
            RefreshToken = pair.RefreshToken,
            ExpiresAt = pair.ExpiresIn is { } seconds ? now.AddSeconds(seconds) : null,
            UpdatedAt = now,
        };
    }

    private async Task RefreshBoardAsync(
        WarCommandApiClient client,
        Catalog catalog,
        BoardState board,
        Guid deploymentId,
        Guid viewerId,
        BoardPresenter presenter,
        FileClientLog log)
    {
        var now = DateTimeOffset.UtcNow;
        var wire = await client.GetBoardAsync(deploymentId, query: null, CancellationToken.None).ConfigureAwait(true);
        foreach (var body in wire)
        {
            var overlayLabel = catalog.RequestType(body.TypeId)?.OverlayLabel ?? body.TypeId.ToUpperInvariant();
            board.Upsert(body.ToBoardRow(overlayLabel), now);
        }

        var rows = board.Rows.Select(r => BoardRowViewModel.FromPrimary(r, viewerId, now)).ToList();
        var secondary = board.SecondaryStrip.Select(r => BoardRowViewModel.FromSecondary(r, now)).ToList();
        var overflow = board.Overflow;
        var overflowUrgent = overflow.Count(r => r.Priority == Priority.Urgent);

        presenter.RenderBoard(rows, secondary, overflow.Count, overflowUrgent);

        // The realtime socket is not wired up yet, so nothing else ever called SetConnectionState
        // and the icon sat grey on a fully signed-in agent that was reaching the server every five
        // seconds. A poll that came back IS the connection, and it is the honest signal until the
        // socket lands.
        _tray?.SetConnectionState(RealtimeConnectionState.Connected);
        _menuState = _menuState with
        {
            OpenRequestCount = rows.Count + secondary.Count + overflow.Count,
            MyRequestCount = rows.Count(r => r.Accent == RowAccent.Mine),
        };
        log.Info($"Board refreshed: {rows.Count} on the board, {secondary.Count} on the secondary strip.");
    }
}
