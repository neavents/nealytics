using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ProjectContextTests
{
    internal static ProjectCatalog Build(TelemetryEngineOptions options) =>
        new(options, ProjectContext.Compile("baseline", options) with { ProjectId = null });

    internal static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new() { Name = "legacy" }],
        NamedMetrics = [new() { Name = "legacy_count" }],
        ProjectSchemas =
        [
            new()
            {
                ProjectId = "alpha",
                Dimensions = [new() { Name = "region" }, new() { Name = "unit" }],
                Measures = [new() { Name = "amount", Type = "Int64", Minimum = 0, Maximum = 100, Aggregations = "sum,avg", UnitDimension = "unit", ServerOnly = true }],
                TenantAttributes = [new() { Name = "cohort" }],
                NamedMetrics = [new() { Name = "activity", EventType = "first" }],
                EventSegments = [new() { Name = "local", Filters = ["region:east"] }],
                ComputedDimensions = [new() { Name = "normalized", Source = "region", Operation = "lower" }],
                SavedReports = [new() { Name = "overview", Fields = [new() { Name = "value", Metric = "activity" }], Segment = "local", GroupBy = ["normalized"] }],
                Rollups = [new() { Name = "alpha_hour", Grain = "hour", Dimensions = "region", Measures = "amount:sum" }]
            },
            new()
            {
                ProjectId = "beta",
                Dimensions = [new() { Name = "category" }, new() { Name = "unit" }],
                Measures = [new() { Name = "amount", Type = "Int64", Minimum = -100, Maximum = 200, Aggregations = "avg", UnitDimension = "unit" }],
                NamedMetrics = [new() { Name = "activity", EventType = "second" }],
                Rollups = [new() { Name = "beta_day", Grain = "day" }]
            }
        ]
    };

    [Fact]
    public void LogicalDefinitionsStayLocalAndThePhysicalUnionKeepsEveryCompatibleColumn()
    {
        var options = Options();
        var catalog = Build(options);
        Assert.True(catalog.Enabled);
        Assert.Equal(2, catalog.Declared.Count);
        var alpha = catalog.Resolve("alpha")!;
        var beta = catalog.Resolve("beta")!;
        Assert.Equal("alpha", alpha.ProjectId);
        Assert.Equal("beta", beta.ProjectId);
        Assert.True(alpha.Dimensions.IsActive("region"));
        Assert.False(alpha.Dimensions.IsActive("category"));
        Assert.DoesNotContain("region", beta.Columns.Allowed);
        Assert.Contains("normalized", alpha.Analytics.Value.Allowed);
        Assert.DoesNotContain("normalized", alpha.Columns.Allowed);
        Assert.Null(beta.ComputedDimensions.Find("normalized"));
        Assert.Equal("first", alpha.NamedMetrics.Find("activity")!.EventType);
        Assert.Equal("second", beta.NamedMetrics.Find("activity")!.EventType);
        Assert.Null(alpha.NamedMetrics.Find("legacy_count"));
        Assert.NotNull(alpha.EventSegments.Find("local"));
        Assert.Null(beta.EventSegments.Find("local"));
        Assert.NotNull(alpha.SavedReports.Find("overview"));
        Assert.Null(beta.SavedReports.Find("overview"));
        Assert.True(alpha.TenantAttributes.IsDeclared("cohort"));
        Assert.False(beta.TenantAttributes.Enabled);
        Assert.Null(catalog.Resolve("unknown"));
        Assert.Null(catalog.Resolve(null));
        Assert.Null(catalog.Resolve("Alpha"));
        Assert.Equal(new[] { "legacy", "region", "unit", "category" }, catalog.Storage.Dimensions.Declared.Select(column => column.Name));
        var physical = Assert.Single(catalog.Storage.Measures.Active);
        Assert.Equal(-100, physical.Minimum);
        Assert.Equal(200, physical.Maximum);
        Assert.Equal(new[] { "avg", "sum" }, physical.Aggregations.Order());
        Assert.False(physical.ServerOnly);
        Assert.Null(physical.UnitDimension);
        Assert.True(alpha.Measures.Find("amount")!.ServerOnly);
        Assert.Equal(0, alpha.Measures.Find("amount")!.Minimum);
        Assert.Equal("unit", alpha.Measures.Find("amount")!.UnitDimension);
        Assert.Equal("cohort", Assert.Single(catalog.Storage.TenantAttributes.Declared));
        options.ProjectSchemas[0].NamedMetrics[0].EventType = "changed";
        options.ProjectSchemas.Clear();
        Assert.Equal("first", alpha.NamedMetrics.Find("activity")!.EventType);
        Assert.Equal(2, catalog.Declared.Count);
    }

    [Fact]
    public void LegacyFallbackIsExplicitAndNeverOverridesAConfiguredProject()
    {
        var options = Options();
        options.AllowLegacyProjectSchema = true;
        var catalog = Build(options);
        Assert.Same(catalog.Default, catalog.Resolve("unconfigured"));
        Assert.Same(catalog.Default, catalog.Resolve(null));
        Assert.NotSame(catalog.Default, catalog.Resolve("alpha"));
        options.ProjectSchemas = null!;
        catalog = Build(options);
        Assert.False(catalog.Enabled);
        Assert.Empty(catalog.Declared);
        Assert.Same(catalog.Default, catalog.Resolve("any"));
        Assert.Same(catalog.Default.Dimensions, catalog.Storage.Dimensions);
        Assert.Same(catalog.Default.Measures, catalog.Storage.Measures);
        Assert.Same(catalog.Default.TenantAttributes, catalog.Storage.TenantAttributes);
        Assert.Same(catalog.Default.Rollups, catalog.Storage.Rollups);
        Assert.Throws<ArgumentNullException>(() => new ProjectCatalog(null!, catalog.Default));
        Assert.Throws<ArgumentNullException>(() => new ProjectCatalog(options, null!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("long")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("limit")]
    [InlineData("low_cap")]
    [InlineData("high_cap")]
    public void InvalidProjectDeclarationsCannotActivate(string failure)
    {
        var options = Options();
        switch (failure)
        {
            case "null": options.ProjectSchemas[0] = null!; break;
            case "empty": options.ProjectSchemas[0].ProjectId = null!; break;
            case "blank": options.ProjectSchemas[0].ProjectId = " \t "; break;
            case "long": options.ProjectSchemas[0].ProjectId = new string('x', 257); break;
            case "duplicate": options.ProjectSchemas[1].ProjectId = " alpha "; break;
            case "version": options.ProjectSchemas[0].Version = 2; break;
            case "limit": options.MaxProjectSchemas = 1; break;
            case "low_cap": options.MaxProjectSchemas = 0; break;
            case "high_cap": options.MaxProjectSchemas = 1025; break;
        }
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public void ExactLimitsAndIdentifierLengthAreAccepted(int cap)
    {
        var options = Options();
        options.MaxProjectSchemas = cap;
        options.ProjectSchemas = [new() { ProjectId = " " + new string('x', 256) + " " }];
        var catalog = Build(options);
        Assert.Equal(new string('x', 256), Assert.Single(catalog.Declared).ProjectId);
        Assert.NotNull(catalog.Resolve(new string('x', 256)));
    }

    [Theory]
    [InlineData("dimension")]
    [InlineData("measure")]
    [InlineData("collision")]
    [InlineData("dimension_cap")]
    [InlineData("measure_cap")]
    [InlineData("attribute_cap")]
    [InlineData("rollup_table")]
    [InlineData("rollup_view")]
    public void IncompatibleOrUnboundedPhysicalUnionsFailBeforeStorage(string failure)
    {
        var options = Options();
        switch (failure)
        {
            case "dimension": options.ProjectSchemas[1].Dimensions.Add(new() { Name = "region", Type = "UInt64" }); break;
            case "measure": options.ProjectSchemas[1].Measures[0].Type = "UInt64"; break;
            case "collision": options.ProjectSchemas[1].Dimensions.Add(new() { Name = "amount" }); options.ProjectSchemas[1].Measures.Clear(); break;
            case "dimension_cap": options.MaxDimensions = 3; break;
            case "measure_cap":
                options.MaxMeasures = 1;
                options.ProjectSchemas[1].Measures.Add(new() { Name = "other", Type = "Int64", Aggregations = "sum" });
                options.ProjectSchemas[1].Measures.RemoveAt(0);
                break;
            case "attribute_cap":
                options.ProjectSchemas[0].TenantAttributes = Enumerable.Range(0, 16).Select(index => new TenantAttributeOptions { Name = "a" + index }).ToList();
                options.ProjectSchemas[1].TenantAttributes = [new() { Name = "other" }];
                break;
            case "rollup_table": options.ProjectSchemas[1].Rollups[0].Name = "alpha_hour"; break;
            case "rollup_view":
                options.ProjectSchemas[1].Dimensions.Add(new() { Name = "region" });
                options.ProjectSchemas[1].Measures[0].Aggregations = "sum,avg";
                options.ProjectSchemas[1].Rollups = [new() { Name = "alpha_hour", Grain = "hour", Dimensions = "region", Measures = "amount:sum", EventTypes = "second" }];
                break;
        }
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Fact]
    public void RetiredAndUnboundedColumnsKeepUnionSemanticsAndReadinessIsShared()
    {
        var options = Options();
        options.ProjectSchemas[1].Dimensions.Add(new() { Name = "region", Retired = true });
        options.ProjectSchemas[1].Measures[0].Minimum = null;
        options.ProjectSchemas[1].Measures[0].Maximum = null;
        var catalog = Build(options);
        Assert.True(catalog.Storage.Dimensions.IsActive("region"));
        Assert.Null(catalog.Storage.Measures.Find("amount")!.Minimum);
        Assert.Null(catalog.Storage.Measures.Find("amount")!.Maximum);
        var alpha = catalog.Resolve("alpha")!;
        var rollup = Assert.Single(alpha.Rollups.Declared);
        Assert.False(alpha.Rollups.IsRoutable(rollup));
        Assert.Equal("completion has not been verified", alpha.Rollups.UnroutableReason(rollup));
        catalog.Storage.Rollups.MarkRoutable(rollup);
        Assert.True(alpha.Rollups.IsRoutable(rollup));
        Assert.Equal("alpha_hour", Assert.Single(alpha.Rollups.Routable).Name);
        alpha.Rollups.MarkUnroutable(rollup, "test fence");
        Assert.Equal("test fence", catalog.Storage.Rollups.UnroutableReason(rollup));
        Assert.DoesNotContain(catalog.Resolve("beta")!.Rollups.Declared, candidate => candidate.Name == rollup.Name);
        options.ProjectSchemas[0].Dimensions[0].Retired = true;
        options.ProjectSchemas[0].ComputedDimensions.Clear();
        options.ProjectSchemas[0].EventSegments.Clear();
        options.ProjectSchemas[0].SavedReports.Clear();
        options.ProjectSchemas[0].Rollups.Clear();
        options.ProjectSchemas[0].Measures[0].Retired = true;
        options.ProjectSchemas[1].Measures[0].Retired = true;
        catalog = Build(options);
        Assert.False(catalog.Storage.Dimensions.IsActive("region"));
        Assert.False(catalog.Storage.Measures.IsActive("amount"));
    }

    [Fact]
    public void IdenticalPhysicalRollupsKeepTheirOwnLogicalMeasureMetadata()
    {
        var options = Options();
        options.Rollups = [new() { Name = "global", Grain = "day" }];
        options.ProjectSchemas[1].Measures[0].Aggregations = "sum,avg";
        options.ProjectSchemas[1].Dimensions.Add(new() { Name = "region" });
        options.ProjectSchemas[1].Rollups = [new() { Name = "alpha_hour", Grain = "hour", Dimensions = "region", Measures = "amount:sum" }];
        var catalog = Build(options);
        Assert.Equal(2, catalog.Storage.Rollups.Declared.Count);
        Assert.Equal(0, Assert.Single(catalog.Resolve("alpha")!.Rollups.Declared).Measures[0].Measure.Minimum);
        Assert.Equal(-100, Assert.Single(catalog.Resolve("beta")!.Rollups.Declared).Measures[0].Measure.Minimum);
        var global = Assert.Single(catalog.Default.Rollups.Declared);
        catalog.Storage.Rollups.MarkRoutable(global);
        Assert.True(catalog.Default.Rollups.IsRoutable(global));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredProjectWritesAlwaysRequirePinnedKeys(bool legacy)
    {
        var options = Options();
        options.AllowLegacyProjectSchema = legacy;
        options.AllowedProjectKeys = "legacy,oldpin";
        options.Projects = [new() { Key = "oldpin", ProjectId = "beta" }];
        options.IngestionKeys = [new() { Key = "a", Scope = "server", ProjectId = "alpha" }, new() { Key = "unknown", Scope = "public", ProjectId = "unknown" }];
        var keys = new ApiKeyValidator(Microsoft.Extensions.Options.Options.Create(options), NullLogger<ApiKeyValidator>.Instance);
        Assert.True(keys.MayWriteProject("a", "alpha"));
        Assert.False(keys.MayWriteProject("a", "beta"));
        Assert.True(keys.MayWriteProject("oldpin", "beta"));
        Assert.False(keys.MayWriteProject("legacy", "alpha"));
        Assert.Equal(legacy, keys.MayWriteProject("legacy", "other"));
        Assert.Equal(legacy, keys.MayWriteProject("unknown", "unknown"));
        Assert.True(keys.TryResolve("legacy", out var unpinned));
        Assert.NotSame(IngestionKeyPolicy.Unpinned, unpinned);
        Assert.False(ApiKeyValidator.MayWriteProject(unpinned!, "beta"));
        options.ProjectSchemas.Clear();
        keys = new ApiKeyValidator(Microsoft.Extensions.Options.Options.Create(options), NullLogger<ApiKeyValidator>.Instance);
        Assert.True(keys.MayWriteProject("legacy", "alpha"));
        Assert.True(keys.TryResolve("legacy", out unpinned));
        Assert.Same(IngestionKeyPolicy.Unpinned, unpinned);
    }

    [Fact]
    public void RequestContextIsSelectedOnceAndHasAnExplicitDefaultOutsideHttp()
    {
        var catalog = Build(Options());
        var accessor = new HttpContextAccessor();
        Assert.Same(catalog.Default, new ProjectRequestContext(accessor, catalog).Current);
        accessor.HttpContext = new DefaultHttpContext();
        Assert.Same(catalog.Default, new ProjectRequestContext(accessor, catalog).Current);
        accessor.HttpContext.Items[ProjectRequestContext.ItemKey] = "wrong type";
        Assert.Same(catalog.Default, new ProjectRequestContext(accessor, catalog).Current);
        var selected = catalog.Resolve("alpha");
        accessor.HttpContext.Items[ProjectRequestContext.ItemKey] = selected;
        var request = new ProjectRequestContext(accessor, catalog);
        accessor.HttpContext.Items[ProjectRequestContext.ItemKey] = catalog.Resolve("beta");
        Assert.Same(selected, request.Current);
        Assert.Throws<ArgumentNullException>(() => new ProjectRequestContext(null!, catalog));
        Assert.Throws<ArgumentNullException>(() => new ProjectRequestContext(accessor, null!));
    }

    [Theory]
    [InlineData("UInt8")]
    [InlineData("UInt16")]
    [InlineData("UInt32")]
    [InlineData("UInt64")]
    [InlineData("Int16")]
    [InlineData("Int32")]
    [InlineData("Int64")]
    [InlineData("Float32")]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    public void LogicalBoundsAreNormalizedBeforeWalAndAgreeWithPhysicalParsing(string type)
    {
        var options = Options();
        options.ProjectSchemas[0].Measures[0].Type = type;
        options.ProjectSchemas[1].Measures[0].Type = type;
        var catalog = Build(options);
        foreach (string raw in new[] { "0", "100", "-1", "101", "invalid", "NaN", "Infinity", "1e100", "", "12.5" })
        {
            var payload = new GlobalTelemetryPayload { ProjectId = "alpha", Measures = new() { ["amount"] = raw } };
            var logical = catalog.Resolve("alpha")!.Measures.Find("amount")!;
            using var buffer = MeasureColumnBuffer.Create(logical, 1);
            buffer.Set(0, raw);
            ProjectMeasureNormalizer.Normalize(payload, catalog);
            Assert.Equal(buffer.RejectedValueCount == 0 ? raw : "", payload.Measures["amount"]);
        }
    }

    [Fact]
    public void NormalizationLeavesLegacyUnknownUnboundedAndAbsentCellsAlone()
    {
        var options = Options();
        options.ProjectSchemas[1].Measures[0].Minimum = null;
        options.ProjectSchemas[1].Measures[0].Maximum = null;
        var catalog = Build(options);
        var payload = new GlobalTelemetryPayload { ProjectId = "unknown", Measures = new() { ["amount"] = "-1", ["other"] = "x" } };
        ProjectMeasureNormalizer.Normalize(payload, null);
        ProjectMeasureNormalizer.Normalize(payload, catalog);
        payload = new GlobalTelemetryPayload { ProjectId = "beta", Measures = payload.Measures };
        ProjectMeasureNormalizer.Normalize(payload, catalog);
        Assert.Equal("-1", payload.Measures["amount"]);
        Assert.Equal("x", payload.Measures["other"]);
        payload = new GlobalTelemetryPayload { ProjectId = "beta" };
        ProjectMeasureNormalizer.Normalize(payload, catalog);
        options.ProjectSchemas.Clear();
        ProjectMeasureNormalizer.Normalize(payload, Build(options));
        var measure = new MeasureRegistry(new TelemetryEngineOptions
        {
            Measures = [new() { Name = "m", Type = "Int64", Minimum = 0, Aggregations = "sum" }]
        }, new DimensionRegistry(new TelemetryEngineOptions())).Find("m")!;
        Assert.True(MeasureValueRules.Accepts(measure, "1"));
        Assert.False(MeasureValueRules.Accepts(measure, "-1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasureValueRules.Accepts(measure with { Kind = (MeasureValueKind)999 }, "1"));
    }
}
