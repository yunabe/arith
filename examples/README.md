# Arith examples

Small programs showing what language v0.1 can express — and what the
generated code actually does at runtime. Run any of them with:

```console
dotnet run --project src/Arith.Cli -- run examples/<name>.arith [args...]
```

Every example is also executed by `ExampleProgramsTests`, so they always
compile and produce the outputs shown here.

| Example | Try | Shows |
| --- | --- | --- |
| `fib.arith` | `fib.arith 10` → `fib(10) = 89` | Naive double recursion (exponential — try 35) |
| `factorial.arith` | `factorial.arith 20` → `20! = 2432902008176640000` | **Checked arithmetic**: `21` faults instead of wrapping |
| `tailsum.arith` | `tailsum.arith 100000` | **Tail-call experiment** — see below |
| `gcd.arith` | `gcd.arith 252 105` → `gcd(252, 105) = 21` | Euclid's algorithm; two typed `main` arguments |
| `primes.arith` | `primes.arith 30` | Trial division, `while` + early `return` |
| `fizzbuzz.arith` | `fizzbuzz.arith 15` | The classic; remainders and `else if` chains |
| `collatz.arith` | `collatz.arith 27` → `… 111 steps` | `3n + 1` loop; a famous open problem |
| `pow.arith` | `pow.arith 3 13` → `3^13 = 1594323` | Exponentiation by squaring via `/ 2` and `% 2` (no bitwise ops needed) |
| `pi.arith` | `pi.arith 1000` → `pi ~= 3.141592653340544 …` | Nilakantha series; invariant float output |
| `mandelbrot.arith` | `mandelbrot.arith` | ASCII Mandelbrot: nested loops, `f64`, building rows by string concatenation |

## The tail-call experiment (`tailsum.arith`)

`sum(n, acc)` calls itself in tail position, but the Arith emitter does not
emit the IL `tail.` prefix — so whether deep recursion survives depends
entirely on what the backend does with a self-call followed by `ret`.
Measured on .NET 10, arm64 macOS:

| Run | Result |
| --- | --- |
| `arith run tailsum.arith 100000` | completes — 100k frames fit the default stack |
| `arith run tailsum.arith 1000000` | **StackOverflow** — the JIT did *not* apply its implicit tail-call optimization |
| `arith build tailsum.arith --aot`, then `tailsum 100000000` | **completes** — ILC's whole-program optimizer turns the self tail call into a loop |

The same IL, two backends, two answers: the JIT keeps one stack frame per
call, while NativeAOT effectively rewrites the recursion into iteration.
The exact overflow *boundary* is platform-dependent — it varies with the
platform's stack size (default .NET thread stacks differ across
Linux/macOS/Windows) and the JIT's heuristics — so the automated tests
avoid it: they check correctness at a small depth and pin only the
platform-independent extreme, a depth so large that without tail-call
optimization it must overflow on any configuration. Probing where *your*
machine's boundary lies is the manual part of the experiment. Emitting the
`tail.` prefix from the compiler would make the JIT behavior deterministic
— a nice future experiment.

## Other things worth trying

- `factorial.arith 21` — checked `i64` multiplication faults with
  `System.OverflowException` rather than printing a wrapped wrong answer.
- `fib.arith 35` under `arith run` vs. an `--aot` build — startup time
  dominates the JIT path for short runs (measured numbers in
  [docs/il-emission-notes.md](../docs/il-emission-notes.md) §6).
- `pi.arith 1000000` — three more digits for a thousand times the work.
