using System.Data;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public static class ClickHouseTestSupport
{
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("TelemetryEngine__ClickHouseConnectionString")
        ?? "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;";

    public static async Task<long> CountAsync(string projectId)
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM nealytics_core.global_events WHERE project_id = {p:String}";
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "p", Value = projectId });
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public static async Task ExecuteAsync(string sql)
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static Task TruncateEventsAsync() =>
        ExecuteAsync("TRUNCATE TABLE IF EXISTS nealytics_core.global_events");

    public static Task OptimizeFinalAsync() =>
        ExecuteAsync("OPTIMIZE TABLE nealytics_core.global_events FINAL");
}
