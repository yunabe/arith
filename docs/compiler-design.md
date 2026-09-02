# Arith compiler design (v0.1)

This document describes the planned architecture of the Arith compiler: how the
code is organized into projects, what the pipeline stages are, which data
structures flow between them, and in what order the pieces should be built.
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md) defines *what* to compile; this document
defines *how*. The IL-emission techniques it builds on are described in
[il-emission-notes.md](il-emission-notes.md).

## 1. Goals and constraints

- Compile Arith v0.1 source files to .NET assemblies (`arith build`) and run
  them (`arith run`), matching the CLI sketched in the README.
- Keep the classic stages — lexing, parsing, name resolution / type checking,
  IL generation — as separate, individually testable components.
- Report errors with source locations, and report *multiple* errors per
  compile: every front-end stage recovers and continues rather than stopping
  at the first error (see section 3).
- No external parser generators or compiler frameworks: a hand-written lexer
  and recursive-descent parser, and `System.Reflection.Metadata` for output,
  as already prototyped by `FibCommandEmitter`.

## 2. Project layout

The compiler core becomes a class library, separate from the CLI:

```text
src/
  Arith.Compiler/            new: the compiler as a library (no CLI dependencies)
    Text/                    SourceText, TextSpan, line/column mapping
    Diagnostics/             Diagnostic, DiagnosticBag, error codes
    Syntax/                  tokens, lexer, AST nodes, parser
    Binding/                 symbols, bound (typed) tree, binder/type checker
    Emit/                    IL + metadata emission (in-memory PE image)
    Compilation.cs           facade tying the stages together
  Arith.Cli/                 existing: `build` / `run` / `version` commands, thin
                             wrappers over Arith.Compiler, plus the artifact
                             writer (dll/runtimeconfig/launchers, AOT packaging)
tests/
  Arith.Compiler.Tests/      new: unit tests per stage
  Arith.Cli.Tests/           existing: CLI and end-to-end tests
```

Rationale: the CLI stays a thin argument-parsing shell, unit tests can target
compiler internals without spawning processes (`InternalsVisibleTo` for
`Arith.Compiler.Tests`), and a future `Arith.Compiler` NuGet package or language
server has a natural home. `FibCommandEmitter` stays where it is as a reference
until the real emitter supersedes it.

## 3. Pipeline overview

```text
string (source)
    ↓  SourceText.From
SourceText
    ↓  Lexer
ImmutableArray<Token>
    ↓  Parser
CompilationUnitSyntax (AST)             ← purely syntactic, no types
    ↓  Binder (two passes)
BoundProgram (bound tree + symbols)     ← every expression carries its Type
    ↓  Emitter
EmitResult (in-memory PE image + diagnostics)
    ↓  CLI artifact writer
<name>.dll + <name>.runtimeconfig.json + launchers
```

Every stage appends to a shared `DiagnosticBag` instead of throwing, and
**multi-diagnostic compilation is the policy**: the front end always runs to
completion so one compile reports as many distinct errors as it can. The
lexer substitutes `Bad` tokens and keeps scanning, the parser builds error
placeholder nodes and resynchronizes, and the binder binds whatever it can —
error tokens and error syntax bind to the `Error` type, which suppresses
predictable cascade diagnostics. The only gate is emission: the `Compilation`
facade skips emit when any *error*-severity diagnostic exists, so the emitter
(and only the emitter) may assume a fully valid, well-typed bound tree.

The untyped AST and the bound tree are deliberately **two separate node
hierarchies** (Roslyn-style) rather than one mutable AST annotated in place:

- The AST mirrors the grammar and keeps trivia-level details (spans, the raw
  text of literals) for diagnostics.
- The bound tree mirrors the *semantics*: identifiers become symbol references,
  every expression node has a resolved `Type`, and syntax-only distinctions can
  already be normalized (e.g. `else if` chains are just nested bound ifs).

For a language this small the duplication is cheap, and it keeps the type
checker honest: the emitter consumes only bound nodes, so it can never
accidentally depend on unchecked syntax.

## 4. Stage design

### 4.1 Text and diagnostics

- `TextSpan` — `(Start, Length)` in UTF-16 code units into the source.
- `SourceText` — the source string, its file path, and a lazily built line map
  so diagnostics can render `file:line:column`.
- `Diagnostic` — severity, an error code (`ARITH1234`-style, stable for tests),
  a message, and a `TextSpan`.
- `DiagnosticBag` — append-only collection threaded through every stage.

Error codes get a single registry (constants plus message templates) so tests
assert codes rather than message strings.

### 4.2 Lexer

Hand-written scanner producing a flat token array (not a lazy stream — Arith
files are small and an array simplifies parser lookahead).

- `Token` — `SyntaxKind`, `TextSpan`, and the raw text slice. **No parsed
  literal values.** An unsuffixed integer literal's type — and therefore its
  valid range — depends on the *expected type* at its use site, and
  `-9223372036854775808` is only valid because the literal directly under unary
  `-` is checked as an unsigned magnitude (spec §4.2). So the lexer records
  where a numeric literal is; the binder parses its value once the expected
  type is known.
- Comments and whitespace are skipped (no trivia preservation in v0.1; the
  token spans are enough for diagnostics).
- Lexical errors (unterminated string or block comment, unknown character, bad
  escape) produce a diagnostic plus a `Bad` token, and scanning continues.
- The lexer ends the array with an `EndOfFile` token so the parser never checks
  bounds.

### 4.3 AST and parser

AST nodes are immutable sealed records deriving from an abstract `SyntaxNode`
with a `Span`. The hierarchy mirrors the EBNF in spec §12: one node type per
production (`FunctionDeclarationSyntax`, `LetStatementSyntax`,
`BinaryExpressionSyntax`, …). Consumers use C# pattern matching (`switch` on
the node type) rather than a visitor hierarchy. Note that C# does **not**
check exhaustiveness over such a hierarchy: a switch over the abstract base
warns (CS8509) even when every current subclass is handled, because the
language has [no closed hierarchies yet](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-15.0/closed-hierarchies.md),
and adding a fallback arm silences new-node omissions instead of reporting
them. So every such switch carries an explicit fallback that throws an
internal-compiler-error exception (e.g. `UnreachableException`), and coverage
of new node kinds is enforced by tests, not by the compiler.

The parser is recursive descent, one method per production, with the
precedence levels of spec §8.5 encoded either as the grammar's cascaded
methods or as a single precedence-climbing loop (implementation detail;
precedence-climbing keeps it compact). Notable spots:

- **Statement dispatch needs two tokens of lookahead**: a statement starting
  with an identifier is an assignment if the next token is `=`/`+=`/…, and must
  be a call statement otherwise. A flat token array makes this trivial.
- `else if` is parsed per the grammar (`else` followed by either a block or a
  nested if-statement) — no special AST node.
- **Error recovery, v1**: on an unexpected token, report one diagnostic and
  synchronize — skip tokens until a statement boundary (`;`, `}`, or a keyword
  that starts a statement), then resume. This yields several useful errors per
  file without the complexity of full recovery. The design permits upgrading
  recovery later without touching the AST shape.

The parser always returns a complete tree (using error placeholder nodes where
needed) so later stages need no null handling. Binding always runs, parse
errors or not, so name and type errors surface alongside syntax errors in the
same compile; error placeholders bind to the `Error` type, which suppresses
cascading diagnostics (see section 3).

### 4.4 Binding and type checking

Binding runs in **two passes over the compilation unit**, because declaration
order is insignificant (spec §1) and functions may be mutually recursive:

1. **Declaration pass** — build the global `FunctionSymbol` table from all
   function declarations: name, parameter symbols/types, return type. Detect
   duplicate function names, a user-declared `print`, and validate the `main`
   entry point (exists, unique, no parameters, returns `void` or `i32`).
2. **Body pass** — bind each function body against the completed table,
   producing a `BoundFunction` per declaration.

Symbols and types:

- `ArithType` — a small closed set (`Bool`, `I32`, `I64`, `F32`, `F64`,
  `String`, plus internal `Void` and `Error`), exposed as singletons.
- `FunctionSymbol`, `ParameterSymbol`, `LocalSymbol`. Locals resolve through a
  scope stack (one scope per `{}` block) implementing shadowing and
  same-scope redeclaration errors per spec §6.
- The `Error` type is assignable to and from everything and silences follow-on
  diagnostics, so one bad expression doesn't cascade into dozens of errors.

Type checking is **bidirectional** where the spec requires it: binding an
expression takes an optional *expected type*, which only affects unsuffixed
numeric literals (spec §4/§7); everything else is bottom-up synthesis with
exact-type matching (no implicit conversions).

Because a literal's expectation can flow through nested expressions
(`let x: i32 = (1 + 2) * 3;` must type every literal as `i32`), the rule is
made deterministic with a *pending* numeric type rather than ad-hoc lookahead:

- An unsuffixed integer (resp. floating-point) literal initially binds as
  `PendingInt` (`PendingFloat`), keeping its raw text.
- Unary `-`, parentheses, and the arithmetic operators propagate pendingness:
  if both operands are pending in the same category, the result stays
  pending; if exactly one operand has a concrete type of the same numeric
  category, that type becomes the expected type of the pending side —
  resolving its literals recursively — and the operator is then checked as
  usual. So `1 + 2i32` and `(1 + 2) + 3i32` are both `i32`.
- **Pending categories never cross.** `PendingInt` resolves only to `i32` or
  `i64`, `PendingFloat` only to `f32` or `f64`. A concrete operand of the
  other numeric category is not a compatible expectation, and an arithmetic
  operator whose two unresolved operands have different pending categories is
  a mixed-numeric-types error, not a merged pending result. So `1 + 2.0f64`,
  `let x: f64 = 1 + 2;`, and `1 + 2.0` are all compile-time errors — Arith
  has no implicit int/float conversion, and literal typing must not smuggle
  one in.
- A *forcing context* resolves a pending expression to a concrete type of its
  own category: a `let` type annotation, the assignment target's type, the
  declared return type at `return`, a call argument's parameter type, the
  concrete-typed other side of a comparison, and positions that require a
  fixed type (`for` range endpoints force `i64`). Where no forcing context
  supplies a type — e.g. `let a = 1 + 2;` or a bare `print(1 + 2);` — the
  category default applies: `i64` for integers, `f64` for floats.
- An explicit conversion's target type is **not** an expected type for its
  operand: `i32(x)` binds `x` with no expectation, applying the category
  default if `x` is still pending, and only then checks/emits the requested
  conversion. So `i32(3000000000)` is an in-range `i64` literal followed by
  an `i64`-to-`i32` conversion — an out-of-range failure follows the
  conversion's *runtime*-error rule (spec §7), and is not a compile-time
  literal-range error — and `f64(1)` is an `i64`-to-`f64` conversion.
- Resolution is the moment of range checking (including the
  unsigned-magnitude rule under unary `-`): `let x: i32 = 3000000000;` errors
  here, not in the lexer or parser.

The resolution order is fixed and purely local — no unification or global
inference — which matches spec §7's "the default literal type is used when a
unique expected type cannot be determined." This machinery belongs to the
*first* binder milestone (section 6, step 4): even
`fn main() -> i32 { return 0; }` depends on it.

Other checks owned by this stage:

- Operator/operand type rules (spec §8), including `+` on strings and the
  `==`/`!=`-only rule for `bool` and `string`.
- Explicit conversion calls: `i32(x)` etc. parse as ordinary calls whose callee
  is a type name; the binder turns them into `BoundConversionExpression` and
  validates the source/target type pair (spec §7).
- `print(x)` binds to a `BoundPrintStatement`-style node with the argument's
  type recorded — the emitter picks the lowering from it (section 4.5).
- `break`/`continue` outside a loop; the loop variable of `for` being
  read-only.
- **Definite return analysis** (spec §5: every reachable path through a
  value-returning function must return). A small conservative recursion over
  the bound tree: a block "definitely returns" if any statement does; an `if`
  does iff both branches exist and do; loops are never counted (their
  conditions are not evaluated at compile time in v0.1).

The output, `BoundProgram`, is the emitter's entire input: the function symbol
table, a bound body per function, and the designated entry point.

### 4.5 IL emission

Generalizes `FibCommandEmitter` from one hard-coded program to the bound tree,
keeping its SRM approach. Structure:

1. **Layout pass** — as il-emission-notes §2 explains, MethodDef handles must
   be predictable before bodies referencing them are written. First assign
   every `FunctionSymbol` its `MethodDefinitionHandle` (row numbers in a fixed
   order), then emit bodies; calls — including recursive and forward calls —
   just look the handle up.
2. **Per-function body emission** — a tree walk over the bound body using
   `InstructionEncoder` + `ControlFlowBuilder` labels:
   - Locals: each `LocalSymbol` gets a slot in the method's locals signature.
     Slots are assigned per function (no reuse across sibling scopes in v0.1 —
     simpler, and the JIT does not care).
   - Control flow lowers directly to labels and branches: `if`/`else` and
     `while` (test at top, back-edge branch) are standard. `break`/`continue`
     branch to the innermost loop's exit/continue labels, tracked on a stack
     during the walk. No separate lowering pass — the language is small
     enough to lower inline; introduce a lowering stage only if this walk
     grows unwieldy.
   - **Range `for` must never increment past the endpoint.** Endpoints are
     `i64` and evaluated once into temps (spec §9.3), so
     `for i in start..=end` with `end == i64.MaxValue` must run the body for
     `MaxValue` and then terminate — a naive
     `while (i <= end) { body; i += 1; }` instead overflows (checked) or
     wraps into an infinite loop (unchecked) after the last iteration. The
     half-open form may use the naive shape safely, because the body only
     runs when `i < end`, so `i + 1` cannot overflow:

     ```text
     ..   :  i = start; goto TEST
             BODY: body            // continue → INC, break → EXIT
             INC:  i = i + 1       // safe: i < end here
             TEST: if i < end goto BODY
             EXIT:
     ```

     The closed form checks the endpoint *after the body, before the
     increment*, and `continue` targets that check — never the increment:

     ```text
     ..=  :  i = start; if i > end goto EXIT
             BODY: body            // continue → CHECK, break → EXIT
             CHECK: if i == end goto EXIT
             i = i + 1             // safe: i < end here
             goto BODY
             EXIT:
     ```

     Both increments are provably non-overflowing, so they emit plain `add`.
   - Short-circuit `&&`/`||` lower to branches (no `and`/`or` on bools with
     side-effecting operands).
   - Checked integer arithmetic uses `add.ovf`/`sub.ovf`/`mul.ovf` (spec §11);
     division/remainder rely on the runtime's `DivideByZeroException`/overflow
     behavior. Numeric conversions use `conv.ovf.*` for float→int and
     int→smaller-int (runtime error on out-of-range/NaN per spec §7) and plain
     `conv.*` where no check is needed.
   - String `==` lowers to a call to the static
     `string.Equals(string, string)` — **ordinal content equality** per spec
     §8.2 — and `!=` to its negation; never to reference equality via `ceq`.
     String concatenation calls `String.Concat`.
   - `string(value)` and numeric `print` must be **culture-invariant**
     (spec §10.1). The typed numeric `Console.WriteLine` /
     `TextWriter.WriteLine` overloads do not qualify: they format through the
     writer's format provider — normally the current culture — so `3.5` could
     print as `3,5`. Numeric values therefore lower to the appropriate
     `ToString(IFormatProvider)` call with `CultureInfo.InvariantCulture`,
     and bool to its (already culture-independent) `Boolean.ToString`;
     `print` shares this convert-to-string lowering with `string(value)` and
     always calls `Console.WriteLine(string)`.
   - **`maxStack` is computed, not guessed**: the walk tracks the simulated
     stack depth and records the true maximum (il-emission-notes §4 explains
     why relying on the tiny-header default is a trap).
3. **Assembly assembly** — metadata tables, entry-point wiring (a `void main`
   still yields exit code 0; an `i32 main`'s return value is the exit code),
   and `ManagedPEBuilder`. The emitter's product is an **in-memory PE image**
   inside an `EmitResult` (section 4.6); it does not touch the file system.
   Writing `<name>.dll`, `runtimeconfig.json`, and the POSIX/Windows
   launchers is the CLI artifact writer's job, and a future `--aot` mode
   packages the same `EmitResult` bytes via `NativeAotPublisher` — so there
   is exactly one IL-generation path.

### 4.6 Compilation facade and CLI

```csharp
SyntaxTree syntaxTree = SyntaxTree.Parse(sourceText);       // lex + parse only
Compilation compilation = Compilation.Create(syntaxTree);   // bind
EmitResult result = compilation.Emit(assemblyName);
// EmitResult: Success, Diagnostics (all stages), PE image bytes when Success
```

`SyntaxTree.Parse` stops at syntax (as the name promises), `Compilation` owns
semantics, and `Emit` returns the PE image as bytes plus the accumulated
diagnostics instead of writing files. The compiler library never touches the
output directory; a CLI-side **artifact writer** turns an `EmitResult` into
on-disk artifacts, and every packaging mode consumes the same bytes:

- `arith build <file.arith> [-o <dir>]` — compile; on failure print each
  diagnostic as `file:line:col: error ARITHxxxx: message` and exit 1; on
  success write `<name>.dll`, `<name>.runtimeconfig.json`, and the launchers.
- `arith run <file.arith>` — build into a temp/cache directory, then execute
  via the `dotnet` host (reusing `ProcessRunner`), forwarding the exit code.
- A future `arith build --aot` hands the same `EmitResult` bytes to
  `NativeAotPublisher`: AOT is packaging, not a second emission path.
- `arith experiment build-fib-command` remains until the real pipeline covers
  it, then can be retired.

## 5. Testing strategy

- **Lexer**: token-kind/span/text expectations per snippet, including every
  error case (unterminated string, bad escape, stray character).
- **Parser**: assert the AST shape via a compact S-expression-style dump helper
  (`(fn main (block (return (int 0))))`) so tests stay readable; plus
  diagnostics tests for recovery cases.
- **Binder**: the highest-value suite. Positive cases assert bound types;
  negative cases assert *error code + span*. A small annotated-source helper
  (markers in the test source designating the expected span) keeps these
  terse. Every "compile-time error" sentence in LANGUAGE_SPEC.md should map to
  at least one test here.
- **Emitter/end-to-end**: compile a `.arith` source, run the output with the
  `dotnet` host, assert stdout and exit code — in `Arith.Cli.Tests`, reusing
  `CliRunner`/`ProcessRunner`. Cover the runtime-error contract too (overflow,
  division by zero, bad conversions → nonzero exit, message on stderr).
- Spec §11's evaluation-order guarantees get targeted end-to-end tests
  (side-effecting call order in arguments and operands), and so does the
  closed-range endpoint rule — e.g.
  `for i in (9223372036854775807 - 2)..=9223372036854775807` must run
  exactly three iterations and terminate.

## 6. Implementation order

Follow the README roadmap, but reach a running end-to-end slice as early as
possible — emission problems (signatures, maxStack, entry-point wiring) are
the riskiest part and should not wait until last:

1. **Infrastructure**: `Arith.Compiler` project, `SourceText`, `TextSpan`,
   diagnostics.
2. **Lexer** — complete (it is small), with tests.
3. **AST + parser** — complete grammar, minimal (sync-point) error recovery,
   with tests.
4. **Binder, subset**: function tables, locals, `i64`/`i32` arithmetic,
   `let`, `return`, calls, `print` — including the pending-literal
   expected-type machinery of section 4.4, which even
   `fn main() -> i32 { return 0; }` needs.
5. **Emitter for that subset + `arith build`/`run`** → first `.arith` file
   runs end to end.
6. **Control flow**: `if`/`while`/`for`/`break`/`continue`, logical operators,
   definite-return analysis.
7. **Remaining types and conversions**: `bool`/`f32`/`f64`/`string` rules
   (extending the pending-literal machinery to floats), explicit conversions,
   string concatenation and ordinal equality.
8. **Hardening**: diagnostics polish, spec-coverage test sweep, README update,
   retire the experiment command.

Steps 4–7 each extend binder + emitter + tests together, keeping the compiler
runnable at every step.
