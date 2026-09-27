using Nightrunner.Core;

namespace Nightrunner.Tests;

// The viewport's session caches are bounded by bytes, least recently used first out.
public class ByteBudgetCacheTests
{
    private static ByteBudgetCache<string, byte[]> Make(long budget) =>
        new(budget, b => b.LongLength, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void StaysWithinBudgetAndEvictsLeastRecentlyUsed()
    {
        var c = Make(300);
        c.GetOrAdd("a", new byte[100]);
        c.GetOrAdd("b", new byte[100]);
        c.GetOrAdd("c", new byte[100]);
        Assert.True(c.TryGetValue("A", out _));       // a is now the most recent; b is the oldest
        c.GetOrAdd("d", new byte[100]);
        Assert.Equal(300, c.Bytes);
        Assert.Equal(3, c.Count);
        Assert.False(c.TryGetValue("b", out _));
        Assert.True(c.TryGetValue("a", out _));
        Assert.True(c.TryGetValue("c", out _));
        Assert.True(c.TryGetValue("d", out _));
        Assert.Equal(1, c.Evictions);
    }

    [Fact]
    public void KeepsTheNewestEntryEvenWhenItAloneIsOverBudget()
    {
        var c = Make(50);
        c.GetOrAdd("small", new byte[10]);
        var big = c.GetOrAdd("big", new byte[500]);
        Assert.Equal(500, big.Length);
        Assert.Equal(1, c.Count);
        Assert.True(c.TryGetValue("big", out _));
    }

    [Fact]
    public void FirstValueAddedWinsAndTheFactoryIsSkippedOnAHit()
    {
        var c = Make(1000);
        var first = c.GetOrAdd("k", new byte[1]);
        var second = c.GetOrAdd("k", new byte[2]);
        Assert.Same(first, second);
        int calls = 0;
        c.GetOrAdd("k", _ => { calls++; return new byte[3]; });
        Assert.Equal(0, calls);
        Assert.Equal(1, c.Bytes);
    }

    [Fact]
    public void NullValuesAreCachedAtZeroBytes()
    {
        var c = new ByteBudgetCache<string, byte[]?>(10, b => b?.LongLength ?? 0);
        Assert.Null(c.GetOrAdd("missing", _ => null));
        Assert.True(c.TryGetValue("missing", out var v));
        Assert.Null(v);
        Assert.Equal(0, c.Bytes);
    }

    [Fact]
    public void ConcurrentAddsNeverExceedTheBudget()
    {
        var c = Make(64 * 1024);
        Parallel.For(0, 10_000, i => c.GetOrAdd((i % 500).ToString(), _ => new byte[1024]));
        Assert.True(c.Bytes <= c.Budget);
        Assert.Equal(c.Count * 1024L, c.Bytes);
    }
}
