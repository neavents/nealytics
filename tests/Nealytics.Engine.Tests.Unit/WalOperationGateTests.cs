using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalOperationGateTests
{
    [Fact]
    public async Task StopWaitsForEveryExistingOperationAndRejectsNewOnes()
    {
        WalOperationGate gate = new();
        gate.Enter();
        gate.Enter();
        Task idle = gate.Stop();
        Assert.Same(idle, gate.Stop());
        Assert.False(idle.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => gate.Enter());

        gate.Exit();
        Assert.False(idle.IsCompleted);
        gate.Exit();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.Stop().IsCompletedSuccessfully);
    }

    [Fact]
    public void StoppingAnIdleGateCompletesImmediately()
    {
        WalOperationGate gate = new();
        gate.Enter();
        gate.ThrowIfFailed();
        gate.Exit();
        Assert.True(gate.Stop().IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => gate.Enter());
    }

    [Fact]
    public async Task FailureIsStickyAndDoesNotStrandAlreadyEnteredOperations()
    {
        WalOperationGate gate = new();
        gate.Enter();
        IOException failure = new("partial I/O");
        Assert.Same(failure, gate.Fail(failure));
        Assert.Same(failure, gate.Fail(new Exception("subsequent failure")));
        Assert.Same(failure, Assert.Throws<IOException>(() => gate.Enter()));
        Assert.Same(failure, Assert.Throws<IOException>(() => gate.ThrowIfFailed()));
        Task idle = gate.Stop();
        Assert.False(idle.IsCompleted);
        gate.Exit();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => gate.Enter());
    }
}
