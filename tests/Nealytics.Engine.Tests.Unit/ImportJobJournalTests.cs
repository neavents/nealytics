using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-import-journal-" + Guid.NewGuid().ToString("N"));
    private string JobDirectory => Path.Combine(_directory, "import-jobs");
    private string Manifest(ImportJobState state) => Path.Combine(JobDirectory, state.Id.ToString("N") + ".json");

    [Fact]
    public void ReplacementCommitsOnlyAfterFileFlushPublishAndDirectorySynchronization()
    {
        List<string> calls = [];
        using var journal = new ImportJobJournal(_directory,
            (path, mode) => new TrackingFile(path, mode, calls),
            path => { WalDirectoryDurability.Synchronize(path); calls.Add("sync"); },
            (source, destination) => { calls.Add("publish"); File.Move(source, destination, true); });
        calls.Clear();
        var state = ImportJobTestSupport.Pending();
        journal.Save(state);
        Assert.Equal(new[] { "write", "write", "flush:True", "dispose", "publish", "sync" }, calls);
        Assert.Equal(state, Assert.Single(journal.Load(1)));
        state = state with { Status = "ready", NextByteOffset = 3, AcceptedRecords = 1, Receipt = new(1, 3, null) };
        journal.Save(state);
        Assert.Equal(state, Assert.Single(journal.Load(1)));
        Assert.Single(Directory.GetFiles(JobDirectory, "*.json"));
        Assert.Empty(Directory.GetFiles(JobDirectory, "*.tmp"));
    }

    [Fact]
    public void ProcessLeaseRejectsASecondOwnerAndIsReleasedAfterStopOrFailedConstruction()
    {
        var first = new ImportJobJournal(_directory);
        Assert.Throws<IOException>(() => new ImportJobJournal(_directory));
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Load(1).ToArray());
        Assert.Throws<ObjectDisposedException>(() => first.Save(ImportJobTestSupport.Pending()));
        Assert.Throws<IOException>(() => new ImportJobJournal(_directory, Open,
            _ => throw new IOException("injected sync failure"), (a, b) => File.Move(a, b, true)));
        using var recovered = new ImportJobJournal(_directory);
        Assert.Empty(recovered.Load(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(66)]
    public void ModifiedChecksumOrBodyBytesAreNeverRecovered(int offset)
    {
        using var journal = new ImportJobJournal(_directory);
        var state = ImportJobTestSupport.Pending();
        journal.Save(state);
        byte[] bytes = File.ReadAllBytes(Manifest(state));
        bytes[offset] ^= 1;
        File.WriteAllBytes(Manifest(state), bytes);
        Assert.Equal("Import job manifest checksum does not match its contents.",
            Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(8193)]
    public void RecoveryChecksFileSizeBeforeReadingTheBody(int size)
    {
        using var journal = new ImportJobJournal(_directory);
        File.WriteAllBytes(Path.Combine(JobDirectory, Guid.NewGuid().ToString("N") + ".json"), new byte[size]);
        Assert.Equal("Import job manifest exceeds its recovery byte boundary.",
            Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
    }

    [Theory]
    [InlineData("null", "Import job manifest is empty.")]
    [InlineData("{", "Import job manifest is not valid JSON.")]
    public void ValidChecksumsDoNotMakeInvalidJsonAValidCheckpoint(string body, string diagnostic)
    {
        using var journal = new ImportJobJournal(_directory);
        WriteBody(Guid.NewGuid(), body);
        Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
    }

    [Fact]
    public void WrongIdentitiesVersionsAndHistoryLimitsFailClosed()
    {
        using var journal = new ImportJobJournal(_directory);
        var first = ImportJobTestSupport.Pending();
        journal.Save(first);
        string renamed = Path.Combine(JobDirectory, Guid.NewGuid().ToString("N") + ".json");
        File.Move(Manifest(first), renamed);
        Assert.Equal("Import job identity does not match its file.", Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
        File.Delete(renamed);
        WriteBody(first.Id, JsonSerializer.Serialize(first with { Version = 2 }, TelemetryAotContext.Default.ImportJobState));
        Assert.Equal("Import job identity or contract is invalid.", Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
        File.Delete(Manifest(first));
        journal.Save(first);
        journal.Save(ImportJobTestSupport.Pending());
        Assert.Equal(2, journal.Load(2).Count());
        Assert.Equal("Import job history exceeds ImportJobMaxJobs; preserve it for recovery.",
            Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
    }

    [Fact]
    public void TornTemporaryReplacementsCannotCreateConfirmedProgress()
    {
        using var journal = new ImportJobJournal(_directory);
        var pending = ImportJobTestSupport.Pending();
        journal.Save(pending);
        string temporary = Path.Combine(JobDirectory, pending.Id.ToString("N") + ".tmp");
        File.WriteAllText(temporary, "torn");
        Assert.Equal(pending, Assert.Single(journal.Load(1)));
        Assert.False(File.Exists(temporary));
        File.WriteAllText(Path.Combine(JobDirectory, "unknown.tmp"), "preserve");
        Assert.Equal("Import job temporary files exceed their recovery boundary.",
            Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
        Assert.True(File.Exists(Path.Combine(JobDirectory, "unknown.tmp")));
        File.Delete(Path.Combine(JobDirectory, "unknown.tmp"));
        File.WriteAllText(Path.Combine(JobDirectory, Guid.NewGuid().ToString("N") + ".tmp"), "torn");
        Assert.Throws<InvalidDataException>(() => journal.Load(0).ToArray());
    }

    [Fact]
    public void GrowingFilesDoNotBypassTheBoundedReadAndFailedStoreRecoveryReleasesItsLease()
    {
        using (var journal = new ImportJobJournal(_directory)) journal.Save(ImportJobTestSupport.Pending());
        using (var journal = new ImportJobJournal(_directory,
            (path, mode) => mode == FileMode.Open ? new UnderreportedFile(path) : Open(path, mode),
            WalDirectoryDurability.Synchronize, (a, b) => File.Move(a, b, true)))
        {
            Assert.Equal("Import job manifest checksum does not match its contents.",
                Assert.Throws<InvalidDataException>(() => journal.Load(1).ToArray()).Message);
        }
        Assert.Throws<InvalidDataException>(() => new ImportJobStore(new ImportJobJournal(_directory), 0));
        using var recovered = new ImportJobJournal(_directory);
        Assert.Single(recovered.Load(1));
    }

    [Fact]
    public async Task StoreLifecycleBoundsAndUncertaintyHaveExplicitOwnership()
    {
        using var store = new ImportJobStore(new ImportJobJournal(_directory), 1);
        Assert.True(store.StartAsync(CancellationToken.None).IsCompletedSuccessfully);
        var state = ImportJobTestSupport.Ready();
        store.Save(state);
        store.MarkUncertain(Guid.NewGuid());
        store.MarkUncertain(state.Id);
        Assert.Equal(state, store.Find(state.Id));
        Assert.Equal("Import job history is full.", Assert.Throws<InvalidOperationException>(() => store.Save(ImportJobTestSupport.Pending())).Message);
        Assert.Equal(1, store.Count);
        await store.StopAsync(CancellationToken.None);
        Assert.Throws<ObjectDisposedException>(() => store.Find(state.Id));
        Assert.Throws<ObjectDisposedException>(() => store.Save(state));
        Assert.Throws<ObjectDisposedException>(() => store.Count);
        Assert.Throws<ObjectDisposedException>(() => { _ = store.StartAsync(CancellationToken.None); });
        store.Dispose();
        using var recovered = new ImportJobStore(new ImportJobJournal(_directory), 1);
        Assert.Equal(state, recovered.Find(state.Id));
    }

    private void WriteBody(Guid id, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        File.WriteAllBytes(Path.Combine(JobDirectory, id.ToString("N") + ".json"),
            [.. Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)) + "\n"), .. bytes]);
    }

    internal static FileStream Open(string path, FileMode mode) => new(path, mode,
        mode == FileMode.Open ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);

    private sealed class TrackingFile(string path, FileMode mode, List<string> calls)
        : FileStream(path, mode, mode == FileMode.Open ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> buffer) { calls.Add("write"); base.Write(buffer); }
        public override void Flush(bool flushToDisk) { calls.Add("flush:" + flushToDisk); base.Flush(flushToDisk); }
        protected override void Dispose(bool disposing) { calls.Add("dispose"); base.Dispose(disposing); }
    }
    private sealed class UnderreportedFile(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
    {
        private bool _lengthRead;
        public override long Length
        {
            get
            {
                Assert.False(_lengthRead);
                _lengthRead = true;
                return base.Length - 1;
            }
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
