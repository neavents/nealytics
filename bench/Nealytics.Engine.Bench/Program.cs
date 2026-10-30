using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nealytics.Engine.Bench.Measurements;
using Octonica.ClickHouseClient;

// Nealytics single-pod load-test harness.
//
// Drives an endpoint at rising concurrency, records throughput + latency percentiles +
// error rate, verifies the zero-loss invariant (events accepted == rows stored) for write
// modes, and appends a Markdown result block to bench/RESULTS.md.
//
// Modes:
//   noop        GET /health  — pure HTTP/Kestrel/loopback ceiling (no WAL/channel/DB)
//   track       POST /track  — single-event durable ingest (WAL group-commit path)
//   beacon      POST /beacon — batched ingest (many events per request)
//   timeline|timeseries|active|top   read endpoints (JWT)
//   breakdown|breakdown-rollup       the same grouping answered from raw and from a rollup
//   breakdown-measure|breakdown-measure-rollup   the same, for sum(amount)
//   set-breakdown|set-breakdown-group|set-pivot-attribute|set-timeseries   one question over a
//                    tenant set (a token carrying tenant_set instead of tenant_id), from raw and
//                    from a tenant-attribute rollup; seeded by ./scripts/run-benchmark.sh set
//
// This tool is intentionally exempt from the engine's no-var and no-comment rules.

BenchOptions options = BenchOptions.Parse(args);
Console.WriteLine(options.Describe());

using SocketsHttpHandler handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = Math.Max(256, options.Concurrency.Max() * 2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true
};
using HttpClient client = new HttpClient(handler)
{
    BaseAddress = new Uri(options.BaseUrl),
    Timeout = TimeSpan.FromSeconds(100)
};

await WaitForLivenessAsync(client);
await PreflightSourceAsync(client, options);

if (options.Warmup > 0)
{
    Console.WriteLine($"Warmup: {options.Warmup} requests...");
    await RunLevelAsync(client, options, Math.Min(32, options.Concurrency.Max()), options.Warmup, 0, $"warmup-{Now()}", false, false);
}

List<LevelResult> results = new List<LevelResult>();
foreach (int concurrency in options.Concurrency)
{
    // A seeding run pins every level to one tenant so the read modes have a single target. The
    // per-level tenant exists to keep the zero-loss count honest, so pinning it means the stored
    // count is cumulative across levels rather than per level -- hence loss verification is off
    // for a seed run, and the script says so.
    string tenant = options.SeedTenant ?? $"bench-{options.Mode}-c{concurrency}-{Now()}";
    Console.WriteLine($"\nLevel: concurrency={concurrency}, {(options.Duration > 0 ? options.Duration + "s" : options.Requests + " reqs")}, tenant={tenant}");
    LevelResult result = await RunLevelAsync(client, options, concurrency, options.Requests, options.Duration, tenant, options.IsWriteMode && options.VerifyLoss);
    results.Add(result);
    Console.WriteLine(result.ConsoleLine());
}

string report = BuildReport(options, results);
Console.WriteLine("\n" + report);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutFile))!);
await File.AppendAllTextAsync(options.OutFile, report + "\n");
Console.WriteLine($"Appended results to {options.OutFile}");

if (options.ManifestFile is not null)
{
    BenchmarkManifest manifest = new()
    {
        Revision = Environment.GetEnvironmentVariable("GIT_SHA") ?? "uncommitted",
        Target = BenchmarkManifest.SafeTarget(options.BaseUrl),
        Mode = options.Mode,
        WarmupRequests = options.Warmup,
        RequestsPerLevel = options.Requests,
        DurationSeconds = options.Duration,
        OfferedRequestsPerSecond = options.OfferedRate,
        EventsPerRequest = options.Mode == "beacon" ? options.BeaconBatch : 1,
        VerifyStoredCount = options.IsWriteMode && options.VerifyLoss,
        ClickHouseVersion = await GetClickHouseVersionAsync(options.ClickHouseConnectionString),
        DeclaredEngineSettings = BenchmarkManifest.CaptureSettings(Environment.GetEnvironmentVariable),
        Levels = results
    };

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ManifestFile))!);
    await File.WriteAllTextAsync(options.ManifestFile, JsonSerializer.Serialize(manifest, BenchmarkJsonContext.Default.BenchmarkManifest));
    Console.WriteLine($"Wrote manifest to {options.ManifestFile}");
}

return results.All(result => BenchmarkVerification.Passed(
    result, options.IsWriteMode && options.VerifyLoss)) ? 0 : 1;

async Task WaitForLivenessAsync(HttpClient c)
{
    for (int i = 0; i < 60; i++)
    {
        try
        {
            HttpResponseMessage response = await c.GetAsync("/health");
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
        }
        await Task.Delay(1000);
    }
    throw new InvalidOperationException($"Target {options.BaseUrl} never became live (GET /health).");
}

async Task<LevelResult> RunLevelAsync(
    HttpClient c, BenchOptions opt, int concurrency, int requests, double durationSeconds,
    string tenant, bool countLoss, bool measure = true)
{
    LatencyHistogram acknowledgments = new();
    LatencyHistogram scheduledLatencies = new();
    LatencyHistogram visibility = new();
    ArrivalSchedule schedule = new(measure ? opt.OfferedRate : 0);
    long ok = 0;
    long errors = 0;
    long peakQueue = 0;
    long seq = -1;
    long visibilityDropped = 0;
    long visibilityTimeouts = 0;
    Channel<(Guid EventId, long Started)> probes = Channel.CreateBounded<(Guid, long)>(64);
    bool probeVisibility = measure && opt.IsWriteMode && opt.VisibilityEvery > 0;
    Task visibilityTask = probeVisibility
        ? ObserveVisibilityAsync(opt.ClickHouseConnectionString, tenant, probes.Reader, visibility,
            () => Interlocked.Increment(ref visibilityTimeouts))
        : Task.CompletedTask;
    using Process process = Process.GetCurrentProcess();
    TimeSpan cpuBefore = process.TotalProcessorTime;
    long allocatedBefore = GC.GetTotalAllocatedBytes();

    using CancellationTokenSource metricsCts = new CancellationTokenSource();
    Task metricsTask = opt.MetricsUrl is null
        ? Task.CompletedTask
        : SampleQueueDepthAsync(c, opt.MetricsUrl, v => peakQueue = Math.Max(peakQueue, v), metricsCts.Token);

    string? jwt = opt.NeedsJwt ? MakeJwt(opt.JwtKey, "bench", opt.ReadTenant ?? tenant, opt.ReadSet) : null;

    Stopwatch sw = Stopwatch.StartNew();
    Task[] workers = new Task[concurrency];
    for (int w = 0; w < concurrency; w++)
    {
        int workerIndex = w;
        workers[workerIndex] = Task.Run(async () =>
        {
            while (true)
            {
                if (!schedule.IsOpenLoop && durationSeconds > 0)
                {
                    if (sw.Elapsed.TotalSeconds >= durationSeconds)
                    {
                        break;
                    }
                }
                long i = Interlocked.Increment(ref seq);
                if (durationSeconds <= 0 && i >= requests)
                {
                    break;
                }
                TimeSpan due = schedule.Offset(i);

                if (schedule.IsOpenLoop && durationSeconds > 0 && due.TotalSeconds >= durationSeconds)
                {
                    break;
                }

                TimeSpan delay = due - sw.Elapsed;

                while (schedule.IsOpenLoop && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay);
                    delay = due - sw.Elapsed;
                }

                bool probe = probeVisibility && i % opt.VisibilityEvery == 0;
                Guid eventId = probe ? Guid.NewGuid() : Guid.Empty;
                long startTicks = Stopwatch.GetTimestamp();
                bool success = await SendOneAsync(c, opt, tenant, i, jwt, eventId);
                double ms = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                scheduledLatencies.Record(schedule.IsOpenLoop ? sw.Elapsed.TotalMilliseconds - due.TotalMilliseconds : ms);
                if (success)
                {
                    acknowledgments.Record(ms);
                    Interlocked.Increment(ref ok);

                    if (probe && !probes.Writer.TryWrite((eventId, startTicks)))
                    {
                        Interlocked.Increment(ref visibilityDropped);
                    }
                }
                else
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
    }
    await Task.WhenAll(workers);
    sw.Stop();
    TimeSpan clientCpu = process.TotalProcessorTime - cpuBefore;
    long clientAllocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
    process.Refresh();
    long clientWorkingSet = process.WorkingSet64;
    probes.Writer.Complete();
    await visibilityTask;

    metricsCts.Cancel();
    try { await metricsTask; } catch { }

    double elapsedSeconds = sw.Elapsed.TotalSeconds;
    int eventsPerRequest = opt.Mode == "beacon" ? opt.BeaconBatch : 1;
    double throughput = ok / Math.Max(elapsedSeconds, 1e-9);

    long eventsSent = (long)ok * eventsPerRequest;
    long stored = countLoss ? await PollStoredCountAsync(opt.ClickHouseConnectionString, tenant, eventsSent) : -1;

    return new LevelResult
    {
        TenantId = tenant,
        Concurrency = concurrency,
        Ok = ok,
        Errors = errors,
        ElapsedSeconds = elapsedSeconds,
        Throughput = throughput,
        EventThroughput = throughput * eventsPerRequest,
        EventsPerRequest = eventsPerRequest,
        EventsSent = eventsSent,
        P50 = acknowledgments.Percentile(50),
        P95 = acknowledgments.Percentile(95),
        P99 = acknowledgments.Percentile(99),
        Max = acknowledgments.MaximumMilliseconds,
        ScheduledP95 = scheduledLatencies.Percentile(95),
        ScheduledP99 = scheduledLatencies.Percentile(99),
        VisibilitySamples = visibility.Count,
        VisibilityP95 = visibility.Count == 0 ? null : visibility.Percentile(95),
        VisibilityP99 = visibility.Count == 0 ? null : visibility.Percentile(99),
        VisibilityDropped = visibilityDropped,
        VisibilityTimeouts = visibilityTimeouts,
        ClientCpuMilliseconds = clientCpu.TotalMilliseconds,
        ClientAllocatedBytes = clientAllocated,
        ClientWorkingSetBytes = clientWorkingSet,
        PeakQueue = peakQueue,
        Stored = stored
    };
}

async Task<bool> SendOneAsync(HttpClient c, BenchOptions opt, string tenant, long i, string? jwt, Guid eventId)
{
    try
    {
        if (opt.Mode == "noop")
        {
            using HttpResponseMessage res = await c.GetAsync("/health");
            return res.IsSuccessStatusCode;
        }

        if (opt.Mode == "track")
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/telemetry/track?k={opt.ProjectKey}")
            {
                Content = new StringContent(TrackPayload("bench", tenant, $"s{i}", eventId), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage res = await c.SendAsync(req);
            return res.StatusCode == HttpStatusCode.Accepted;
        }

        if (opt.Mode == "beacon")
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/telemetry/beacon?k={opt.ProjectKey}")
            {
                Content = new StringContent(BeaconPayload("bench", tenant, i, opt.BeaconBatch, opt.Declared, eventId), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage res = await c.SendAsync(req);
            return res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted;
        }

        using HttpRequestMessage readReq = new HttpRequestMessage(HttpMethod.Get, ReadUrl(opt.Mode));
        readReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using HttpResponseMessage readRes = await c.SendAsync(readReq);
        return readRes.StatusCode == HttpStatusCode.OK;
    }
    catch
    {
        return false;
    }
}

// The read URLs live in one place because the preflight source check below has to interrogate the
// exact query the benchmark then runs. Two copies would drift, and a preflight that verifies a
// different query than the hot loop measures is worse than no preflight at all.
//
// breakdown and breakdown-rollup differ only in whether the range lands on a day boundary. Both
// group by the same column over the same rows, so the pair isolates the routing decision rather
// than comparing two different questions. RollupPlanner.IsAligned refuses a rollup for the first
// because answering 00:00:01-00:00:01 from daily buckets would return whole days -- a wrong number
// that looks entirely healthy.
static string ReadUrl(string mode) => mode switch
{
    "timeline" => "/api/v1/telemetry/timeline?limit=100",
    "timeseries" => "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day",
    "active" => "/api/v1/analytics/active?interval=day&by=user&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z",
    "top" => "/api/v1/analytics/top?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z",
    "breakdown" => "/api/v1/analytics/breakdown?groupBy=product_id&metric=events&eventType=bench&from=2026-01-01T00:00:01Z&to=2026-12-31T00:00:01Z&limit=100",
    "breakdown-rollup" => "/api/v1/analytics/breakdown?groupBy=product_id&metric=events&eventType=bench&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&limit=100",
    "breakdown-measure" => "/api/v1/analytics/breakdown?groupBy=product_id&metric=sum(amount)&eventType=bench&from=2026-01-01T00:00:01Z&to=2026-12-31T00:00:01Z&limit=100",
    "breakdown-measure-rollup" => "/api/v1/analytics/breakdown?groupBy=product_id&metric=sum(amount)&eventType=bench&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&limit=100",
    "set-breakdown" => "/api/v1/analytics/breakdown?groupBy=tenant_id&metric=sum(amount)&filter=currency:TRY&eventType=sale&from=2026-01-01T00:00:01Z&to=2026-12-31T00:00:01Z&limit=1000",
    "set-breakdown-group" => "/api/v1/analytics/breakdown?groupBy=tenant.region&metric=sum(amount)&filter=currency:TRY&eventType=sale&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&limit=100",
    "set-pivot-attribute" => "/api/v1/analytics/pivot?groupBy=tenant.region&metric=events&metric=sessions&metric=sum(amount)&filter=currency:TRY&from=2026-01-01T00:00:01Z&to=2026-12-31T00:00:01Z&limit=100",
    "set-timeseries" => "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day&eventType=sale",
    _ => throw new InvalidOperationException($"Unknown mode '{mode}'.")
};

// Which source the mode claims to measure. A benchmark that records "rollup: 40x faster" while the
// planner quietly fell back to raw is a lie told with real numbers, and nothing else in the harness
// would notice -- both paths return 200 and identical rows by design.
static string? ExpectedSource(string mode) => mode switch
{
    "breakdown" or "breakdown-measure" => "raw",
    "breakdown-rollup" => "rollup:",
    "breakdown-measure-rollup" => "rollup:",
    "set-breakdown" or "set-pivot-attribute" => "raw",
    "set-breakdown-group" => "rollup:daily_by_org",
    _ => null
};

static async Task PreflightSourceAsync(HttpClient c, BenchOptions opt)
{
    string? expected = ExpectedSource(opt.Mode);
    if (expected is null)
    {
        return;
    }

    using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, ReadUrl(opt.Mode));
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MakeJwt(opt.JwtKey, "bench", opt.ReadTenant ?? "bench", opt.ReadSet));
    using HttpResponseMessage res = await c.SendAsync(req);
    string body = await res.Content.ReadAsStringAsync();

    if (!res.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Preflight for mode '{opt.Mode}' returned {(int)res.StatusCode}: {body}");
    }

    using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
    string source = document.RootElement.TryGetProperty("source", out System.Text.Json.JsonElement element)
        ? element.GetString() ?? ""
        : "";
    int rows = document.RootElement.TryGetProperty("rows", out System.Text.Json.JsonElement rowsElement)
        ? rowsElement.GetArrayLength()
        : 0;

    if (!source.StartsWith(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Mode '{opt.Mode}' is meant to measure '{expected}' but the engine answered from '{source}'. " +
            "Recording this run would attribute one path's timings to the other.");
    }

    if (rows == 0)
    {
        throw new InvalidOperationException(
            $"Mode '{opt.Mode}' returned no rows. Seed the tenant first (./scripts/run-benchmark.sh read) " +
            "-- an empty table benchmarks the absence of work.");
    }

    Console.WriteLine($"Preflight: mode={opt.Mode} source={source} rows={rows}");
}

static string TrackPayload(string projectId, string tenantId, string sessionId, Guid eventId)
{
    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    string identity = eventId == Guid.Empty ? string.Empty : $"\"eventId\":\"{eventId}\",";
    return $"{{{identity}\"projectId\":\"{projectId}\",\"tenantId\":\"{tenantId}\",\"sessionId\":\"{sessionId}\",\"eventType\":\"bench\",\"metadataJson\":\"{{}}\",\"timestamp\":\"{timestamp}\"}}";
}

// Only carries dimensions and measures when the bench schema is mounted. Sending them to an engine
// that declares none is not harmless: the sanitizer drops each unknown key, counts it and logs it,
// so the write benchmark would be measuring rejection bookkeeping and the run would fill the log
// with drops that look like a defect.
static string BeaconPayload(string projectId, string tenantId, long i, int batch, bool declared, Guid eventId)
{
    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    StringBuilder sb = new StringBuilder("[");
    for (int j = 0; j < batch; j++)
    {
        if (j > 0)
        {
            sb.Append(',');
        }
        string identity = j == 0 && eventId != Guid.Empty ? $"\"eventId\":\"{eventId}\"," : string.Empty;
        sb.Append($"{{{identity}\"projectId\":\"{projectId}\",\"tenantId\":\"{tenantId}\",\"sessionId\":\"s{i}_{j}\",\"eventType\":\"bench\",\"metadataJson\":\"{{}}\",\"timestamp\":\"{timestamp}\"");
        if (declared)
        {
            long n = i * batch + j;
            sb.Append($",\"objectId\":\"p{n % 500}\"");
            sb.Append($",\"dimensions\":{{\"product_id\":\"p{n % 500}\",\"category_id\":\"c{n % 20}\",\"campaign_id\":\"k{n % 5}\"}}");
            sb.Append($",\"measures\":{{\"amount\":\"{n % 1000}\"}}");
        }
        sb.Append('}');
    }
    sb.Append(']');
    return sb.ToString();
}

static async Task<long> PollStoredCountAsync(string connectionString, string tenant, long expected)
{
    for (int attempt = 0; attempt < 90; attempt++)
    {
        long count = await CountAsync(connectionString, tenant);
        if (count >= expected)
        {
            return count;
        }
        await Task.Delay(1000);
    }
    return await CountAsync(connectionString, tenant);
}

static async Task<long> CountAsync(string connectionString, string tenant)
{
    try
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM nealytics_core.global_events WHERE tenant_id = {t:String}";
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "t", Value = tenant });
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
    catch
    {
        return -1;
    }
}

static async Task<string?> GetClickHouseVersionAsync(string connectionString)
{
    try
    {
        await using ClickHouseConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"ClickHouse version measurement unavailable: {exception.GetType().Name}");
        return null;
    }
}

static async Task ObserveVisibilityAsync(
    string connectionString,
    string tenant,
    ChannelReader<(Guid EventId, long Started)> samples,
    LatencyHistogram observed,
    Action timedOut)
{
    await using ClickHouseConnection connection = new(connectionString);
    await connection.OpenAsync();

    await foreach ((Guid eventId, long started) in samples.ReadAllAsync())
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        try
        {
            while (true)
            {
                await using ClickHouseCommand command = connection.CreateCommand();
                command.CommandText = "SELECT count() FROM nealytics_core.global_events"
                    + " WHERE project_id = 'bench' AND tenant_id = {tenant:String} AND event_id = {event:UUID}";
                command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenant", Value = tenant });
                command.Parameters.Add(new ClickHouseParameter { ParameterName = "event", Value = eventId });
                object? result = await command.ExecuteScalarAsync(timeout.Token);

                if (Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0)
                {
                    observed.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    break;
                }

                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut();
        }
    }
}

static async Task SampleQueueDepthAsync(HttpClient c, string metricsUrl, Action<long> observe, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            string text = await c.GetStringAsync(metricsUrl, ct);
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("nealytics_queue_depth_current", StringComparison.Ordinal) && !line.StartsWith("#", StringComparison.Ordinal))
                {
                    int space = line.LastIndexOf(' ');
                    if (space > 0 && double.TryParse(line.AsSpan(space + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                    {
                        observe((long)value);
                    }
                }
            }
        }
        catch
        {
        }
        try { await Task.Delay(200, ct); } catch { }
    }
}

static string MakeJwt(string key, string projectId, string tenantId, string? tenantSet = null)
{
    long exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
    string scope = tenantSet is null ? $"\"tenant_id\":\"{tenantId}\"" : $"\"tenant_set\":\"{tenantSet}\"";
    string payload = Base64Url(Encoding.UTF8.GetBytes($"{{\"project_id\":\"{projectId}\",{scope},\"exp\":{exp}}}"));
    string signingInput = header + "." + payload;
    using HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    string signature = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
    return signingInput + "." + signature;
}

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

static string BuildReport(BenchOptions opt, List<LevelResult> results)
{
    string sha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "uncommitted";
    string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    string budget = opt.Duration > 0
        ? opt.Duration.ToString(CultureInfo.InvariantCulture) + "s/level"
        : $"{opt.Requests} reqs/level";
    StringBuilder sb = new StringBuilder();
    sb.AppendLine($"## Run {stamp} — mode=`{opt.Mode}`, target=`{BenchmarkManifest.SafeTarget(opt.BaseUrl)}`, git=`{sha}`");
    sb.AppendLine();
    sb.AppendLine($"- budget: {budget}, events/request: {(opt.Mode == "beacon" ? opt.BeaconBatch : 1)}, verify-loss: {opt.IsWriteMode && opt.VerifyLoss}");
    sb.AppendLine($"- offered-rate: {opt.OfferedRate} req/s (0 means closed loop); visibility sample interval: {opt.VisibilityEvery} requests (0 means disabled)");
    sb.AppendLine("- latency histograms are bounded; percentiles and maximum are upper-bucket estimates with microsecond rounding and 1/256 power-of-two sub-buckets");
    sb.AppendLine();
    sb.AppendLine("| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (LevelResult r in results)
    {
        string lossCell = r.Stored < 0 ? "n/a" : $"{r.Stored}/{r.EventsSent}" + (r.Stored == r.EventsSent ? " ✅" : " ❌");
        sb.AppendLine($"| {r.Concurrency} | {r.Ok} | {r.Errors} | {r.Throughput:N0} | {r.EventThroughput:N0} | {r.P50:N2} | {r.P95:N2} | {r.P99:N2} | {r.Max:N1} | {lossCell} | {r.PeakQueue} |");
    }
    sb.AppendLine();
    sb.AppendLine("| Concurrency | Scheduled p95 ms | Scheduled p99 ms | Visibility samples | Visibility p95 ms | Visibility p99 ms | Probe drops/timeouts | Client CPU ms | Client allocations | Client working set |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

    foreach (LevelResult result in results)
    {
        string visibilityP95 = result.VisibilityP95?.ToString("N2", CultureInfo.InvariantCulture) ?? "n/a";
        string visibilityP99 = result.VisibilityP99?.ToString("N2", CultureInfo.InvariantCulture) ?? "n/a";
        sb.AppendLine(
            $"| {result.Concurrency} | {result.ScheduledP95:N2} | {result.ScheduledP99:N2}"
            + $" | {result.VisibilitySamples} | {visibilityP95} | {visibilityP99}"
            + $" | {result.VisibilityDropped}/{result.VisibilityTimeouts} | {result.ClientCpuMilliseconds:N1}"
            + $" | {result.ClientAllocatedBytes} | {result.ClientWorkingSetBytes} |");
    }

    return sb.ToString();
}

public sealed class LevelResult
{
    public string TenantId { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public long Ok { get; init; }
    public long Errors { get; init; }
    public double ElapsedSeconds { get; init; }
    public double Throughput { get; init; }
    public double EventThroughput { get; init; }
    public int EventsPerRequest { get; init; }
    public long EventsSent { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Max { get; init; }
    public double ScheduledP95 { get; init; }
    public double ScheduledP99 { get; init; }
    public long VisibilitySamples { get; init; }
    public double? VisibilityP95 { get; init; }
    public double? VisibilityP99 { get; init; }
    public long VisibilityDropped { get; init; }
    public long VisibilityTimeouts { get; init; }
    public double ClientCpuMilliseconds { get; init; }
    public long ClientAllocatedBytes { get; init; }
    public long ClientWorkingSetBytes { get; init; }
    public long PeakQueue { get; init; }
    public long Stored { get; init; }

    public string ConsoleLine()
    {
        string loss = Stored < 0 ? "" : $" stored={Stored}/{EventsSent}";
        return $"  ok={Ok} err={Errors} {Throughput:N0} req/s ({EventThroughput:N0} ev/s) p50={P50:N2}ms p95={P95:N2}ms p99={P99:N2}ms max={Max:N0}ms{loss}";
    }
}

public sealed class BenchOptions
{
    public string BaseUrl { get; init; } = "http://localhost:5199";
    public string Mode { get; init; } = "track";
    public string ProjectKey { get; init; } = "test-key-1";
    public string JwtKey { get; init; } = "local-integration-test-key-at-least-32-bytes!!";
    public int[] Concurrency { get; init; } = new[] { 1, 8, 32, 64, 128, 256, 512 };
    public int Requests { get; init; } = 40000;
    public double Duration { get; init; }
    public int Warmup { get; init; } = 3000;
    public int BeaconBatch { get; init; } = 50;
    public bool VerifyLoss { get; init; } = true;
    public string ClickHouseConnectionString { get; init; } = "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;";
    public string OutFile { get; init; } = "bench/RESULTS.md";
    public string? MetricsUrl { get; init; }
    public string? ReadTenant { get; init; }
    public string? ReadSet { get; init; }
    public string? ManifestFile { get; init; }
    public double OfferedRate { get; init; }
    public int VisibilityEvery { get; init; }

    public bool Declared { get; init; }
    public string? SeedTenant { get; init; }

    public bool IsWriteMode => Mode == "track" || Mode == "beacon";
    public bool NeedsJwt => !IsWriteMode && Mode != "noop";

    public string Describe() =>
        $"Nealytics bench: mode={Mode} target={BenchmarkManifest.SafeTarget(BaseUrl)} concurrency=[{string.Join(",", Concurrency)}] " +
        (Duration > 0 ? $"duration={Duration.ToString(CultureInfo.InvariantCulture)}s/level" : $"requests={Requests}/level") +
        $" warmup={Warmup}" + (Mode == "beacon" ? $" beaconBatch={BeaconBatch}" : "");

    public static BenchOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Benchmark arguments must be option/value pairs.", nameof(args));
        }

        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        string Env(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;

        BenchOptions options = new()
        {
            BaseUrl = map.GetValueOrDefault("url", Env("BENCH_URL", "http://localhost:5199")),
            Mode = map.GetValueOrDefault("mode", "track"),
            ProjectKey = map.GetValueOrDefault("key", Env("BENCH_KEY", "test-key-1")),
            JwtKey = map.GetValueOrDefault("jwt-key", Env("TelemetryEngine__JwtSymmetricKey", "local-integration-test-key-at-least-32-bytes!!")),
            Concurrency = ParseInts(map.GetValueOrDefault("concurrency", "1,8,32,64,128,256,512")),
            Requests = int.Parse(map.GetValueOrDefault("requests", "40000"), CultureInfo.InvariantCulture),
            Duration = double.Parse(map.GetValueOrDefault("duration", "0"), CultureInfo.InvariantCulture),
            Warmup = int.Parse(map.GetValueOrDefault("warmup", "3000"), CultureInfo.InvariantCulture),
            BeaconBatch = int.Parse(map.GetValueOrDefault("beacon-batch", "50"), CultureInfo.InvariantCulture),
            Declared = string.Equals(map.GetValueOrDefault("declared", "false"), "true", StringComparison.OrdinalIgnoreCase),
            SeedTenant = map.GetValueOrDefault("seed-tenant", "") is { Length: > 0 } s ? s : null,
            VerifyLoss = !string.Equals(map.GetValueOrDefault("verify-loss", "true"), "false", StringComparison.OrdinalIgnoreCase),
            ClickHouseConnectionString = map.GetValueOrDefault("ch", Env("TelemetryEngine__ClickHouseConnectionString", "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;")),
            OutFile = map.GetValueOrDefault("out", "bench/RESULTS.md"),
            MetricsUrl = map.GetValueOrDefault("metrics-url", Environment.GetEnvironmentVariable("BENCH_METRICS_URL") ?? "") is { Length: > 0 } m ? m : null,
            ReadTenant = map.GetValueOrDefault("read-tenant", "") is { Length: > 0 } t ? t : null,
            ReadSet = map.GetValueOrDefault("read-set", "") is { Length: > 0 } rs ? rs : null,
            ManifestFile = map.GetValueOrDefault("manifest", "") is { Length: > 0 } manifest ? manifest : null,
            OfferedRate = double.Parse(map.GetValueOrDefault("rate", "0"), CultureInfo.InvariantCulture),
            VisibilityEvery = int.Parse(map.GetValueOrDefault("visibility-every", "0"), CultureInfo.InvariantCulture)
        };

        _ = new ArrivalSchedule(options.OfferedRate);

        if (options.Concurrency.Length == 0 || options.Concurrency.Any(value => value <= 0)
            || options.Requests <= 0 || !double.IsFinite(options.Duration) || options.Duration < 0
            || options.Warmup < 0 || options.BeaconBatch <= 0 || options.VisibilityEvery < 0)
        {
            throw new ArgumentException("Invalid benchmark workload limits.", nameof(args));
        }

        return options;
    }

    static int[] ParseInts(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
           .ToArray();
}
