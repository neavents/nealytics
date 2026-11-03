namespace Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class WalFileSelection
{
    public WalFileSelection(IWalFile file, string backend, string fallbackReason, bool registeredFile)
    {
        File = file;
        Backend = backend;
        FallbackReason = fallbackReason;
        RegisteredFile = registeredFile;
    }

    public IWalFile File { get; }
    public string Backend { get; }
    public string FallbackReason { get; }
    public bool RegisteredFile { get; }
}
