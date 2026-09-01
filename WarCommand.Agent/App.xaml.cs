using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Link;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Client.Tokens;
using WarCommand.Agent.Core;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Dev;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Core.Tray;
using WarCommand.Agent.Dev;
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

    private const string AgentVersion = "0.0.0-dev";

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
    private BoardView? _board;
    private TrayMenuState _menuState = new();

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
            new FileClientLog(paths).Warn("Another agent is already running. This launch is exiting.");
            Shutdown();
            return;
        }

        _webOrigins = profile.WebOrigins;
        _settings = new SettingsStore(paths);
        _menuState = _menuState with
        {
            IsDev = profile.IsDev,
            SettingsAvailable = true,
            ScreenCaptureEnabled = _settings.Current.ScreenCaptureEnabled,
            SoundsEnabled = _settings.Current.Sounds.AllSound,
        };
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

        var window = new AgentWindow(_settings, devices: null);
        _window = window;
        _board = window.BoardView;
        MainWindow = window;
        window.Closing += OnWindowClosing;
        var board = window.BoardView;
        board.SetHeader(new BoardHeader { Title = "WarCommand", Hint = "RightAlt+H ?" });
        board.SetStatus(FormattableString.Invariant(
            $"WARCOMMAND 0.0.0-dev  /  {(profile.IsDev ? "DEV" : "PROD")}  /  {profile.ApiBaseAddress.Host}"));
        window.Show();
        _menuState = _menuState with { SecondScreenVisible = true };

        if (profile.IsDev)
        {
            var fakeSource = new FakeCoordinateSource();
            _devCoordinateSources = new CoordinateSourceRegistry([fakeSource], DevCoordinateSources.FakeOnly());
            board.SetDevControlsVisible(true);
            board.SimulatePttRequested += async (_, _) => await OnSimulatePttAsync(board).ConfigureAwait(true);
        }

        try
        {
            await RunAgentLoopAsync(profile, paths, log, board).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            log.Error("Startup failed.", ex);
            board.ShowEmptyState("API unreachable", profile.ApiBaseAddress.Host);
        }
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
    private void OnTrayCommand(object? sender, TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ToggleSecondScreen:
                ToggleSecondScreen();
                break;
            case TrayCommand.CopyPairingCode:
                CopyPairingCode();
                break;
            case TrayCommand.EnterPairingCode:
                ShowPairingCodeDialog();
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
            case TrayCommand.Quit:
                Shutdown();
                break;
            default:
                break;
        }
    }

    /// <summary>The tray's Settings row. Same window as the board, a different tab.</summary>
    private void ShowSettings() => ShowWindowOn(settings: true);

    /// <summary>
    /// Brings the one window up on the tab the caller wants. The tray's double-click and its
    /// Settings row are the same action but for which tab lands in front.
    /// </summary>
    private void ShowWindowOn(bool settings)
    {
        if (_window is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (settings)
        {
            window.ShowSettingsTab();
        }
        else
        {
            window.ShowBoardTab();
        }

        _ = window.Activate();
        _menuState = _menuState with { SecondScreenVisible = true };
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
        };
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

    /// <summary>Shows or hides the one window, on its Board tab. Never closes it.</summary>
    private void ToggleSecondScreen()
    {
        if (_window is not { } window)
        {
            return;
        }

        if (window.IsVisible && window.Tabs.SelectedIndex == 0)
        {
            window.Hide();
            _menuState = _menuState with { SecondScreenVisible = false };
            return;
        }

        ShowWindowOn(settings: false);
    }

    /// <summary>
    /// The close button hides the window rather than destroying it. A closed WPF window cannot be
    /// shown again, so closing it would make the tray's Second-screen mode row a dead toggle.
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
        _menuState = _menuState with { SecondScreenVisible = false };
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

    private async Task RunAgentLoopAsync(AgentProfile profile, AgentPaths paths, FileClientLog log, BoardView window)
    {
        var tokenStore = new TokenStore(paths, log: log);
        _tokenStore = tokenStore;
        var apiOptions = new ApiClientOptions
        {
            BaseAddress = profile.ApiBaseAddress,
            AgentVersion = "0.0.0-dev",
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

        var me = await AuthenticateAsync(client, tokenStore, paths, profile, log).ConfigureAwait(true);
        AdoptAccount(me);
        await RenderForAsync(client, me, window, log).ConfigureAwait(true);
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
        WarCommandApiClient client, MeResponse me, BoardView window, FileClientLog log)
    {
        var membership = me.Memberships.FirstOrDefault(m => m.Deployment is not null);

        if (membership?.Deployment is null)
        {
            // Signed in, standing nowhere. The account goes in the header either way: an agent that
            // shows nothing about who it is reads as signed out, whatever it is holding.
            var group = me.Memberships.Count > 0 ? me.Memberships[0] : null;
            window.SetHeader(new BoardHeader
            {
                Title = group is null ? "WarCommand" : $"WarCommand / {group.GroupName}",
                Right = me.User.Callsign,
                Hint = "RightAlt+H ?",
            });
            window.ShowEmptyState(
                group is null ? "No group" : "No live deployment",
                group is null
                    ? $"signed in as {me.User.Callsign}, join from the web"
                    : $"signed in as {me.User.Callsign}, start one from the web");
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
        _menuState = _menuState with { OpenRequestCount = 0, MyRequestCount = 0 };

        window.SetHeader(new BoardHeader
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

        await RefreshBoardAsync(client, catalog, board, deploymentId, membership.MembershipId, window, log)
            .ConfigureAwait(true);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                await RefreshBoardAsync(client, catalog, board, deploymentId, membership.MembershipId, window, log)
                    .ConfigureAwait(true);
            }
            catch (WarCommandApiException ex)
            {
                log.Warn($"Board poll failed: {ex.Code}");
            }
            catch (HttpRequestException ex)
            {
                log.Warn($"Board poll failed: {ex.Message}");
            }
        };
        timer.Start();
        _pollTimer = timer;
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
                    AgentVersion = "0.0.0-dev",
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
        _board?.ShowEmptyState("Not set up", _menuState.PairingCode is { } shown
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
        if (_board is not { } window)
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
            await RenderForAsync(client, me, window, log).ConfigureAwait(true);
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
        BoardView window,
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

        window.RenderBoard(rows, secondary, overflow.Count, overflowUrgent);
        _menuState = _menuState with
        {
            OpenRequestCount = rows.Count + secondary.Count + overflow.Count,
            MyRequestCount = rows.Count(r => r.Accent == RowAccent.Mine),
        };
        log.Info($"Board refreshed: {rows.Count} on the board, {secondary.Count} on the secondary strip.");
    }
}
