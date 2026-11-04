namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class ImportJobJournal : IDisposable
{
    private const int ManifestBytes = 8_192;
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly Func<string, FileMode, FileStream> _open;
    private readonly Action<string> _synchronize;
    private readonly Action<string, string> _publish;

    public ImportJobJournal(string directory)
        : this(directory, Open, WalDirectoryDurability.Synchronize, (source, target) => File.Move(source, target, true))
    {
    }

    internal ImportJobJournal(string directory, Func<string, FileMode, FileStream> open,
        Action<string> synchronize, Action<string, string> publish)
    {
        _directory = Path.Combine(Path.GetFullPath(directory), "import-jobs");
        _open = open;
        _synchronize = synchronize;
        _publish = publish;
        List<string> missing = WalDirectory.FindMissingDirectories(_directory, Directory.Exists);
        Directory.CreateDirectory(_directory);
        _lease = open(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate);
        try
        {
            WalDirectoryDurability.AcquireLease(_lease.SafeFileHandle);
            _lease.Flush(flushToDisk: true);
            synchronize(_directory);
            foreach (string created in missing)
            {
                string? parent = Path.GetDirectoryName(created);
                if (parent is not null)
                {
                    synchronize(parent);
                }
            }
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    public IEnumerable<ImportJobState> Load(int limit)
    {
        EnsureOwned();
        int temporary = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            if (++temporary > limit || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
            {
                throw new InvalidDataException("Import job temporary files exceed their recovery boundary.");
            }
            File.Delete(path);
        }
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (++count > limit)
            {
                throw new InvalidDataException("Import job history exceeds ImportJobMaxJobs; preserve it for recovery.");
            }
            ImportJobState state = Read(path);
            state.Validate();
            if (Path.GetFileNameWithoutExtension(path) != state.Id.ToString("N"))
            {
                throw new InvalidDataException("Import job identity does not match its file.");
            }
            yield return state;
        }
    }

    public void Save(ImportJobState state)
    {
        EnsureOwned();
        state.Validate();
        using WalRecordBuffer buffer = new(ManifestBytes - 65, reserveForEncoding: true);
        using (Utf8JsonWriter writer = new(buffer))
        {
            JsonSerializer.Serialize(writer, state, TelemetryAotContext.Default.ImportJobState);
        }
        byte[] checksum = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(buffer.WrittenMemory.Span)) + "\n");
        string temporary = Path.Combine(_directory, state.Id.ToString("N") + ".tmp");
        using (FileStream target = _open(temporary, FileMode.CreateNew))
        {
            target.Write(checksum);
            target.Write(buffer.WrittenMemory.Span);
            target.Flush(flushToDisk: true);
        }
        _publish(temporary, Path.Combine(_directory, state.Id.ToString("N") + ".json"));
        _synchronize(_directory);
    }

    private ImportJobState Read(string path)
    {
        using FileStream source = _open(path, FileMode.Open);
        long length = source.Length;
        if (length is < 66 or > ManifestBytes)
        {
            throw new InvalidDataException("Import job manifest exceeds its recovery byte boundary.");
        }
        byte[] bytes = new byte[(int)length];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1 || bytes[64] != (byte)'\n'
            || Encoding.ASCII.GetString(bytes, 0, 64) != Convert.ToHexString(SHA256.HashData(bytes.AsSpan(65))))
        {
            throw new InvalidDataException("Import job manifest checksum does not match its contents.");
        }
        try
        {
            return JsonSerializer.Deserialize(bytes.AsSpan(65), TelemetryAotContext.Default.ImportJobState)
                ?? throw new InvalidDataException("Import job manifest is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Import job manifest is not valid JSON.");
        }
    }

    private static FileStream Open(string path, FileMode mode) => new(path, mode,
        mode == FileMode.Open ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);

    private void EnsureOwned() => ObjectDisposedException.ThrowIf(!_lease.CanRead, this);

    public void Dispose() => _lease.Dispose();
}
