namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;

public sealed class EventTimeSeriesPoint
{
    public DateTime Bucket { get; set; }
    public long Count { get; set; }
}
