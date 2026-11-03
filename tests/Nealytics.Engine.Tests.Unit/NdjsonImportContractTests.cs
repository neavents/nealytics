using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NdjsonImportContractTests
{
    [Fact]
    public void DefaultsAreDisabledAndTheEnabledLimitsAreFrozen()
    {
        var options = new TelemetryEngineOptions();
        Assert.False(options.EnableNdjsonImport);
        Assert.Equal(65_536, options.ImportMaxLineBytes);
        Assert.Equal(1_000, options.ImportMaxRecords);
        Assert.Equal(2, options.ImportMaxConcurrency);
        var limits = NdjsonImportLimits.Create(options);
        options.ImportMaxLineBytes = 1;
        options.ImportMaxRecords = 1;
        options.ImportMaxConcurrency = 1;
        Assert.Equal(new NdjsonImportLimits(65_536, 1_000, 2), limits);
        Assert.Throws<ArgumentNullException>(() => NdjsonImportLimits.Create(null!));
    }

    [Theory]
    [InlineData(0, 1000, 2, 1_048_576, "ImportMaxLineBytes")]
    [InlineData(1_048_577, 1000, 2, 2_000_000, "ImportMaxLineBytes")]
    [InlineData(65, 1000, 2, 64, "ImportMaxLineBytes")]
    [InlineData(1, 0, 2, 1, "ImportMaxRecords")]
    [InlineData(1, 10_001, 2, 1, "ImportMaxRecords")]
    [InlineData(1, 1000, 0, 1, "ImportMaxConcurrency")]
    [InlineData(1, 1000, 65, 1, "ImportMaxConcurrency")]
    public void InvalidConfigurationFailsBeforeImportActivation(int line, int records, int concurrency, int body, string field)
    {
        var options = new TelemetryEngineOptions
        {
            ImportMaxLineBytes = line, ImportMaxRecords = records, ImportMaxConcurrency = concurrency, MaxRequestBodyBytes = body
        };
        Assert.StartsWith(field, Assert.Throws<InvalidOperationException>(() => NdjsonImportLimits.Create(options)).Message);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1_048_576, 10_000, 64)]
    public void BothEndsOfEachSupportedLimitAreAdmitted(int line, int records, int concurrency)
    {
        var options = new TelemetryEngineOptions { ImportMaxLineBytes = line, ImportMaxRecords = records, ImportMaxConcurrency = concurrency };
        Assert.Equal(new NdjsonImportLimits(line, records, concurrency), NdjsonImportLimits.Create(options));
    }

    [Theory]
    [InlineData(null, 202, true)]
    [InlineData("line_too_large", 413, false)]
    [InlineData("body_too_large", 413, false)]
    [InlineData("record_limit", 400, false)]
    public void ReceiptsDescribeTheNextUnacceptedRecordAndContainNoStorageDetails(string? error, int status, bool complete)
    {
        var response = new NdjsonImportResponse(2, 17, error);
        Assert.Equal(2, response.AcceptedRecords);
        Assert.Equal(3, response.NextLine);
        Assert.Equal(17, response.NextByteOffset);
        Assert.Equal(error, response.Error);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(complete, response.Complete);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("application/x-ndjson; charset=\"\"")]
    [InlineData("text/plain")]
    [InlineData("")]
    public void InvalidMediaTypesNeverReachTheImportReader(string value) => Assert.False(ImportNdjsonEndpoint.AcceptsContentType(value));
}
