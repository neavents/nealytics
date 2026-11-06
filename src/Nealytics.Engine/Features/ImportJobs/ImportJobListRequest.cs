namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

internal sealed record ImportJobListRequest(int Limit, Guid After)
{
    public static ImportJobListRequest? Parse(IQueryCollection query)
    {
        int limit = 50;
        Guid cursor = Guid.Empty;
        if (query.TryGetValue("limit", out StringValues size)
            && (size.Count != 1 || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > 100))
        {
            return null;
        }
        if (query.TryGetValue("after", out StringValues after)
            && (after.Count != 1 || after[0] is not { Length: 36 }
                || !Guid.TryParseExact(after[0], "D", out cursor) || cursor == Guid.Empty))
        {
            return null;
        }
        return new(limit, cursor);
    }
}
