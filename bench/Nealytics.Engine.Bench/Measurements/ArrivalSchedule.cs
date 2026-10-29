namespace Nealytics.Engine.Bench.Measurements;

public sealed class ArrivalSchedule
{
    public ArrivalSchedule(double requestsPerSecond)
    {
        if (!double.IsFinite(requestsPerSecond) || requestsPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond));
        }

        RequestsPerSecond = requestsPerSecond;
    }

    public double RequestsPerSecond { get; }

    public bool IsOpenLoop => RequestsPerSecond > 0;

    public TimeSpan Offset(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (!IsOpenLoop)
        {
            return TimeSpan.Zero;
        }

        double ticks = sequence / RequestsPerSecond * TimeSpan.TicksPerSecond;

        if (!double.IsFinite(ticks) || ticks >= long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}
