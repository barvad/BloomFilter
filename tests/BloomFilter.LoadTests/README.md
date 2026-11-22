# BloomFilter Load Tests

A separate console project: regular `dotnet test` does not run it, only builds.
The tests check not only the speed but also the invariants: no loss and false
negatives, accuracy of rotation counters, memory window, and allocations.

## Running

```powershell
dotnet run -c Release --project tests/BloomFilter.LoadTests
dotnet run -c Release --project tests/BloomFilter.LoadTests -- all --profile standard
dotnet run -c Release --project tests/BloomFilter.LoadTests -- rotation --profile long --threads 16
```

Exit code: `0` - all checks passed, `1` - invariant violated (or `--min-ops` not reached),
`2` - argument error.

## Scenarios

| Scenario | What it checks |
|---|---|
| `write` | Single-threaded write to `ConcurrentBloomFilter` and to the ring without rotation: throughput, latency, zero allocations, no false negatives |
| `parallel-write` | Parallel write to the ring without rotation: exact unique counter, no loss, no false negatives |
| `read` | Parallel read of a pre-filled ring: throughput and zero false negatives |
| `rotation` | Ring rotation on millions of inserts: rotation counters, active index, memory window for 3 filters |
| `fpr` | Actual false positive rate on a filter and ring filled to capacity |
| `memory` | Ring size vs theoretical size and allocations per operation |

## Parameters

- `--profile quick|standard|long` - volume and duration (default `quick`, about 3 seconds);
  `standard` - about 10 seconds, `long` - minutes and up to several hundred MB of memory.
- `--threads N` - number of threads (default depends on the profile).
- `--min-ops N` - lower bound of throughput for scenarios where it is measured.
- `--list`, `--help`.

## Double Hashing and Small Filters

The double hashing step is made odd: `(h1 + i * (h2 | 1)) % bitCount`.
The size of the bit array is always a multiple of 64, so the odd step guarantees an orbit
of at least 64 positions, and `k` key checks cannot collapse into a pair
of positions even on small filters.

Before this fix, at a capacity of 1000 with a target ratio of `1e-9`, single
false positives were observed on millions of checks (`h2` often had a large common
divisor with `bitCount`). On larger filters, the effect was negligible, but now
the behavior is stable across all sizes.

Changing the position formula is incompatible with filters saved by the old code via
`SaveTo`: the hash scheme version is not available in the format. At the time of the fix, such
files did not exist.
