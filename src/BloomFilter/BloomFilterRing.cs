using System.Runtime.InteropServices;

namespace BloomFilter
{
    /// <summary>
    /// A lock-free ring rotator for Bloom filters for concurrent writes and reads.
    /// </summary>
    /// <remarks>
    /// Contains n filters. Writing always goes to the active filter; when it reaches the
    /// threshold of unique values (90% capacity by default), the next filter in the ring becomes active,
    /// and the oldest instance is cleared and reused.
    /// Existence checking is performed across all n filters, so the "memory" window is
    /// up to n * rotationThreshold values.
    ///
    /// The state of the ring is stored in an immutable snapshot (array of filters + active index), which is
    /// published by the CAS winner. Rotation is performed by exactly one thread; the others just continue to write.
    ///
    /// During rotation, the freed filter is cleared in place and reused, so new
    /// instances and bitmaps are not allocated. The cost: at the moment of clearing, a reader that still
    /// holds the old snapshot may get a false negative for the oldest values -
    /// they are leaving the window anyway.
    ///
    /// If the ring managed to rotate between the bit writing and the publishing, the writer notices this
    /// (the snapshot has changed) and repeats the operation on the current snapshot, so the value is not lost.
    ///
    /// The cost of avoiding the lock: with simultaneous insertion of the same value by several
    /// threads, the unique counter may increase more than once (check and insert are not atomic).
    /// Like any Bloom filter, the count is approximate due to false positives.
    /// </remarks>
    public sealed class BloomFilterRing : IBloomFilterRing
    {
        private readonly int _filterCount;
        private readonly long _rotationThreshold;
        private readonly long _expectedItems;
        private readonly double _falsePositiveRate;

        private RingState _state;
        private long _totalUniqueCount;
        private long _rotationCount;

        internal BloomFilterRing(int filterCount, long expectedItems, long rotationThreshold, double falsePositiveRate)
        {
            _filterCount = filterCount;
            _rotationThreshold = rotationThreshold;
            _expectedItems = expectedItems;
            _falsePositiveRate = falsePositiveRate;
            _state = CreateState(activeIndex: 0);
        }

        /// <summary>
        /// The number of filters in the ring (n).
        /// </summary>
        public int FilterCount => _filterCount;

        /// <summary>
        /// The calculated threshold of unique values after which rotation occurs.
        /// </summary>
        public long RotationThreshold => _rotationThreshold;

        /// <summary>
        /// The index of the active filter in the ring.
        /// </summary>
        public int ActiveIndex => Volatile.Read(ref _state).ActiveIndex;

        /// <summary>
        /// The number of unique values accepted by the active filter.
        /// </summary>
        public long ActiveUniqueCount
        {
            get
            {
                RingState state = Volatile.Read(ref _state);
                return Volatile.Read(ref state.ActiveCount);
            }
        }

        /// <summary>
        /// The total number of unique values accepted by the rotator since its creation.
        /// </summary>
        public long TotalUniqueCount => Interlocked.Read(ref _totalUniqueCount);

        /// <summary>
        /// How many times the ring has already switched to the next filter.
        /// </summary>
        public long RotationCount => Interlocked.Read(ref _rotationCount);

        /// <summary>
        /// Adds a string to the active filter if the rotator does not yet recognize this value.
        /// Returns true if the value is encountered for the first time.
        /// </summary>
        public bool TryAdd(ReadOnlySpan<char> item)
        {
            return TryAdd(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Adds a byte array to the active filter if the rotator does not yet recognize this value.
        /// Returns true if the value is encountered for the first time.
        /// </summary>
        public bool TryAdd(ReadOnlySpan<byte> data)
        {
            // On retry, skip the existence check: the value was already written in the previous
            // attempt, so the second time it is not considered "unique" - the counter will increase only once.
            bool retried = false;

            while (true)
            {
                RingState state = Volatile.Read(ref _state);

                if (!retried && state.MightContain(data))
                {
                    return false;
                }

                int active = state.ActiveIndex;
                state.Filters[active].Add(data);

                // If the ring rotated during the write, the bits might have gone to a filter,
                // which is already out of the window. Repeat the operation on the current snapshot.
                if (!ReferenceEquals(Volatile.Read(ref _state), state))
                {
                    retried = true;
                    continue;
                }

                long count = Interlocked.Increment(ref state.ActiveCount);
                Interlocked.Increment(ref _totalUniqueCount);

                if (count >= _rotationThreshold)
                {
                    TryRotate(state);
                }

                return true;
            }
        }

        /// <summary>
        /// Adds a string to the active filter. The return value is ignored.
        /// </summary>
        public void Add(ReadOnlySpan<char> item)
        {
            TryAdd(item);
        }

        /// <summary>
        /// Adds a byte array to the active filter. The return value is ignored.
        /// </summary>
        public void Add(ReadOnlySpan<byte> data)
        {
            TryAdd(data);
        }

        /// <summary>
        /// Checks for the presence of a string in at least one filter of the ring.
        /// False - the element is DEFINITELY not present. True - the element is likely present.
        /// </summary>
        public bool MightContain(ReadOnlySpan<char> item)
        {
            return MightContain(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Checks for the presence of a byte array in at least one filter of the ring.
        /// False - the element is DEFINITELY not present. True - the element is likely present.
        /// </summary>
        public bool MightContain(ReadOnlySpan<byte> data)
        {
            return Volatile.Read(ref _state).MightContain(data);
        }

        /// <summary>
        /// Completely clears the ring, publishing a new empty snapshot.
        /// </summary>
        /// <remarks>
        /// Does not block writers: values added during the call may be lost.
        /// Call when writing is stopped.
        /// </remarks>
        public void Clear()
        {
            while (true)
            {
                RingState current = Volatile.Read(ref _state);
                RingState fresh = CreateState(activeIndex: 0);

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, fresh, current), current))
                {
                    break;
                }
            }

            Interlocked.Exchange(ref _totalUniqueCount, 0);
            Interlocked.Exchange(ref _rotationCount, 0);
        }

        private RingState CreateState(int activeIndex)
        {
            var filters = new ConcurrentBloomFilter[_filterCount];
            for (int i = 0; i < filters.Length; i++)
            {
                filters[i] = new ConcurrentBloomFilter(_expectedItems, _falsePositiveRate);
            }

            return new RingState(filters, activeIndex);
        }

        private void TryRotate(RingState state)
        {
            // For each snapshot, rotation is started by only one winning thread.
            if (Interlocked.CompareExchange(ref state.RotationStarted, 1, 0) != 0)
            {
                return;
            }

            int next = (state.ActiveIndex + 1) % state.Filters.Length;

            // The freed filter is cleared in place and reused: no new bitmaps are allocated.
            // In the old snapshot, no one is writing to it anymore - writers always go to ActiveIndex.
            state.Filters[next].Reset();

            // The array of references does not change, so snapshots can share it.
            // Only the active filter index is changed.
            // CAS, not Volatile.Write: if a parallel Clear() has already published a new snapshot,
            // rotation should not overwrite it with its outdated state.
            var rotated = new RingState(state.Filters, next);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, rotated, state), state))
            {
                Interlocked.Increment(ref _rotationCount);
            }
        }

        private sealed class RingState
        {
            public readonly ConcurrentBloomFilter[] Filters;
            public readonly int ActiveIndex;

            /// <summary>Counter of unique values of the active filter; needed only while the snapshot is current.</summary>
            public long ActiveCount;

            /// <summary>Flag "rotation of this snapshot has already started" (0/1).</summary>
            public int RotationStarted;

            public RingState(ConcurrentBloomFilter[] filters, int activeIndex)
            {
                Filters = filters;
                ActiveIndex = activeIndex;
            }

            public bool MightContain(ReadOnlySpan<byte> data)
            {
                // Go from the active filter to the oldest: recently added values
                // are found with the first check, and rare misses check all n anyway.
                int index = ActiveIndex;

                for (int i = 0; i < Filters.Length; i++)
                {
                    if (Filters[index].MightContain(data))
                    {
                        return true;
                    }

                    index = index == 0 ? Filters.Length - 1 : index - 1;
                }

                return false;
            }
        }
    }
}
