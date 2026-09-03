# Changelog

All notable changes to Arith are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The language version is defined by
[LANGUAGE_SPEC.md](LANGUAGE_SPEC.md); the compiler version is printed by
`arith version` and matches the git tag.

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

[0.1.0]: https://github.com/yunabe/arith/releases/tag/v0.1.0
