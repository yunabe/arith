# Changelog

All notable changes to Arith are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The language version is defined by
[LANGUAGE_SPEC.md](LANGUAGE_SPEC.md); the compiler version is printed by
`arith version` and matches the git tag.

## [Unreleased]

### Added

- Verify generated IL during ordinary tests using the official ILVerify
  library, covering all examples and focused cases in normal/debug modes,
  uncalled methods, and the entry-point bridge. Corrupted-image tests check
  that invalid stack operations, return types, and `maxStack` are rejected.
- Emit a matching Portable PDB alongside managed assemblies, mapping IL
  offsets back to `.arith` source ranges. `arith run` uses these symbols
  too, so runtime stack traces can include source paths and line numbers.
- Preserve source spans through binding and pending-literal resolution;
  record document SHA-256 checksums from the original bytes, including BOMs.
  Generated loop machinery and implicit returns have hidden sequence
  points, and the entry-point bridge has no source mapping. Document names are
  preserved verbatim in the library and resolved to absolute paths by the CLI.
- Add `--debug` to managed `build` / `run` to disable JIT optimizations and
  insert IL source boundaries, preserving fault lines and call frames.
  Optimization remains enabled by default; `--debug --aot` is rejected, and
  NativeAOT debug-symbol packaging is not included.

### Fixed

- Report `ARITH2005` ("nesting is too deep") instead of crashing the process
  with a stack overflow when expressions or statements nest more than 256
  levels deep — parentheses, unary operators, call arguments, array
  literals, index chains, array types, `if` bodies, `else if` chains, and
  nested interpolated strings included. Flat operator chains such as
  `1 + 1 + … + 1` no longer count as nesting: the binder, pending-literal
  resolution, and the emitter walk them iteratively, so a 15,000-term sum
  compiles and prints `15000`.
- Emit repeat-array fill loops with the condition before the body, satisfying
  ILVerify's backward-branch rule when enclosing expressions leave operands
  on the stack (for example, `grid[0] = [value; count]`).
- Parse `f32` literals directly at single precision, avoiding double rounding
  for both suffixed literals and literals with an expected `f32` type.
- Stop interpolation-hole scanning at its closing brace and reuse the line
  boundary, avoiding quadratic work for many holes on the same line.
- Forward `arith run` stdout and stderr during execution with bounded buffers,
  instead of retaining all output until exit. When writing or flushing an
  output sink throws, terminate the child process to avoid leaving it blocked
  on an undrained pipe. Suppressed broken-pipe errors do not trigger this cleanup.

## [0.2.0] - 2026-09-06

The complete implementation of language version 0.2, which adds arrays and
string tooling on top of 0.1 (whose specification is preserved at the
`v0.1.0` tag).

### Language

- Array types `[]T` for every element type, nesting included (`[][]i64` is
  a jagged array). Arrays are fixed-length reference values with mutable
  elements; the types are structural.
- Array creation: the element-list form `[1, 2, 3]` and the repeat form
  `[value; count]` (the one value is shared by every slot — significant
  when elements are arrays). An expected array type propagates its element
  type into both forms, recursively; an empty `[]` requires one. A negative
  repeat count is a runtime error.
- Indexing `a[i]` with `i64` indexes, chaining (`grid[i][j]`,
  `rows(3)[0]`), and element assignment — plain and compound — through any
  index expression; out-of-range indexes are runtime errors, for reads and
  writes alike.
- The built-in `len(array)` — the first expression-valued built-in; like
  `print`, the name cannot be redeclared.
- `for x in array` iterates elements in index order; the loop variable has
  the element type and, like a range loop's, is body-scoped and read-only.
- `fn main(args: []string)` receives all command-line arguments verbatim —
  any count, no parsing, no usage exit. `[]string` must be `main`'s only
  parameter, and no other array type may appear on `main`.
- Conversions from `string` to every other primitive type (`i64(s)`,
  `bool(s)`, …), parsing with exactly the `main`-argument grammar; failures
  are runtime errors. Conversions never involve arrays.
- Interpolated strings `f"x = ${x}"` — exactly equivalent to the
  concatenation `"x = " + string(x)`, holes taking any primitive
  expression, with nesting, block comments inside holes, and the `\$`
  escape (a bare `$` is a compile-time error).
- Assignment targets generalized: the left side is a variable or any index
  expression (a chain may be rooted at a call result).

### Compiler and CLI

- New diagnostics ARITH1006, ARITH2004, and ARITH3021–ARITH3027, and the
  ARITH3002 message now names the redeclared built-in; all listed in
  docs/diagnostics.md.
- Emitted array code keeps the checked-semantics discipline: repeat counts
  narrow with `conv.ovf.u` (negative counts fault before allocation),
  indexes with `conv.ovf.i`; bool elements store as bytes and load
  zero-extended; nested-array allocations name their element type through
  the TypeSpec metadata table.
- The entry-point bridge passes the runtime's `string[]` straight through
  to a `main(args: []string)`.
- Five new examples: `calc.arith` (an `args`-driven calculator),
  `matmul.arith` (jagged-array matrix multiplication), `sort.arith`
  (in-place insertion sort through shared array storage), `sieve.arith`
  (a `[]bool` Sieve of Eratosthenes), and `life.arith` (Conway's Game of
  Life on a jagged `[][]bool` board); all run in CI like every example.

## [0.1.0] - 2026-09-03

The first complete implementation of language version 0.1.

### Language

- Primitive types `bool`, `i32`, `i64`, `f32`, `f64`, and `string`, with
  expected-type inference for unsuffixed numeric literals and no implicit
  conversions.
- Functions, recursion, local variables with shadowing, `if`/`else`,
  `while`, range-based `for` (`..` and `..=`), `break`, and `continue`.
- Arithmetic, comparison, equality, and short-circuit logical operators;
  checked integer arithmetic; ordinal string equality and concatenation.
- Explicit conversions between numeric types and from any primitive to
  `string`; the built-in `print`.
- Typed `main` parameters that receive parsed command-line arguments, with a
  generated usage line and exit code 2 on bad input.

### Compiler and tooling

- `arith build` emits a framework-dependent .NET assembly (plus
  `runtimeconfig.json` and launchers) directly with
  `System.Reflection.Metadata`; `arith build --aot` produces a single native
  executable through the NativeAOT toolchain; `arith run` compiles and runs
  in one step.
- Multi-diagnostic compilation: every stage recovers and continues, and
  diagnostics carry stable `ARITHxxxx` codes ([docs/diagnostics.md](docs/diagnostics.md)).
- An `examples/` directory of ten programs, each pinned by an end-to-end
  test, including a measured tail-call experiment.
- CI on Linux, macOS, and Windows.

[Unreleased]: https://github.com/yunabe/arith/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/yunabe/arith/releases/tag/v0.2.0
[0.1.0]: https://github.com/yunabe/arith/releases/tag/v0.1.0
