namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

internal sealed class ImportJobStore : IHostedService, IDisposable
{
    private readonly ImportJobJournal _journal;
    private readonly Dictionary<Guid, ImportJobState> _states = [];
    private readonly object _sync = new();
    private readonly int _limit;
    private bool _faulted;
    private bool _disposed;

    public ImportJobStore(ImportJobJournal journal, int limit)
    {
        _journal = journal;
        _limit = limit;
        try
        {
            foreach (ImportJobState state in journal.Load(limit))
            {
                _states.Add(state.Id, state.Status == "processing" ? state with { Status = "needs_reconciliation" } : state);
            }
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureReady();
                return _states.Count;
            }
        }
    }

    public ImportJobState? Find(Guid id)
    {
        lock (_sync)
        {
            EnsureReady();
            return _states.GetValueOrDefault(id);
        }
    }

    public ImportJobPage List(string project, int limit, Guid after)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        lock (_sync)
        {
            EnsureReady();
            ImportJobState[] candidates = _states.Values
                .Where(state => state.ProjectId == project && state.Id.CompareTo(after) > 0)
                .OrderBy(state => state.Id)
                .Take(limit + 1)
                .ToArray();
            ImportJobResponse[] jobs = new ImportJobResponse[Math.Min(limit, candidates.Length)];
            for (int index = 0; index < jobs.Length; index++)
            {
                jobs[index] = candidates[index].Response();
            }
            return new(jobs, candidates.Length > limit ? jobs[^1].JobId : null);
        }
    }

    public void Save(ImportJobState state)
    {
        lock (_sync)
        {
            EnsureReady();
            if (!_states.ContainsKey(state.Id) && _states.Count == _limit)
            {
                throw new InvalidOperationException("Import job history is full.");
            }
            try
            {
                _journal.Save(state);
                _states[state.Id] = state;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    public void MarkUncertain(Guid id)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(id, out ImportJobState? state) && state.Status == "processing")
            {
                _states[id] = state with { Status = "needs_reconciliation" };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            EnsureReady();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new IOException("Import job journal is unavailable until restart.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _journal.Dispose();
        }
    }
}
