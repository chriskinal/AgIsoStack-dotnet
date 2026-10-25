// A USB serial device (CDC-ACM, as a CANable with slcan firmware is) through Android's USB host
// interface: apps cannot open /dev/tty* there. No driver, no root: the app asks the user once
// for permission to use the device.
#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;

namespace AgIsoStack;

/// <summary>
/// A CDC-ACM device on Android, named <c>usb:VVVV:PPPP</c> (vendor and product id, hex): the
/// first device with those ids, so the name holds across an unplug. Opening it the first time
/// asks the user for permission and fails until it is given (a <see cref="SlcanLink"/> tries
/// again every second).
/// </summary>
public sealed class AndroidUsbSerialPort : ISerialPort
{
    const string PermissionAction = "AgIsoStack.USB_PERMISSION";
    const int UsbClassCdcData = 10;
    const int TimeoutMs = 100;

    readonly string name;
    UsbDeviceConnection? connection;
    UsbInterface? data;
    UsbEndpoint? input, output;
    UsbDevice? device;
    byte[] readBuffer = new byte[512];
    long lastPresenceCheck;
    static long permissionAskedAt = -1;

    public AndroidUsbSerialPort(string name) => this.name = name;

    static UsbManager Manager => (UsbManager)Application.Context.GetSystemService(Context.UsbService)!;

    static string NameOf(UsbDevice d) => $"usb:{d.VendorId:x4}:{d.ProductId:x4}";

    static bool IsSerial(UsbDevice d)
    {
        for (int i = 0; i < d.InterfaceCount; i++)
            if ((int)d.GetInterface(i).InterfaceClass == UsbClassCdcData) return true;
        return false;
    }

    /// <summary>The USB serial devices plugged in, by name.</summary>
    public static IReadOnlyList<string> List() =>
        (Manager.DeviceList?.Values ?? Enumerable.Empty<UsbDevice>()).Where(IsSerial).Select(NameOf).Distinct().Order(StringComparer.Ordinal).ToArray();

    UsbDevice? Find() => Manager.DeviceList?.Values.FirstOrDefault(d => NameOf(d) == name && IsSerial(d));

    public void Open()
    {
        var manager = Manager;
        var d = Find() ?? throw new IOException($"{name} is not plugged in");
        if (!manager.HasPermission(d))
        {
            // One dialog at a time: asked again only after a minute without an answer.
            long now = Environment.TickCount64;
            if (permissionAskedAt < 0 || now - permissionAskedAt > 60000)
            {
                permissionAskedAt = now;
                var context = Application.Context;
                var intent = new Intent(PermissionAction).SetPackage(context.PackageName);
                var flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
                manager.RequestPermission(d, PendingIntent.GetBroadcast(context, 0, intent, flags));
            }
            throw new UnauthorizedAccessException($"waiting for permission to use {name}");
        }
        permissionAskedAt = -1;
        UsbInterface? dataInterface = null;
        for (int i = 0; i < d.InterfaceCount && dataInterface == null; i++)
            if ((int)d.GetInterface(i).InterfaceClass == UsbClassCdcData) dataInterface = d.GetInterface(i);
        if (dataInterface == null) throw new IOException($"{name} has no serial data interface");
        UsbEndpoint? inEp = null, outEp = null;
        for (int i = 0; i < dataInterface.EndpointCount; i++)
        {
            var e = dataInterface.GetEndpoint(i)!;
            if (e.Type != UsbAddressing.XferBulk) continue;
            if (e.Direction == UsbAddressing.In) inEp = e;
            else outEp = e;
        }
        if (inEp == null || outEp == null) throw new IOException($"{name} has no bulk endpoints");
        var c = manager.OpenDevice(d) ?? throw new IOException($"{name} could not be opened");
        if (!c.ClaimInterface(dataInterface, true))
        {
            c.Close();
            throw new IOException($"{name} is in use");
        }
        // DTR and RTS on (SET_CONTROL_LINE_STATE to the control interface, 0): some firmwares
        // send nothing until a terminal is "connected".
        c.ControlTransfer((UsbAddressing)0x21, 0x22, 0x03, 0, null, 0, TimeoutMs);
        device = d;
        connection = c;
        data = dataInterface;
        input = inEp;
        output = outEp;
        readBuffer = new byte[Math.Max(64, inEp.MaxPacketSize)];
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var c = connection ?? throw new IOException("not open");
        int n = c.BulkTransfer(input, readBuffer, Math.Min(count, readBuffer.Length), TimeoutMs);
        if (n > 0)
        {
            Array.Copy(readBuffer, 0, buffer, offset, n);
            return n;
        }
        // -1 is a timeout as well as a failure: gone only if the device is.
        CheckPresent();
        return 0;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var c = connection ?? throw new IOException("not open");
        var chunk = new byte[Math.Min(count, 16384)];
        for (int done = 0; done < count;)
        {
            int n = Math.Min(chunk.Length, count - done);
            Array.Copy(buffer, offset + done, chunk, 0, n);
            int sent = c.BulkTransfer(output, chunk, n, 1000);
            if (sent <= 0)
            {
                lastPresenceCheck = 0;
                CheckPresent();
                throw new IOException($"{name}: write failed");
            }
            done += sent;
        }
    }

    // At most twice a second: the device list is a call to the system.
    void CheckPresent()
    {
        long now = Environment.TickCount64;
        if (now - lastPresenceCheck < 500) return;
        lastPresenceCheck = now;
        var d = device;
        if (d == null || Manager.DeviceList?.Values.Any(x => x.DeviceName == d.DeviceName) != true)
            throw new IOException($"{name} was unplugged");
    }

    public void Dispose()
    {
        var c = connection;
        connection = null;
        if (c == null) return;
        try { if (data != null) c.ReleaseInterface(data); } catch { }
        try { c.Close(); } catch { }
    }
}
#endif
