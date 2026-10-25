namespace LinearTv.Builder.Library;

/// <summary>
/// Turns a path as the Jellyfin server sees it into one this machine can open:
/// <c>/mnt/tank/media/Films/x.mkv</c> with <c>/mnt/tank/media=\\TRUENAS\media</c> becomes
/// <c>\\TRUENAS\media\Films\x.mkv</c>. The longest matching prefix wins. With no mapping that
/// matches, the path is used as it is.
/// </summary>
internal sealed class PathMap
{
    private readonly List<(string From, string To)> _rules;

    public PathMap(IEnumerable<string> rules)
    {
        _rules = rules
            .Select(rule =>
            {
                var at = rule.IndexOf('=', StringComparison.Ordinal);
                if (at <= 0)
                {
                    throw new ArgumentException($"A path mapping is server-path=local-path, not '{rule}'.");
                }

                return (From: rule[..at].TrimEnd('/', '\\'), To: rule[(at + 1)..].TrimEnd('/', '\\'));
            })
            .OrderByDescending(r => r.From.Length)
            .ToList();
    }

    public string ToLocal(string serverPath)
    {
        foreach (var (from, to) in _rules)
        {
            if (serverPath.StartsWith(from, StringComparison.Ordinal)
                && (serverPath.Length == from.Length || serverPath[from.Length] is '/' or '\\'))
            {
                var rest = serverPath[from.Length..].TrimStart('/', '\\')
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                return rest.Length == 0 ? to : to + Path.DirectorySeparatorChar + rest;
            }
        }

        return serverPath;
    }
}
