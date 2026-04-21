## Both benchmarks show allocation increases across all sizes:

ParsingBenchmark

Size	Phase 11	Phase 10	Change
Small	12,080 B	14,360 B	+2,280 B (+18.9%)
Medium	83,515 B	102,363 B	+18,848 B (+22.6%)
Large	376,781 B	462,754 B	+85,973 B (+22.8%)

LintBenchmark

Size	FixEnabled	Phase 11	Phase 10	Change
Small	False	23.36 KB	26.00 KB	+2.64 KB (+11.3%)
Small	True	59.02 KB	61.78 KB	+2.76 KB (+4.7%)
Medium	False	565.44 KB	591.41 KB	+25.97 KB (+4.6%)
Medium	True	4,223.55 KB	4,249.58 KB	+26.03 KB (+0.6%)
Large	False	8,696.70 KB	8,818.78 KB	+122.08 KB (+1.4%)
Large	True	84,522.26 KB	84,643.54 KB	+121.28 KB (+0.1%)

Speed also slightly regressed (Large Parse: ~16.6 ms vs ~16.0 ms baseline).

### Root Cause
StringNode? (Nullable<StringNode>) embeds the entire ~72-byte struct inline in the containing class (Job, Step, Event, etc.), vs the previous 8-byte reference to a heap-allocated StringNode. Each containing class grew significantly, and the reduced GC count from fewer individual allocations doesn't offset the increased object sizes.

### Recommendation
 The struct approach doesn't work for StringNode because it's primarily stored as nullable properties on heap-allocated classes. This is a fundamental mismatch — struct conversion only helps when values are stack-allocated or stored in dense arrays.

-----
