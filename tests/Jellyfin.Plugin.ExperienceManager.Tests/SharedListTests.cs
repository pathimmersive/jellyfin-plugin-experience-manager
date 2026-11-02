using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class SharedListTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();
    private int _builds;
    private Func<IReadOnlyList<int>>? _next;

    private SharedList<int> List() => new(() =>
    {
        var n = Interlocked.Increment(ref _builds);
        return _next?.Invoke() ?? [n];
    }, MaxAge, Debounce, _time);

    [Fact]
    public void TheFirstCallBuildsAndLaterCallsAreServedFromTheCache()
    {
        using var list = List();

        Assert.Equal([1], list.Get(out var first));
        Assert.False(first);
        Assert.Equal([1], list.Get(out var second));
        Assert.True(second);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task AnOldListIsServedWhileItIsRebuiltInTheBackground()
    {
        using var list = List();
        list.Get(out _);

        _time.Advance(MaxAge);
        Assert.Equal([1], list.Get(out var hit));
        Assert.True(hit);

        await list.WhenRebuilt();
        Assert.Equal([2], list.Get(out _));
    }

    [Fact]
    public async Task ALibraryChangeMakesTheListStaleOnlyAfterTheDebounce()
    {
        using var list = List();
        list.Get(out _);

        list.Changed();
        _time.Advance(Debounce - TimeSpan.FromSeconds(1));
        list.Changed();
        _time.Advance(Debounce - TimeSpan.FromSeconds(1));
        list.Get(out _);
        await list.WhenRebuilt();
        Assert.Equal(1, _builds);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([1], list.Get(out _));
        await list.WhenRebuilt();
        Assert.Equal([2], list.Get(out _));
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task AChangeDuringARebuildIsNotLost()
    {
        using var list = List();
        list.Get(out _);
        list.Changed();
        _time.Advance(Debounce);

        using var building = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _next = () =>
        {
            building.Set();
            release.Wait();
            return [99];
        };
        list.Get(out _);
        building.Wait();
        list.Changed();
        _time.Advance(Debounce);
        release.Set();
        await list.WhenRebuilt();

        _next = null;
        list.Get(out _);
        await list.WhenRebuilt();
        Assert.Equal(3, _builds);
    }

    [Fact]
    public async Task AFailedRebuildKeepsServingThePreviousList()
    {
        using var list = List();
        list.Get(out _);
        _next = () => throw new InvalidOperationException("database busy");

        _time.Advance(MaxAge);
        list.Get(out _);
        await list.WhenRebuilt();

        Assert.Equal([1], list.Get(out var hit));
        Assert.True(hit);
    }
}
