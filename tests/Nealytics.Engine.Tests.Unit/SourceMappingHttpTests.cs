using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class SourceMappingHttpTests
{
    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task MappingUsesThePinnedProjectAndPreservesOriginalReceiptsAndSanitizedWal(string path)
    {
        await using var factory = new NdjsonImportHttpTests.Factory(path, kestrel: true, configure: Configure);
        var client = factory.Client;
        var live = factory.Services.GetRequiredService<IOptions<TelemetryEngineOptions>>().Value;
        live.NdjsonSourceMappings["p"].Fields.Clear();
        string prefix = SourceRecordMappingTests.Event(",\"value\":7,\"projectId\":\"foreign\",\"unicode\":\"日本語é\"") + "\r\n";
        string body = prefix + "{}\n" + SourceRecordMappingTests.Event(",\"value\":100");
        using var response = await client.PostAsync(NdjsonImportHttpTests.Route + "?k=server-key",
            new StringContent(body, Encoding.UTF8, "application/x-ndjson"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_mapping", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(2, receipt.RootElement.GetProperty("nextLine").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.False(receipt.RootElement.GetProperty("complete").GetBoolean());
        var stored = JsonSerializer.Deserialize(Assert.Single(await File.ReadAllLinesAsync(factory.WalPath)), TelemetryAotContext.Default.GlobalTelemetryPayload)!;
        Assert.Equal("p", stored.ProjectId);
        Assert.Equal("t", stored.TenantId);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), stored.EventId);
        Assert.Equal(new KeyValuePair<string, string>("tag", "7"), Assert.Single(stored.Dimensions!));
        Assert.Equal(new KeyValuePair<string, string>("amount", "7"), Assert.Single(stored.Measures!));
    }

    [Fact]
    public async Task ASecondProjectSelectsItsOwnMappingAndAnUnconfiguredProjectKeepsCanonicalInput()
    {
        await using var factory = new NdjsonImportHttpTests.Factory(configure: options =>
        {
            Configure(options);
            var second = SourceRecordMappingTests.Options();
            second.Fields["projectId"].Default = "q";
            second.Fields["tenantId"] = new() { Default = "second" };
            options.NdjsonSourceMappings["q"] = second;
            options.IngestionKeys.Add(new() { Key = "q-key", Scope = "server", ProjectId = "q" });
            options.IngestionKeys.Add(new() { Key = "r-key", Scope = "server", ProjectId = "r" });
        });
        var client = factory.Client;
        using var second = await Send(client, "q-key", SourceRecordMappingTests.Event());
        using var canonical = await Send(client, "r-key", NdjsonRecordReaderTests.Event("r"));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, canonical.StatusCode);
        var records = (await File.ReadAllLinesAsync(factory.WalPath)).Select(line => JsonSerializer.Deserialize(line, TelemetryAotContext.Default.GlobalTelemetryPayload)!).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal("q", records[0].ProjectId);
        Assert.Equal("second", records[0].TenantId);
        Assert.Equal("r", records[1].ProjectId);
        Assert.Equal("t", records[1].TenantId);
    }

    [Fact]
    public async Task MappingOutputCannotEscapePinnedProjectAuthorization()
    {
        await using var factory = new NdjsonImportHttpTests.Factory(configure: options =>
        {
            Configure(options);
            options.NdjsonSourceMappings["p"].Fields["projectId"] = new() { Path = ["project"] };
        });
        using var response = await Send(factory.Client, "server-key", SourceRecordMappingTests.Event(",\"project\":\"foreign\""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("project_not_permitted_for_key", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(0, factory.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task FragmentedMappedLinesRetainFramingAndStopOnTheirFirstMappingFailure()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        var mapping = Nealytics.Engine.Features.SourceMapping.SourceRecordMapping.Compile(SourceRecordMappingTests.Options());
        string body = SourceRecordMappingTests.Event() + "\r\n[]\n" + SourceRecordMappingTests.Event();
        var reader = new NdjsonRecordReaderTests.ProbeReader(new NdjsonRecordReaderTests.SegmentedReader(Encoding.UTF8.GetBytes(body)));
        List<Nealytics.Engine.Features.ImportNdjson.NdjsonRecord> records = [];
        await foreach (var record in Nealytics.Engine.Features.ImportNdjson.NdjsonRecordReader.ReadAsync(reader, 1024, deadline.Token, mapping: mapping)) records.Add(record);
        Assert.Equal(2, records.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(SourceRecordMappingTests.Event() + "\r\n"), records[0].WireBytes);
        Assert.Equal("p", records[0].Payload!.ProjectId);
        Assert.Equal("invalid_mapping", records[1].Error);
        Assert.Null(records[1].Payload);
        Assert.Equal(0, records[1].WireBytes);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public async Task InvalidUnicodeInMappedTextReturnsARejectionReceipt(string text)
    {
        await using var factory = new NdjsonImportHttpTests.Factory(configure: Configure);
        string first = SourceRecordMappingTests.Event(",\"value\":7") + "\n";
        string invalid = SourceRecordMappingTests.Event().Replace("\"tenant\":\"t\"", "\"tenant\":\"" + text + "\"");
        using var response = await Send(factory.Client, "server-key", first + invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_mapping", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(first), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.Equal(1, factory.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnabledImportValidatesMappingsAtStartupWhileDisabledImportIgnoresThem(bool enabled)
    {
        await using var factory = new NdjsonImportHttpTests.Factory(enabled: enabled,
            configure: options => options.NdjsonSourceMappings["p"] = new()
            {
                Fields = new() { ["userId"] = new() { Default = "invalid-partial-declaration" } }
            });
        if (enabled)
        {
            Assert.Throws<InvalidOperationException>(() => factory.Client);
        }
        else
        {
            using var response = await Send(factory.Client, "server-key", "");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string key, string body) =>
        client.PostAsync(NdjsonImportHttpTests.Route + "?k=" + key,
            new StringContent(body, Encoding.UTF8, "application/x-ndjson"));

    private static void Configure(TelemetryEngineOptions options)
    {
        var mapping = SourceRecordMappingTests.Options();
        mapping.Fields["dimensions.tag"] = new() { Path = ["value"] };
        mapping.Fields["dimensions.unknown"] = new() { Default = "hidden" };
        mapping.Fields["measures.amount"] = new() { Path = ["value"] };
        options.Dimensions = [new() { Name = "tag" }];
        options.Measures = [new() { Name = "amount", Type = "Int64" }];
        options.NdjsonSourceMappings["p"] = mapping;
    }
}
