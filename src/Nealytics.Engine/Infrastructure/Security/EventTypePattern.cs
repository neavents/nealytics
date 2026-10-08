namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public sealed class EventTypePattern
{
    private static readonly Regex TokenPattern =
        new(@"^(\*|[A-Za-z][A-Za-z0-9_.:-]{0,127}\*?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FrozenSet<string> _exact;
    private readonly string[] _prefixes;
    private readonly bool _everything;

    private EventTypePattern(FrozenSet<string> exact, string[] prefixes, bool everything)
    {
        _exact = exact;
        _prefixes = prefixes;
        _everything = everything;
    }

    public static EventTypePattern None { get; } = new(FrozenSet<string>.Empty, [], false);

    public bool IsEmpty => !_everything && _exact.Count == 0 && _prefixes.Length == 0;

    public static EventTypePattern Parse(string? raw, string setting)
    {
        string[] tokens = (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return None;
        }

        HashSet<string> exact = new(StringComparer.Ordinal);
        List<string> prefixes = [];
        bool everything = false;

        foreach (string token in tokens)
        {
            if (!TokenPattern.IsMatch(token))
            {
                throw new InvalidOperationException(
                    $"{setting} lists '{token}', which is not an event type or a prefix ending in '*'. "
                    + "Event types start with a letter and contain letters, digits, '_', '.', ':' or '-'.");
            }

            if (token == "*")
            {
                everything = true;
            }
            else if (token.EndsWith('*'))
            {
                prefixes.Add(token[..^1]);
            }
            else
            {
                exact.Add(token);
            }
        }

        return new EventTypePattern(exact.ToFrozenSet(StringComparer.Ordinal), [.. prefixes], everything);
    }

    public bool Matches(string eventType)
    {
        if (_everything || _exact.Contains(eventType))
        {
            return true;
        }

        foreach (string prefix in _prefixes)
        {
            if (eventType.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
