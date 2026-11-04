using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobStateTests
{
    [Fact]
    public void ConfirmedAndProcessingStatesRoundTripWithSourceGeneratedJson()
    {
        var pending = ImportJobTestSupport.Pending();
        var ready = ImportJobTestSupport.Ready();
        var complete = ready with { SourceBytes = 3, Status = "complete" };
        var rejected = ready with { Status = "rejected", Receipt = new(1, 3, "invalid_json") };
        foreach (var state in new[] { pending, ready, complete, rejected })
        {
            state.Validate();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, TelemetryAotContext.Default.ImportJobState);
            var recovered = JsonSerializer.Deserialize(json, TelemetryAotContext.Default.ImportJobState)!;
            Assert.Equal(state, recovered);
            Assert.Equal(new ImportJobResponse(state.Id, state.SourceId, state.SourceBytes, state.Status,
                state.NextByteOffset, state.AcceptedRecords, state.ChunkId, state.Receipt), recovered.Response());
        }
    }

    public static IEnumerable<object[]> InvalidStates()
    {
        var state = ImportJobTestSupport.Pending();
        foreach (var value in new[]
        {
            state with { Version = 2 }, state with { Id = Guid.Empty }, state with { ChunkId = Guid.Empty },
            state with { ProjectId = null! }, state with { ProjectId = " " }, state with { ProjectId = new string('p', 257) },
            state with { SourceId = null! }, state with { SourceId = "bad" }, state with { Contract = "bad" },
            state with { ChunkHash = "bad" }
        }) yield return [value, "Import job identity or contract is invalid."];
        foreach (var value in new[]
        {
            state with { SourceBytes = -1 }, state with { NextByteOffset = -1 }, state with { NextByteOffset = 7 },
            state with { AcceptedRecords = -1 }, state with { AcceptedRecords = 1 }, state with { ChunkOffset = -1 },
            state with { ChunkOffset = 1 }, state with { ChunkBytes = -1 }, state with { ChunkBytes = 1_048_577, SourceBytes = 2_000_000 },
            state with { ChunkBytes = 7 }
        }) yield return [value, "Import job counters exceed their source boundaries."];
        yield return [state with { Receipt = new(0, 0, null) }, "An unconfirmed import chunk cannot contain a receipt."];
        yield return [state with { NextByteOffset = 1 }, "An unconfirmed import chunk cannot contain a receipt."];
        state = ImportJobTestSupport.Ready();
        foreach (var value in new[]
        {
            state with { Receipt = null }, state with { Receipt = new(-1, 3, null) },
            state with { Receipt = new(10_001, 3, null) }, state with { Receipt = new(1, -1, null) },
            state with { Receipt = new(1, 4, null) }, state with { Receipt = new(4, 3, null), AcceptedRecords = 4, NextByteOffset = 4 },
            state with { AcceptedRecords = 0 }, state with { NextByteOffset = 4 },
            state with { Status = "rejected", Receipt = new(1, 3, "") },
            state with { Status = "rejected", Receipt = new(1, 3, new string('x', 129)) },
            state with { Receipt = new(1, 2, null), NextByteOffset = 2 },
            state with { Status = "unknown" }, state with { Status = "complete" },
            state with { Status = "rejected" }, state with { Receipt = new(1, 3, "bad") }
        }) yield return [value, "Import job receipt does not match its committed progress."];
    }

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void CorruptStatesCannotBecomeCheckpoints(ImportJobState state, string diagnostic)
    {
        var error = Assert.Throws<InvalidDataException>(state.Validate);
        Assert.Equal(diagnostic, error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void MaximumValidStateBoundariesAndAnEmptyFileAreRepresentable()
    {
        var state = ImportJobTestSupport.Pending() with
        {
            ProjectId = new string('p', 256), SourceBytes = long.MaxValue, NextByteOffset = long.MaxValue - 1_048_576,
            ChunkOffset = long.MaxValue - 1_048_576, ChunkBytes = 1_048_576, AcceptedRecords = long.MaxValue - 1_048_576
        };
        state.Validate();
        state = state with
        {
            Status = "complete", NextByteOffset = long.MaxValue,
            Receipt = new(10_000, 1_048_576, null)
        };
        state.Validate();
        (state with { Status = "rejected", Receipt = new(10_000, 1_048_576, new string('x', 128)) }).Validate();
        (ImportJobTestSupport.Ready() with { Status = "rejected", Receipt = new(1, 3, "x") }).Validate();
        (ImportJobTestSupport.Pending() with
        {
            SourceBytes = 0, ChunkBytes = 0, Status = "complete", Receipt = new(0, 0, null)
        }).Validate();
    }

    [Fact]
    public void ErrorAndResultModelsHaveSourceGeneratedTransportContracts()
    {
        Assert.Equal("{\"error\":\"job_busy\"}", JsonSerializer.Serialize(new ImportJobError("job_busy"), TelemetryAotContext.Default.ImportJobError));
        Assert.Equal(new ImportJobResult(409, null, "offset_conflict"), ImportJobResult.Reject(409, "offset_conflict"));
        var response = ImportJobTestSupport.Ready().Response();
        string json = JsonSerializer.Serialize(response, TelemetryAotContext.Default.ImportJobResponse);
        Assert.Equal(response, JsonSerializer.Deserialize(json, TelemetryAotContext.Default.ImportJobResponse));
        Assert.DoesNotContain("contract", json);
        Assert.DoesNotContain("projectId", json);
        Assert.DoesNotContain("chunkHash", json);
    }
}
