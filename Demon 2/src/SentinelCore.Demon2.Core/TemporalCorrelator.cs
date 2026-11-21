namespace SentinelCore.Demon2.Core;

/// <summary>Builds a bounded evidence graph from process, parent, file path, and network identities.</summary>
public sealed class TemporalCorrelator
{
    private readonly IEventStore _store;
    private readonly TimeSpan _window;
    private readonly int _maxBufferedEvents;

    public TemporalCorrelator(IEventStore store, TimeSpan? window = null, int maxBufferedEvents = 4096)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _window = window ?? TimeSpan.FromMinutes(10);
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedEvents);
        _maxBufferedEvents = maxBufferedEvents;
    }

    public async Task<IReadOnlyList<CanonicalEvent>> GetRelatedEventsAsync(CanonicalEvent current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var lowerBound = current.TimestampUtc.Subtract(_window);
        var upperBound = current.TimestampUtc.Add(_window);
        var candidates = (await _store.LoadRecentEventsAsync(lowerBound, _maxBufferedEvents, cancellationToken))
            .Where(x => x.TimestampUtc >= lowerBound && x.TimestampUtc <= upperBound)
            .DistinctBy(EventIdentity)
            .ToArray();

        var related = new Dictionary<string, CanonicalEvent>(StringComparer.Ordinal)
        {
            [EventIdentity(current)] = current
        };
        var processKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domainKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentities(current);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                var identity = EventIdentity(candidate);
                if (related.ContainsKey(identity) || !JoinsKnown(candidate)) continue;
                related[identity] = candidate;
                AddIdentities(candidate);
                changed = true;
            }
        }

        return related.Values.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.SourceEventId, StringComparer.Ordinal).ToArray();

        bool JoinsKnown(CanonicalEvent item)
        {
            var itemProcess = ProcessKeys(item);
            if (itemProcess.Overlaps(processKeys)) return true;
            if (PathKeys(item).Overlaps(pathKeys)) return true;
            return DomainKeys(item).Overlaps(domainKeys);
        }

        void AddIdentities(CanonicalEvent item)
        {
            processKeys.UnionWith(ProcessKeys(item));
            pathKeys.UnionWith(PathKeys(item));
            domainKeys.UnionWith(DomainKeys(item));
        }
    }

    private static HashSet<string> ProcessKeys(CanonicalEvent item)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add("guid", item.ProcessGuid);
        Add("pid", item.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("guid", item.ParentProcessGuid);
        Add("pid", item.ParentProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return keys;

        void Add(string type, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) keys.Add(type + ":" + value.Trim());
        }
    }

    private static HashSet<string> PathKeys(CanonicalEvent item)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(item.ImagePath);
        Add(item.TargetPath);
        if (item.Fields is not null && item.Fields.TryGetValue("targetObject", out var targetObject)) Add(targetObject);
        return keys;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var path = value.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (path.Length > 0) keys.Add("path:" + path);
        }
    }

    private static HashSet<string> DomainKeys(CanonicalEvent item)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(item.RemoteHost);
        if (item.Fields is not null)
        {
            Add(item.Fields.GetValueOrDefault("queryName"));
            Add(item.Fields.GetValueOrDefault("destinationHostname"));
        }
        return keys;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var host = value.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
            if (host.Length > 0) keys.Add("host:" + host);
        }
    }

    private static string EventIdentity(CanonicalEvent item) => item.Source + ":" + item.SourceEventId;
}
