using Microsoft.AspNetCore.Http;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobConfigurationTests
{
    [Fact]
    public void VersionOneCanonicalEmptySchemaHasAStablePersistedContract()
    {
        Assert.Equal("D5C020A0960EFB23182BF922AF56E348C061E79E38CDFA7F6DC331790AFCF0D4",
            ImportJobTestSupport.Contracts().Find("p"));
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 4097, 1)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 1, -1)]
    public void StartupRejectsUnsupportedLimits(bool ndjson, int jobs, int bytes)
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = ndjson, ImportJobMaxJobs = jobs, MaxRequestBodyBytes = bytes };
        Assert.Equal("Import jobs require NDJSON import, 1–4096 jobs and a positive body limit.",
            Assert.Throws<InvalidOperationException>(() => ImportJobLimits.Create(options)).Message);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(4096, 1_048_576, 1_048_576)]
    [InlineData(256, 2_000_000, 1_048_576)]
    public void StartupFreezesSupportedLimits(int jobs, int body, int expected)
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, ImportJobMaxJobs = jobs, MaxRequestBodyBytes = body };
        var limits = ImportJobLimits.Create(options);
        options.ImportJobMaxJobs = 7;
        Assert.Equal(new ImportJobLimits(jobs, expected), limits);
        Assert.False(new TelemetryEngineOptions().EnableImportJobs);
        Assert.Equal(256, new TelemetryEngineOptions().ImportJobMaxJobs);
    }

    [Fact]
    public void RequestsRequireStableIdentitiesAndUnsignedDecodedSourceOffsets()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Import-Source"] = new string('a', 64);
        context.Request.Headers["X-Import-Source-Bytes"] = "9223372036854775807";
        context.Request.QueryString = new("?offset=9223372036854775807");
        var job = Guid.NewGuid(); var chunk = Guid.NewGuid();
        Assert.Equal(new ImportJobRequest(job, chunk, ImportJobTestSupport.Source, long.MaxValue, long.MaxValue),
            ImportJobRequest.Parse(job, chunk, context.Request));
        Assert.Null(ImportJobRequest.Parse(Guid.Empty, chunk, context.Request));
        Assert.Null(ImportJobRequest.Parse(job, Guid.Empty, context.Request));
        foreach (string value in new[] { "", "-1", "+1", " 1", "1 ", "9223372036854775808", "1,2" })
        {
            context.Request.Headers["X-Import-Source-Bytes"] = value;
            Assert.Null(ImportJobRequest.Parse(job, chunk, context.Request));
        }
        context.Request.Headers["X-Import-Source-Bytes"] = "5";
        foreach (string offset in new[] { "", "-1", "+1", " 1", "6", "9223372036854775808", "0&offset=0" })
        {
            context.Request.QueryString = new("?offset=" + offset);
            Assert.Null(ImportJobRequest.Parse(job, chunk, context.Request));
        }
        context.Request.Headers["X-Import-Source-Bytes"] = "0";
        context.Request.QueryString = new("?offset=0");
        Assert.Equal(0, ImportJobRequest.Parse(job, chunk, context.Request)!.Offset);
        context.Request.Headers["X-Import-Source"] = "bad";
        Assert.Null(ImportJobRequest.Parse(job, chunk, context.Request));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789abcdefABCDEF", false)]
    [InlineData("g", false)]
    public void DigestsRequireExactlySixtyFourAsciiHexDigits(string? value, bool expected)
    {
        Assert.Equal(expected, ImportJobRequest.IsDigest(value));
        Assert.False(ImportJobRequest.IsDigest(new string('G', 64)));
        Assert.False(ImportJobRequest.IsDigest(new string('Ａ', 64)));
        Assert.False(ImportJobRequest.IsDigest(new string('a', 65)));
        Assert.True(ImportJobRequest.IsDigest(string.Concat(Enumerable.Repeat("01234567aAbBcCdD", 4))));
    }

    [Fact]
    public void FingerprintsFreezeRelevantMappingsSchemasAndLimitsAndIgnoreBindingOrder()
    {
        var options = ImportJobTestSupport.Options();
        options.ProjectSchemas = [new() { ProjectId = "p", Measures = [new() { Name = "amount", Type = "Int64", ServerOnly = true, UnitDimension = "" }] }];
        options.NdjsonSourceMappings["p"] = SourceRecordMappingTests.Options();
        var first = ImportJobTestSupport.Contracts(options);
        string fingerprint = first.Find("p");
        options.NdjsonSourceMappings["p"].Fields = options.NdjsonSourceMappings["p"].Fields.Reverse().ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(fingerprint, ImportJobTestSupport.Contracts(options).Find("p"));
        Assert.NotEqual(fingerprint, first.Find("P"));
        options.NdjsonSourceMappings["p"].Fields["projectId"].Default = "q";
        Assert.NotEqual(fingerprint, ImportJobTestSupport.Contracts(options).Find("p"));
        Assert.Equal(fingerprint, first.Find("p"));
        Assert.Equal(first.Find("unknown"), first.Find("different"));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("alias")]
    [InlineData("line")]
    [InlineData("records")]
    [InlineData("body")]
    [InlineData("dimension")]
    [InlineData("measure")]
    [InlineData("mapping")]
    public void EachIngestionContractComponentAffectsNewChunks(string change)
    {
        var options = ImportJobTestSupport.Options();
        var original = ImportJobTestSupport.Contracts(options).Find("p");
        switch (change)
        {
            case "server": options.ServerEventTypes = "private"; break;
            case "alias": options.AliasEventType = "alias"; break;
            case "line": options.ImportMaxLineBytes++; break;
            case "records": options.ImportMaxRecords++; break;
            case "body": options.MaxRequestBodyBytes++; break;
            case "dimension": options.Dimensions = [new() { Name = "tag" }]; break;
            case "measure": options.Measures = [new() { Name = "amount", Type = "Float64" }]; break;
            case "mapping": options.NdjsonSourceMappings["p"] = SourceRecordMappingTests.Options(); break;
        }
        Assert.NotEqual(original, ImportJobTestSupport.Contracts(options).Find("p"));
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("server-only")]
    [InlineData("project-schema")]
    public void LogicalIngestionRulesRemainPartOfTheContractWithoutASourceMapping(string change)
    {
        var options = ImportJobTestSupport.Options();
        options.Dimensions = [new() { Name = "unit" }];
        options.Measures = [new() { Name = "amount", Type = "Int64" }];
        options.ProjectSchemas = [new() { ProjectId = "p", Dimensions = [new() { Name = "unit" }], Measures = [new() { Name = "amount", Type = "Int64" }] }];
        string original = ImportJobTestSupport.Contracts(options).Find("p");
        switch (change)
        {
            case "unit": options.ProjectSchemas[0].Measures[0].UnitDimension = "unit"; break;
            case "server-only": options.ProjectSchemas[0].Measures[0].ServerOnly = true; break;
            case "project-schema": options.ProjectSchemas[0].Dimensions = [new() { Name = "tag" }]; break;
        }
        var updated = ImportJobTestSupport.Contracts(options);
        Assert.NotEqual(original, updated.Find("p"));
        Assert.NotEqual(updated.Find("p"), updated.Find("other"));
    }
}
