using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    await RunLevelAsync(client, options, Math.Min(32, options.Concurrency.Max()), options.Warmup, 0, $"warmup-{Now()}", false);
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
return results.Any(r => r.Errors > 0 || (options.IsWriteMode && options.VerifyLoss && r.Stored >= 0 && r.Stored < r.EventsSent)) ? 1 : 0;

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

async Task<LevelResult> RunLevelAsync(HttpClient c, BenchOptions opt, int concurrency, int requests, double durationSeconds, string tenant, bool countLoss)
{
    List<double>[] workerLatencies = new List<double>[concurrency];
    int ok = 0;
    int errors = 0;
    long peakQueue = 0;
    long seq = -1;

    using CancellationTokenSource metricsCts = new CancellationTokenSource();
    Task metricsTask = opt.MetricsUrl is null
        ? Task.CompletedTask
        : SampleQueueDepthAsync(c, opt.MetricsUrl, v => peakQueue = Math.Max(peakQueue, v), metricsCts.Token);

    string? jwt = opt.NeedsJwt ? MakeJwt(opt.JwtKey, "bench", opt.ReadTenant ?? tenant) : null;

    Stopwatch sw = Stopwatch.StartNew();
    Task[] workers = new Task[concurrency];
    for (int w = 0; w < concurrency; w++)
    {
        int workerIndex = w;
        workers[workerIndex] = Task.Run(async () =>
        {
            List<double> local = new List<double>(4096);
            while (true)
            {
                if (durationSeconds > 0)
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
                long startTicks = Stopwatch.GetTimestamp();
                bool success = await SendOneAsync(c, opt, tenant, i, jwt);
                double ms = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                if (success)
                {
                    local.Add(ms);
                    Interlocked.Increment(ref ok);
                }
                else
                {
                    Interlocked.Increment(ref errors);
                }
            }
            workerLatencies[workerIndex] = local;
        });
    }
    await Task.WhenAll(workers);
    sw.Stop();

    metricsCts.Cancel();
    try { await metricsTask; } catch { }

    double elapsedSeconds = sw.Elapsed.TotalSeconds;
    int eventsPerRequest = opt.Mode == "beacon" ? opt.BeaconBatch : 1;
    double throughput = ok / Math.Max(elapsedSeconds, 1e-9);

    double[] okLatencies = workerLatencies
        .Where(l => l is not null)
        .SelectMany(l => l!)
        .OrderBy(v => v)
        .ToArray();

    long eventsSent = (long)ok * eventsPerRequest;
    long stored = countLoss ? await PollStoredCountAsync(opt.ClickHouseConnectionString, tenant, eventsSent) : -1;

    return new LevelResult
    {
        Concurrency = concurrency,
        Ok = ok,
        Errors = errors,
        ElapsedSeconds = elapsedSeconds,
        Throughput = throughput,
        EventThroughput = throughput * eventsPerRequest,
        EventsPerRequest = eventsPerRequest,
        EventsSent = eventsSent,
        P50 = Percentile(okLatencies, 50),
        P95 = Percentile(okLatencies, 95),
        P99 = Percentile(okLatencies, 99),
        Max = okLatencies.Length > 0 ? okLatencies[^1] : 0,
        PeakQueue = peakQueue,
        Stored = stored
    };
}

async Task<bool> SendOneAsync(HttpClient c, BenchOptions opt, string tenant, long i, string? jwt)
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
                Content = new StringContent(TrackPayload("bench", tenant, $"s{i}"), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage res = await c.SendAsync(req);
            return res.StatusCode == HttpStatusCode.Accepted;
        }

        if (opt.Mode == "beacon")
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/telemetry/beacon?k={opt.ProjectKey}")
            {
                Content = new StringContent(BeaconPayload("bench", tenant, i, opt.BeaconBatch, opt.Declared), Encoding.UTF8, "application/json")
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

static string Window(int offsetSeconds)
{
    DateTime to = DateTime.UtcNow.Date.AddDays(1).AddSeconds(offsetSeconds);
    DateTime from = to.AddDays(-90);
    return string.Create(CultureInfo.InvariantCulture, $"from={from:yyyy-MM-ddTHH:mm:ssZ}&to={to:yyyy-MM-ddTHH:mm:ssZ}");
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
    "timeseries" => $"/api/v1/analytics/timeseries?{Window(0)}&interval=day",
    "active" => $"/api/v1/analytics/active?interval=day&by=user&{Window(0)}",
    "top" => $"/api/v1/analytics/top?{Window(0)}",
    "breakdown" => $"/api/v1/analytics/breakdown?groupBy=product_id&metric=events&eventType=bench&{Window(1)}&limit=100",
    "breakdown-rollup" => $"/api/v1/analytics/breakdown?groupBy=product_id&metric=events&eventType=bench&{Window(0)}&limit=100",
    "breakdown-measure" => $"/api/v1/analytics/breakdown?groupBy=product_id&metric=sum(amount)&eventType=bench&{Window(1)}&limit=100",
    "breakdown-measure-rollup" => $"/api/v1/analytics/breakdown?groupBy=product_id&metric=sum(amount)&eventType=bench&{Window(0)}&limit=100",
    _ => throw new InvalidOperationException($"Unknown mode '{mode}'.")
};

// Which source the mode claims to measure. A benchmark that records "rollup: 40x faster" while the
// planner quietly fell back to raw is a lie told with real numbers, and nothing else in the harness
// would notice -- both paths return 200 and identical rows by design.
static string? ExpectedSource(string mode) => mode switch
{
    "breakdown" or "breakdown-measure" => "raw",
    "breakdown-rollup" => $"rollup:",
    "breakdown-measure-rollup" => $"rollup:",
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
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MakeJwt(opt.JwtKey, "bench", opt.ReadTenant ?? "bench"));
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

static string TrackPayload(string projectId, string tenantId, string sessionId)
{
    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    return $"{{\"projectId\":\"{projectId}\",\"tenantId\":\"{tenantId}\",\"sessionId\":\"{sessionId}\",\"eventType\":\"bench\",\"metadataJson\":\"{{}}\",\"timestamp\":\"{timestamp}\"}}";
}

// Only carries dimensions and measures when the bench schema is mounted. Sending them to an engine
// that declares none is not harmless: the sanitizer drops each unknown key, counts it and logs it,
// so the write benchmark would be measuring rejection bookkeeping and the run would fill the log
// with drops that look like a defect.
static string BeaconPayload(string projectId, string tenantId, long i, int batch, bool declared)
{
    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    StringBuilder sb = new StringBuilder("[");
    for (int j = 0; j < batch; j++)
    {
        if (j > 0)
        {
            sb.Append(',');
        }
        sb.Append($"{{\"projectId\":\"{projectId}\",\"tenantId\":\"{tenantId}\",\"sessionId\":\"s{i}_{j}\",\"eventType\":\"bench\",\"metadataJson\":\"{{}}\",\"timestamp\":\"{timestamp}\"");
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

static double Percentile(double[] sorted, double p)
{
    if (sorted.Length == 0)
    {
        return 0;
    }
    double rank = p / 100.0 * (sorted.Length - 1);
    int lo = (int)Math.Floor(rank);
    int hi = (int)Math.Ceiling(rank);
    if (lo == hi)
    {
        return sorted[lo];
    }
    double frac = rank - lo;
    return sorted[lo] * (1 - frac) + sorted[hi] * frac;
}

static string MakeJwt(string key, string projectId, string tenantId)
{
    long exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
    string payload = Base64Url(Encoding.UTF8.GetBytes($"{{\"project_id\":\"{projectId}\",\"tenant_id\":\"{tenantId}\",\"exp\":{exp}}}"));
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
    string budget = opt.Duration > 0 ? $"{opt.Duration}s/level" : $"{opt.Requests} reqs/level";
    StringBuilder sb = new StringBuilder();
    sb.AppendLine($"## Run {stamp} — mode=`{opt.Mode}`, target=`{opt.BaseUrl}`, git=`{sha}`");
    sb.AppendLine();
    sb.AppendLine($"- budget: {budget}, events/request: {(opt.Mode == "beacon" ? opt.BeaconBatch : 1)}, verify-loss: {opt.IsWriteMode && opt.VerifyLoss}");
    sb.AppendLine();
    sb.AppendLine("| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (LevelResult r in results)
    {
        string lossCell = r.Stored < 0 ? "n/a" : $"{r.Stored}/{r.EventsSent}" + (r.Stored >= r.EventsSent ? " ✅" : " ❌");
        sb.AppendLine($"| {r.Concurrency} | {r.Ok} | {r.Errors} | {r.Throughput:N0} | {r.EventThroughput:N0} | {r.P50:N2} | {r.P95:N2} | {r.P99:N2} | {r.Max:N1} | {lossCell} | {r.PeakQueue} |");
    }
    return sb.ToString();
}

sealed class LevelResult
{
    public int Concurrency { get; init; }
    public int Ok { get; init; }
    public int Errors { get; init; }
    public double ElapsedSeconds { get; init; }
    public double Throughput { get; init; }
    public double EventThroughput { get; init; }
    public int EventsPerRequest { get; init; }
    public long EventsSent { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Max { get; init; }
    public long PeakQueue { get; init; }
    public long Stored { get; init; }

    public string ConsoleLine()
    {
        string loss = Stored < 0 ? "" : $" stored={Stored}/{EventsSent}";
        return $"  ok={Ok} err={Errors} {Throughput:N0} req/s ({EventThroughput:N0} ev/s) p50={P50:N2}ms p95={P95:N2}ms p99={P99:N2}ms max={Max:N0}ms{loss}";
    }
}

sealed class BenchOptions
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

    public bool Declared { get; init; }
    public string? SeedTenant { get; init; }

    public bool IsWriteMode => Mode == "track" || Mode == "beacon";
    public bool NeedsJwt => !IsWriteMode && Mode != "noop";

    public string Describe() =>
        $"Nealytics bench: mode={Mode} target={BaseUrl} concurrency=[{string.Join(",", Concurrency)}] " +
        (Duration > 0 ? $"duration={Duration}s/level" : $"requests={Requests}/level") +
        $" warmup={Warmup}" + (Mode == "beacon" ? $" beaconBatch={BeaconBatch}" : "");

    public static BenchOptions Parse(string[] args)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        string Env(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;

        return new BenchOptions
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
            ReadTenant = map.GetValueOrDefault("read-tenant", "") is { Length: > 0 } t ? t : null
        };
    }

    static int[] ParseInts(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
           .ToArray();
}
