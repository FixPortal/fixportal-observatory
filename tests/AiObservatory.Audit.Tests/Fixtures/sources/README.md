# Synthetic source facts

These fixtures contain invented counters only. No local installation history or transcript content is included.

- Claude: 100 uncached input, 20 output, 60 cache read, 40 total cache creation. The 30 five-minute and 10 one-hour values are subsets of the 40 creation tokens.
- Codex: 100 inclusive input, 60 cached input, 10 inclusive output, and 4 reasoning output. The independent result is 40 uncached input, 60 cache read, 10 output and 4 thoughts. The identical last-usage counters establish the first observed increment; this attributes it to observation time, without asserting an earlier consumption timestamp.

The tests create independent later cumulative Codex facts of 180 input, 100 cache, 30 output and 9 reasoning. Their observed increment is 80 inclusive input (40 uncached plus 40 cache), 20 inclusive output and 5 reasoning. Compatibility retains the final session total on the final observation date.
