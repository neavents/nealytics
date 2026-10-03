using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Declaring the schema in a file rather than in indexed environment variables.
///
/// Not a second configuration system — one more provider feeding the same TelemetryEngine section.
/// It exists because the shape does not scale: eight dimensions, five measures and two rollups is
/// roughly seventy TelemetryEngine__Measures__11__ lines, which cannot be reviewed in a diff, and
/// a deployment kept them in a compose file that was in no repository at all.
/// </summary>
public class SchemaFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"schema_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private TelemetryEngineOptions Bind(string json, params (string Key, string Value)[] environment)
    {
        File.WriteAllText(_path, json);

        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TelemetryEngine:MaxDimensions"] = "64",
        });
        builder.AddJsonFile(_path, optional: false);
        builder.AddInMemoryCollection(
            environment.ToDictionary(e => e.Key, e => (string?)e.Value));

        return builder.Build().GetSection("TelemetryEngine").Get<TelemetryEngineOptions>()
            ?? new TelemetryEngineOptions();
    }

    [Fact]
    public void AFileDeclaresDimensionsMeasuresAndRollups()
    {
        TelemetryEngineOptions options = Bind("""
        {
          "TelemetryEngine": {
            "Dimensions": [{ "Name": "widget_id", "Type": "String" }],
            "Measures": [{ "Name": "dwell_ms", "Type": "UInt32", "Aggregations": "sum,avg" }],
            "Rollups": [{ "Name": "daily", "Grain": "day", "Dimensions": "widget_id", "Measures": "dwell_ms:sum" }]
          }
        }
        """);

        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        RollupRegistry rollups = new(options, dimensions, measures);

        dimensions.Active.Should().ContainSingle(d => d.Name == "widget_id");
        measures.Active.Should().ContainSingle(m => m.Name == "dwell_ms");
        rollups.Declared.Should().ContainSingle(r => r.Name == "daily");
    }

    [Fact]
    public void AnEnvironmentVariableStillWins()
    {
        TelemetryEngineOptions options = Bind(
            """{ "TelemetryEngine": { "RetentionDays": 90 } }""",
            ("TelemetryEngine:RetentionDays", "30"));

        options.RetentionDays.Should().Be(
            30,
            "the file is for the declaration; a one-off override must not need the file edited");
    }

    [Fact]
    public void CommentKeysAreIgnored_SoTheFileCanExplainItself()
    {
        TelemetryEngineOptions options = Bind("""
        {
          "//": ["why this file exists"],
          "TelemetryEngine": {
            "//": "and why each entry is here",
            "Dimensions": [{ "//": "a note", "Name": "widget_id", "Type": "String" }]
          }
        }
        """);

        new DimensionRegistry(options).Active.Should().ContainSingle(d => d.Name == "widget_id");
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nealytics.slnx"))) return directory;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Nealytics.slnx not found walking up from the test output.");
    }

    /// <summary>
    /// Every schema file the repository ships, not just the headline example.
    ///
    /// Enumerated rather than listed, because a listed file is one someone forgets to add. The
    /// benchmark's schema is the case in point: a typo in it refuses the boot, and the only thing
    /// that would have surfaced it is a five-minute benchmark run nobody does on a whim.
    /// </summary>
    public static TheoryData<string> ShippedSchemas()
    {
        TheoryData<string> data = [];
        DirectoryInfo root = RepositoryRoot();

        foreach (string path in Directory.EnumerateFiles(root.FullName, "*schema*.json", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            data.Add(Path.GetRelativePath(root.FullName, path));
        }

        return data;
    }

    [Fact]
    public void ThereAreShippedSchemasToCheck()
    {
        // Without this, a glob that stopped matching would make the theory below vacuously green.
        ShippedSchemas().Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(ShippedSchemas))]
    public void AShippedSchemaIsValidAndDeclaresNothingTheEngineKnows(string relativePath)
    {
        string path = Path.Combine(RepositoryRoot().FullName, relativePath);

        TelemetryEngineOptions options = Bind(File.ReadAllText(path));

        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        RollupRegistry rollups = new(options, dimensions, measures);

        // It boots. An example that refuses the boot teaches the wrong thing loudly, and a
        // benchmark schema that refuses it wastes a ClickHouse reset before saying so.
        dimensions.Active.Should().NotBeEmpty($"{relativePath} declares dimensions");
        rollups.Declared.Should().NotBeEmpty($"{relativePath} declares at least one rollup");

        // A rollup naming a dimension or measure the same file never declared would be created by
        // the reconciler as a view over a column that does not exist, and fail at DDL time on a
        // live deployment rather than here.
        foreach (Rollup rollup in rollups.Declared)
        {
            foreach (RollupColumn column in rollup.Dimensions)
            {
                column.Name.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void AMalformedFileFailsLoudly_RatherThanBootingWithNoSchema()
    {
        File.WriteAllText(_path, "{ not json");

        ConfigurationBuilder builder = new();
        builder.AddJsonFile(_path, optional: false);

        Action build = () => builder.Build();

        build.Should().Throw<Exception>(
            "silently booting with an empty declaration would drop every dimension at ingest while "
            + "reporting itself healthy");
    }
}
