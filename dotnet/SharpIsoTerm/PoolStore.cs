// Stored object pools (versions): an implement that has uploaded once loads its pool from the
// terminal next time instead of sending it again. Keyed by the implement's 64-bit NAME and the
// version label (7 bytes, or 32 for VT version 5 and 6 extended labels).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpIsoTerm;

/// <summary>Where the terminals keep pool versions (one store for all). Called on the stack's thread; keep it
/// quick (the bus waits). An exception counts as a failure and is reported through
/// <see cref="VirtualTerminalStack.HandlerFailed"/>.</summary>
public interface IPoolStore
{
    /// <summary>Stores a pool, or one part of it: a pool can come in several parts, the first
    /// with <paramref name="append"/> false. False if it could not be stored.</summary>
    bool Save(ulong name, ReadOnlySpan<byte> label, ReadOnlySpan<byte> data, bool append);

    /// <summary>The pool stored under this label, or null.</summary>
    byte[]? Load(ulong name, ReadOnlySpan<byte> label);

    /// <summary>The labels stored for this implement that are this many bytes long.</summary>
    IEnumerable<byte[]> List(ulong name, int labelLength);

    /// <summary>Removes one version. False if it could not be removed.</summary>
    bool Remove(ulong name, ReadOnlySpan<byte> label);

    /// <summary>Removes every version of this label length. False if they could not be removed.</summary>
    bool RemoveAll(ulong name, int labelLength);
}

/// <summary>Pools kept in memory, gone when the process ends: for tests.</summary>
public sealed class MemoryPoolStore : IPoolStore
{
    readonly Dictionary<(ulong Name, string Label), List<byte>> pools = new();

    /// <summary>How many versions are held.</summary>
    public int Count => pools.Count;

    public bool Save(ulong name, ReadOnlySpan<byte> label, ReadOnlySpan<byte> data, bool append)
    {
        var key = (name, Convert.ToHexString(label));
        if (!append || !pools.TryGetValue(key, out var pool)) pools[key] = pool = new List<byte>();
        pool.AddRange(data.ToArray());
        return true;
    }

    public byte[]? Load(ulong name, ReadOnlySpan<byte> label) =>
        pools.TryGetValue((name, Convert.ToHexString(label)), out var pool) ? pool.ToArray() : null;

    public IEnumerable<byte[]> List(ulong name, int labelLength) =>
        pools.Keys.Where(k => k.Name == name && k.Label.Length == labelLength * 2).Select(k => Convert.FromHexString(k.Label)).ToList();

    public bool Remove(ulong name, ReadOnlySpan<byte> label)
    {
        pools.Remove((name, Convert.ToHexString(label)));
        return true;
    }

    public bool RemoveAll(ulong name, int labelLength)
    {
        foreach (var key in pools.Keys.Where(k => k.Name == name && k.Label.Length == labelLength * 2).ToList()) pools.Remove(key);
        return true;
    }
}

/// <summary>Pools as files: DIRECTORY/NAME/LABEL.iop, NAME and LABEL in hex.</summary>
public sealed class FilePoolStore : IPoolStore
{
    readonly string root;

    public FilePoolStore(string directory)
    {
        root = directory;
        Directory.CreateDirectory(root);
    }

    string Folder(ulong name) => Path.Combine(root, name.ToString("X16"));
    string File(ulong name, ReadOnlySpan<byte> label) => Path.Combine(Folder(name), Convert.ToHexString(label) + ".iop");

    public bool Save(ulong name, ReadOnlySpan<byte> label, ReadOnlySpan<byte> data, bool append)
    {
        Directory.CreateDirectory(Folder(name));
        using var file = new FileStream(File(name, label), append ? FileMode.Append : FileMode.Create, FileAccess.Write);
        file.Write(data);
        return true;
    }

    public byte[]? Load(ulong name, ReadOnlySpan<byte> label)
    {
        string path = File(name, label);
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
    }

    public IEnumerable<byte[]> List(ulong name, int labelLength)
    {
        if (!Directory.Exists(Folder(name))) return Array.Empty<byte[]>();
        return Directory.GetFiles(Folder(name), "*.iop")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(hex => hex!.Length == labelLength * 2)
            .Select(hex => Convert.FromHexString(hex!))
            .ToList();
    }

    public bool Remove(ulong name, ReadOnlySpan<byte> label)
    {
        System.IO.File.Delete(File(name, label));
        return true;
    }

    public bool RemoveAll(ulong name, int labelLength)
    {
        foreach (var label in List(name, labelLength)) Remove(name, label);
        return true;
    }
}
