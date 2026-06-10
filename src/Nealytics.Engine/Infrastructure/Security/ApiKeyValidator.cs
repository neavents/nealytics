namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed partial class ApiKeyValidator
{
    private readonly FrozenSet<string> _validKeys;

    public ApiKeyValidator(IOptions<TelemetryEngineOptions> options, ILogger<ApiKeyValidator> logger)
    {
        string rawKeys = options.Value.AllowedProjectKeys;
        _validKeys = string.IsNullOrWhiteSpace(rawKeys)
            ? FrozenSet<string>.Empty
            : rawKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToFrozenSet(StringComparer.Ordinal);

        if (_validKeys.Count == 0)
        {
            LogNoKeysConfigured(logger);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "No project keys configured. All ingestion requests will be rejected.")]
    private static partial void LogNoKeysConfigured(ILogger logger);

    public bool IsValid(string key) => _validKeys.Contains(key);
}
