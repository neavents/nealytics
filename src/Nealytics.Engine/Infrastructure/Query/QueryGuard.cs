namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed class QueryGuard
{
    public const int StatusRangeTooWide = StatusCodes.Status400BadRequest;
    public const int StatusTooExpensive = StatusCodes.Status422UnprocessableEntity;

    private readonly string _settings;

    public QueryGuard(IOptions<TelemetryEngineOptions> options)
        : this(options.Value)
    {
    }

    public QueryGuard(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxQueryRangeDays <= 0)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:MaxQueryRangeDays must be positive, but was {options.MaxQueryRangeDays}.");
        }

        if (options.QueryExecutionTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:QueryExecutionTimeoutSeconds must be positive, but was {options.QueryExecutionTimeoutSeconds}.");
        }

        MaxRangeDays = options.MaxQueryRangeDays;
        MaxRange = TimeSpan.FromDays(options.MaxQueryRangeDays);
        ExecutionTimeoutSeconds = options.QueryExecutionTimeoutSeconds;
        _settings = string.Create(
            CultureInfo.InvariantCulture,
            $" SETTINGS max_execution_time = {ExecutionTimeoutSeconds}, timeout_overflow_mode = 'throw'");
    }

    public int MaxRangeDays { get; }

    public TimeSpan MaxRange { get; }

    public int ExecutionTimeoutSeconds { get; }

    public bool Admits(DateTime from, DateTime to) => to - from <= MaxRange;

    public DateTime Floor(DateTime upper) => upper - MaxRange;

    public string Limit(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return string.Concat(sql, _settings);
    }

    public string RangeDetail(DateTime from, DateTime to) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The range from {from:O} to {to:O} spans {(to - from).TotalDays:0.##} days. At most {MaxRangeDays} days are accepted; narrow 'from' and 'to'.");

    public IResult RejectRange(DateTime from, DateTime to) =>
        Results.Problem(
            detail: RangeDetail(from, to),
            statusCode: StatusRangeTooWide,
            title: "Query range too wide");

    public IResult RejectTimeout() =>
        Results.Problem(
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"The query ran past the {ExecutionTimeoutSeconds} second execution limit. Narrow the range, add a filter or group by fewer keys."),
            statusCode: StatusTooExpensive,
            title: "Query exceeded its execution time");
}
