namespace Nealytics.Engine.Features.UpsertTenantAttributes;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed class ClickHouseTenantAttributeWriter : ITenantAttributeWriter
{
    internal static readonly string InsertCommand =
        "INSERT INTO " + TenantAttributeRegistry.QualifiedTable
        + " (project_id, attribute, tenant_id, value, updated_at) VALUES";

    private readonly ClickHouseConnectionFactory _connectionFactory;

    public ClickHouseTenantAttributeWriter(ClickHouseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task WriteAsync(
        string projectId, IReadOnlyList<TenantAttributeRow> rows, DateTime updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int count = rows.Count;
        string[] projects = new string[count];
        string[] attributes = new string[count];
        string[] tenants = new string[count];
        string[] values = new string[count];
        DateTimeOffset[] stamps = new DateTimeOffset[count];
        DateTimeOffset stamp = TelemetryInsertMath.ToClickHouseTimestamp(updatedAt);

        for (int i = 0; i < count; i++)
        {
            TenantAttributeRow row = rows[i];
            projects[i] = projectId;
            attributes[i] = row.Attribute;
            tenants[i] = row.TenantId;
            values[i] = row.Value;
            stamps[i] = stamp;
        }

        Dictionary<string, object?> columns = new(StringComparer.Ordinal)
        {
            ["project_id"] = projects,
            ["attribute"] = attributes,
            ["tenant_id"] = tenants,
            ["value"] = values,
            ["updated_at"] = stamps,
        };

        await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);

        try
        {
            await using ClickHouseColumnWriter writer =
                await lease.Connection.CreateColumnWriterAsync(InsertCommand, cancellationToken);

            await writer.WriteTableAsync(columns, count, cancellationToken);
            await writer.EndWriteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lease.Discard();
            throw;
        }
    }
}
