# How `arith experiment build-fib-command` emits a .NET assembly

This note explains what `FibCommandEmitter` actually writes, as background for the
Arith compiler's future code-generation stage. The emitter uses
`System.Reflection.Metadata` (SRM) — the same library the C# compiler uses to write
assemblies — so everything below maps 1:1 to API calls in
[`FibCommandEmitter.cs`](../src/Arith.Cli/Experiments/FibCommandEmitter.cs).

The authoritative reference is ECMA-335 (Common Language Infrastructure), especially
partition II (metadata) and partition III (IL instructions).

## Quickstart

Requires the .NET 10 SDK (the emitted program targets the .NET 10 runtime).

```console
$ dotnet run --project src/Arith.Cli -- experiment build-fib-command out
$ dotnet out/fib.dll 10
fib(10) = 89
$ out/fib 10      # same thing, via the launcher script
fib(10) = 89
```

Four files are written into the output directory (created if missing):

| File | Role |
| --- | --- |
| `fib.dll` | The hand-emitted assembly containing all logic |
| `fib.runtimeconfig.json` | Tells the `dotnet` host to load the .NET 10 shared framework |
| `fib` | POSIX shell launcher (marked executable) wrapping `dotnet fib.dll` |
| `fib.cmd` | The equivalent Windows launcher |

Behavior of the emitted program:

- `fib(0) = fib(1) = 1` and `fib(n) = fib(n - 1) + fib(n - 2)`, hence `fib(10) = 89`.
- Negative inputs fall into the `n < 2` base case and return 1.
- The recursion is intentionally naive, so the cost grows exponentially; inputs
  around 40 and above take noticeably long.
- Anything but exactly one integer argument prints `usage: fib <n>` to stderr and
  exits with code 1.

Note the scope: this experiment emits one fixed, hard-coded program to demonstrate
IL generation. It does not read Arith source code — compiling `.arith` files is the
job of the future `arith build`, which will generalize the techniques shown here.

## 1. What a .NET assembly file is

`fib.dll` is a PE (Portable Executable) file, the same container format as native
Windows binaries. Inside it, the CLR-specific payload has three layers:

```text
PE headers                          ← ManagedPEBuilder
└─ CLI header (entry-point token, flags)
   ├─ Metadata root                 ← MetadataRootBuilder
   │  ├─ #~ stream: the metadata TABLES (Module, TypeDef, MethodDef, ...)
   │  ├─ #Strings: identifier names ("Program", "Fib", "System.Console", ...)
   │  ├─ #US: user strings (string literals used by IL `ldstr`)
   │  ├─ #Blob: signatures and other binary blobs
   │  └─ #GUID: the module version id (MVID)
   └─ IL stream: the method bodies  ← MethodBodyStreamEncoder
```

Metadata is relational: fixed-size rows in tables that reference each other and the
heaps by index. A "token" like `0x06000001` is just table 06 (MethodDef), row 1.
`MetadataBuilder` is essentially an in-memory version of these tables and heaps.

## 2. The tables fib.dll uses

| Table | Rows in fib.dll | Purpose |
| --- | --- | --- |
| Module | `fib.dll` | Module name + MVID |
| Assembly | `fib` | The assembly identity |
| AssemblyRef | `System.Runtime`, `System.Console` | Which assemblies we depend on |
| TypeRef | `Object`, `Int32`, `TextWriter`, `Console` | Types we use from those assemblies |
| MemberRef | `Write`, `WriteLine`, `get_Error`, `TryParse`, ... | Methods we call on TypeRefs (name + signature blob) |
| TypeDef | `<Module>`, `Program` | Types we define |
| MethodDef | `Fib`, `Main` | Methods we define (flags, name, signature, body offset) |
| Param | `n`, `args` | Parameter names (cosmetic; types live in the signature) |
| StandAloneSig | locals of `Main` | Local-variable signature referenced by the method body |

Details worth knowing:

- **Row order is meaning.** A TypeDef does not list its methods; it stores the row
  index of its *first* method, and owns every method up to the next TypeDef's first
  method. The special `<Module>` type must be row 1 of TypeDef. This is why the
  emitter can predict `Fib` = MethodDef row 1 before adding it — and why a real
  compiler typically runs a layout pass that assigns handles before emitting bodies.
- **References are by name, not by index into the other assembly.** A MemberRef is
  (declaring TypeRef, name string, signature blob). The runtime resolves it at JIT
  time. Version numbers on AssemblyRefs are unified to whatever the shared framework
  provides, which is why referencing `System.Runtime 10.0.0.0` just works.
- **A static class is metadata spelling**: `abstract sealed` TypeDef flags.
  **A property getter** is an ordinary method named `get_Error`; the Property table
  is only needed for reflection/tooling, so calling the getter needs no Property row.

## 3. Signatures

Types of parameters, returns, and locals are encoded as compact binary blobs
(ECMA-335 §II.23.2), not as table rows. `BlobEncoder` writes them:

```csharp
// static long Fib(int n)  →  blob: 00 01 0A 08   (DEFAULT, 1 param, I8, I4)
new BlobEncoder(blob).MethodSignature()
    .Parameters(1, r => r.Type().Int64(), p => p.AddParameter().Type().Int32());
```

`out int` is "byref int32" (`p.AddParameter().Type(isByRef: true).Int32()`), and
`string[]` is `SZArray().String()`. Instance methods (like `TextWriter.WriteLine`)
set `isInstanceMethod: true`, which changes both the blob's calling-convention byte
and how the JIT counts the hidden `this` argument.

## 4. Method bodies and IL

IL is a stack machine: instructions push and pop an evaluation stack. Arguments
are pushed in source order (so the last argument sits on top of the stack) and the
call instruction consumes them all. Each method body
in the IL stream is a small header (max stack, code size, locals-signature token)
followed by raw instruction bytes; `MethodBodyStreamEncoder.AddMethodBody` writes
the header and returns the body's offset, which the MethodDef row records.

`Fib` compiles to:

```text
          ldarg.0            // push n
          ldc.i4.2           // push 2
          bge   RECURSE      // if (n >= 2) goto RECURSE
          ldc.i8 1           // push 1L
          ret                // return 1
RECURSE:  ldarg.0
          ldc.i4.1
          sub                // n - 1
          call  long Program::Fib(int32)
          ldarg.0
          ldc.i4.2
          sub                // n - 2
          call  long Program::Fib(int32)
          add
          ret                // return Fib(n-1) + Fib(n-2)
```

Branch targets are byte offsets. `InstructionEncoder` + `ControlFlowBuilder` provide
labels (`DefineLabel` / `MarkLabel` / `Branch`) and patch the offsets afterwards —
the Arith compiler will lean on this for `if` / `while` / `for` lowering.

Other instructions used by `Main`: `ldlen`/`ldelem.ref` (array access), `ldloc`/
`ldloca` (local and address-of-local for the `out` argument), `ldstr` (loads a #US
heap string), and `callvirt` for the virtual `TextWriter.WriteLine`.

The `maxStack` value in the body header is a promise to the JIT about the deepest
evaluation stack. `Fib` peaks at **3** — after the first recursive call its result
stays on the stack while `n` and `2` are pushed for the second one
(`[Fib(n - 1), n, 2]`) — and `Main` peaks at 2. Get it wrong (too small) and the
runtime rejects the method with `InvalidProgramException`.

A trap worth knowing: a body under 64 bytes with no locals and `maxStack <= 8`
is written in the *tiny* header format, which does not record `maxStack` at all
(the runtime assumes 8). `Fib` qualifies, so an understated value would be
silently masked there today and only blow up once the body grows a *fat* header —
for example after adding a few instructions. A code generator should therefore
always compute the true depth rather than relying on what happens to run.

## 5. Putting it together

`ManagedPEBuilder` wraps the metadata and the IL stream in PE headers, stores the
`Main` MethodDef token as the CLI entry point, and sets `CorFlags.ILOnly`. The
result runs with `dotnet fib.dll` plus a `fib.runtimeconfig.json` that names the
shared framework (`Microsoft.NETCore.App` 10.0.0) — the same file layout
`arith build` is planned to produce.

## 6. Inspecting the output

Useful tools to look at the generated file:

```console
dotnet tool install -g ilspycmd
ilspycmd -il path/to/fib.dll       # disassemble the IL
ilspycmd path/to/fib.dll           # decompile back to C#
```

`ILSpy` (GUI) and the `mdv` metadata visualizer from dotnet/metadata-tools show the
raw tables and heaps, which is the fastest way to build intuition for section 2.
