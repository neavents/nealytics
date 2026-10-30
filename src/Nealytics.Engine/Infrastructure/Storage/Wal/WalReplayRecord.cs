namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using Nealytics.Engine.Infrastructure.Serialization;

internal readonly record struct WalReplayRecord(GlobalTelemetryPayload Payload, int FramedBytes);
