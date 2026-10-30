namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal sealed class WalDirectory : IDisposable
{
    private const string RecoveryName = "telemetry_wal.replay";
    private readonly string _directory;
    private readonly Action<string> _synchronize;
    private readonly FileStream _lease;

    internal readonly record struct SegmentSize(long Bytes, long Records);

    internal static SegmentSize Measure(string path) => new SegmentSize(new FileInfo(path).Length, CountFrames(path));

    public WalDirectory(string directory, Action<string>? synchronize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _synchronize = synchronize ?? WalDirectoryDurability.Synchronize;
        List<string> missing = FindMissingDirectories(_directory, Directory.Exists);
        Directory.CreateDirectory(_directory);
        _lease = new FileStream(Path.Combine(_directory, ".nealytics-wal.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            WalDirectoryDurability.AcquireLease(_lease.SafeFileHandle);
            _lease.Flush(flushToDisk: true);
            _synchronize(_directory);
            foreach (string created in missing)
            {
                string? parent = Path.GetDirectoryName(created);
                if (parent is not null)
                {
                    _synchronize(parent);
                }
            }
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    internal static List<string> FindMissingDirectories(string directory, Func<string, bool> exists)
    {
        List<string> missing = new List<string>();
        string? current = directory;
        while (current is not null && !exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }
        return missing;
    }

    public string ActivePath => Path.Combine(_directory, "telemetry_wal.log");

    public void Synchronize()
    {
        EnsureOwned();
        _synchronize(_directory);
    }

    public string[] RecoveryPaths()
    {
        EnsureOwned();
        List<string> paths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(_directory, RecoveryName + "*"))
        {
            Generation(path);
            paths.Add(path);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths.ToArray();
    }

    public void SealActive()
    {
        EnsureOwned();
        if (!File.Exists(ActivePath) || new FileInfo(ActivePath).Length == 0)
        {
            return;
        }
        string[] recovered = RecoveryPaths();
        string destination = Path.Combine(_directory, RecoveryName);
        if (recovered.Length > 0)
        {
            long next = checked(Generation(recovered[^1]) + 1);
            destination += "." + next.ToString("D20", CultureInfo.InvariantCulture);
        }
        using (FileStream active = new FileStream(ActivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            active.Flush(flushToDisk: true);
        }
        File.Move(ActivePath, destination);
        _synchronize(_directory);
    }

    public void DeleteRecovery(IReadOnlyList<string> paths)
    {
        EnsureOwned();
        foreach (string path in paths)
        {
            if (!string.Equals(Path.GetDirectoryName(path), _directory, StringComparison.Ordinal))
            {
                throw new ArgumentException("Recovery files must belong to the owned WAL directory.", nameof(paths));
            }
            Generation(path);
        }
        foreach (string path in paths)
        {
            File.Delete(path);
        }
        _synchronize(_directory);
    }

    private static long Generation(string path)
    {
        string name = Path.GetFileName(path);
        if (name == RecoveryName)
        {
            return 0;
        }
        ReadOnlySpan<char> suffix = name.AsSpan();
        if (suffix.Length != RecoveryName.Length + 21
            || !suffix.StartsWith(RecoveryName + ".", StringComparison.Ordinal)
            || !long.TryParse(suffix[(RecoveryName.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value <= 0)
        {
            throw new InvalidDataException($"Unrecognized WAL recovery segment '{name}'.");
        }
        return value;
    }

    internal static long CountFrames(string path)
    {
        using FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.SequentialScan);
        Span<byte> buffer = stackalloc byte[4096];
        long records = 0;
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            foreach (byte value in buffer[..count])
            {
                if (value == (byte)'\n')
                {
                    records++;
                }
            }
        }
        return records;
    }

    private void EnsureOwned() => ObjectDisposedException.ThrowIf(!_lease.CanRead, this);

    public void Dispose() => _lease.Dispose();
}
