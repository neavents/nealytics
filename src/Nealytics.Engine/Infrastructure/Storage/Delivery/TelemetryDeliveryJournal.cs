namespace Nealytics.Engine.Infrastructure.Storage.Delivery;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class TelemetryDeliveryJournal
{
    private readonly string _directory;
    private readonly int _maxRecords;
    private readonly long _maxBytes;
    private readonly HashSet<Guid> _delivered = new HashSet<Guid>();
    private int _records;
    private long _bytes;

    public TelemetryDeliveryJournal(IOptions<TelemetryEngineOptions> options)
    {
        _maxRecords = options.Value.DeliveryJournalMaxRecords;
        _maxBytes = options.Value.DeliveryJournalMaxBytes;
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxBytes, 1);
        _directory = Path.Combine(Path.GetFullPath(options.Value.WriteAheadLogDirectory), "delivery");
        Directory.CreateDirectory(_directory);
        WalDirectoryDurability.Synchronize(Path.GetDirectoryName(_directory)!);
    }

    public bool IsDelivered(GlobalTelemetryPayload payload) => _delivered.Contains(payload.WalRecordId);

    public IEnumerable<TelemetryDeliveryUnit> Load()
    {
        _delivered.Clear();
        _records = 0;
        _bytes = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            File.Delete(path);
        }
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            Account(new FileInfo(path).Length);
            TelemetryDeliveryUnit unit = Read(path);
            Validate(unit);
            ReserveRecords(unit.Items.Length);
            if (Path.GetFileNameWithoutExtension(path) != unit.Id.ToString("N"))
            {
                throw new InvalidDataException("The delivery manifest identity does not match its file.");
            }
            if (File.Exists(CommittedPath(unit)))
            {
                Remember(unit);
            }
            else
            {
                yield return unit;
            }
        }
    }

    public TelemetryDeliveryUnit Prepare(TelemetryDeliveryUnit unit)
    {
        Validate(unit);
        using WalRecordBuffer buffer = new WalRecordBuffer((int)Math.Min(int.MaxValue, _maxBytes - _bytes), reserveForEncoding: true);
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, unit, TelemetryAotContext.Default.TelemetryDeliveryUnit);
        }
        Account(buffer.WrittenMemory.Length + 65L);
        ReserveRecords(unit.Items.Length);
        byte[] checksum = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(buffer.WrittenMemory.Span)) + "\n");
        string temporary = Path.Combine(_directory, unit.Id.ToString("N") + ".tmp");
        using (FileStream target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            target.Write(checksum);
            target.Write(buffer.WrittenMemory.Span);
            target.Flush(flushToDisk: true);
        }
        File.Move(temporary, ManifestPath(unit));
        WalDirectoryDurability.Synchronize(_directory);
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, TelemetryAotContext.Default.TelemetryDeliveryUnit)!;
    }

    private static TelemetryDeliveryUnit Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 66 || bytes[64] != (byte)'\n'
            || !Encoding.ASCII.GetString(bytes.AsSpan(0, 64)).Equals(
                Convert.ToHexString(SHA256.HashData(bytes.AsSpan(65))), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The delivery manifest checksum does not match its contents.");
        }
        return JsonSerializer.Deserialize(bytes.AsSpan(65), TelemetryAotContext.Default.TelemetryDeliveryUnit)
            ?? throw new InvalidDataException("The delivery manifest is empty.");
    }

    public void Complete(TelemetryDeliveryUnit unit)
    {
        using (FileStream marker = new FileStream(CommittedPath(unit), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            marker.Flush(flushToDisk: true);
        }
        WalDirectoryDurability.Synchronize(_directory);
        Remember(unit);
    }

    public void Clear()
    {
        foreach (string path in Directory.EnumerateFiles(_directory))
        {
            File.Delete(path);
        }
        WalDirectoryDurability.Synchronize(_directory);
        _delivered.Clear();
        _records = 0;
        _bytes = 0;
    }

    private void Account(long bytes)
    {
        if (bytes > _maxBytes - _bytes)
        {
            throw new InvalidDataException("The delivery journal exceeds DeliveryJournalMaxBytes; preserve it for recovery.");
        }
        _bytes += bytes;
    }

    private void ReserveRecords(int count)
    {
        if (count > _maxRecords - _records)
        {
            throw new InvalidDataException("The delivery journal exceeds DeliveryJournalMaxRecords; preserve it for recovery.");
        }
        _records += count;
    }

    private static void Validate(TelemetryDeliveryUnit unit)
    {
        if (unit.Version != 1 || unit.Id == Guid.Empty || unit.SourceId == Guid.Empty
            || unit.Schema is null || unit.Schema.Length != 64 || unit.Items is null || unit.Items.Length == 0 || unit.IngestedAt == default)
        {
            throw new InvalidDataException("The delivery manifest header is invalid.");
        }
        HashSet<Guid> identities = new HashSet<Guid>();
        foreach (GlobalTelemetryPayload payload in unit.Items)
        {
            if (payload is null || payload.WalRecordId == Guid.Empty || !identities.Add(payload.WalRecordId))
            {
                throw new InvalidDataException("The delivery manifest contains missing or duplicate record identities.");
            }
        }
    }

    private void Remember(TelemetryDeliveryUnit unit)
    {
        foreach (GlobalTelemetryPayload payload in unit.Items)
        {
            if (!_delivered.Add(payload.WalRecordId))
            {
                throw new InvalidDataException("A WAL record belongs to multiple committed delivery units.");
            }
        }
    }

    private string ManifestPath(TelemetryDeliveryUnit unit) => Path.Combine(_directory, unit.Id.ToString("N") + ".json");
    private string CommittedPath(TelemetryDeliveryUnit unit) => Path.Combine(_directory, unit.Id.ToString("N") + ".committed");
}
