using System.Globalization;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.SourceMapping;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SourceRecordMappingTests
{
    internal static SourceMappingOptions Options() => new()
    {
        Fields = new(StringComparer.Ordinal)
        {
            ["eventId"] = new() { Path = ["source", "id"] },
            ["timestamp"] = new() { Path = ["source", "time"] },
            ["projectId"] = new() { Default = "p" },
            ["tenantId"] = new() { Path = ["source", "tenant"] },
            ["eventType"] = new() { Default = "action" }
        }
    };

    internal static string Event(string extra = "") =>
        "{\"source\":{\"id\":\"" + KafkaTestSupport.Identity
        + "\",\"time\":\"2026-01-01T00:00:00Z\",\"tenant\":\"t\"}" + extra + "}";

    [Fact]
    public void NestedMappingMaterializesOnlyDeclaredFieldsAndPreservesAllTextTargets()
    {
        var options = Options();
        string[] optional = ["sessionId", "userId", "objectId", "trafficClass", "pagePath", "referrer", "deviceClass", "os", "browser", "country", "metadataJson"];
        foreach (string target in optional) options.Fields[target] = new() { Path = ["data", target] };
        options.Fields["dimensions.tag"] = new() { Path = ["data", "tag"] };
        options.Fields["measures.amount"] = new() { Path = ["data", "amount"] };
        var data = optional.ToDictionary(target => target, target => "value-" + target);
        data["tag"] = "日本語 é";
        data["amount"] = "7.50";
        using var document = JsonDocument.Parse(Event(",\"data\":" + JsonSerializer.Serialize(data)
            + ",\"projectId\":\"foreign\",\"_walRecordId\":\"22222222-2222-2222-2222-222222222222\",\"seq\":9"));
        var payload = SourceRecordMapping.Compile(options).Read(document.RootElement, 4096);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), payload.EventId);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), payload.Timestamp);
        Assert.Equal(DateTimeKind.Utc, payload.Timestamp.Kind);
        Assert.Equal("p", payload.ProjectId);
        Assert.Equal("t", payload.TenantId);
        Assert.Equal("action", payload.EventType);
        Assert.Equal("value-sessionId", payload.SessionId);
        Assert.Equal("value-userId", payload.UserId);
        Assert.Equal("value-objectId", payload.ObjectId);
        Assert.Equal("value-trafficClass", payload.TrafficClass);
        Assert.Equal("value-pagePath", payload.PagePath);
        Assert.Equal("value-referrer", payload.Referrer);
        Assert.Equal("value-deviceClass", payload.DeviceClass);
        Assert.Equal("value-os", payload.Os);
        Assert.Equal("value-browser", payload.Browser);
        Assert.Equal("value-country", payload.Country);
        Assert.Equal("value-metadataJson", payload.MetadataJson);
        Assert.Equal(new KeyValuePair<string, string>("tag", "日本語 é"), Assert.Single(payload.Dimensions!));
        Assert.Equal(new KeyValuePair<string, string>("amount", "7.50"), Assert.Single(payload.Measures!));
        Assert.Equal(Guid.Empty, payload.WalRecordId);
        Assert.Equal(0u, payload.Seq);
    }

    [Fact]
    public void UnmappedOptionalFieldsRetainCanonicalDefaults()
    {
        using var document = JsonDocument.Parse(Event());
        var payload = SourceRecordMapping.Compile(Options()).Read(document.RootElement, 4096);
        Assert.Equal("", payload.SessionId);
        Assert.Null(payload.UserId);
        Assert.Null(payload.ObjectId);
        Assert.Equal("", payload.TrafficClass);
        Assert.Equal("", payload.PagePath);
        Assert.Equal("", payload.Referrer);
        Assert.Equal("", payload.DeviceClass);
        Assert.Equal("", payload.Os);
        Assert.Equal("", payload.Browser);
        Assert.Equal("", payload.Country);
        Assert.Equal("{}", payload.MetadataJson);
        Assert.Empty(payload.Dimensions!);
        Assert.Empty(payload.Measures!);
    }

    [Theory]
    [InlineData("projectId")]
    [InlineData("tenantId")]
    [InlineData("eventType")]
    public void MissingMappedEnvelopeTextStaysEmptyForNormalIngestionPolicyValidation(string target)
    {
        var options = Options();
        options.Fields[target] = new() { Path = ["absent"] };
        using var document = JsonDocument.Parse(Event());
        var payload = SourceRecordMapping.Compile(options).Read(document.RootElement, 4096);
        Assert.Equal(target == "projectId" ? "" : "p", payload.ProjectId);
        Assert.Equal(target == "tenantId" ? "" : "t", payload.TenantId);
        Assert.Equal(target == "eventType" ? "" : "action", payload.EventType);
    }

    [Theory]
    [InlineData("1.25", "1.25")]
    [InlineData("-42", "-42")]
    [InlineData("1e2", "1e2")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("\"é\"", "é")]
    public void CustomScalarsUseTheirJsonRepresentation(string input, string expected)
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["value"] };
        options.Fields["measures.amount"] = new() { Path = ["value"] };
        using var document = JsonDocument.Parse(Event(",\"value\":" + input));
        var payload = SourceRecordMapping.Compile(options).Read(document.RootElement, 4096);
        Assert.Equal(expected, payload.Dimensions!["tag"]);
        Assert.Equal(expected, payload.Measures!["amount"]);
    }

    [Theory]
    [InlineData("", "fallback")]
    [InlineData(",\"data\":null", "fallback")]
    [InlineData(",\"data\":{}", "fallback")]
    [InlineData(",\"data\":{\"tag\":null}", "fallback")]
    [InlineData(",\"data\":{\"tag\":\"\"}", "")]
    [InlineData(",\"data\":{\"tag\":\"real\"}", "real")]
    public void DefaultsApplyOnlyToMissingOrNullPaths(string extra, string expected)
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["data", "tag"], Default = "fallback" };
        options.Fields["userId"] = new() { Path = ["absent"] };
        using var document = JsonDocument.Parse(Event(extra));
        var payload = SourceRecordMapping.Compile(options).Read(document.RootElement, 4096);
        Assert.Equal(expected, payload.Dimensions!["tag"]);
        Assert.Null(payload.UserId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("\"secret\"")]
    public void DefaultsDoNotHideInvalidIntermediateShapes(string input)
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["data", "tag"], Default = "fallback" };
        using var document = JsonDocument.Parse(Event(",\"data\":" + input));
        var error = Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(options).Read(document.RootElement, 4096));
        Assert.Equal("Source mapping path crosses a non-object value.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public void CustomFieldsRejectContainersInsteadOfStringifyingThem(string input)
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["value"] };
        using var document = JsonDocument.Parse(Event(",\"value\":" + input));
        var error = Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(options).Read(document.RootElement, 4096));
        Assert.Equal("Source mapping requires a compatible scalar value.", error.Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void EnvelopeTextDoesNotCoerceNonStringValues(string input)
    {
        var options = Options();
        options.Fields["projectId"] = new() { Path = ["value"], Default = "p" };
        using var document = JsonDocument.Parse(Event(",\"value\":" + input));
        Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(options).Read(document.RootElement, 4096));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    public void SourceMustBeAnObject(string input)
    {
        using var document = JsonDocument.Parse(input);
        var error = Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(Options()).Read(document.RootElement, 4096));
        Assert.Equal("Source mapping requires a JSON object.", error.Message);
    }

    [Theory]
    [InlineData("id", "\"bad\"")]
    [InlineData("id", "null")]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("time", "null")]
    [InlineData("time", "\"bad\"")]
    [InlineData("time", "\"2026-01-01T00:00:00\"")]
    [InlineData("time", "\"2026-01-01T00:00:00+00:00\"")]
    [InlineData("time", "\"2026-01-01T00:00:00z\"")]
    [InlineData("time", "\"1899-12-31T23:59:59Z\"")]
    [InlineData("time", "\"2300-01-01T00:00:00Z\"")]
    public void IdentityAndTimeCannotBeInventedOrNormalizedFromInvalidSources(string field, string value)
    {
        var options = Options();
        options.Fields[field == "id" ? "eventId" : "timestamp"] = new() { Path = ["invalid"] };
        using var document = JsonDocument.Parse(Event(",\"invalid\":" + value));
        var error = Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(options).Read(document.RootElement, 4096));
        Assert.Equal("Source mapping requires a nonzero eventId and UTC-Z timestamp in the storage calendar.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z")]
    [InlineData("2299-12-31T23:59:59.9999999Z")]
    public void CalendarEdgesAndFractionalPrecisionRemainUtc(string timestamp)
    {
        using var document = JsonDocument.Parse(Event().Replace("2026-01-01T00:00:00Z", timestamp));
        var payload = SourceRecordMapping.Compile(Options()).Read(document.RootElement, 4096);
        Assert.Equal(DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), payload.Timestamp);
        Assert.Equal(DateTimeKind.Utc, payload.Timestamp.Kind);
    }

    [Fact]
    public void LiteralPathSegmentsSupportDotsAndEscapesWithoutSelectorInterpretation()
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["a.b", "é/\"~"] };
        using var document = JsonDocument.Parse(Event(",\"a.b\":{\"é/\\\"~\":\"ok\"}"));
        Assert.Equal("ok", SourceRecordMapping.Compile(options).Read(document.RootElement, 4096).Dimensions!["tag"]);
    }

    [Fact]
    public void TextBudgetCountsRepeatedValuesAndUtf8DefaultBytesAtTheExactBoundary()
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Default = "日本語é" };
        using var document = JsonDocument.Parse(Event());
        int bytes = Encoding.UTF8.GetByteCount(KafkaTestSupport.Identity + "2026-01-01T00:00:00Z" + "ptaction日本語é");
        var mapping = SourceRecordMapping.Compile(options);
        Assert.Equal("日本語é", mapping.Read(document.RootElement, bytes).Dimensions!["tag"]);
        Assert.Equal("Source mapping exceeds its text byte budget.",
            Assert.Throws<InvalidDataException>(() => mapping.Read(document.RootElement, bytes - 1)).Message);
        options.Fields["dimensions.again"] = new() { Default = "日本語é" };
        Assert.Throws<InvalidDataException>(() => SourceRecordMapping.Compile(options).Read(document.RootElement, bytes));
    }

    [Fact]
    public void CompilationCopiesPathsDefaultsAndTargetsAwayFromMutableOptions()
    {
        var options = Options();
        var mapping = SourceRecordMapping.Compile(options);
        options.Fields["eventId"].Path[1] = "wrong";
        options.Fields["projectId"].Default = "foreign";
        options.Fields.Clear();
        using var document = JsonDocument.Parse(Event());
        var payload = mapping.Read(document.RootElement, 4096);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), payload.EventId);
        Assert.Equal("p", payload.ProjectId);
    }
}
