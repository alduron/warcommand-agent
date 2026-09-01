using System.Diagnostics;
using Microsoft.Win32;
using WarCommand.Agent.Client.Diagnostics;

namespace WarCommand.Agent.Startup;

/// <summary>
/// Whether Windows launches the agent when this user signs in. One value under HKCU's Run key.
/// </summary>
/// <remarks>
/// The registry is the source of truth, never a copy in settings.json. A user who switches this off
/// in Task Manager's Startup tab, or an uninstall that removes the value, has to be believed: a
/// mirrored preference would keep reporting "on" for a launch that no longer happens, and the tray
/// would be lying about the one thing the user just changed by hand.
///
/// HKCU rather than HKLM because the install is per-user and needs no elevation. Writing the
/// machine-wide key would ask for administrator rights and would start the agent for accounts that
/// never installed it.
/// </remarks>
public sealed class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value name. Also what Task Manager's Startup tab shows.</summary>
    public const string ValueName = "WarCommand";

    private readonly IClientLog _log;
    private readonly string _executablePath;

    public WindowsStartup(IClientLog? log = null, string? executablePath = null)
    {
        _log = log ?? NullClientLog.Instance;
        _executablePath = executablePath ?? CurrentExecutablePath();
    }

    /// <summary>True when Windows will start the agent at sign-in.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _log.Warn($"Could not read the startup registration: {ex.GetType().Name}");
                return false;
            }
        }
    }

    /// <summary>Turns it on or off. Returns the state actually in force afterwards.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return IsEnabled;
            }

            if (enabled)
            {
                // Quoted: %LOCALAPPDATA%\Programs\WarCommand contains no space today, but the user
                // can install anywhere, and an unquoted path with one is a classic hijack.
                key.SetValue(ValueName, $"\"{_executablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return enabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warn($"Could not write the startup registration: {ex.GetType().Name}");
            return IsEnabled;
        }
    }

    /// <summary>
    /// Rewrites the registered path when it is stale, and does nothing when startup is off.
    /// An update installs to the same directory, but a user who moves the install or reinstalls
    /// elsewhere would otherwise keep a Run value pointing at an exe that is gone.
    /// </summary>
    public void Reconcile()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string current)
            {
                return;
            }

            var expected = $"\"{_executablePath}\"";
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
                _log.Info("Startup registration repointed at the current install.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warn($"Could not reconcile the startup registration: {ex.GetType().Name}");
        }
    }

    /// <summary>The running exe. MainModule rather than the assembly, which is a dll under WPF.</summary>
    private static string CurrentExecutablePath()
    {
        using var current = Process.GetCurrentProcess();
        return current.MainModule?.FileName ?? Environment.ProcessPath ?? string.Empty;
    }
}
