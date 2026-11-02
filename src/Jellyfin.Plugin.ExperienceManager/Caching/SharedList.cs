using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExperienceManager.Caching;

/// <summary>
/// A list that is the same for everyone, built once and shared. When it goes stale, by age or
/// because the library changed, callers keep getting the previous list while one rebuild runs in
/// the background; only the very first call waits for a build.
/// </summary>
/// <typeparam name="T">The list's element type.</typeparam>
public sealed class SharedList<T> : IDisposable
{
    private readonly Func<IReadOnlyList<T>> _build;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ITimer _debounceTimer;
    private IReadOnlyList<T>? _items;
    private DateTimeOffset _builtAt;
    private bool _stale;
    private long _changes;
    private Task? _rebuild;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedList{T}"/> class.
    /// </summary>
    /// <param name="build">Builds the list; it runs at most once at a time.</param>
    /// <param name="maxAge">How long a built list is served before it is rebuilt in the background.</param>
    /// <param name="debounce">How long after the last change the list is marked stale.</param>
    /// <param name="time">The clock and timers, replaceable in tests.</param>
    public SharedList(Func<IReadOnlyList<T>> build, TimeSpan maxAge, TimeSpan debounce, TimeProvider? time = null)
    {
        _build = build;
        _maxAge = maxAge;
        _debounce = debounce;
        _time = time ?? TimeProvider.System;
        _debounceTimer = _time.CreateTimer(_ => MarkStale(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Gets the list.
    /// </summary>
    /// <param name="hit">Whether the list came from the cache rather than a build this call waited for.</param>
    /// <returns>The current list, or the previous one while a rebuild runs.</returns>
    public IReadOnlyList<T> Get(out bool hit)
    {
        lock (_gate)
        {
            if (_items is not null)
            {
                if ((_stale || _time.GetUtcNow() - _builtAt >= _maxAge) && _rebuild is null)
                {
                    _rebuild = Task.Run(Rebuild);
                }

                hit = true;
                return _items;
            }
        }

        // Cold: the first caller builds, others wait for the same build.
        lock (_build)
        {
            lock (_gate)
            {
                if (_items is not null)
                {
                    hit = true;
                    return _items;
                }
            }

            var changes = Interlocked.Read(ref _changes);
            var built = _build();
            Store(built, changes);
            hit = false;
            return built;
        }
    }

    /// <summary>
    /// Records that the underlying data changed. The list is marked stale once changes have stopped
    /// for the debounce interval, so a library scan causes one rebuild, not one per item.
    /// </summary>
    public void Changed() => _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Builds the list now if it has never been built. Used to warm the cache at startup.
    /// </summary>
    public void Warm() => Get(out _);

    /// <summary>
    /// Waits for a background rebuild, if one is running. For tests.
    /// </summary>
    /// <returns>A task that completes when no rebuild is running.</returns>
    internal Task WhenRebuilt()
    {
        lock (_gate)
        {
            return _rebuild ?? Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _debounceTimer.Dispose();

    private void MarkStale()
    {
        lock (_gate)
        {
            _stale = true;
            _changes++;
        }
    }

    // A change that arrived while the list was being built leaves it stale, so it is built again.
    private void Store(IReadOnlyList<T> built, long changesAtStart)
    {
        lock (_gate)
        {
            _items = built;
            _builtAt = _time.GetUtcNow();
            _stale = _changes != changesAtStart;
        }
    }

    private void Rebuild()
    {
        try
        {
            var changes = Interlocked.Read(ref _changes);
            IReadOnlyList<T> built;
            lock (_build)
            {
                built = _build();
            }

            Store(built, changes);
        }
        catch (Exception)
        {
            // The previous list keeps being served; the next call tries again.
        }
        finally
        {
            lock (_gate)
            {
                _rebuild = null;
            }
        }
    }
}
