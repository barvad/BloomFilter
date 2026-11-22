using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BloomFilter
{
    /// <summary>
    /// A thread-safe Bloom filter.
    /// </summary>
    public sealed class ConcurrentBloomFilter
    {
        private readonly ulong[] _bits;
        private readonly ulong _bitCount;
        private readonly int _hashCount;

        /// <summary>
        /// Creates a thread-safe Bloom filter.
        /// </summary>
        /// <param name="expectedItems">Expected number of unique items (e.g., 1_500_000 with a safety margin).</param>
        /// <param name="falsePositiveRate">Acceptable false positive rate (e.g., 0.001 for 0.1%).</param>
        public ConcurrentBloomFilter(long expectedItems, double falsePositiveRate = 0.001)
        {
            if (expectedItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedItems));
            if (falsePositiveRate is <= 0 or >= 1)
                throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

            // Optimal number of bits: m = -(n * ln(p)) / (ln(2)^2)
            double m = -(expectedItems * Math.Log(falsePositiveRate)) / (Math.Log(2) * Math.Log(2));

            // Round up to the nearest multiple of 64 to fit perfectly into ulong[]
            long ulongCount = (long)Math.Ceiling(m / 64.0);
            _bits = new ulong[ulongCount];
            _bitCount = (ulong)ulongCount * 64;

            // Optimal number of hash functions: k = (m / n) * ln(2)
            _hashCount = Math.Max(1, (int)Math.Round((_bitCount / (double)expectedItems) * Math.Log(2)));
        }

        // Private constructor for deserialization from stream/file
        private ConcurrentBloomFilter(ulong[] bits, int hashCount)
        {
            _bits = bits;
            _bitCount = (ulong)bits.Length * 64;
            _hashCount = hashCount;
        }

        /// <summary>
        /// Size of the allocated memory in bytes.
        /// </summary>
        public long SizeInBytes => (long)_bits.Length * sizeof(ulong);

        /// <summary>
        /// Clears the bit map, preserving its size and the number of hash functions.
        /// </summary>
        /// <remarks>
        /// Not thread-safe with respect to concurrent reads: false negatives are possible
        /// during the clearing process. In the ring buffer, this is called only for the filter that
        /// leaves the window, so its data is already outdated.
        /// </remarks>
        public void Reset()
        {
            Array.Clear(_bits);
        }

        /// <summary>
        /// Adds a string (URL) to the filter without heap allocation.
        /// </summary>
        public void Add(ReadOnlySpan<char> item)
        {
            Add(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Adds an arbitrary byte array to the filter.
        /// </summary>
        public void Add(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6); // Division by 64
                ulong mask = 1UL << (int)(bitIndex & 63); // Remainder of division by 64

                // Quick check before atomic write reduces CPU cache load (cache line bouncing)
                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    Interlocked.Or(ref _bits[arrayIndex], mask);
                }
            }
        }

        /// <summary>
        /// Checks the presence of a string (URL).
        /// False - the item is DEFINITELY not there. True - the item is probably there.
        /// </summary>
        public bool MightContain(ReadOnlySpan<char> item)
        {
            return MightContain(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Checks the presence of a byte array.
        /// </summary>
        public bool MightContain(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6);
                ulong mask = 1UL << (int)(bitIndex & 63);

                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Combined method: checks presence and immediately adds if the item was not present.
        /// Returns true if the item was added for the first time (was not seen before).
        /// </summary>
        public bool TryAdd(ReadOnlySpan<char> item)
        {
            return TryAdd(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Atomically checks and adds a byte sequence.
        /// Returns true when the value was added for the first time.
        /// </summary>
        public bool TryAdd(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);
            bool isNew = false;

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6);
                ulong mask = 1UL << (int)(bitIndex & 63);

                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    ulong previous = Interlocked.Or(ref _bits[arrayIndex], mask);
                    if ((previous & mask) == 0)
                    {
                        isNew = true;
                    }
                }
            }

            return isNew;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (ulong h1, ulong h2) ComputeHashes(ReadOnlySpan<byte> data)
        {
            // Obtain 128 bits (two independent 64-bit hashes) in one go
            UInt128 hash = XxHash128.HashToUInt128(data);

            // Make the double hashing step odd. The bit map size is a multiple of 64,
            // so an odd step ensures an orbit of at least 64 positions:
            // k checks cannot collapse into a pair of positions even in small filters.
            return ((ulong)hash, (ulong)(hash >> 64) | 1UL);
        }

        /// <summary>
        /// Saves the filter's state to a stream (e.g., to a file before a restart).
        /// </summary>
        public void SaveTo(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(_hashCount);
            writer.Write(_bits.Length);

            // Write the entire array directly as raw bytes without unnecessary copies
            ReadOnlySpan<byte> rawBytes = MemoryMarshal.AsBytes(_bits.AsSpan());
            stream.Write(rawBytes);
        }

        /// <summary>
        /// Loads the filter from a stream.
        /// </summary>
        public static ConcurrentBloomFilter LoadFrom(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int hashCount = reader.ReadInt32();
            int length = reader.ReadInt32();

            var bits = new ulong[length];
            Span<byte> rawBytes = MemoryMarshal.AsBytes(bits.AsSpan());
            stream.ReadExactly(rawBytes);

            return new ConcurrentBloomFilter(bits, hashCount);
        }
    }
}
