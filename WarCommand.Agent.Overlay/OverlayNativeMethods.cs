using System.Runtime.InteropServices;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The extended window styles 06-overlay-ux.md requires of the in-game surface, and nothing else.
/// </summary>
/// <remarks>
/// Every call here targets a window this process created. Nothing is opened, read, written or
/// posted at the game: an overlay that draws beside the game is not an overlay that touches it.
/// </remarks>
internal static class OverlayNativeMethods
{
    internal const int GwlExStyle = -20;

    /// <summary>Per-pixel alpha. WPF sets this itself for AllowsTransparency; re-asserted here.</summary>
    internal const nint WsExLayered = 0x00080000;

    /// <summary>Click-through. Without it the overlay eats the shot the player is taking.</summary>
    internal const nint WsExTransparent = 0x00000020;

    /// <summary>Keeps it out of alt-tab, which is where a topmost tool surface does not belong.</summary>
    internal const nint WsExToolWindow = 0x00000080;

    /// <summary>It can never take focus, so it can never pull the player out of the game.</summary>
    internal const nint WsExNoActivate = 0x08000000;

    /// <summary>All four, which is the whole contract. Applied as one value.</summary>
    internal const nint OverlayStyles = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
