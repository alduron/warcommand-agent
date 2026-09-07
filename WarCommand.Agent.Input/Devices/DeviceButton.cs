using System.Globalization;

namespace WarCommand.Agent.Input.Devices;

/// <summary>
/// One button on one HID controller: a stick, a throttle, a button box. Identified by the device's
/// vendor and product, never by the USB port it happens to be in, so a HOTAS unplugged after a
/// flight and plugged back into another socket keeps its bindings.
/// </summary>
/// <remarks>
/// Two identical devices are indistinguishable to this. That is a real limit and the settings page
/// says so rather than binding the wrong stick silently.
/// </remarks>
public readonly record struct DeviceButton(string DeviceKey, int Button)
{
    /// <summary>The prefix a persisted device binding wears, so a chord label can never parse as one.</summary>
    private const string Prefix = "hid:";

    /// <summary>Nothing. A secondary binding that has not been chosen.</summary>
    public static DeviceButton None => default;

    /// <summary>False when no device button has been chosen.</summary>
    public bool IsBound => !string.IsNullOrEmpty(DeviceKey) && Button > 0;

    /// <summary>
    /// The persisted form, and the only string projection that round-trips. What the settings page
    /// shows is <see cref="Display"/>, which needs the device list to name the stick.
    /// </summary>
    public string Label => IsBound
        ? Prefix + DeviceKey + ":" + Button.ToString(CultureInfo.InvariantCulture)
        : "(unbound)";

    /// <summary>The label. There is no other persisted projection.</summary>
    public override string ToString() => Label;

    /// <summary>
    /// What the settings page draws: the device's own name and the button number, or the raw
    /// vendor and product when the stick is not plugged in right now. A binding to a device that
    /// is unplugged is still a binding, and hiding it would read as the setting being lost.
    /// </summary>
    public string Display(IReadOnlyList<DeviceInfo>? devices)
    {
        if (!IsBound)
        {
            return "Not set";
        }

        // Copied out first: this is a struct, and a lambda in one cannot touch an instance member.
        var key = DeviceKey;
        var name = devices?.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))?.Name
            ?? key;
        return name + " B" + Button.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a device button back from its own <see cref="Label"/>.</summary>
    public static bool TryParse(string? label, out DeviceButton button)
    {
        button = None;
        if (label is null || !label.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = label[Prefix.Length..];
        var split = rest.LastIndexOf(':');
        if (split <= 0 || split == rest.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(rest[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        button = new DeviceButton(rest[..split], number);
        return true;
    }
}

/// <summary>A controller the machine can see right now.</summary>
/// <param name="Key">Vendor and product, the stable half of the device path.</param>
/// <param name="Name">The product string the device reports, or the key when it reports none.</param>
/// <param name="Buttons">How many buttons it declares. Zero for a device that declares none.</param>
public sealed record DeviceInfo(string Key, string Name, int Buttons);

/// <summary>One button edge on one device.</summary>
public sealed class DeviceButtonEventArgs(DeviceButton button, bool down) : EventArgs
{
    /// <summary>Which button moved.</summary>
    public DeviceButton Button { get; } = button;

    /// <summary>True on press, false on release.</summary>
    public bool Down { get; } = down;
}

/// <summary>
/// Where HOTAS button edges come from. One implementation reads real HID reports; the tests use a
/// fake, and the whole binding path is proven against that rather than against hardware.
/// </summary>
public interface IDeviceButtonSource : IDisposable
{
    /// <summary>Every controller currently attached.</summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>A button went down or came up. Raised on the source's own thread.</summary>
    event EventHandler<DeviceButtonEventArgs>? ButtonChanged;

    /// <summary>Begins listening. Calling it twice is a no-op.</summary>
    void Start();
}
