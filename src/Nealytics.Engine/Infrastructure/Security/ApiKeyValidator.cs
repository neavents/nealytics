namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed class ApiKeyValidator
{
    private readonly FrozenSet<string> _validKeys;

    public ApiKeyValidator(IOptions<TelemetryEngineOptions> options)
    {
        string rawKeys = options.Value.AllowedProjectKeys;
        _validKeys = string.IsNullOrWhiteSpace(rawKeys)
            ? FrozenSet<string>.Empty
            : rawKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToFrozenSet(StringComparer.Ordinal);
    }

    public bool IsValid(string key) => _validKeys.Contains(key);
}
