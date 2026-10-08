namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

public sealed partial class JwksKeyCache : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumOnDemandInterval = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly Uri _address;
    private readonly TimeSpan _refreshInterval;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IReadOnlyList<SecurityKey> _keys = [];
    private long _lastRefreshTicks;

    public JwksKeyCache(Uri address, TimeSpan refreshInterval, HttpClient http, ILogger logger)
    {
        _address = address;
        _refreshInterval = refreshInterval;
        _http = http;
        _logger = logger;
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Could not refresh the read-token signing keys from {Address}; tokens signed by keys "
            + "not yet seen are refused until it succeeds.")]
    private static partial void LogRefreshFailed(ILogger logger, Uri address, Exception exception);

    public IReadOnlyList<SecurityKey> Keys => Volatile.Read(ref _keys);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        string document = await _http.GetStringAsync(_address, cancellationToken).ConfigureAwait(false);
        JsonWebKeySet set = new(document);
        Volatile.Write(ref _keys, set.GetSigningKeys().ToArray());
        Interlocked.Exchange(ref _lastRefreshTicks, DateTime.UtcNow.Ticks);
    }

    public void RequestRefresh()
    {
        long last = Interlocked.Read(ref _lastRefreshTicks);

        if (DateTime.UtcNow.Ticks - last < MinimumOnDemandInterval.Ticks)
        {
            return;
        }

        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait = _refreshInterval;

            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRefreshFailed(_logger, _address, exception);
                wait = RetryDelay;
            }

            try
            {
                await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        _http.Dispose();
        base.Dispose();
    }
}
