using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class MeasureColumnBufferTests
{
    private static TelemetryColumnLayout Layout(params MeasureOptions[] measures)
    {
        TelemetryEngineOptions options = new() { Measures = [.. measures] };
        DimensionRegistry registry = new(options);
        return new TelemetryColumnLayout(registry, new MeasureRegistry(options, registry));
    }

    private static MeasureOptions Measure(
        string name, string type, double? minimum = null, double? maximum = null) =>
        new() { Name = name, Type = type, Minimum = minimum, Maximum = maximum };

    private static GlobalTelemetryPayload Event(Dictionary<string, string>? measures) =>
        new()
        {
            ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
            MetadataJson = "{}", Measures = measures,
        };

    private static (object Segment, int Rejected) Fill(
        TelemetryColumnLayout layout, params GlobalTelemetryPayload[] batch)
    {
        using TelemetryColumnBuffers buffers = new(batch.Length, layout);
        buffers.Fill(batch);

        MeasureColumnBuffer buffer = buffers.MeasureBuffers[0];
        return (buffer.BuildSegment(batch.Length), buffer.RejectedValueCount);
    }

    [Fact]
    public void ValidValue_LandsTyped()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "11200" }));

        rejected.Should().Be(0);
        segment.Should().BeOfType<ArraySegment<uint?>>()
            .Which.Should().Equal((uint?)11200);
    }

    [Fact]
    public void AbsentMeasure_IsNull_NotZero()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(null));

        rejected.Should().Be(0, "an event that does not carry a measure has not rejected anything");
        segment.Should().BeOfType<ArraySegment<uint?>>()
            .Which.Should().Equal(
                new uint?[] { null },
                "avg over a measure defaulted to 0 is wrong for every event type that does not carry it");
    }

    [Fact]
    public void EmptyString_IsNull_NotZero()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "" }));

        rejected.Should().Be(0);
        segment.Should().BeOfType<ArraySegment<uint?>>().Which.Should().Equal((uint?)null);
    }

    [Fact]
    public void UnparseableValue_IsCountedAndWrittenNull()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "not-a-number" }));

        rejected.Should().Be(1, "a value that arrived and did not survive is a data loss event");
        segment.Should().BeOfType<ArraySegment<uint?>>().Which.Should().Equal((uint?)null);
    }

    [Fact]
    public void FractionalValueForAnIntegerMeasure_IsRejected_NotTruncated()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("qty", "UInt32")),
            Event(new Dictionary<string, string> { ["qty"] = "2.7" }));

        rejected.Should().Be(1);
        segment.Should().BeOfType<ArraySegment<uint?>>()
            .Which.Should().Equal(
                new uint?[] { null },
                "silently truncating 2.7 to 2 writes a number nobody sent and nothing reports it");
    }

    [Fact]
    public void NegativeValueForAnUnsignedMeasure_IsRejected()
    {
        (_, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "-1" }));

        rejected.Should().Be(1);
    }

    [Fact]
    public void ValueAboveDeclaredMaximum_IsRejected()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32", minimum: 0, maximum: 1_800_000)),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "5400000" }));

        rejected.Should().Be(1,
            "a ninety minute dwell is a forgotten tab, and the cap belongs in the declaration "
            + "rather than in engine code that would have to know what a dwell is");
        segment.Should().BeOfType<ArraySegment<uint?>>().Which.Should().Equal((uint?)null);
    }

    [Fact]
    public void ValueAtDeclaredBounds_IsAccepted()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32", minimum: 0, maximum: 1_800_000)),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "1800000" }));

        rejected.Should().Be(0, "the bounds are inclusive");
        segment.Should().BeOfType<ArraySegment<uint?>>().Which.Should().Equal((uint?)1_800_000);
    }

    [Fact]
    public void ValueBelowDeclaredMinimum_IsRejected()
    {
        (_, int rejected) = Fill(
            Layout(Measure("score", "Int32", minimum: 0)),
            Event(new Dictionary<string, string> { ["score"] = "-5" }));

        rejected.Should().Be(1);
    }

    [Fact]
    public void DecimalMeasure_KeepsItsScale()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("price", "Decimal")),
            Event(new Dictionary<string, string> { ["price"] = "185.50" }));

        rejected.Should().Be(0);
        segment.Should().BeOfType<ArraySegment<decimal?>>().Which.Should().Equal((decimal?)185.50m);
    }

    [Fact]
    public void FloatMeasure_ParsesWithInvariantCulture()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("cls", "Float64")),
            Event(new Dictionary<string, string> { ["cls"] = "0.02" }));

        rejected.Should().Be(0);
        segment.Should().BeOfType<ArraySegment<double?>>().Which.Should().Equal((double?)0.02d);
    }

    [Fact]
    public void CommaDecimalSeparator_IsRejected_NotReinterpreted()
    {
        (_, int rejected) = Fill(
            Layout(Measure("cls", "Float64")),
            Event(new Dictionary<string, string> { ["cls"] = "0,02" }));

        rejected.Should().Be(1,
            "the wire format is invariant, and reading 0,02 as 2 would be a hundredfold error "
            + "that nothing downstream could detect");
    }

    [Fact]
    public void OneRejectedValue_DoesNotCostTheOtherEventsInTheBatch()
    {
        (object segment, int rejected) = Fill(
            Layout(Measure("dwell_ms", "UInt32")),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "100" }),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "broken" }),
            Event(new Dictionary<string, string> { ["dwell_ms"] = "300" }));

        rejected.Should().Be(1);
        segment.Should().BeOfType<ArraySegment<uint?>>()
            .Which.Should().Equal((uint?)100, null, (uint?)300);
    }

    [Fact]
    public void MeasureColumns_FollowDimensionColumnsInTheInsertList()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "article_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32" }],
        };

        DimensionRegistry registry = new(options);
        TelemetryColumnLayout layout = new(registry, new MeasureRegistry(options, registry));

        layout.ColumnNames.Should().EndWith(["article_id", "dwell_ms"],
            "the writer pairs the name list against the value arrays positionally, so the order "
            + "has to be stable and it has to be one list");
    }

    [Fact]
    public void BuildColumns_HasOneEntryPerColumnName()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "article_id", Type = "String" }],
            Measures =
            [
                new MeasureOptions { Name = "dwell_ms", Type = "UInt32" },
                new MeasureOptions { Name = "price", Type = "Decimal" },
            ],
        };

        DimensionRegistry registry = new(options);
        TelemetryColumnLayout layout = new(registry, new MeasureRegistry(options, registry));

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill([Event(null)]);

        buffers.BuildColumns().Keys.Should().BeEquivalentTo(layout.ColumnNames,
            "a name in the INSERT with no value array is a rejected batch, and so is the reverse");
    }

    [Fact]
    public void RetiredMeasure_IsNotWritten()
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Retired = true }],
        };

        DimensionRegistry registry = new(options);
        TelemetryColumnLayout layout = new(registry, new MeasureRegistry(options, registry));

        layout.ColumnNames.Should().NotContain("dwell_ms");
        layout.Measures.Should().BeEmpty();
    }
}
