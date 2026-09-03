# Arith Language Specification

This document defines version 0.1 of the Arith programming language.

## 1. General rules

- Arith source files use the `.arith` extension.
- Source files are encoded as UTF-8.
- Only function declarations are allowed at the top level.
- Function declaration order is insignificant. A function may be called before its declaration.
- A local variable may be referenced only after it has been declared.
- Statements generally end with a semicolon (`;`).
- Names are case-sensitive.
- Expressions are evaluated from left to right.

## 2. Lexical structure

### 2.1 Identifiers

An identifier begins with an ASCII letter or `_`, followed by zero or more ASCII letters, digits, or `_` characters.

```text
[A-Za-z_][A-Za-z0-9_]*
```

### 2.2 Keywords

```text
fn       let      return
if       else     while
for      in       break     continue
true     false
bool     i32      i64       f32       f64       string
```

### 2.3 Comments

Arith supports line comments and block comments.

```arith
// A comment that continues to the end of the line

/*
   A block comment
*/
```

Block comments cannot be nested.

## 3. Types

Arith version 0.1 provides the following types:

| Type | Meaning | Corresponding .NET type |
| --- | --- | --- |
| `bool` | Boolean value | `System.Boolean` |
| `i32` | 32-bit signed integer | `System.Int32` |
| `i64` | 64-bit signed integer | `System.Int64` |
| `f32` | IEEE 754 single-precision floating-point number | `System.Single` |
| `f64` | IEEE 754 double-precision floating-point number | `System.Double` |
| `string` | UTF-16 string | `System.String` |

A function either returns a value of one of these types or returns no value. A function that returns no value omits the `->` return type clause and corresponds to .NET `void`. `void` is not a type name in Arith source and cannot be used for a variable or parameter.

Arith has no `null` value. A `string` value is always non-null.

## 4. Literals

### 4.1 Boolean literals

```arith
true
false
```

Boolean literals have type `bool`.

### 4.2 Integer literals

Arith supports decimal integer literals.

```arith
0
42
10i32
10i64
```

- An integer literal without a suffix has the default type `i64`.
- The `i32` and `i64` suffixes explicitly select a type.
- When an expected integer type is available, an unsuffixed literal may take that type if its value can be represented exactly.
- Leading zeros are allowed and carry no meaning: `007` is the value `7`.

```arith
let x: i32 = 10;       // Treated as i32

fn main() -> i32 {
    return 0;           // Treated as i32
}
```

An integer literal outside the range of its type is a compile-time error. A negative number is parsed as unary `-` applied to a positive literal, not as a single negative literal. To make the minimum value of each integer type expressible, an integer literal directly beneath unary `-` is checked as an unsigned magnitude.

### 4.3 Floating-point literals

Arith supports decimal floating-point literals containing a decimal point.

```arith
3.14
1.5f32
1.5f64
```

- A floating-point literal without a suffix has the default type `f64`.
- The `f32` and `f64` suffixes explicitly select a type.
- When an expected floating-point type is available, an unsuffixed literal may take that type.

Scientific notation and literal forms for `NaN` and infinity are not supported in version 0.1. (Command-line arguments to `main` follow a separate, more permissive grammar; see Section 5.1.)

### 4.4 String literals

A string literal is enclosed in double quotes.

```arith
"hello"
"hello\nworld"
```

The following escape sequences are supported:

| Sequence | Meaning |
| --- | --- |
| `\n` | Line feed |
| `\r` | Carriage return |
| `\t` | Horizontal tab |
| `\"` | Double quote |
| `\\` | Backslash |

## 5. Functions

A function is declared with `fn`.

```arith
fn add(a: i64, b: i64) -> i64 {
    return a + b;
}

fn greet() {
    print("hello");
}
```

- A parameter is written as `name: type`.
- A function that returns a value specifies its type with `-> type`.
- User-defined function overloading is not supported.
- Declaring more than one function with the same name is a compile-time error.
- Every reachable path through a value-returning function must return a value.
- `return;` may be used in a function that returns no value.
- Functions cannot be nested.
- Recursive calls are allowed.

### 5.1 Entry point

An executable program must contain exactly one `main` function. `main` either returns no value or returns `i32`; any other return type is a compile-time error. If it returns `i32`, that value becomes the process exit code. A `main` function with no return value produces an exit code of `0`.

`main` may declare parameters of any primitive type. Each parameter receives one command-line argument, converted from text before `main` runs:

```arith
fn main(count: i64, label: string) {
    for i in 0..count {
        print(label);
    }
}
```

- The program must be invoked with exactly one argument per parameter, in order.
- Numeric arguments are parsed culture-invariantly: integers as decimal digits with an optional leading sign, and floating-point values in decimal notation (an exponent is allowed). A value outside the parameter type's finite range — including a floating-point value whose exponent overflows to infinity — fails to parse, and the `NaN` and `Infinity` spellings are not accepted.
- This argument grammar is deliberately not the source-literal grammar of Section 4: an exponent such as `2.5e2` is accepted as an argument although it is not a valid source literal, and conversely `Infinity` and `NaN` are rejected as arguments although `print` can produce them (Section 7) — a printed non-finite value cannot be fed back in as an argument.
- `bool` arguments must be `true` or `false`, ASCII case-insensitive.
- `string` arguments are passed through unchanged.
- Leading and trailing white space is ignored in non-`string` arguments.

If the argument count is wrong or an argument fails to parse, the program prints a usage line describing the expected parameters to standard error and exits with code `2` without running `main`.

## 6. Variables and scope

A local variable is declared with `let`.

```arith
let count = 0;
let limit: i32 = 10;
```

- If the type annotation is omitted, the type is inferred from the initializer.
- An initializer is required.
- Local variables may be reassigned.
- A value of a different type cannot be assigned to a variable.
- A name cannot be redeclared in the same scope.
- An inner scope may declare a name already used in an outer scope. The inner declaration shadows the outer declaration.
- A function parameter is a reassignable variable in the scope of that function body.

Every function body and control-flow body enclosed in braces creates a new lexical scope.

```arith
let x = 10;

if x > 0 {
    let message = "positive";
    print(message);
}

// message is not available here
```

## 7. Conversions

Arith does not perform implicit conversions between distinct numeric types. The expected-type behavior for unsuffixed numeric literals described in Section 4 is the only exception.

An expected type may come from a type-annotated initializer, an assignment, a `return` statement, a function parameter, or the other operand of a binary operator. An expected type only applies to a literal of the same numeric category: an expected integer type affects only integer literals, and an expected floating-point type affects only floating-point literals. The default literal type is used when a unique expected type cannot be determined.

The target type of an explicit conversion does not provide an expected type to its operand. The operand is typed on its own — using the default literal type if nothing else determines it — and the conversion is then applied to that value. For example, `i32(3000000000)` converts the `i64` value `3000000000` to `i32` and therefore produces a runtime error, and `f64(1)` converts the `i64` value `1` to `f64`.

A type name acts as a built-in conversion function for explicit numeric conversions.

```arith
let small: i32 = 10;
let large = i64(small);
let value = f64(large) / 4.0;
```

The following numeric conversions are supported:

- Any integer type to any integer type
- Any integer type to any floating-point type
- Any floating-point type to any floating-point type
- Any floating-point type to any integer type

Converting a floating-point value to an integer type discards the fractional part, rounding toward zero: `i64(1.9)` is `1` and `i64(-1.9)` is `-1`, and a value exactly between two integers is not rounded away from zero either (`i64(2.5)` is `2`).

Converting to an integer produces a runtime error if the result is out of range. Converting `NaN` or infinity to an integer also produces a runtime error. Conversions that lose precision are allowed. Conversions between `bool` and a numeric type are not supported.

Any primitive value may be converted to a string with `string(value)`; the formatting is culture-independent:

- A `bool` value converts to `"true"` or `"false"` — the language's own literal spellings.
- An integer converts to its decimal digits, with a leading `-` when negative.
- A floating-point value converts to the shortest decimal string that converts back to exactly the same value (.NET's round-trip formatting): `string(0.1 + 0.2)` is `"0.30000000000000004"`, and for the `f32` value `1.0f32 / 3.0f32` it is `"0.33333334"`. A whole number has no fractional part (`string(250.0)` is `"250"`), and negative zero is `"-0"`. Magnitudes below `0.0001` or of at least 10¹⁷ use exponent notation with a capital `E` and at least two exponent digits: `string(0.00001)` is `"1E-05"` and `string(100000000000000000.0)` is `"1E+17"`, while `string(10000000000000000.0)` is still `"10000000000000000"`.
- Non-finite floating-point values convert to `"NaN"`, `"Infinity"`, and `"-Infinity"`.
- A `string` converts to itself.

Converting a `string` to another type is not supported in version 0.1.

## 8. Operators

### 8.1 Arithmetic operators

```text
+  -  *  /  %
```

- Arithmetic operators accept two values of the same numeric type and produce that same type.
- Different numeric types cannot be mixed in a binary operation.
- `/` performs integer division for integer operands and floating-point division for floating-point operands.
- Integer division rounds toward zero, and the remainder takes the sign of the dividend: `-7 / 2` is `-3`, `-7 % 2` is `-1`, `7 / -2` is `-3`, and `7 % -2` is `1`. `a == (a / b) * b + a % b` therefore always holds. This is the convention of the underlying IL `div`/`rem` instructions and of common hardware (and of C, C#, Java, Go, and Rust); floor division is a candidate for a future version (Section 13).
- `%` is available only for two integer operands of the same type.
- Unary `+` does not exist. Unary `-` may be applied to a numeric value.
- `+` may also concatenate two `string` values.

```arith
let a = 5 / 2;                 // 2: i64
let b = 5.0 / 2.0;             // 2.5: f64
let message = "answer=" + string(a);
```

### 8.2 Comparison operators

```text
==  !=  <  <=  >  >=
```

- All comparison operators may be used with two values of the same numeric type.
- Only `==` and `!=` may be used with `bool` and `string` values.
- String `==` compares contents by ordinal (UTF-16 code unit) equality, and `!=` is its negation. Whether two equal strings are the same object is not observable.
- A comparison produces a `bool` value.
- Floating-point comparisons follow the .NET IEEE 754 behavior.

### 8.3 Logical operators

```text
!  &&  ||
```

Logical operators accept only `bool` values. `&&` and `||` use short-circuit evaluation.

### 8.4 Assignment operators

```text
=  +=  -=  *=  /=  %=
```

Except that the left-hand side is evaluated only once, a compound assignment is equivalent to the corresponding binary operation followed by a regular assignment. Assignments and compound assignments are statements and do not produce values.

### 8.5 Precedence

Operators are listed below from highest to lowest precedence. Binary operators on the same row are left-associative.

| Precedence | Operator or construct |
| --- | --- |
| 1 | `()`, function calls |
| 2 | unary `-`, `!` |
| 3 | `*`, `/`, `%` |
| 4 | `+`, `-` |
| 5 | `<`, `<=`, `>`, `>=` |
| 6 | `==`, `!=` |
| 7 | `&&` |
| 8 | `||` |

Assignment is not an expression.

## 9. Control flow

### 9.1 `if`

```arith
if x > 0 {
    print("positive");
} else if x < 0 {
    print("negative");
} else {
    print("zero");
}
```

The condition must have type `bool`. An `if` statement does not produce a value.

### 9.2 `while`

```arith
let i = 0;

while i < 10 {
    print(i);
    i += 1;
}
```

The condition must have type `bool` and is evaluated before each iteration.

### 9.3 Range-based `for`

Arith supports half-open ranges with `..` and closed ranges with `..=`.

```arith
for i in 0..10 {
    print(i);       // 0 through 9
}

for i in 0..=10 {
    print(i);       // 0 through 10
}
```

- The start and end expressions must have type `i64`.
- The start and end expressions are each evaluated once before the loop, from left to right.
- The loop variable has type `i64` and is available only inside the loop body.
- The loop variable cannot be reassigned.
- If the start is greater than the end, the loop performs no iterations.
- Descending ranges, custom steps, and ranges as first-class values are not supported in version 0.1.

### 9.4 `break` and `continue`

`break;` terminates the innermost enclosing loop. `continue;` starts the next iteration of the innermost enclosing loop. Using either statement outside a loop is a compile-time error.

## 10. Built-in functions

### 10.1 `print`

`print` writes a value to standard output followed by a newline.

```arith
print(true);
print(42);
print(3.14);
print("hello");
```

It accepts `bool`, `i32`, `i64`, `f32`, `f64`, and `string` values. Each value prints exactly as its `string(value)` conversion (Section 7), so `print(true)` prints `true`, `print(0.1 + 0.2)` prints `0.30000000000000004`, and `print(1.0 / 0.0)` prints `Infinity`.

`print` is a compiler-recognized built-in rather than a user-defined function. A user cannot declare a function named `print`.

## 11. Runtime behavior

- Integer addition, subtraction, multiplication, division, remainder, and unary negation are checked operations.
- Integer overflow produces a runtime error.
- Integer division or remainder by zero produces a runtime error.
- Floating-point arithmetic follows .NET and IEEE 754 behavior; division by zero may produce infinity or `NaN`.
- Function arguments and subexpressions are evaluated from left to right.

## 12. Grammar

The following simplified EBNF describes the syntax of version 0.1. Lexical details and type constraints are defined in the preceding sections.

```ebnf
program          = { function-declaration } , EOF ;

function-declaration
                 = "fn" , identifier , "(" , [ parameter-list ] , ")" ,
                   [ "->" , type ] , block ;
parameter-list   = parameter , { "," , parameter } ;
parameter        = identifier , ":" , type ;
type             = "bool" | "i32" | "i64" | "f32" | "f64" | "string" ;

block            = "{" , { statement } , "}" ;
statement        = let-statement
                 | assignment-statement
                 | expression-statement
                 | return-statement
                 | if-statement
                 | while-statement
                 | for-statement
                 | break-statement
                 | continue-statement ;

let-statement    = "let" , identifier , [ ":" , type ] , "=" , expression , ";" ;
assignment-statement
                 = identifier , ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) ,
                   expression , ";" ;
expression-statement
                 = call-expression , ";" ;
return-statement = "return" , [ expression ] , ";" ;
if-statement     = "if" , expression , block ,
                   [ "else" , ( if-statement | block ) ] ;
while-statement  = "while" , expression , block ;
for-statement    = "for" , identifier , "in" , expression ,
                   ( ".." | "..=" ) , expression , block ;
break-statement  = "break" , ";" ;
continue-statement
                 = "continue" , ";" ;

expression       = logical-or ;
logical-or       = logical-and , { "||" , logical-and } ;
logical-and      = equality , { "&&" , equality } ;
equality         = comparison , { ( "==" | "!=" ) , comparison } ;
comparison       = additive , { ( "<" | "<=" | ">" | ">=" ) , additive } ;
additive         = multiplicative , { ( "+" | "-" ) , multiplicative } ;
multiplicative   = unary , { ( "*" | "/" | "%" ) , unary } ;
unary            = ( "-" | "!" ) , unary | primary ;
primary          = literal
                 | call-expression
                 | identifier
                 | "(" , expression , ")" ;
call-expression  = ( identifier | type ) , "(" , [ argument-list ] , ")" ;
argument-list    = expression , { "," , expression } ;
literal          = boolean-literal
                 | integer-literal
                 | float-literal
                 | string-literal ;
```

## 13. Features outside version 0.1

The following features are candidates for future versions and are not defined in version 0.1:

- Global variables and constants
- Arrays, tuples, structs, enumerations, and classes
- User-defined types
- Function values, lambda expressions, and closures
- Generics and user-defined overloads
- Modules and `import`
- `null` and nullable types
- Character types and character literals
- Bitwise operators
- Scientific notation and binary, octal, or hexadecimal integer literals
- Floor division and a divisor-signed remainder (Python's `//` and `%`), whose non-negative remainder is the natural fit for cyclic indexing once arrays exist
- Descending ranges and custom range steps
- Access to the raw command-line argument array (typed `main` parameters cover the common cases)
