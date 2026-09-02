# Arith

[![CI](https://github.com/yunabe/arith/actions/workflows/ci.yml/badge.svg)](https://github.com/yunabe/arith/actions/workflows/ci.yml)

Arith is a small programming language that compiles simple arithmetic programs into executable .NET assemblies.

Rather than interpreting expressions one at a time, Arith type-checks the source code and translates it into an assembly containing .NET Common Intermediate Language (CIL, commonly called IL) and metadata. The .NET runtime executes the generated code and its JIT compiler translates the IL into machine code for the target CPU.

The goal of this project is to explore the fundamental stages of a compiler—lexing, parsing, type checking, and code generation—through a small, approachable language.

> [!NOTE]
> The compiler implements all of language v0.1 (architecture in
> [docs/compiler-design.md](docs/compiler-design.md)): `arith build` and
> `arith run` compile and execute every feature in
> [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md).

## Example

```arith
fn sum_range(start: i64, end: i64) -> i64 {
    let total = 0;

    for i in start..end {
        total += i;
    }

    return total;
}

fn main() -> i32 {
    let result = sum_range(1, 11);

    if result > 50 {
        print("large:");
        print(result);
    } else {
        print("small:");
        print(result);
    }

    return 0;
}
```

Expected output:

```text
large:
55
```

## Version 0.1 features

- Primitive `bool`, `i32`, `i64`, `f32`, `f64`, and `string` types
- Functions declared with `fn`, with support for `return`
- Local variables declared with `let`, including reassignment
- Arithmetic, comparison, and logical operators
- `if` / `else`, `while`, and range-based `for` statements
- `break` and `continue`
- A built-in `print` function that prints one value per line
- Explicit numeric conversions
- Checked integer arithmetic
- Generation of a .NET assembly with `main` as its entry point

See [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) for the complete syntax and semantics.

## Commands

```console
arith build hello.arith [-o <dir>]   # compile into a .NET assembly
arith build hello.arith --aot        # compile into a single native executable
arith run hello.arith                # compile and run, forwarding the exit code
```

The source file must be named `<program-name>.arith`, where `<program-name>`
starts with a letter or `_` and contains only letters, digits, `_`, and `-`
(a CLI rule, not part of the language); the outputs are named after it.

`build` produces framework-dependent artifacts:

```text
hello.dll                  the compiled assembly
hello.runtimeconfig.json   names the shared framework for the dotnet host
hello / hello.cmd          convenience launchers
```

The generated program can also be run with the .NET CLI:

```console
dotnet hello.dll
```

With `--aot`, the same emitted IL is instead compiled ahead-of-time by the
official NativeAOT toolchain into one native executable that runs without the
`dotnet` host (requires the platform's native linker, e.g. Xcode Command Line
Tools on macOS; see [docs/il-emission-notes.md](docs/il-emission-notes.md)).

On failure, diagnostics are printed as `file:line:col: error ARITHxxxx: message`.

## Compiler pipeline

```text
Arith source
    ↓ lexing
token stream
    ↓ parsing
abstract syntax tree (AST)
    ↓ name resolution and type checking
typed syntax tree
    ↓ IL and metadata generation
.NET assembly
```

The stages are separate components of the `Arith.Compiler` library, and every stage reports errors with their source locations.

## Development

Building the compiler requires the .NET 10 SDK.

```console
dotnet build                                   # build all projects
dotnet test                                    # run the test suite
dotnet run --project src/Arith.Cli -- version  # run the CLI
```

The `version` command prints the CLI version:

```text
0.1.0
```

The repository is laid out as follows:

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
v0.1 is complete.

Arrays, structs, classes, closures, generics, modules, and `null` are outside the scope of version 0.1 and are candidates for future versions ([LANGUAGE_SPEC.md §13](LANGUAGE_SPEC.md)).
