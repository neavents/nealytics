using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

internal static class ImportJobTestSupport
{
    internal const string Source = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static TelemetryEngineOptions Options() => new()
    {
        EnableImportJobs = true, EnableNdjsonImport = true, MaxRequestBodyBytes = 4096,
        ImportMaxLineBytes = 1024, ImportMaxRecords = 2, ImportJobMaxJobs = 2
    };
    internal static ImportJobContracts Contracts(TelemetryEngineOptions? options = null)
    {
        options ??= Options();
        var projects = new ProjectCatalog(options, ProjectContext.Compile("baseline", options));
        return new(options, projects, ImportJobLimits.Create(options));
    }
    internal static ImportJobRequest Request(long total = 6, long offset = 0, Guid? job = null, Guid? chunk = null) =>
        new(job ?? Guid.NewGuid(), chunk ?? Guid.NewGuid(), Source, total, offset);
    internal static ImportJobState Pending(ImportJobRequest? request = null)
    {
        request ??= Request();
        return new(1, request.JobId, "p", request.SourceId, request.SourceBytes, Contracts().Find("p"),
            "processing", request.Offset, 0, request.ChunkId, request.Offset, 3, Hash("{}\n"), null);
    }
    internal static ImportJobState Ready(ImportJobRequest? request = null) => Pending(request) with
    {
        Status = "ready", NextByteOffset = 3, AcceptedRecords = 1, Receipt = new(1, 3, null)
    };
    internal static PipeReader Reader(string body) => PipeReader.Create(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(body)));
    internal static async Task<NdjsonImportResponse> Accept(PipeReader reader)
    {
        var read = await reader.ReadAsync();
        long bytes = read.Buffer.Length;
        reader.AdvanceTo(read.Buffer.End);
        return new(bytes == 0 ? 0 : 1, bytes, null);
    }

    internal sealed class ChunkReader(params ReadResult[] results) : PipeReader
    {
        private readonly Queue<ReadResult> _results = new(results);
        public int Completions { get; private set; }
        public int Advances { get; private set; }
        public Action? OnAdvance { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? CompleteFailure { get; set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            ReadFailure is not null && Advances > 0 ? ValueTask.FromException<ReadResult>(ReadFailure) : new(_results.Dequeue());
        public override void AdvanceTo(SequencePosition consumed) { Advances++; OnAdvance?.Invoke(); }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
        public override void Complete(Exception? exception = null) { Completions++; if (CompleteFailure is not null) throw CompleteFailure; }
        public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
        public override void CancelPendingRead() => throw new NotSupportedException();
    }

    internal sealed class Harness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "nealytics-import-jobs-" + Guid.NewGuid().ToString("N"));
        public TelemetryEngineOptions Settings { get; } = Options();
        public ImportJobStore Store { get; private set; }
        public ImportJobCoordinator Coordinator { get; private set; }
        public Harness(Func<string, ImportJobJournal>? journal = null, ArrayPool<byte>? pool = null)
        {
            Store = new(journal?.Invoke(Directory) ?? new(Directory), Settings.ImportJobMaxJobs);
            Coordinator = new(Store, Contracts(Settings), ImportJobLimits.Create(Settings), pool);
        }
        public void Restart(Action<TelemetryEngineOptions>? configure = null)
        {
            Coordinator.Dispose();
            Store.Dispose();
            configure?.Invoke(Settings);
            Store = new(new ImportJobJournal(Directory), Settings.ImportJobMaxJobs);
            Coordinator = new(Store, Contracts(Settings), ImportJobLimits.Create(Settings));
        }
        public Task<ImportJobResult> Append(ImportJobRequest request, string body = "{}\n",
            Func<PipeReader, Task<NdjsonImportResponse>>? ingest = null, string project = "p", CancellationToken token = default) =>
            Coordinator.AppendAsync(request, project, Reader(body), ingest ?? Accept, token);
        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
