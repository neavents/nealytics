using System.Text.RegularExpressions;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The AOT smoke script asks only for things it declares.
///
/// <b>What this cost to learn.</b> The script declared <c>m_float64</c> with no
/// <c>Aggregations</c> — so it defaulted to <c>sum,avg,min,max,count</c> — and then asked for
/// <c>metric=p95(m_float64)</c>. The engine correctly answered <c>400</c>: refusing an aggregation
/// nobody declared is a feature, not a bug. But the check sits near the end of a run whose earlier
/// stages had always failed first, so it had <b>never once executed</b>, and finding it took a
/// three-hour ILC publish.
///
/// Both halves are in the same file, twenty lines apart. That makes this a static consistency
/// question, answerable in milliseconds, and there is no reason to spend a compile on it.
///
/// This does not replace the smoke run. Nothing here proves the AOT binary works — only running it
/// does. What it does is guarantee that when the binary <i>is</i> built, every assertion in the
/// script is one the engine could satisfy, so a failure means what it says.
/// </summary>
public class AotSmokeScriptTests
{
    private static string Script()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "scripts", "aot-smoke.sh");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "scripts/aot-smoke.sh not found. It is the only thing that catches a missing rd.xml "
            + "runtime directive, because dotnet test runs on the JIT where MakeGenericType always "
            + "works.");
    }

    /// <summary>Indexed env declarations, e.g. <c>Measures__8__Aggregations</c>, by name.</summary>
    private static Dictionary<string, Dictionary<string, string>> Declared(string section)
    {
        Dictionary<string, Dictionary<string, string>> byIndex = new(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
            Script(),
            $@"TelemetryEngine__{section}__(?<index>\d+)__(?<field>\w+)=(?<value>[^\s\\]+)"))
        {
            string index = match.Groups["index"].Value;

            if (!byIndex.TryGetValue(index, out Dictionary<string, string>? fields))
            {
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                byIndex[index] = fields;
            }

            fields[match.Groups["field"].Value] = match.Groups["value"].Value;
        }

        Dictionary<string, Dictionary<string, string>> byName = new(StringComparer.Ordinal);

        foreach (Dictionary<string, string> fields in byIndex.Values)
        {
            if (fields.TryGetValue("Name", out string? name)) byName[name] = fields;
        }

        return byName;
    }

    /// <summary>Every URL passed to the script's own <c>check</c> helper.</summary>
    private static List<string> CheckedPaths() =>
        [.. Regex.Matches(Script(), @"^check\s+""[^""]+""\s+""(?<path>[^""]+)""", RegexOptions.Multiline)
            .Select(match => match.Groups["path"].Value)];

    [Fact]
    public void TheScriptIsParsedRatherThanAssumed()
    {
        // Guards the guard. A regex that quietly matched nothing would make every assertion below
        // pass over an empty set -- which is the exact failure shape this whole file is about.
        Declared("Measures").Should().HaveCountGreaterThan(5);
        Declared("Dimensions").Should().HaveCountGreaterThan(3);
        CheckedPaths().Should().HaveCountGreaterThan(10);
    }

    [Fact]
    public void EveryMeasureAggregationTheScriptAsksForIsDeclaredOnThatMeasure()
    {
        Dictionary<string, Dictionary<string, string>> measures = Declared("Measures");

        foreach (string path in CheckedPaths())
        {
            Match metric = Regex.Match(path, @"metric=(?<agg>\w+)\((?<measure>\w+)\)");
            if (!metric.Success) continue;

            string aggregation = metric.Groups["agg"].Value;
            string measure = metric.Groups["measure"].Value;

            measures.Should().ContainKey(
                measure, $"the script asks for {aggregation}({measure}) but never declares it");

            // The engine's own default when Aggregations is omitted -- see MeasureOptions.
            string declared = measures[measure].GetValueOrDefault(
                "Aggregations", "sum,avg,min,max,count");

            declared.Split(',', StringSplitOptions.TrimEntries)
                .Should().Contain(
                    aggregation,
                    $"the script asks for {aggregation}({measure}), and an aggregation the "
                    + "declaration does not offer is a 400 by design. Add it to "
                    + $"TelemetryEngine__Measures__N__Aggregations rather than dropping the check -- "
                    + "the point of it is to exercise that code path in a real AOT binary.");
        }
    }

    [Fact]
    public void EveryGroupByTheScriptAsksForIsADeclaredDimensionOrACoreColumn()
    {
        HashSet<string> groupable = [.. Declared("Dimensions").Keys];

        // Core columns are compiled in rather than declared, so they are not in the env block.
        foreach (string core in new[]
                 {
                     "event_type", "object_id", "session_id", "user_id", "device_class", "os",
                     "browser", "country", "traffic_class", "page_path", "referrer",
                 })
        {
            groupable.Add(core);
        }

        foreach (string path in CheckedPaths())
        {
            Match match = Regex.Match(path, @"groupBy=(?<column>\w+)");
            if (!match.Success) continue;

            groupable.Should().Contain(
                match.Groups["column"].Value,
                "a groupBy the deployment does not declare is a 400, and the smoke run would "
                + "report it as an AOT failure");
        }
    }

    [Fact]
    public void EveryRollupNamesOnlyDeclaredColumns()
    {
        // A rollup over a column the registry does not offer refuses the boot outright -- which in
        // a smoke run reads as "the AOT binary will not start", sending you to rd.xml for a
        // configuration mistake.
        Dictionary<string, Dictionary<string, string>> rollups = Declared("Rollups");
        HashSet<string> dimensions = [.. Declared("Dimensions").Keys];
        HashSet<string> measures = [.. Declared("Measures").Keys];

        foreach (Dictionary<string, string> rollup in rollups.Values)
        {
            foreach (string dimension in (rollup.GetValueOrDefault("Dimensions") ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                dimensions.Should().Contain(dimension);
            }

            foreach (string spec in (rollup.GetValueOrDefault("Measures") ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                measures.Should().Contain(spec.Split(':')[0]);
            }
        }
    }

    [Fact]
    public void TheScriptStillProvesTheThingItExistsFor()
    {
        string script = Script();

        // If the write-path assertion is ever softened, this file becomes a very confident test of
        // nothing: a published binary returns 202 on ingest and passes every health check whether
        // or not a single row was committed.
        script.Should().Contain(
            "write path committed the row",
            "the one failure mode rd.xml exists to prevent is silent -- accepted, healthy, stored "
            + "nowhere");
    }
}
