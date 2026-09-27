namespace Nightrunner.Core;

/// <summary>
/// A thread-safe least-recently-used cache bounded by bytes, not entries. Each value is weighed once when it is added;
/// adding past the budget evicts the least recently used entries until the total fits again. The newest entry always
/// stays, even when it alone is over budget, so a caller gets back what it just built.
/// </summary>
/// <remarks>
/// Evicting only drops the cache's reference. Anything still using an evicted value (a viewport drawing it) keeps it
/// alive; the next request for that key builds it again. <see cref="Scope"/> hands out views of the same store whose
/// keys are mapped (the viewer's stock view: a name resolved without mods is never the same entry as the name resolved
/// with them), all sharing one budget.
/// </remarks>
public sealed class ByteBudgetCache<TKey, TValue> where TKey : notnull
{
    private readonly record struct Entry(TKey Key, TValue Value, long Bytes);

    private sealed class Store(long budget, Func<TValue, long> weigh, IEqualityComparer<TKey>? comparer)
    {
        public readonly Lock Lock = new();
        public readonly Dictionary<TKey, LinkedListNode<Entry>> Map = new(comparer);
        public readonly LinkedList<Entry> Lru = new();   // first = most recently used
        public readonly Func<TValue, long> Weigh = weigh;
        public readonly long Budget = budget;
        public long Bytes;
        public long Evictions;
    }

    private readonly Store _s;
    private readonly Func<TKey, TKey>? _scope;

    public ByteBudgetCache(long budget, Func<TValue, long> weigh, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        _s = new Store(budget, weigh, comparer);
    }

    private ByteBudgetCache(Store store, Func<TKey, TKey> scope)
    {
        _s = store;
        _scope = scope;
    }

    /// <summary>
    /// A view of this cache whose keys are stored as <paramref name="scope"/>(key): the same budget, entries and
    /// evictions, but a key asked through the view never meets the same key asked through this cache (unless the
    /// mapping returns it unchanged). The totals (<see cref="Bytes"/>, <see cref="Count"/>, <see cref="Values"/>) are the store's.
    /// </summary>
    public ByteBudgetCache<TKey, TValue> Scope(Func<TKey, TKey> scope) =>
        new(_s, _scope is { } outer ? k => scope(outer(k)) : scope);

    private TKey Key(TKey key) => _scope is { } f ? f(key) : key;

    public long Budget => _s.Budget;

    /// <summary>Bytes currently held, as weighed when each entry was added.</summary>
    public long Bytes { get { lock (_s.Lock) return _s.Bytes; } }

    /// <summary>Entries dropped to stay within the budget since the cache was made.</summary>
    public long Evictions { get { lock (_s.Lock) return _s.Evictions; } }

    public int Count { get { lock (_s.Lock) return _s.Map.Count; } }

    /// <summary>Snapshot of the cached values, most recently used first.</summary>
    public TValue[] Values
    {
        get
        {
            lock (_s.Lock) return _s.Lru.Select(e => e.Value).ToArray();
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        var k = Key(key);
        lock (_s.Lock)
        {
            if (_s.Map.TryGetValue(k, out var node))
            {
                Touch(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// The cached value, or <paramref name="factory"/>'s result added under the key. The factory runs outside the lock,
    /// so two threads asking for the same missing key may both build it; the first one added wins and both get it.
    /// The factory gets the key as asked (not the scoped one).
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGetValue(key, out var hit)) return hit;
        return GetOrAdd(key, factory(key));
    }

    /// <summary>The cached value, or <paramref name="value"/> added under the key.</summary>
    public TValue GetOrAdd(TKey key, TValue value)
    {
        var k = Key(key);
        long bytes = Math.Max(0, _s.Weigh(value));
        lock (_s.Lock)
        {
            if (_s.Map.TryGetValue(k, out var node))
            {
                Touch(node);
                return node.Value.Value;
            }
            _s.Map[k] = _s.Lru.AddFirst(new Entry(k, value, bytes));
            _s.Bytes += bytes;
            while (_s.Bytes > _s.Budget && _s.Lru.Count > 1)
            {
                var last = _s.Lru.Last!;
                _s.Lru.RemoveLast();
                _s.Map.Remove(last.Value.Key);
                _s.Bytes -= last.Value.Bytes;
                _s.Evictions++;
            }
            return value;
        }
    }

    /// <summary>Empty the store (every scope of it).</summary>
    public void Clear()
    {
        lock (_s.Lock)
        {
            _s.Map.Clear();
            _s.Lru.Clear();
            _s.Bytes = 0;
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        if (node != _s.Lru.First)
        {
            _s.Lru.Remove(node);
            _s.Lru.AddFirst(node);
        }
    }
}
