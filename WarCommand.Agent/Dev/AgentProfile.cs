using System.Linq;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Dev;

/// <summary>
/// Which launch this is: production, pointed at the real API, or the local dev loop, pointed at
/// whatever <c>WARCOMMAND_API_BASE_URL</c> names. Read from environment variables only, so a
/// Release build behaves identically until somebody opts in on their own machine.
/// </summary>
/// <remarks>
/// This is wiring, not a game fact: it decides which URL the agent talks to and where its tokens
/// live, never anything about Wardogs itself. contracts/game-profile.json is untouched either way.
/// </remarks>
public sealed class AgentProfile
{
    /// <summary>Set to "dev" to run the local loop. Anything else, or unset, is production.</summary>
    public const string ProfileVariable = "WARCOMMAND_PROFILE";

    /// <summary>Overrides the API base address. Must be https; see TransportSecurity.</summary>
    public const string ApiBaseUrlVariable = "WARCOMMAND_API_BASE_URL";

    /// <summary>A web-issued pairing code to redeem once. Never persisted, never logged.</summary>
    public const string PairCodeVariable = "WARCOMMAND_PAIR_CODE";

    /// <summary>
    /// Set to 1 to launch the tray and nothing else: no API call, no device registration, no
    /// window. Implies the dev profile. This is the tray's own iteration loop, so working on the
    /// icon or the menu needs neither docker nor the TLS proxy. See DEVELOPING.md.
    /// </summary>
    public const string TrayOnlyVariable = "WARCOMMAND_TRAY_ONLY";

    /// <summary>
    /// Set to 1 to draw the overlay on the primary monitor with the board from 06-overlay-ux.md,
    /// and stop there: no API, no device registration, no game. Implies the dev profile. This is
    /// the overlay's iteration loop, and the only way to look at the surface before the game ships.
    /// See DEVELOPING.md.
    /// </summary>
    public const string OverlayDemoVariable = "WARCOMMAND_OVERLAY_DEMO";

    /// <summary>
    /// Set to 1 to take the cold-start path: activate the device into a brand new guest user of its
    /// own instead of waiting to be paired. Never the default, because the account the agent should
    /// hold is whoever is signed in on the web, guest account included.
    /// </summary>
    public const string ColdStartVariable = "WARCOMMAND_COLD_START";

    /// <summary>
    /// Origins the loopback pairing listener will talk to, comma separated. Exact matches only.
    /// Unset takes the profile's default: the deployed web app in production, the local dev server
    /// in dev. Set it to an empty string to switch the loopback link off entirely.
    /// </summary>
    public const string WebOriginsVariable = "WARCOMMAND_WEB_ORIGINS";

    /// <summary>The local API through a TLS-terminating dev proxy. See DEVELOPING.md: the agent
    /// pins to https, so a plain http://localhost:8000 is refused by TransportSecurity on purpose,
    /// and that refusal is not weakened for dev. A local proxy provides the https front door.</summary>
    private static readonly Uri DefaultDevApiBaseAddress = new("https://localhost:8443");

    private static readonly Uri DefaultProductionApiBaseAddress = new("https://api.warcommand.app");

    /// <summary>Where the web app runs locally.</summary>
    private static readonly string[] DefaultDevWebOrigins =
    [
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    ];

    /// <summary>The deployed web app, and only it. The apex only: www redirects to it, so no page
    /// is ever served from www and an origin that never appears must never be allowlisted.</summary>
    private static readonly string[] DefaultProductionWebOrigins =
    [
        "https://warcommand.app",
    ];

    private AgentProfile(
        bool isDev,
        bool isTrayOnly,
        bool isOverlayDemo,
        bool isColdStart,
        Uri apiBaseAddress,
        string? pairCode,
        IReadOnlyList<string> webOrigins)
    {
        IsDev = isDev;
        IsTrayOnly = isTrayOnly;
        IsOverlayDemo = isOverlayDemo;
        IsColdStart = isColdStart;
        ApiBaseAddress = apiBaseAddress;
        PairCode = pairCode;
        WebOrigins = webOrigins;
    }

    public bool IsDev { get; }

    /// <summary>Tray only: the startup sequence stops after the icon. Always a dev launch.</summary>
    public bool IsTrayOnly { get; }

    /// <summary>Overlay demo: the surface, drawn with sample rows, and nothing else. Always dev.</summary>
    public bool IsOverlayDemo { get; }

    /// <summary>Mint an account of the agent's own rather than waiting to be paired to one.</summary>
    public bool IsColdStart { get; }

    public Uri ApiBaseAddress { get; }

    public string? PairCode { get; }

    /// <summary>Exact origins the loopback pairing listener answers. Never matched by suffix.</summary>
    public IReadOnlyList<string> WebOrigins { get; }

    public static AgentProfile Resolve()
    {
        var isTrayOnly = IsTruthy(Environment.GetEnvironmentVariable(TrayOnlyVariable));
        var isOverlayDemo = IsTruthy(Environment.GetEnvironmentVariable(OverlayDemoVariable));

        // One build, one tray icon, one switch. The environment variable still wins so the dev
        // scripts keep working, but a normal launch reads the choice the tray wrote.
        var isDev = isTrayOnly || isOverlayDemo || string.Equals(
            Environment.GetEnvironmentVariable(ProfileVariable), "dev", StringComparison.OrdinalIgnoreCase)
            || (Environment.GetEnvironmentVariable(ProfileVariable) is null
                && BackendFile.Read() == AgentBackend.Local);

        var overrideUrl = Environment.GetEnvironmentVariable(ApiBaseUrlVariable);
        var apiBaseAddress = !string.IsNullOrWhiteSpace(overrideUrl)
            ? new Uri(overrideUrl, UriKind.Absolute)
            : isDev
                ? DefaultDevApiBaseAddress
                : DefaultProductionApiBaseAddress;

        var pairCode = Environment.GetEnvironmentVariable(PairCodeVariable);
        var isColdStart = IsTruthy(Environment.GetEnvironmentVariable(ColdStartVariable));
        return new AgentProfile(
            isDev,
            isTrayOnly,
            isOverlayDemo,
            isColdStart,
            apiBaseAddress,
            string.IsNullOrWhiteSpace(pairCode) ? null : pairCode,
            ResolveWebOrigins(isDev));
    }

    /// <summary>
    /// Comma separated, trailing slashes trimmed. An unset variable keeps the profile's defaults;
    /// an empty one switches the loopback link off, since an empty allowlist refuses everything.
    /// </summary>
    private static string[] ResolveWebOrigins(bool isDev)
    {
        var raw = Environment.GetEnvironmentVariable(WebOriginsVariable);
        if (raw is null)
        {
            return isDev ? DefaultDevWebOrigins : DefaultProductionWebOrigins;
        }

        return [.. raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => origin.TrimEnd('/'))];
    }

    /// <summary>1, true, yes and on all mean on. Anything else, including unset, is off.</summary>
    private static bool IsTruthy(string? value) => value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Storage root for this launch. Dev gets its own subdirectory of the same
    /// <c>%LOCALAPPDATA%\WarCommand\</c> tree from 10-agent-spec.md, so a dev token never overwrites
    /// a production one, but every file is still where the spec says it is, and tokens.dat is still
    /// the only file holding a credential, still DPAPI CurrentUser scope, unchanged.
    /// </summary>
    public AgentPaths ResolvePaths() => IsDev
        ? new AgentPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarCommand", "dev"))
        : AgentPaths.Default;
}
