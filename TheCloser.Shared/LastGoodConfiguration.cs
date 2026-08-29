using Microsoft.Extensions.Configuration;

namespace TheCloser.Shared;

// Per-activation value-copy snapshot of the live configuration root. The pipeline always
// receives this snapshot, never the live root: under reloadOnChange a failed reload can empty
// the live providers, and retained IConfigurationSection references are live views that would
// go empty with them. The build-vs-reload overlap race is accepted with a one-activation blast
// radius (see the fix design's Configuration section).
internal sealed class LastGoodConfiguration
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private IConfiguration _snapshot = new ConfigurationBuilder().Build();
    private bool _emptyWarningLogged;
    private bool _populated;

    public IConfiguration Refresh(IConfiguration liveRoot, Action<string> log)
    {
        var pairs = liveRoot.AsEnumerable().ToList();

        if (pairs.Count > 0)
        {
            _emptyWarningLogged = false;

            if (ValuesMatch(pairs))
            {
                return _snapshot;
            }

            _values.Clear();

            foreach (var pair in pairs)
            {
                _values[pair.Key] = pair.Value;
            }

            _snapshot = new ConfigurationBuilder().AddInMemoryCollection(_values).Build();
            _populated = true;
        }
        else if (_populated && !_emptyWarningLogged)
        {
            log("Configuration reload produced an empty root; keeping the last good snapshot.");
            _emptyWarningLogged = true;
        }

        return _snapshot;
    }

    private bool ValuesMatch(IReadOnlyCollection<KeyValuePair<string, string?>> pairs)
    {
        if (!_populated || pairs.Count != _values.Count)
        {
            return false;
        }

        return pairs.All(pair => _values.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }
}
