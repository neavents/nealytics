namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// A project id that deletes its rows when the test finishes.
///
/// <remarks>
/// <para>Tests generate a fresh <c>p-load-{guid}</c> style id so runs cannot collide, and then
/// leave the rows behind. That was invisible while two other classes ran
/// <c>TRUNCATE TABLE global_events</c> in their setup — the truncate was, accidentally, the
/// suite's only cleanup. Removing it (because it also destroyed a running deployment's real analytics)
/// turned that into unbounded growth: the load test alone writes 8,000 rows a run, under a new
/// project id every time, so nothing would ever remove them.</para>
///
/// <para>A unique id per run is still right — it is what makes the assertions independent. What
/// was missing is the other half.</para>
/// </remarks>
/// </summary>
public sealed class TestProject : IAsyncDisposable
{
    private TestProject(string id) => Id = id;

    public string Id { get; }

    public static TestProject New(string prefix) => new($"{prefix}-{Guid.NewGuid():N}");

    public override string ToString() => Id;

    public ValueTask DisposeAsync() => new(ClickHouseTestSupport.DeleteProjectsAsync(Id));
}
