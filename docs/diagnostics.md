# Diagnostics reference

Every diagnostic the Arith compiler reports carries a stable `ARITHxxxx` code.
The CLI prints them as

```text
file:line:col: error ARITHxxxx: message
```

with 1-based line and column numbers, and continues past each error so one
compile reports as many distinct problems as it can (see
[compiler-design.md §3](compiler-design.md)). Codes are grouped by the stage
that reports them and are never renumbered once released; a retired code stays
reserved.

Process exit codes: `arith build`/`arith run` exit with `1` when the compile
reports errors (or the input cannot be read or written), and a compiled
program exits with `2` when its `main` receives a wrong number of arguments or
an unparsable one ([LANGUAGE_SPEC.md §5.1](../LANGUAGE_SPEC.md)).

## Lexical errors (ARITH1xxx)

| Code | Message | Reported when |
| --- | --- | --- |
| ARITH1001 | `unexpected character '{0}'` | A character cannot start any token, such as `@`, a lone `&` or `\|`, or a smart quote |
| ARITH1002 | `unterminated string literal` | A string literal reaches the end of the line or file without its closing `"` |
| ARITH1003 | `invalid escape sequence '{0}'` | A backslash is followed by anything other than `n`, `r`, `t`, `"`, or `\` |
| ARITH1004 | `unterminated block comment` | A `/*` comment reaches the end of the file |
| ARITH1005 | `invalid suffix '{0}' on numeric literal` | A number is followed by letters that are not a matching type suffix (`10abc`, `10f32`, `1.5i64`) |

## Syntax errors (ARITH2xxx)

| Code | Message | Reported when |
| --- | --- | --- |
| ARITH2001 | `unexpected {0}, expected {1}` | The parser needed a specific token or construct and found something else |
| ARITH2002 | `only a call expression can be used as a statement` | An expression other than a call appears as a statement (`1 + 2;`) |
| ARITH2003 | `trailing comma is not allowed` | A parameter or argument list ends with `,` |

## Semantic errors (ARITH3xxx)

| Code | Message | Reported when |
| --- | --- | --- |
| ARITH3001 | `function '{0}' is already declared` | Two functions share a name |
| ARITH3002 | `'print' is a built-in function and cannot be redeclared` | A function named `print` is declared |
| ARITH3003 | `program must contain a 'main' function` | No `main` is declared |
| ARITH3004 | `'main' must return no value or i32` | `main` declares any other return type |
| ARITH3005 | `'{0}' is not defined` | A variable name is used before or without a declaration in scope |
| ARITH3006 | `function '{0}' is not defined` | A call names a function that does not exist |
| ARITH3007 | *(reserved — never assigned)* | — |
| ARITH3008 | `function '{0}' takes {1} argument(s) but was given {2}` | A call, `print`, or conversion has the wrong number of arguments |
| ARITH3009 | `expected type '{0}' but found '{1}'` | A value's type does not match its context: annotation, assignment target, return type, parameter, condition, or range endpoint |
| ARITH3010 | `operator '{0}' cannot be applied to operands of type '{1}' and '{2}'` | A binary operator's operand types are invalid or mixed (including a compound assignment) |
| ARITH3011 | `operator '{0}' cannot be applied to an operand of type '{1}'` | Unary `-` on a non-numeric or `!` on a non-`bool` |
| ARITH3012 | `integer literal '{0}' is out of range for type '{1}'` | An integer literal does not fit its (inferred or annotated) type |
| ARITH3013 | `'{0}' is already declared in this scope` | A `let` or parameter reuses a name already declared in the same scope |
| ARITH3014 | `cannot return a value from a function with no return type` | `return value;` in a function without `->` |
| ARITH3015 | `function must return a value of type '{0}'` | `return;` in a function that declares a return type |
| ARITH3016 | `not every path through '{0}' returns a value` | A value-returning function has a reachable path without `return` |
| ARITH3017 | `expression does not produce a value` | A call to a function with no return type is used where a value is required |
| ARITH3018 | `loop variable '{0}' cannot be reassigned` | A range-`for` variable is the target of an assignment |
| ARITH3019 | `'{0}' can only be used inside a loop` | `break` or `continue` outside `while`/`for` |
| ARITH3020 | `cannot convert from '{0}' to '{1}'` | An explicit conversion pair is unsupported (`bool` either way, or from `string`) |

The registry itself is [`ErrorCodes.cs`](../src/Arith.Compiler/Diagnostics/ErrorCodes.cs);
a test keeps this table and the registry in sync.
