using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class KafkaRecordParserTests
{
    [Fact]
    public void ExplicitIdentityTimeAndTrustedFieldsSurviveSanitization()
    {
        var parser = Parser();
        var payload = parser.Parse(Encoding.UTF8.GetBytes(KafkaTestSupport.Event(
            ",\"_walRecordId\":\"22222222-2222-2222-2222-222222222222\",\"dimensions\":{\"tag\":\"x\",\"other\":\"hidden\"},\"measures\":{\"amount\":\"7\",\"other\":\"9\"}")), DateTime.UtcNow);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), payload.EventId);
        Assert.Equal(Guid.Empty, payload.WalRecordId);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), payload.Timestamp);
        Assert.Equal(DateTimeKind.Utc, payload.Timestamp.Kind);
        Assert.Equal("p", payload.ProjectId);
        Assert.Equal("t", payload.TenantId);
        Assert.Equal("action", payload.EventType);
        Assert.Equal("tag", Assert.Single(payload.Dimensions!).Key);
        Assert.Equal("x", payload.Dimensions!["tag"]);
        Assert.Equal("amount", Assert.Single(payload.Measures!).Key);
        Assert.Equal("7", payload.Measures!["amount"]);
    }

    public static IEnumerable<object?[]> InvalidRecords()
    {
        foreach (string? value in new[] { null, "", "null", "[]", "1", "{}", "{", "{\"eventId\":null}", "{\"eventId\":1}" }) yield return [value];
        foreach (string id in new[] { "not-a-guid", Guid.Empty.ToString() })
            yield return [KafkaTestSupport.Event().Replace(KafkaTestSupport.Identity, id)];
        yield return [KafkaTestSupport.Event().Replace("\"eventId\"", "\"otherId\"")];
        yield return [KafkaTestSupport.Event().Replace("\"timestamp\"", "\"otherTime\"")];
        yield return [KafkaTestSupport.Event().Replace("\"2026-01-01T00:00:00Z\"", "null")];
        yield return [KafkaTestSupport.Event().Replace("\"2026-01-01T00:00:00Z\"", "1")];
        foreach (string time in new[] { "badZ", "2026-01-01T00:00:00", "2026-01-01T00:00:00+00:00", "1899-12-31T23:59:59Z", "2300-01-01T00:00:00Z" })
            yield return [KafkaTestSupport.Event().Replace("2026-01-01T00:00:00Z", time)];
        yield return [KafkaTestSupport.Event().Replace("\"projectId\":\"p\"", "\"projectId\":\"other\"")];
        yield return [KafkaTestSupport.Event().Replace("\"tenantId\":\"t\"", "\"tenantId\":\"\"")];
        yield return [KafkaTestSupport.Event().Replace("\"action\"", "\"forbidden\"")];
        yield return [KafkaTestSupport.Event(",\"dimensions\":1")];
        yield return [KafkaTestSupport.Event() + "{}"];
        yield return [KafkaTestSupport.Event(",\"metadataJson\":\"" + new string('x', 65_536) + "\"")];
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void RejectsInvalidInputBeforeAnyPersistence(string? json)
    {
        var error = Assert.Throws<InvalidDataException>(() => Parser().Parse(json is null ? null : Encoding.UTF8.GetBytes(json), DateTime.UtcNow));
        Assert.DoesNotContain("hidden", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z")]
    [InlineData("2299-12-31T23:59:59.999Z")]
    public void ExactCalendarBoundariesArePreserved(string timestamp)
    {
        var time = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var value = Encoding.UTF8.GetBytes(KafkaTestSupport.Event().Replace("2026-01-01T00:00:00Z", timestamp));
        Assert.Equal(time, Parser().Parse(value, time).Timestamp);
    }

    [Fact]
    public void SizeLimitCountsUtf8BytesAndAcceptsTheExactBoundary()
    {
        var options = KafkaTestSupport.Options();
        byte[] value = Encoding.UTF8.GetBytes(KafkaTestSupport.Event(",\"metadataJson\":\"" + new string('é', 1_024) + "\""));
        options.Kafka!.MaxMessageBytes = value.Length;
        var parser = Parser(options);
        Assert.Equal(new string('é', 1_024), parser.Parse(value, DateTime.UtcNow).MetadataJson);
        Assert.Throws<InvalidDataException>(() => parser.Parse([.. value, (byte)' '], DateTime.UtcNow));
    }

    [Fact]
    public void PerProjectContextCannotLeakUnionFields()
    {
        var options = KafkaTestSupport.Options();
        options.ProjectSchemas = [new() { ProjectId = "p", Dimensions = [new() { Name = "local" }] }, new() { ProjectId = "other", Dimensions = [new() { Name = "foreign" }] }];
        var payload = Parser(options).Parse(Encoding.UTF8.GetBytes(KafkaTestSupport.Event(",\"dimensions\":{\"local\":\"yes\",\"foreign\":\"no\",\"tag\":\"no\"}")), DateTime.UtcNow);
        Assert.Equal("local", Assert.Single(payload.Dimensions!).Key);
    }

    private static KafkaRecordParser Parser(TelemetryEngineOptions? options = null)
    {
        options ??= KafkaTestSupport.Options();
        return new KafkaRecordParser(KafkaTestSupport.Settings(options), options, NullLoggerFactory.Instance);
    }
}
