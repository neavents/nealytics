namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Octonica.ClickHouseClient;

public readonly struct PooledClickHouseConnection : IAsyncDisposable
{
    private readonly Lease _lease;

    internal PooledClickHouseConnection(ClickHouseConnection connection, ClickHouseConnectionPool pool)
    {
        _lease = new Lease(connection, pool);
    }

    public ClickHouseConnection Connection => _lease.Connection;

    public void Discard() => _lease.Discard();

    public ValueTask DisposeAsync()
    {
        _lease.Return();
        return ValueTask.CompletedTask;
    }

    private sealed class Lease
    {
        private readonly ClickHouseConnectionPool _pool;
        private int _discarded;
        private int _returned;

        public Lease(ClickHouseConnection connection, ClickHouseConnectionPool pool)
        {
            Connection = connection;
            _pool = pool;
        }

        public ClickHouseConnection Connection { get; }

        public void Discard() => Volatile.Write(ref _discarded, 1);

        public void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                _pool.Return(Connection, Volatile.Read(ref _discarded) != 0);
            }
        }
    }
}
