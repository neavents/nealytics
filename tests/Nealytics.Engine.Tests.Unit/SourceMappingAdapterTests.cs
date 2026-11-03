using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SourceMappingAdapterTests
{
    [Fact]
    public void KafkaUsesItsFrozenMappingBeforePolicyAndSanitization()
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SourceMapping = SourceRecordMappingTests.Options();
        options.Kafka.SourceMapping.Fields["dimensions.tag"] = new() { Path = ["value"] };
        options.Kafka.SourceMapping.Fields["dimensions.other"] = new() { Default = "hidden" };
        options.Kafka.SourceMapping.Fields["measures.amount"] = new() { Path = ["value"] };
        var settings = KafkaTestSupport.Settings(options);
        options.Kafka.SourceMapping.Fields.Clear();
        var parser = new KafkaRecordParser(settings, options, NullLoggerFactory.Instance);
        var payload = parser.Parse(Encoding.UTF8.GetBytes(SourceRecordMappingTests.Event(",\"value\":7")), DateTime.UtcNow);
        Assert.Equal("p", payload.ProjectId);
        Assert.Equal("t", payload.TenantId);
        Assert.Equal("action", payload.EventType);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), payload.EventId);
        Assert.Equal("7", payload.Measures!["amount"]);
        Assert.Equal(new KeyValuePair<string, string>("tag", "7"), Assert.Single(payload.Dimensions!));
        Assert.Throws<InvalidDataException>(() => parser.Parse(Encoding.UTF8.GetBytes("{}"), DateTime.UtcNow));
        Assert.Equal("Kafka record is not a valid telemetry JSON object.",
            Assert.Throws<InvalidDataException>(() => parser.Parse(Encoding.UTF8.GetBytes("{secret"), DateTime.UtcNow)).Message);
    }

    [Theory]
    [InlineData("projectId", "other", "project_not_permitted_for_key")]
    [InlineData("tenantId", "", "missing_tenant_id")]
    [InlineData("eventType", "forbidden", "event_type_not_permitted_for_key")]
    public void KafkaMappingCannotBypassNormalIngestionPolicy(string target, string value, string tag)
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SourceMapping = SourceRecordMappingTests.Options();
        options.Kafka.SourceMapping.Fields[target] = new() { Default = value };
        var parser = new KafkaRecordParser(KafkaTestSupport.Settings(options), options, NullLoggerFactory.Instance);
        Assert.Equal("Kafka record rejected by ingestion policy: " + tag,
            Assert.Throws<InvalidDataException>(() => parser.Parse(Encoding.UTF8.GetBytes(SourceRecordMappingTests.Event()), DateTime.UtcNow)).Message);
    }

    [Fact]
    public void KafkaRejectsInvalidMappingsWhenSettingsAreCreated()
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SourceMapping = new();
        Assert.Throws<InvalidOperationException>(() => KafkaTestSupport.Settings(options));
    }

    [Fact]
    public void InvalidUtf8InsideAMappedStringCannotBecomeAnAcceptedEnvelope()
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SourceMapping = SourceRecordMappingTests.Options();
        var parser = new KafkaRecordParser(KafkaTestSupport.Settings(options), options, NullLoggerFactory.Instance);
        byte[] value = Encoding.UTF8.GetBytes(SourceRecordMappingTests.Event());
        int position = Encoding.UTF8.GetString(value).IndexOf("\"tenant\":\"t\"", StringComparison.Ordinal) + 10;
        value[position] = 0xff;
        var error = Assert.Throws<InvalidDataException>(() => parser.Parse(value, DateTime.UtcNow));
        Assert.Equal("Source mapping contains invalid Unicode text.", error.Message);
        Assert.Null(error.InnerException);
    }
}
