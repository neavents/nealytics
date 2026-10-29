using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public sealed class IngestionDurabilityContractTests
{
    [Theory]
    [InlineData("?k=unit-key-1", null, HttpStatusCode.Accepted)]
    [InlineData("", "unit-key-1", HttpStatusCode.Accepted)]
    [InlineData("?k=wrong", "unit-key-1", HttpStatusCode.Accepted)]
    [InlineData("?k=unit-key-1", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("?k=wrong", null, HttpStatusCode.Unauthorized)]
    public async Task TrackPreservesCredentialPrecedenceAndDurableAcknowledgment(
        string query, string? header, HttpStatusCode expected)
    {
        await using ContractFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid eventId = Guid.NewGuid();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track" + query)
        {
            Content = new StringContent(EventJson(eventId), Encoding.UTF8, "application/json")
        };
        if (header is not null)
        {
            request.Headers.Add("X-Project-Key", header);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        WriteAheadLogger wal = factory.Services.GetRequiredService<WriteAheadLogger>();
        TelemetryChannelBroker broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
        if (expected == HttpStatusCode.Accepted)
        {
            Assert.Equal(1, wal.UncommittedRecordCount);
            Assert.True(broker.Reader.TryRead(out GlobalTelemetryPayload? published));
            Assert.Equal(eventId, published!.EventId);
            await AssertDurableAsync(factory, eventId);
        }
        else
        {
            Assert.Equal(0, wal.UncommittedRecordCount);
            Assert.False(broker.Reader.TryRead(out _));
            Assert.Empty(await File.ReadAllBytesAsync(factory.WalPath));
        }
    }

    [Fact]
    public async Task BeaconSkipsInvalidElementsAndPublishesOnlyDurableValidEvents()
    {
        await using ContractFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid first = Guid.NewGuid();
        Guid last = Guid.NewGuid();
        string body = "[" + EventJson(first) + ",null,{\"projectId\":\"p\"}," + EventJson(last) + "]";

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/telemetry/beacon?k=unit-key-1",
            new StringContent(body, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        TelemetryChannelBroker broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
        Assert.True(broker.Reader.TryRead(out GlobalTelemetryPayload? one));
        Assert.True(broker.Reader.TryRead(out GlobalTelemetryPayload? two));
        Assert.Equal(first, one!.EventId);
        Assert.Equal(last, two!.EventId);
        Assert.False(broker.Reader.TryRead(out _));
        await AssertDurableAsync(factory, first, last);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamedBeaconKeepsItsDurablePrefixAfterMalformedTailOrDisconnect(bool disconnect)
    {
        await using ContractFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WriteAheadLogger wal = factory.Services.GetRequiredService<WriteAheadLogger>();
        using TcpClient socket = new();
        await socket.ConnectAsync(client.BaseAddress!.Host, client.BaseAddress.Port);
        using NetworkStream stream = socket.GetStream();
        Guid eventId = Guid.NewGuid();
        string prefix = "[" + EventJson(eventId) + "," + new string(' ', 65_536);
        string headers = "POST /api/v1/telemetry/beacon?k=unit-key-1 HTTP/1.1\r\n"
            + "Host: localhost\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await WriteChunkAsync(stream, prefix);
        await WaitForAsync(() => wal.UncommittedRecordCount == 1);

        if (disconnect)
        {
            socket.Close();
        }
        else
        {
            await WriteChunkAsync(stream, "broken]");
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            using StreamReader reader = new(stream, Encoding.ASCII);
            string? status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("400", status);
        }

        TelemetryChannelBroker broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
        await WaitForAsync(() => broker.Reader.Count == 1);
        Assert.True(broker.Reader.TryRead(out GlobalTelemetryPayload? published));
        Assert.Equal(eventId, published!.EventId);
        await AssertDurableAsync(factory, eventId);
    }

    [Fact]
    public async Task DisconnectedIncompleteTrackDoesNotCreateAWalRecord()
    {
        await using ContractFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WriteAheadLogger wal = factory.Services.GetRequiredService<WriteAheadLogger>();
        using TcpClient socket = new();
        await socket.ConnectAsync(client.BaseAddress!.Host, client.BaseAddress.Port);
        NetworkStream stream = socket.GetStream();
        string request = "POST /api/v1/telemetry/track?k=unit-key-1 HTTP/1.1\r\n"
            + "Host: localhost\r\nContent-Length: 500\r\nContent-Type: application/json\r\n\r\n"
            + "{\"projectId\":\"p\",";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        socket.Client.Shutdown(SocketShutdown.Send);
        using StreamReader reader = new(stream, Encoding.ASCII);
        try
        {
            await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Kestrel may reset an incomplete request instead of returning a response.
        }

        using HttpResponseMessage health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Empty(await File.ReadAllBytesAsync(factory.WalPath));
        Assert.Equal(0, wal.UncommittedRecordCount);
    }

    private static string EventJson(Guid id) =>
        $$"""{"eventId":"{{id}}","projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""";

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task ConfigurableBeaconGroupingPreservesTheExistingHttpAndDurabilityContract(int records)
    {
        await using ContractFactory factory = new(records);
        using HttpClient client = factory.CreateClient();
        Guid[] ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        string body = "[" + string.Join(",", ids.Select(EventJson)) + "]";
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/telemetry/beacon?k=unit-key-1", new StringContent(body, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        WriteAheadLogger wal = factory.Services.GetRequiredService<WriteAheadLogger>();
        Assert.Equal(8, wal.FlushedRecordCount);
        Assert.InRange(wal.FlushGroupCount, 1, 8);
        if (records == 1)
        {
            Assert.Equal(8, wal.FlushGroupCount);
        }
        TelemetryChannelBroker broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
        List<Guid> published = [];
        while (broker.Reader.TryRead(out GlobalTelemetryPayload? payload))
        {
            published.Add(payload.EventId);
        }
        Assert.Equal(ids, published);
        await AssertDurableAsync(factory, ids);
    }

    private static async Task WriteChunkAsync(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("X") + "\r\n"));
        await stream.WriteAsync(bytes);
        await stream.WriteAsync("\r\n"u8.ToArray());
        await stream.FlushAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task AssertDurableAsync(ContractFactory factory, params Guid[] expected)
    {
        string[] lines = await File.ReadAllLinesAsync(factory.WalPath);
        Guid[] actual = lines.Select(line => JsonSerializer.Deserialize(
            line, TelemetryAotContext.Default.GlobalTelemetryPayload)!.EventId).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal((byte)'\n', (await File.ReadAllBytesAsync(factory.WalPath))[^1]);
    }

    private sealed class ContractFactory : NoDatabaseWebFactory
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-contract-" + Guid.NewGuid().ToString("N"));
        private readonly string _backend = Environment.GetEnvironmentVariable("NEALYTICS_TEST_WAL_BACKEND") ?? "managed";
        private readonly int _records;

        public ContractFactory(int records = 64)
        {
            _records = records;
            UseKestrel(0);
        }

        public string WalPath => Path.Combine(_directory, "telemetry_wal.log");

        public new HttpClient CreateClient()
        {
            HttpClient client = base.CreateClient();
            Assert.Equal(_backend, Services.GetRequiredService<WriteAheadLogger>().Selection.Backend);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor[] processors = services.Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && (descriptor.ImplementationType == typeof(TelemetryBatchProcessor)
                        || descriptor.ImplementationType == typeof(ClickHouseSchemaMigrator))).ToArray();
                foreach (ServiceDescriptor processor in processors)
                {
                    services.Remove(processor);
                }
                services.PostConfigure<TelemetryEngineOptions>(options =>
                {
                    options.WriteAheadLogDirectory = _directory;
                    options.WalIoBackend = _backend;
                    options.BeaconWalBatchRecords = _records;
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}
