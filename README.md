# Arith

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/arith-logo-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/assets/arith-logo.svg">
    <img src="docs/assets/arith-logo.svg" alt="Arith logo: four circles connected by a right-pointing arrow above the ARITH wordmark" width="440">
  </picture>
</p>

[![CI](https://github.com/yunabe/arith/actions/workflows/ci.yml/badge.svg)](https://github.com/yunabe/arith/actions/workflows/ci.yml)

Arith is a small programming language that compiles simple arithmetic programs into executable .NET assemblies.

Rather than interpreting expressions one at a time, Arith type-checks the source code and translates it into an assembly containing .NET Common Intermediate Language (CIL, commonly called IL) and metadata. The .NET runtime executes the generated code and its JIT compiler translates the IL into machine code for the target CPU.

The goal of this project is to explore the fundamental stages of a compiler—lexing, parsing, type checking, and code generation—through a small, approachable language.

> [!NOTE]
> The compiler implements all of language v0.2 — arrays, `main(args:
> []string)`, string conversions, and `f"..."` interpolation on top of v0.1
> — as defined by [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) (architecture in
> [docs/compiler-design.md](docs/compiler-design.md)). Earlier versions are
> preserved at their git tags:
> [v0.1.0](https://github.com/yunabe/arith/releases/tag/v0.1.0).

## Example

```arith
fn average(values: []f64) -> f64 {
    let total = 0.0;
    for value in values {
        total += value;
    }
    return total / f64(len(values));
}

fn main() {
    let scores = [80.0, 92.5, 77.0];
    scores[2] = 88.0;

    let avg = average(scores);
    print(f"average of ${len(scores)} scores = ${avg}");
    if avg >= 85.0 {
        print("high!");
    } else {
        print("keep going");
    }
}
```

Expected output:

```text
average of 3 scores = 86.83333333333333
high!
```

## Version 0.2 features

- Primitive `bool`, `i32`, `i64`, `f32`, `f64`, and `string` types
- Array types `[]T` (nesting included), with literals, a `[value; count]`
  repeat form, indexing, element assignment, and the built-in `len`
- Functions declared with `fn`, with support for `return`
- Local variables declared with `let`, including reassignment
- Arithmetic, comparison, and logical operators
- `if` / `else`, `while`, and `for` over ranges and arrays
- `break` and `continue`
- A built-in `print` function that prints one value per line
- Interpolated strings: `f"x = ${x}"`
- Typed `main` parameters that receive parsed command-line arguments, or
  `main(args: []string)` receiving all of them verbatim
- Explicit numeric conversions, and conversions between `string` and the
  other primitives: `string(x)` and `i64("42")`
- Checked integer arithmetic
- Generation of a .NET assembly with `main` as its entry point

See [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) for the complete syntax and semantics.

## Commands

```console
arith build hello.arith [-o <dir>]   # compile into a .NET assembly
arith build hello.arith --debug     # preserve fault lines and call frames
arith build hello.arith --aot        # compile into a single native executable
arith run hello.arith [args...]      # compile and run, forwarding the exit code
arith run --debug hello.arith        # run with JIT optimizations disabled
```

A `main` may take command-line arguments in one of two forms
([LANGUAGE_SPEC.md §5.1](LANGUAGE_SPEC.md)). With **primitive parameters**,
the program takes exactly one argument per parameter, each parsed to that
parameter's type before `main` runs; a wrong argument count or an unparsable
value prints a usage line and exits with code 2. With **`main(args:
[]string)`** — a single parameter, and the only array type `main` accepts —
the program takes any number of arguments and receives them verbatim, with no
parsing and no usage path; it converts them itself as needed
(`examples/calc.arith` does). With `arith run`, put `--` before values that
start with `-`:

```console
arith run greet.arith 3 hello        # fn main(count: i64, label: string)
arith run calc.arith sum 1 2 3       # fn main(args: []string)
arith run negate.arith -- -5
```

`arith run` forwards standard output and standard error as the program runs,
so progress output is visible before the program exits.

The source file must be named `<program-name>.arith`, where `<program-name>`
starts with a letter or `_` and contains only letters, digits, `_`, and `-`
(a CLI rule, not part of the language); the outputs are named after it.

`build` produces framework-dependent artifacts:

```text
hello.dll                  the compiled assembly
hello.pdb                  Portable PDB: IL offsets mapped to source locations
hello.runtimeconfig.json   names the shared framework for the dotnet host
hello / hello.cmd          convenience launchers
```

The generated program can also be run with the .NET CLI:

```console
dotnet hello.dll
```

Keep the matching `.pdb` beside the `.dll` when moving the output. Both
`arith run` and `dotnet hello.dll` can then include the original `.arith`
file and line in exception stack traces. Use `--debug` when investigating a
runtime failure: it disables JIT optimizations and preserves call frames.
For example, save this as `fault.arith`:

```arith
fn main() {
    let x = 10;
    let y = 0;
    print(x);
    print(x / y);
}
```

`arith run --debug fault.arith` prints `10` followed by:

```text
Unhandled exception. System.DivideByZeroException: Attempted to divide by zero.
   at Program.main() in /path/to/fault.arith:line 5
   at Program.<Main>(String[] args)
```

The PDB records source ranges and a SHA-256 checksum of the input file;
the source itself is not embedded. Without `--debug`, JIT optimization remains
enabled: division, bounds-check, and overflow faults can report the function's
first statement instead of the failing line, and inlining may remove frames.
This is source-location support; debugger local-variable scopes and expression evaluation are not
implemented. See the [PDB emission notes](docs/il-emission-notes.md#7-portable-pdb-and-source-locations).

With `--aot`, the same emitted IL is instead compiled ahead-of-time by the
official NativeAOT toolchain into one native executable that runs without the
`dotnet` host (requires the platform's native linker, e.g. Xcode Command Line
Tools on macOS; see [docs/il-emission-notes.md](docs/il-emission-notes.md)).
Portable PDB output currently applies to managed `build` / `run`; packaging
native debug symbols for `--aot` is a separate follow-up.
`--debug` and `--aot` cannot be combined.

On failure, diagnostics are printed as `file:line:col: error ARITHxxxx: message`;
every code is listed in [docs/diagnostics.md](docs/diagnostics.md).

## Compiler pipeline

```text
Arith source
    ↓ lexing
token stream
    ↓ parsing
abstract syntax tree (AST)
    ↓ name resolution and type checking
typed syntax tree
    ↓ IL, metadata, and sequence-point generation
.NET assembly + Portable PDB
```

The stages are separate components of the `Arith.Compiler` library, and every stage reports errors with their source locations.

## Development

Building the compiler requires the .NET 10 SDK.

```console
dotnet build                                   # build all projects
dotnet test                                    # run the test suite
dotnet run --project src/Arith.Cli -- version  # run the CLI
```

`dotnet test` also verifies generated assemblies with the official
[ILVerify library](https://github.com/dotnet/runtime/tree/main/src/coreclr/tools/ILVerification).
It checks every emitted method, including unused functions and the entry-point
bridge, across all examples and focused cases in normal and `--debug` modes.
The verifier is a test-only NuGet dependency; no global tool installation is
needed. See the [verification notes](docs/il-emission-notes.md#9-verifying-generated-il).

The `version` command prints the CLI version:

```text
0.2.0
```

The repository is laid out as follows:

- `examples/` — runnable example programs, from fizzbuzz to an ASCII Mandelbrot, Conway's Game of Life, and a tail-call experiment (see [examples/README.md](examples/README.md))
- `src/Arith.Compiler` — the compiler as a library (source text, diagnostics, lexer, parser, binder, and IL emitter; architecture in [docs/compiler-design.md](docs/compiler-design.md))
- `tests/Arith.Compiler.Tests` — xUnit v3 unit tests for the compiler stages
- `src/Arith.Cli` — the `arith` command-line tool (`build`, `run`, `version`), including the artifact writer and the NativeAOT packaging behind `build --aot`
- `tests/Arith.Cli.Tests` — xUnit v3 tests
- `Directory.Build.props` / `Directory.Packages.props` — shared build settings and centrally managed NuGet package versions
- Build outputs are written to `artifacts/`

## Roadmap

All stages of the original roadmap — lexer, parser, name resolution and type
checking, IL generation for expressions and control flow, assembly emission
and execution, and diagnostics with test coverage — are implemented; language
v0.2 (arrays, array iteration, `main(args: []string)`, string conversions,
and interpolated strings) is complete.

Structs, classes, closures, generics, modules, and `null` are outside the scope of version 0.2 and are candidates for future versions ([LANGUAGE_SPEC.md §13](LANGUAGE_SPEC.md)). Released versions are recorded in [CHANGELOG.md](CHANGELOG.md).

## License

The Arith compiler and this repository are released under the
[MIT License](LICENSE). Programs you write in Arith are your own; Arith
claims no license over them or over the assemblies compiled from them. Note
that a `--aot` executable embeds .NET runtime components, so distributing
one must additionally comply with the applicable .NET licenses and notices —
see the official [.NET license information](https://github.com/dotnet/core/blob/main/license-information.md).
