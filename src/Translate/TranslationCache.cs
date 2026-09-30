using System;
using System.Collections.Generic;
using System.Linq;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>翻译结果内存缓存（LRU）。相同文本不重复请求。</summary>
    public sealed class TranslationCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<Entry>> _map;
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private readonly object _gate = new object();

        private sealed class Entry
        {
            public string Key;
            public string Value;
        }

        public long Hits { get; private set; }
        public long Misses { get; private set; }

        public TranslationCache(int capacity)
        {
            _capacity = Math.Max(16, capacity);
            _map = new Dictionary<string, LinkedListNode<Entry>>(_capacity, StringComparer.Ordinal);
        }

        public bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    value = node.Value.Value;
                    Hits++;
                    return true;
                }
                Misses++;
                return false;
            }
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value.Value = value;
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                    return;
                }

                var entry = new Entry { Key = key, Value = value };
                var node = _lru.AddFirst(entry);
                _map[key] = node;

                while (_map.Count > _capacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
                Hits = 0;
                Misses = 0;
            }
        }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public double HitRate
        {
            get
            {
                lock (_gate)
                {
                    long total = Hits + Misses;
                    return total == 0 ? 0 : (double)Hits / total;
                }
            }
        }
    }
}
