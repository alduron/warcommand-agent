using System.Runtime.InteropServices;
using System.Windows.Interop;
using WarCommand.Agent.Input.Devices;

namespace WarCommand.Agent.Input;

/// <summary>
/// Reads HOTAS button edges from Raw Input. Out of process, read only, and it never opens the game:
/// Windows hands us the same HID reports the game gets, which is why a button bound here still does
/// whatever Wardogs has it bound to.
/// </summary>
/// <remarks>
/// A message-only window, registered with RIDEV_INPUTSINK so reports keep arriving while Wardogs is
/// foreground. Nothing here is logged: a button number is treated with the same care as a key code,
/// so the only thing that leaves this class is a resolved binding.
/// <para>
/// Devices are keyed on vendor and product, never on the device path, so a stick moved to another
/// USB socket keeps its bindings. Two identical sticks are indistinguishable and the settings page
/// says so.
/// </para>
/// </remarks>
public sealed class RawInputDeviceSource : IDeviceButtonSource
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int RidInput = 0x10000003;
    private const int RidiDeviceName = 0x20000007;
    private const int RidiPreparsedData = 0x20000005;
    private const int RimTypeHid = 2;
    private const int RidevInputSink = 0x00000100;
    private const int RidevDevNotify = 0x00002000;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsagePageButton = 0x09;
    private const ushort HidUsageJoystick = 0x04;
    private const ushort HidUsageGamepad = 0x05;

    /// <summary>HIDP_STATUS_SUCCESS. Anything else is a report shape the device never declared.</summary>
    private const int HidpStatusSuccess = 0x00110000;

    /// <summary>Message-only window parent. Never drawn, never activated.</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Dictionary<IntPtr, Device> _byHandle = [];
    private readonly Dictionary<string, HashSet<int>> _downByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeviceInfo> _devices = [];

    private HwndSource? _window;
    private byte[] _buffer = new byte[256];
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<DeviceButtonEventArgs>? ButtonChanged;

    /// <inheritdoc />
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    /// <inheritdoc />
    public void Start()
    {
        if (_window is not null || _disposed)
        {
            return;
        }

        _window = new HwndSource(new HwndSourceParameters("WarCommandRawInput")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);

        var registrations = new RawInputDevice[]
        {
            new()
            {
                UsagePage = HidUsagePageGeneric,
                Usage = HidUsageJoystick,
                Flags = RidevInputSink | RidevDevNotify,
                Target = _window.Handle,
            },
            new()
            {
                UsagePage = HidUsagePageGeneric,
                Usage = HidUsageGamepad,
                Flags = RidevInputSink | RidevDevNotify,
                Target = _window.Handle,
            },
        };

        _ = RegisterRawInputDevices(registrations, registrations.Length, Marshal.SizeOf<RawInputDevice>());
        Rescan();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var device in _byHandle.Values)
        {
            device.Dispose();
        }

        _byHandle.Clear();
        _window?.RemoveHook(WndProc);
        _window?.Dispose();
        _window = null;
    }

    private static int RawInputHeaderSize => 8 + (2 * IntPtr.Size);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmInput:
                OnRawInput(lParam);
                break;
            case WmInputDeviceChange:
                Rescan();
                break;
            default:
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// One report. The pressed set is diffed against the last one for that device, so a report that
    /// says nothing changed raises nothing: a stick sends its whole state on every poll.
    /// </summary>
    private void OnRawInput(IntPtr handle)
    {
        var size = 0;
        if (GetRawInputData(handle, RidInput, IntPtr.Zero, ref size, RawInputHeaderSize) != 0 || size <= 0)
        {
            return;
        }

        if (_buffer.Length < size)
        {
            _buffer = new byte[size];
        }

        int read;
        unsafe
        {
            fixed (byte* target = _buffer)
            {
                read = GetRawInputData(handle, RidInput, (IntPtr)target, ref size, RawInputHeaderSize);
            }
        }

        if (read < RawInputHeaderSize + 8 || BitConverter.ToInt32(_buffer, 0) != RimTypeHid)
        {
            return;
        }

        var deviceHandle = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(_buffer, 8))
            : new IntPtr(BitConverter.ToInt32(_buffer, 8));

        if (!_byHandle.TryGetValue(deviceHandle, out var device))
        {
            Rescan();
            if (!_byHandle.TryGetValue(deviceHandle, out device))
            {
                return;
            }
        }

        var reportSize = BitConverter.ToInt32(_buffer, RawInputHeaderSize);
        var reportCount = BitConverter.ToInt32(_buffer, RawInputHeaderSize + 4);
        if (reportSize <= 0 || reportCount <= 0)
        {
            return;
        }

        // Only the newest report matters. A batched buffer holds intermediate states nobody held
        // long enough to mean anything, and replaying them fires an action twice.
        var offset = RawInputHeaderSize + 8 + ((reportCount - 1) * reportSize);
        if (offset + reportSize > read)
        {
            return;
        }

        Publish(device, device.Pressed(_buffer, offset, reportSize));
    }

    /// <summary>Raises an edge for every button that changed since this device's last report.</summary>
    private void Publish(Device device, HashSet<int> pressed)
    {
        if (!_downByDevice.TryGetValue(device.Key, out var previous))
        {
            previous = [];
            _downByDevice[device.Key] = previous;
        }

        foreach (var button in pressed)
        {
            if (previous.Add(button))
            {
                ButtonChanged?.Invoke(this, new DeviceButtonEventArgs(new DeviceButton(device.Key, button), down: true));
            }
        }

        foreach (var button in previous.Where(b => !pressed.Contains(b)).ToList())
        {
            previous.Remove(button);
            ButtonChanged?.Invoke(this, new DeviceButtonEventArgs(new DeviceButton(device.Key, button), down: false));
        }
    }

    /// <summary>Rebuilds the device table. Called at start and on every attach or detach.</summary>
    private void Rescan()
    {
        var count = 0;
        if (GetRawInputDeviceList(null, ref count, Marshal.SizeOf<RawInputDeviceList>()) != 0 || count <= 0)
        {
            return;
        }

        var list = new RawInputDeviceList[count];
        if (GetRawInputDeviceList(list, ref count, Marshal.SizeOf<RawInputDeviceList>()) < 0)
        {
            return;
        }

        var seen = new HashSet<IntPtr>();
        foreach (var entry in list.Take(count))
        {
            if (entry.Type != RimTypeHid)
            {
                continue;
            }

            _ = seen.Add(entry.Device);
            if (_byHandle.ContainsKey(entry.Device))
            {
                continue;
            }

            if (Device.TryOpen(entry.Device, out var device))
            {
                _byHandle[entry.Device] = device;
            }
        }

        foreach (var stale in _byHandle.Keys.Where(h => !seen.Contains(h)).ToList())
        {
            _byHandle[stale].Dispose();
            _ = _byHandle.Remove(stale);
        }

        _devices.Clear();
        foreach (var device in _byHandle.Values.Where(d => d.Buttons > 0))
        {
            if (!_devices.Any(d => string.Equals(d.Key, device.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _devices.Add(new DeviceInfo(device.Key, device.Name, device.Buttons));
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, int count, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputData(IntPtr input, int command, IntPtr data, ref int size, int headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceList(RawInputDeviceList[]? list, ref int count, int size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetRawInputDeviceInfoW(IntPtr device, int command, IntPtr data, ref int size);

    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern unsafe int HidP_GetUsages(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort* usageList,
        ref uint usageLength,
        IntPtr preparsed,
        byte* report,
        uint reportLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool HidD_GetProductString(IntPtr device, char* buffer, uint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal int Flags;
        internal IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        internal IntPtr Device;
        internal int Type;
    }

    /// <summary>One attached controller, with the preparsed data its reports are read through.</summary>
    private sealed class Device : IDisposable
    {
        private IntPtr _preparsed;
        private ushort[] _usages;

        private Device(string key, string name, IntPtr preparsed, int buttons)
        {
            Key = key;
            Name = name;
            Buttons = buttons;
            _preparsed = preparsed;
            _usages = new ushort[Math.Max(1, buttons)];
        }

        internal string Key { get; }

        internal string Name { get; }

        internal int Buttons { get; }

        internal static bool TryOpen(IntPtr handle, out Device device)
        {
            device = null!;
            if (DevicePath(handle) is not { Length: > 0 } path || VendorAndProduct(path) is not { } key)
            {
                return false;
            }

            var size = 0;
            if (GetRawInputDeviceInfoW(handle, RidiPreparsedData, IntPtr.Zero, ref size) != 0 || size <= 0)
            {
                return false;
            }

            var preparsed = Marshal.AllocHGlobal(size);
            if (GetRawInputDeviceInfoW(handle, RidiPreparsedData, preparsed, ref size) < 0)
            {
                Marshal.FreeHGlobal(preparsed);
                return false;
            }

            var buttons = HidP_MaxUsageListLength(0, HidUsagePageButton, preparsed);
            if (buttons == 0)
            {
                Marshal.FreeHGlobal(preparsed);
                return false;
            }

            device = new Device(key, ProductName(path) ?? key, preparsed, (int)buttons);
            return true;
        }

        /// <summary>The buttons this report says are down.</summary>
        internal HashSet<int> Pressed(byte[] buffer, int offset, int length)
        {
            var pressed = new HashSet<int>();
            if (_preparsed == IntPtr.Zero)
            {
                return pressed;
            }

            var count = (uint)_usages.Length;
            int status;
            unsafe
            {
                fixed (byte* report = &buffer[offset])
                fixed (ushort* usages = _usages)
                {
                    status = HidP_GetUsages(0, HidUsagePageButton, 0, usages, ref count, _preparsed, report, (uint)length);
                }
            }

            if (status != HidpStatusSuccess)
            {
                return pressed;
            }

            for (var i = 0; i < count; i++)
            {
                _ = pressed.Add(_usages[i]);
            }

            return pressed;
        }

        public void Dispose()
        {
            if (_preparsed != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_preparsed);
                _preparsed = IntPtr.Zero;
            }

            _usages = [];
        }

        private static string? DevicePath(IntPtr handle)
        {
            var size = 0;
            if (GetRawInputDeviceInfoW(handle, RidiDeviceName, IntPtr.Zero, ref size) != 0 || size <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(size * 2);
            try
            {
                return GetRawInputDeviceInfoW(handle, RidiDeviceName, buffer, ref size) > 0
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// The stable half of a device path: VID_xxxx and PID_xxxx. The rest names the socket, which
        /// changes every time a stick is plugged in somewhere else.
        /// </summary>
        private static string? VendorAndProduct(string path)
        {
            var vid = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            var pid = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (vid < 0 || pid < 0 || vid + 8 > path.Length || pid + 8 > path.Length)
            {
                return null;
            }

            return string.Concat(path.AsSpan(vid, 8), "+", path.AsSpan(pid, 8)).ToUpperInvariant();
        }

        /// <summary>The product string the device reports, or null when it reports none.</summary>
        private static string? ProductName(string path)
        {
            var file = CreateFileW(path, 0, 0x00000003, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (file == IntPtr.Zero || file == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                var buffer = new char[126];
                unsafe
                {
                    fixed (char* target = buffer)
                    {
                        if (!HidD_GetProductString(file, target, (uint)(buffer.Length * 2)))
                        {
                            return null;
                        }
                    }
                }

                var name = new string(buffer).TrimEnd(' ').Trim();
                return name.Length > 0 ? name : null;
            }
            finally
            {
                _ = CloseHandle(file);
            }
        }
    }
}
