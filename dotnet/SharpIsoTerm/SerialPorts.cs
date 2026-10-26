// The byte port an slcan adapter is reached through: a serial port on the desktop
// (System.IO.Ports), a USB serial device (CDC-ACM) on Android (AndroidUsbSerialPort). iOS has
// neither: apps there cannot reach a USB adapter.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpIsoTerm;

/// <summary>A byte stream to an adapter. One reader thread and one writer thread may use it at
/// once.</summary>
public interface ISerialPort : IDisposable
{
    /// <summary>Opens it; throws if it is not there or cannot be opened (yet).</summary>
    void Open();
    /// <summary>Waits up to about 100 ms for bytes: how many were read, 0 for none yet. Throws
    /// once the port is gone (unplugged).</summary>
    int Read(byte[] buffer, int offset, int count);
    /// <summary>Writes all of it, or throws once the port is gone.</summary>
    void Write(byte[] buffer, int offset, int count);
}

/// <summary>The serial ports of this device, by name, for <see cref="SlcanLink"/>.</summary>
public static class SerialPorts
{
    /// <summary>The ports an adapter could be on. macOS: the call-out devices
    /// (<c>/dev/cu.*</c>) other than Bluetooth and the debug console. Windows: COM ports. Linux:
    /// <c>/dev/ttyACM*</c> and <c>/dev/ttyUSB*</c>. Android: USB serial devices (CDC-ACM), by
    /// their device name. iOS: none.</summary>
    public static IReadOnlyList<string> List()
    {
#if ANDROID
        return AndroidUsbSerialPort.List();
#elif IOS || __IOS__
        return Array.Empty<string>();
#else
        var names = System.IO.Ports.SerialPort.GetPortNames();
        if (OperatingSystem.IsMacOS())
            names = names.Where(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal)
                                     && !n.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                                     && !n.Contains("debug-console", StringComparison.OrdinalIgnoreCase)).ToArray();
        else if (OperatingSystem.IsLinux())
            names = names.Where(n => n.StartsWith("/dev/ttyACM", StringComparison.Ordinal)
                                     || n.StartsWith("/dev/ttyUSB", StringComparison.Ordinal)).ToArray();
        return names.Order(StringComparer.Ordinal).ToArray();
#endif
    }

    /// <summary>The port of this name (not opened). Android: a name from <see cref="List"/>;
    /// opening it asks the user for permission the first time.</summary>
    public static ISerialPort Get(string name)
    {
#if ANDROID
        return new AndroidUsbSerialPort(name);
#elif IOS || __IOS__
        throw new PlatformNotSupportedException("iOS apps cannot reach a USB serial adapter.");
#else
        return new SystemSerialPort(name);
#endif
    }
}

#if !ANDROID && !IOS && !__IOS__
/// <summary>A serial port through System.IO.Ports. The baud rate does not matter to a USB serial
/// adapter (CDC-ACM); a real UART adapter at 115200 is the slcan default.</summary>
public sealed class SystemSerialPort : ISerialPort
{
    readonly System.IO.Ports.SerialPort port;

    public SystemSerialPort(string name, int baudRate = 115200)
    {
        port = new System.IO.Ports.SerialPort(name, baudRate) { ReadTimeout = 100, WriteTimeout = 1000, DtrEnable = true, RtsEnable = true };
    }

    public void Open()
    {
        // On macOS and Linux a missing device comes back as "access denied": say what it is.
        if (!OperatingSystem.IsWindows() && !System.IO.File.Exists(port.PortName))
            throw new System.IO.IOException($"{port.PortName} is not plugged in");
        port.Open();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try { return port.BaseStream.Read(buffer, offset, count); }
        catch (TimeoutException) { return 0; }
    }

    public void Write(byte[] buffer, int offset, int count) => port.BaseStream.Write(buffer, offset, count);

    public void Dispose()
    {
        try { port.Close(); } catch { }
        port.Dispose();
    }
}
#endif
