namespace Stringline.Graph;

/// <summary>
/// Keeps several graphs on one clock (S21, Fred: boards locked in time): whenever one navigator's time range or follow
/// mode changes, by a pan, a zoom, a preset, follow or the clock moving on, the others take it on with
/// <see cref="GraphNavigator.MatchTime"/>. Each keeps its own corridor range, size and orientation. Not thread-safe;
/// the UI thread owns it, as it owns the navigators.
/// </summary>
public sealed class GraphTimeLink : IDisposable
{
    private readonly List<GraphNavigator> _navigators = [];
    private bool _syncing;

    /// <summary>The navigators linked, in the order they were added.</summary>
    public IReadOnlyList<GraphNavigator> Navigators => _navigators;

    /// <summary>Link a navigator. It takes on the time of those already linked.</summary>
    public void Add(GraphNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        if (_navigators.Contains(navigator))
        {
            return;
        }

        if (_navigators.Count > 0)
        {
            Sync(_navigators[0], navigator);
        }

        _navigators.Add(navigator);
        navigator.Changed += OnChanged;
    }

    /// <summary>Unlink a navigator; it keeps the time it shows.</summary>
    public void Remove(GraphNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        if (_navigators.Remove(navigator))
        {
            navigator.Changed -= OnChanged;
        }
    }

    public void Dispose()
    {
        foreach (var navigator in _navigators)
        {
            navigator.Changed -= OnChanged;
        }

        _navigators.Clear();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_syncing || sender is not GraphNavigator source)
        {
            return;
        }

        foreach (var other in _navigators)
        {
            if (!ReferenceEquals(other, source))
            {
                Sync(source, other);
            }
        }
    }

    private void Sync(GraphNavigator source, GraphNavigator target)
    {
        _syncing = true;
        try
        {
            target.MatchTime(source);
        }
        finally
        {
            _syncing = false;
        }
    }
}
