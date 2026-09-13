using System.Security.Cryptography;

namespace Rdr2MissionSync;

/// <summary>
/// In-memory mission sync coordinator. It caches fingerprints by save hash
/// (LRU, capacity 16) and re-analyzes the host save after a mission completes
/// so the guest can be told which mission is now active.
/// </summary>
public sealed class MissionSyncService
{
    public const int CacheCapacity = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, MissionFingerprint> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Func<byte[]>? _saveReader;

    private byte[]? _lastSaveBytes;
    private MissionFingerprint? _lastFingerprint;

    /// <summary>
    /// Creates a service. When <paramref name="saveReader"/> is supplied,
    /// <see cref="UpdateAfterMissionComplete"/> reads a fresh save from it;
    /// otherwise the most recently analyzed save is re-analyzed.
    /// </summary>
    public MissionSyncService(Func<byte[]>? saveReader = null)
    {
        _saveReader = saveReader;
    }

    /// <summary>Number of distinct saves currently held in the fingerprint cache.</summary>
    public int CachedSaveCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// Analyzes a save and caches the resulting fingerprint by save hash.
    /// Saves that yield no fingerprint are not cached. The most recently
    /// analyzed save is remembered for <see cref="UpdateAfterMissionComplete"/>.
    /// </summary>
    public MissionFingerprint? Analyze(byte[] saveBytes)
    {
        ArgumentNullException.ThrowIfNull(saveBytes);
        var hash = Convert.ToHexString(SHA256.HashData(saveBytes));
        lock (_gate)
        {
            if (_cache.TryGetValue(hash, out var cached))
            {
                Touch(hash);
                _lastSaveBytes = saveBytes;
                _lastFingerprint = cached;
                return cached;
            }
        }

        var fingerprint = MissionSyncAnalyzer.Analyze(saveBytes);
        if (fingerprint is null)
        {
            return null;
        }

        lock (_gate)
        {
            _lastSaveBytes = saveBytes;
            _lastFingerprint = fingerprint;
            Add(hash, fingerprint.Value);
        }
        return fingerprint;
    }

    /// <summary>
    /// Re-analyzes the host save after <paramref name="missionId"/> completed.
    /// Returns the fingerprint of the next active mission, or <c>null</c> when
    /// no save has been analyzed yet or the new save has no active mission.
    /// Throws <see cref="InvalidOperationException"/> when the completed
    /// mission is not the currently active one.
    /// </summary>
    public MissionFingerprint? UpdateAfterMissionComplete(uint missionId)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (_lastSaveBytes is null || _lastFingerprint is null)
            {
                return null;
            }
            if (_lastFingerprint.Value.MissionId != missionId)
            {
                throw new InvalidOperationException(
                    $"Mission {missionId} is not the active mission ({_lastFingerprint.Value.MissionId}).");
            }
            bytes = _saveReader is not null ? _saveReader() : _lastSaveBytes;
        }
        return Analyze(bytes);
    }

    private void Add(string hash, MissionFingerprint fingerprint)
    {
        if (_cache.ContainsKey(hash))
        {
            Touch(hash);
            _cache[hash] = fingerprint;
            return;
        }
        var node = _lru.AddFirst(hash);
        _lruIndex[hash] = node;
        _cache[hash] = fingerprint;
        while (_cache.Count > CacheCapacity)
        {
            var last = _lru.Last;
            if (last is null)
            {
                break;
            }
            _lru.RemoveLast();
            _lruIndex.Remove(last.Value);
            _cache.Remove(last.Value);
        }
    }

    private void Touch(string hash)
    {
        if (_lruIndex.TryGetValue(hash, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }
}