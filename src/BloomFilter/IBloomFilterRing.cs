using System;

namespace BloomFilter;

/// <summary>
/// Defines the public operations of a rotating Bloom filter ring.
/// </summary>
public interface IBloomFilterRing
{
    /// <summary>Gets the number of filters in the ring.</summary>
    int FilterCount { get; }

    /// <summary>Gets the unique-value threshold that triggers rotation.</summary>
    long RotationThreshold { get; }

    /// <summary>Gets the active filter index.</summary>
    int ActiveIndex { get; }

    /// <summary>Gets the number of unique values accepted by the active filter.</summary>
    long ActiveUniqueCount { get; }

    /// <summary>Gets the total number of unique values accepted since creation.</summary>
    long TotalUniqueCount { get; }

    /// <summary>Gets the number of completed rotations.</summary>
    long RotationCount { get; }

    /// <summary>Adds a character sequence if it is not already known.</summary>
    bool TryAdd(ReadOnlySpan<char> item);

    /// <summary>Adds a byte sequence if it is not already known.</summary>
    bool TryAdd(ReadOnlySpan<byte> data);

    /// <summary>Adds a character sequence.</summary>
    void Add(ReadOnlySpan<char> item);

    /// <summary>Adds a byte sequence.</summary>
    void Add(ReadOnlySpan<byte> data);

    /// <summary>Checks whether a character sequence may be present.</summary>
    bool MightContain(ReadOnlySpan<char> item);

    /// <summary>Checks whether a byte sequence may be present.</summary>
    bool MightContain(ReadOnlySpan<byte> data);

    /// <summary>Clears the ring and resets its counters.</summary>
    void Clear();
}
