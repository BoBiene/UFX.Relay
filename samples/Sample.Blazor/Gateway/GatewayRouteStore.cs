using System.Collections.Immutable;

namespace Sample.Blazor.Gateway;

public sealed class GatewayRouteStore
{
    private readonly object sync = new();
    private ImmutableArray<GatewayRoute> routes =
    [
        new GatewayRoute
        {
            Name = "Sample Internal App",
            PathPrefix = "/internal",
            DestinationBaseUrl = "http://localhost:5600/",
            StripPrefix = true,
            IsEnabled = true
        }
    ];

    public IReadOnlyList<GatewayRoute> GetAll()
    {
        lock (sync)
        {
            return routes;
        }
    }

    public GatewayRoute Upsert(GatewayRoute route)
    {
        GatewayRoute normalized = route with
        {
            PathPrefix = NormalizePrefix(route.PathPrefix),
            DestinationBaseUrl = NormalizeDestinationBaseUrl(route.DestinationBaseUrl)
        };

        lock (sync)
        {
            var index = FindIndexById(routes, normalized.Id);
            if (index >= 0)
            {
                routes = routes.SetItem(index, normalized);
            }
            else
            {
                routes = routes.Add(normalized);
            }
        }

        return normalized;
    }

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            var existing = routes.FirstOrDefault(r => r.Id == id);
            if (existing is null)
            {
                return false;
            }

            routes = routes.Remove(existing);
            return true;
        }
    }

    public bool TryMatch(string requestPath, out GatewayRoute route, out string rewrittenPath)
    {
        string normalizedPath = NormalizeRequestPath(requestPath);

        lock (sync)
        {
            var candidates = routes
                .Where(r => r.IsEnabled)
                .OrderByDescending(r => r.PathPrefix.Length);

            foreach (var candidate in candidates)
            {
                if (!normalizedPath.StartsWith(candidate.PathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nextCharIndex = candidate.PathPrefix.Length;
                var isExact = normalizedPath.Length == nextCharIndex;
                var isSubPath = !isExact && normalizedPath[nextCharIndex] == '/';
                if (!isExact && !isSubPath)
                {
                    continue;
                }

                route = candidate;
                rewrittenPath = candidate.StripPrefix
                    ? NormalizeRequestPath(normalizedPath[nextCharIndex..])
                    : normalizedPath;
                return true;
            }
        }

        route = default!;
        rewrittenPath = string.Empty;
        return false;
    }


    private static int FindIndexById(ImmutableArray<GatewayRoute> source, Guid id)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizePrefix(string prefix)
    {
        var trimmed = string.IsNullOrWhiteSpace(prefix) ? "/" : prefix.Trim();

        if (!trimmed.StartsWith('/'))
        {
            trimmed = '/' + trimmed;
        }

        if (trimmed.Length > 1)
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed;
    }

    private static string NormalizeDestinationBaseUrl(string destinationBaseUrl)
    {
        var trimmed = destinationBaseUrl.Trim();
        if (!trimmed.EndsWith('/'))
        {
            trimmed += "/";
        }

        return trimmed;
    }

    private static string NormalizeRequestPath(string requestPath)
    {
        var normalized = string.IsNullOrWhiteSpace(requestPath) ? "/" : requestPath;
        if (!normalized.StartsWith('/'))
        {
            normalized = '/' + normalized;
        }

        return normalized;
    }
}
