namespace Nealytics.Engine.Features.UpsertTenantAttributes;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface ITenantAttributeWriter
{
    Task WriteAsync(
        string projectId, IReadOnlyList<TenantAttributeRow> rows, DateTime updatedAt, CancellationToken cancellationToken);
}
