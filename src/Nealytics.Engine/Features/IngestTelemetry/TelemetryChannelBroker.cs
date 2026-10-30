namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed class TelemetryChannelBroker
{
    private readonly Channel<QueuedPayload> _channel;
    private readonly WalAdmission _admission;
    private readonly int _maxItemBytes;

    public TelemetryChannelBroker(IOptions<TelemetryEngineOptions> options)
        : this(options, channel: null)
    {
    }

    internal TelemetryChannelBroker(IOptions<TelemetryEngineOptions> options, Channel<QueuedPayload>? channel)
    {
        TelemetryEngineOptions settings = options.Value;
        _admission = new WalAdmission(settings.MemoryChannelCapacity, settings.MemoryChannelByteCapacity);
        _maxItemBytes = Math.Min(settings.MemoryChannelByteCapacity, settings.DatabaseBatchByteCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxItemBytes, 1);
        _channel = channel ?? Channel.CreateBounded<QueuedPayload>(settings.MemoryChannelCapacity);
        Reader = new BudgetedReader(_channel.Reader, _admission);
    }

    internal int RetainedBytes => _admission.PendingBytes;

    public async ValueTask PublishAsync(GlobalTelemetryPayload analyticsEvent, CancellationToken cancellationToken)
    {
        int bytes = TelemetryPayloadMemory.Estimate(analyticsEvent);
        if (bytes > _maxItemBytes)
        {
            throw new IOException("The event exceeds the configured channel or database batch byte capacity.");
        }
        await _admission.ReserveAsync(bytes, cancellationToken);
        bool published = false;
        try
        {
            await _channel.Writer.WriteAsync(new QueuedPayload(analyticsEvent, bytes), cancellationToken);
            published = true;
            TelemetryDiagnostics.IngestedEventsTotal.Add(1);
            TelemetryDiagnostics.IncrementQueueCounter();
        }
        finally
        {
            if (!published)
            {
                _admission.Release(bytes);
            }
        }
    }

    public ChannelReader<GlobalTelemetryPayload> Reader { get; }

    internal readonly record struct QueuedPayload(GlobalTelemetryPayload Payload, int Bytes);

    private sealed class BudgetedReader : ChannelReader<GlobalTelemetryPayload>
    {
        private readonly ChannelReader<QueuedPayload> _reader;
        private readonly WalAdmission _admission;

        public BudgetedReader(ChannelReader<QueuedPayload> reader, WalAdmission admission)
        {
            _reader = reader;
            _admission = admission;
        }

        public override Task Completion => _reader.Completion;
        public override bool CanCount => _reader.CanCount;
        public override int Count => _reader.Count;
        public override bool CanPeek => _reader.CanPeek;

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            _reader.WaitToReadAsync(cancellationToken);

        public override bool TryRead([MaybeNullWhen(false)] out GlobalTelemetryPayload item)
        {
            if (_reader.TryRead(out QueuedPayload queued))
            {
                _admission.Release(queued.Bytes);
                item = queued.Payload;
                return true;
            }
            item = null;
            return false;
        }

        public override bool TryPeek([MaybeNullWhen(false)] out GlobalTelemetryPayload item)
        {
            bool available = _reader.TryPeek(out QueuedPayload queued);
            item = queued.Payload;
            return available;
        }
    }
}
