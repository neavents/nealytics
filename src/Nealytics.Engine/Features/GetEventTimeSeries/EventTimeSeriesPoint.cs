namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Text.Json.Serialization;

public sealed class EventTimeSeriesPoint
{
    public DateTime Bucket { get; set; }
    public string? Series { get; set; }
    public long Count { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Value { get; set; }
}
