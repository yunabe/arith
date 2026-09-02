# Arith

Arith is a small programming language that compiles simple arithmetic programs into executable .NET assemblies.

Rather than interpreting expressions one at a time, Arith type-checks the source code and translates it into an assembly containing .NET Common Intermediate Language (CIL, commonly called IL) and metadata. The .NET runtime executes the generated code and its JIT compiler translates the IL into machine code for the target CPU.

The goal of this project is to explore the fundamental stages of a compiler—lexing, parsing, type checking, and code generation—through a small, approachable language.

> [!NOTE]
> The language specification is in place and the compiler is being built stage
> by stage (see [docs/compiler-design.md](docs/compiler-design.md)); lexing and
> parsing are implemented, and name resolution onward is not yet.

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

## Planned commands

The compiler is expected to provide the following command-line interface:

```console
arith build hello.arith
arith run hello.arith
```

For example, `build` will produce framework-dependent artifacts such as:

```text
hello.dll
hello.runtimeconfig.json
```

The generated program can also be run with the .NET CLI:

```console
dotnet hello.dll
```

## Proposed compiler pipeline

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

The initial implementation will keep these stages separate and report errors with their source locations.

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

- `src/Arith.Compiler` — the compiler as a library (currently source text, diagnostics, lexer, and parser; architecture in [docs/compiler-design.md](docs/compiler-design.md))
- `tests/Arith.Compiler.Tests` — xUnit v3 unit tests for the compiler stages
- `src/Arith.Cli` — the `arith` command-line tool (currently `arith version` and `arith experiment build-fib-command`, a code-generation dry run that emits a demo `fib` program as raw IL and metadata, optionally NativeAOT-compiled with `--aot`; see [docs/il-emission-notes.md](docs/il-emission-notes.md))
- `tests/Arith.Cli.Tests` — xUnit v3 tests
- `Directory.Build.props` / `Directory.Packages.props` — shared build settings and centrally managed NuGet package versions
- Build outputs are written to `artifacts/`

## Roadmap

1. Tokens and lexer
2. Abstract syntax tree and recursive-descent parser
3. Name resolution and type checking
4. IL generation for expressions, local variables, and functions
5. IL generation for branches and loops
6. .NET assembly emission and execution
7. Diagnostics and test coverage

Arrays, structs, classes, closures, generics, modules, and `null` are outside the scope of version 0.1.
