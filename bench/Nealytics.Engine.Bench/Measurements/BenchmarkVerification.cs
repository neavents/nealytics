namespace Nealytics.Engine.Bench.Measurements;

public static class BenchmarkVerification
{
    public static bool Passed(LevelResult result, bool verifyStoredCount)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Errors == 0
            && result.VisibilityTimeouts == 0
            && (!verifyStoredCount || result.Stored == result.EventsSent);
    }
}
