using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Rollups;
using Nealytics.Engine.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Nealytics.Engine.Tests.Unit;

public sealed class RollupReadinessTests
{
    [Fact]
    public async Task CanceledReconciliationInvalidatesPreviousRoutingBeforeAnyDatabaseWork()
    {
        TelemetryEngineOptions options = new()
        {
            Rollups = [new RollupOptions { Name = "first", Grain = "day" }]
        };
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        RollupRegistry registry = new(options, dimensions, measures);
        registry.MarkRoutable(registry.Declared[0]);
        await using ClickHouseConnectionFactory factory = new(Options.Create(options));
        ClickHouseSchemaMigrator migrator = new(factory, dimensions, measures, registry, Options.Create(options),
            NullLogger<ClickHouseSchemaMigrator>.Instance);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migrator.StartAsync(canceled.Token));
        Assert.Empty(registry.Routable);
        Assert.Equal("completion has not been verified", registry.UnroutableReason(registry.Declared[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StorageIdentityRefusesAMissingOrNonPersistentObject(int missing)
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        ids[missing] = Guid.Empty;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new RollupStorageIdentity(ids[0], ids[1], ids[2], "declared", "stored"));
        Assert.Contains("persistent UUIDs", error.Message);
    }

    [Fact]
    public void StorageIdentityPreservesEveryPartOfTheProof()
    {
        Guid source = Guid.NewGuid();
        Guid table = Guid.NewGuid();
        Guid view = Guid.NewGuid();
        RollupStorageIdentity identity = new(source, table, view, "declared", "stored");
        Assert.Equal(source, identity.SourceId);
        Assert.Equal(table, identity.TableId);
        Assert.Equal(view, identity.ViewId);
        Assert.Equal("declared", identity.DeclaredDefinition);
        Assert.Equal("stored", identity.StoredDefinition);
    }

    [Fact]
    public void DeclarationDoesNotProveThatAnyStoredRollupIsComplete()
    {
        RollupRegistry registry = Registry();
        Assert.Equal(2, registry.Declared.Count);
        Assert.Empty(registry.Routable);
        Assert.All(registry.Declared, rollup => Assert.False(registry.IsRoutable(rollup)));
        Assert.All(registry.Declared, rollup => Assert.Equal("completion has not been verified", registry.UnroutableReason(rollup)));
        Assert.False(registry.IsRoutable(null!));
    }

    [Fact]
    public void VerificationAndInvalidationApplyOnlyToTheirOwnRollup()
    {
        RollupRegistry registry = Registry();
        Rollup first = registry.Declared[0];
        Rollup second = registry.Declared[1];
        registry.MarkRoutable(first);
        Assert.Same(first, Assert.Single(registry.Routable));
        Assert.True(registry.IsRoutable(first));
        Assert.Null(registry.UnroutableReason(first));
        Assert.False(registry.IsRoutable(second));
        registry.MarkUnroutable(first, "incomplete backfill");
        Assert.Empty(registry.Routable);
        Assert.False(registry.IsRoutable(first));
        Assert.Equal("incomplete backfill", registry.UnroutableReason(first));
        registry.MarkRoutable(second);
        Assert.Same(second, Assert.Single(registry.Routable));
    }

    [Theory]
    [InlineData("table", "view", "changed", "view")]
    [InlineData("table", "view", "table", "changed")]
    [InlineData("ab", "c", "a", "bc")]
    [InlineData("", "a", "a", "")]
    public void DefinitionFingerprintsCoverBothDefinitionsAndTheirBoundary(string table, string view, string otherTable, string otherView)
    {
        string fingerprint = ClickHouseRollupReadiness.Fingerprint(table, view);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, ClickHouseRollupReadiness.Fingerprint(table, view));
        Assert.NotEqual(fingerprint, ClickHouseRollupReadiness.Fingerprint(otherTable, otherView));
    }

    [Fact]
    public void ADefinitionFingerprintUsesSha256OverDelimitedUtf8()
    {
        Assert.Equal("7BA6FFC4ADE251241D0C5F18256EA72B37E23213B4F61AE3CF36DB5E5325503E",
            ClickHouseRollupReadiness.Fingerprint("λ", "🙂"));
    }

    private static RollupRegistry Registry()
    {
        TelemetryEngineOptions options = new()
        {
            Rollups = [new RollupOptions { Name = "first", Grain = "day" }, new RollupOptions { Name = "second", Grain = "hour" }]
        };
        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }
}
