// AlienFX APIv4 (34-byte HID reports) device driver. Confirmed working on the Alienware
// x14's "AW-ELC" embedded lighting controller (VID 0x187C), which uses this protocol
// generation; other Alienware/Dell G-series hardware using the same 34-byte report length
// should work identically since detection here is by VID + report length, not a fixed PID.
// Protocol ported from alienfx-tools' AlienFX-SDK (MIT), https://github.com/T-Troll/alienfx-tools
// Copyright (c) T-Troll and contributors; C# port for local use.

using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace AlienRgb.Core;

public sealed class AlienFxDevice : IDisposable
{
    public const int VendorId = 0x187C;
    private const int OutReportLength = 34; // APIv4: 1 report-id byte + 33 data bytes

    // APIv4 device statuses (report byte 2)
    private const byte StatusReady = 33;
    private const byte StatusBusy = 34;

    private readonly SafeFileHandle _handle;
    private readonly int _inReportLength;
    private bool _inSet;

    public string Description { get; }
    public int ProductId { get; }

    private AlienFxDevice(SafeFileHandle handle, HidDevice device, int inReportLength)
    {
        _handle = handle;
        _inReportLength = inReportLength;
        ProductId = device.ProductID;
        string name;
        try { name = device.GetFriendlyName(); }
        catch { name = "AlienFX controller"; }
        Description = name;
    }

    /// <summary>Find and open the AlienFX lighting controller (VID 0x187C, 34-byte output reports).</summary>
    public static AlienFxDevice Open()
    {
        var candidates = DeviceList.Local.GetHidDevices(VendorId).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "No Alienware (VID 187C) HID device found. Is this an AlienFX-equipped machine?");

        foreach (var device in candidates)
        {
            int outLen;
            try { outLen = device.GetMaxOutputReportLength(); }
            catch { continue; }
            if (outLen != OutReportLength)
                continue;

            var handle = NativeMethods.CreateFile(device.DevicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new InvalidOperationException(
                    $"Found AlienFX controller (PID {device.ProductID:X4}) but could not open it " +
                    $"(Win32 error {Marshal.GetLastWin32Error()}). Another program may hold the device.");

            int inLen;
            try { inLen = device.GetMaxInputReportLength(); }
            catch { inLen = OutReportLength; }
            return new AlienFxDevice(handle, device, inLen > 0 ? inLen : OutReportLength);
        }

        var seen = string.Join(", ", candidates.Select(d =>
        {
            try { return $"PID {d.ProductID:X4} (report len {d.GetMaxOutputReportLength()})"; }
            catch { return $"PID {d.ProductID:X4}"; }
        }));
        throw new InvalidOperationException(
            $"No Alienware HID interface with 34-byte reports (APIv4) found. Interfaces seen: {seen}");
    }

    private void Send(byte[] report)
    {
        if (!NativeMethods.HidD_SetOutputReport(_handle, report, (uint)report.Length))
            throw new InvalidOperationException(
                $"HID output report failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private byte GetStatus()
    {
        var buf = new byte[_inReportLength];
        buf[0] = 0x00; // report ID
        if (!NativeMethods.HidD_GetInputReport(_handle, buf, (uint)buf.Length))
            return 0;
        return buf[2];
    }

    private void WaitForReady()
    {
        // Ready when not busy; status 0 means stalled — bail out rather than spin forever.
        for (int i = 0; i < 100; i++)
        {
            var status = GetStatus();
            if (status != StatusBusy)
                return;
            Thread.Sleep(20);
        }
    }

    private static byte[] Control(byte type)
    {
        var buf = new byte[OutReportLength];
        buf[1] = 0x03;
        buf[2] = 0x21;
        buf[3] = 0x00;
        buf[4] = type; // 1=start new, 2=finish+save, 3=finish+play, 4=remove, 5=play, 6=default, 7=startup
        buf[5] = 0xFF;
        buf[6] = 0xFF;
        return buf;
    }

    /// <summary>Begin a new action set. Must precede color commands.</summary>
    public void Reset()
    {
        WaitForReady();
        Send(Control(4)); // remove current set
        Send(Control(1)); // start new set
        _inSet = true;
    }

    /// <summary>Commit the staged colors ("finish and play"). Colors take effect here.</summary>
    public void Update()
    {
        if (!_inSet)
            return;
        Send(Control(3));
        _inSet = false;
    }

    /// <summary>Stage a static color for up to 26 light IDs. Call <see cref="Update"/> to apply.</summary>
    public void StageColor(IReadOnlyList<int> lightIds, byte r, byte g, byte b)
    {
        if (lightIds.Count == 0)
            return;
        if (!_inSet)
            Reset();

        for (int start = 0; start < lightIds.Count; start += 26)
        {
            int count = Math.Min(26, lightIds.Count - start);
            var buf = new byte[OutReportLength];
            buf[1] = 0x03;
            buf[2] = 0x27; // setOneColor
            buf[3] = r;
            buf[4] = g;
            buf[5] = b;
            buf[6] = 0x00;
            buf[7] = (byte)count;
            for (int i = 0; i < count; i++)
                buf[8 + i] = (byte)lightIds[start + i];
            Send(buf);
        }
    }

    /// <summary>Set a static color on the given lights and apply immediately.</summary>
    public void SetColor(IReadOnlyList<int> lightIds, byte r, byte g, byte b)
    {
        StageColor(lightIds, r, g, b);
        Update();
    }

    /// <summary>Set a static color on a single light ID and apply immediately.</summary>
    public void SetZoneColor(int lightId, byte r, byte g, byte b) =>
        SetColor(new[] { lightId }, r, g, b);

    public void Dispose() => _handle.Dispose();

    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject,
            byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject,
            byte[] reportBuffer, uint reportBufferLength);
    }
}
