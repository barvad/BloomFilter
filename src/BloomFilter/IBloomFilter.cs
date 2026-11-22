using System;

namespace BloomFilter;

/// <summary>
/// Defines the public operations of a thread-safe Bloom filter.
/// </summary>
public interface IBloomFilter
{
    /// <summary>Gets the allocated memory size in bytes.</summary>
    long SizeInBytes { get; }

    /// <summary>Adds a character sequence to the filter.</summary>
    void Add(ReadOnlySpan<char> item);

    /// <summary>Adds a byte sequence to the filter.</summary>
    void Add(ReadOnlySpan<byte> data);

    /// <summary>Checks whether a character sequence may be present.</summary>
    bool MightContain(ReadOnlySpan<char> item);

    /// <summary>Checks whether a byte sequence may be present.</summary>
    bool MightContain(ReadOnlySpan<byte> data);

    /// <summary>Checks and adds a character sequence.</summary>
    bool TryAdd(ReadOnlySpan<char> item);

    /// <summary>Checks and adds a byte sequence.</summary>
    bool TryAdd(ReadOnlySpan<byte> data);

    /// <summary>Clears the filter while preserving its configuration.</summary>
    void Reset();

    /// <summary>Saves the filter state to a stream.</summary>
    void SaveTo(Stream stream);
}
