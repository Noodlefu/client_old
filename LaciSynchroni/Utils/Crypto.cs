using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LaciSynchroni.Utils;

public static class Crypto
{
    /// <summary>Maximum number of entries in each hash cache before old entries are evicted.</summary>
    private const int MaxCacheSize = 10000;

    private static readonly BoundedCache<(string, ushort), string> _hashListPlayersSHA256 = new(MaxCacheSize);
    private static readonly BoundedCache<string, string> _hashListSHA256 = new(MaxCacheSize, StringComparer.Ordinal);

    public static string GetFileHash(this string filePath)
    {
        // Use FileShare.Read to allow other readers but fail if a writer has the file open
        // This prevents computing a hash on a partially-written file
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hashBytes = SHA1.HashData(fs);
        return Convert.ToHexString(hashBytes);  // Single allocation, returns uppercase hex
    }

    public static string GetHash256(this (string, ushort) playerToHash)
    {
        if (_hashListPlayersSHA256.TryGetValue(playerToHash, out var hash) && hash != null)
            return hash;

        var inputBytes = Encoding.UTF8.GetBytes(playerToHash.Item1 + playerToHash.Item2.ToString());
        return _hashListPlayersSHA256.AddOrUpdate(playerToHash, Convert.ToHexString(SHA256.HashData(inputBytes)));
    }

    public static string GetHash256(this string stringToHash)
    {
        return GetOrComputeHashSHA256(stringToHash);
    }

    private static string GetOrComputeHashSHA256(string stringToCompute)
    {
        if (_hashListSHA256.TryGetValue(stringToCompute, out var hash) && hash != null)
            return hash;

        var inputBytes = Encoding.UTF8.GetBytes(stringToCompute);
        return _hashListSHA256.AddOrUpdate(stringToCompute, Convert.ToHexString(SHA256.HashData(inputBytes)));
    }

    /// <summary>
    /// A simple bounded cache that evicts entries when the capacity is exceeded.
    /// Thread-safe for concurrent access.
    /// </summary>
    private sealed class BoundedCache<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TValue> _cache;
        private readonly ConcurrentQueue<TKey> _accessOrder;
        private readonly int _maxSize;
        private readonly Lock _evictionLock = new();

        public BoundedCache(int maxSize, IEqualityComparer<TKey>? comparer = null)
        {
            _maxSize = maxSize;
            _cache = comparer != null
                ? new ConcurrentDictionary<TKey, TValue>(comparer)
                : new ConcurrentDictionary<TKey, TValue>();
            _accessOrder = new ConcurrentQueue<TKey>();
        }

        public bool TryGetValue(TKey key, out TValue? value)
        {
            return _cache.TryGetValue(key, out value);
        }

        public TValue AddOrUpdate(TKey key, TValue value)
        {
            _cache[key] = value;
            _accessOrder.Enqueue(key);

            // Evict if over capacity
            if (_cache.Count > _maxSize)
            {
                EvictOldEntries();
            }

            return value;
        }

        private void EvictOldEntries()
        {
            // Only one thread should evict at a time
            if (!_evictionLock.TryEnter())
                return;

            try
            {
                // Evict until we're under 90% capacity to avoid frequent evictions
                var targetSize = (int)(_maxSize * 0.9);
                while (_cache.Count > targetSize && _accessOrder.TryDequeue(out var oldKey))
                {
                    _cache.TryRemove(oldKey, out _);
                }
            }
            finally
            {
                _evictionLock.Exit();
            }
        }
    }
}
