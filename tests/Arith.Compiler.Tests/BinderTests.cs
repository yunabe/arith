using Arith.Compiler.Binding;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class BinderTests
{
    private static Compilation Compile(string source) =>
        Compilation.Create(SyntaxTree.Parse(SourceText.From(source)));

    private static Compilation CompileMain(string body) => Compile($"fn main() {{ {body} }}");

    private static string[] Codes(Compilation compilation) =>
        [.. compilation.Diagnostics.Select(d => d.Code)];

    private static BoundBlock FunctionBody(Compilation compilation, string name) =>
        compilation.Program.Functions.Single(f => f.Symbol.Name == name).Body;

    /// <summary>Binds one let inside main (error-free) and returns the local's type name.</summary>
    private static string LetType(string letStatement)
    {
        Compilation compilation = CompileMain(letStatement);
        Assert.Empty(compilation.Diagnostics);
        BoundLetStatement let =
            Assert.IsType<BoundLetStatement>(FunctionBody(compilation, "main").Statements[0]);
        return let.Local.Type.Name;
    }

    // ---- Literal typing (design §4.4 pending machinery) -----------------

    [Theory]
    [InlineData("let a = 1;", "i64")]                        // Integer default.
    [InlineData("let a = 1.5;", "f64")]                      // Float default.
    [InlineData("let a: i32 = 10;", "i32")]                  // Annotation forces.
    [InlineData("let a: i32 = (1 + 2) * 3;", "i32")]         // Expectation flows through nesting.
    [InlineData("let a = 1 + 2i32;", "i32")]                 // Concrete operand resolves the pending side.
    [InlineData("let a = 2i32 + 1;", "i32")]                 // ... symmetrically.
    [InlineData("let a = (1 + 2) + 3i32;", "i32")]           // ... recursively.
    [InlineData("let a = 10i64;", "i64")]
    [InlineData("let a: f32 = 1.5;", "f32")]
    [InlineData("let a = 1.5f32;", "f32")]
    [InlineData("let a = 0.5 + 1.5f32;", "f32")]
    [InlineData("let a = -1;", "i64")]
    [InlineData("let a = -(1 + 2);", "i64")]
    [InlineData("let a = true;", "bool")]
    [InlineData("let a = \"hi\";", "string")]
    [InlineData("let a = -9223372036854775808;", "i64")]     // Magnitude rule under unary minus.
    [InlineData("let a: i32 = -2147483648;", "i32")]
    public void LetInitializer_GetsTheSpecifiedType(string letStatement, string expectedType)
    {
        Assert.Equal(expectedType, LetType(letStatement));
    }

    [Fact]
    public void MinimumI64Literal_ResolvesToLongMinValue()
    {
        Compilation compilation = CompileMain("let a = -9223372036854775808;");

        Assert.Empty(compilation.Diagnostics);
        BoundLetStatement let =
            Assert.IsType<BoundLetStatement>(FunctionBody(compilation, "main").Statements[0]);
        BoundLiteralExpression literal = Assert.IsType<BoundLiteralExpression>(let.Initializer);
        Assert.Equal(long.MinValue, literal.Value);
    }

    [Fact]
    public void ReturnZeroFromI32Main_BindsTheLiteralAsI32()
    {
        Compilation compilation = Compile("fn main() -> i32 { return 0; }");

        Assert.Empty(compilation.Diagnostics);
        BoundReturnStatement ret =
            Assert.IsType<BoundReturnStatement>(FunctionBody(compilation, "main").Statements[0]);
        BoundLiteralExpression literal = Assert.IsType<BoundLiteralExpression>(ret.Value!);
        Assert.Same(ArithType.I32, literal.Type);
        Assert.Equal(0, literal.Value);
    }

    [Fact]
    public void PrintWithoutExpectation_DefaultsTheArgumentToI64()
    {
        Compilation compilation = CompileMain("print(1 + 2);");

        Assert.Empty(compilation.Diagnostics);
        BoundPrintStatement print =
            Assert.IsType<BoundPrintStatement>(FunctionBody(compilation, "main").Statements[0]);
        BoundBinaryExpression sum = Assert.IsType<BoundBinaryExpression>(print.Argument);
        Assert.Same(ArithType.I64, sum.Type);
        Assert.Equal(1L, Assert.IsType<BoundLiteralExpression>(sum.Left).Value);
        Assert.Equal(2L, Assert.IsType<BoundLiteralExpression>(sum.Right).Value);
    }

    [Fact]
    public void StringLiteral_IsUnescaped()
    {
        Compilation compilation = CompileMain("print(\"a\\n\\t\\\"b\\\\\");");

        Assert.Empty(compilation.Diagnostics);
        BoundPrintStatement print =
            Assert.IsType<BoundPrintStatement>(FunctionBody(compilation, "main").Statements[0]);
        Assert.Equal("a\n\t\"b\\", Assert.IsType<BoundLiteralExpression>(print.Argument).Value);
    }

    [Fact]
    public void CompoundAssignment_BindsOperatorAndForcedValue()
    {
        Compilation compilation = CompileMain("let a = 1i32; a += 2;");

        Assert.Empty(compilation.Diagnostics);
        BoundAssignmentStatement assignment =
            Assert.IsType<BoundAssignmentStatement>(FunctionBody(compilation, "main").Statements[1]);
        Assert.Equal(BoundBinaryOperatorKind.Addition, assignment.CompoundOperator);
        Assert.Same(ArithType.I32, assignment.Value.Type); // Variable's type forces the literal.
    }

    [Fact]
    public void FunctionsBindInAnyOrder_AndParametersForceArgumentLiterals()
    {
        const string source = """
            fn main() { consume(1, 2.5); }
            fn consume(a: i32, b: f64) { print(a); print(b); }
            """;

        Compilation compilation = Compile(source);

        Assert.Empty(compilation.Diagnostics);
        BoundExpressionStatement statement =
            Assert.IsType<BoundExpressionStatement>(FunctionBody(compilation, "main").Statements[0]);
        BoundCallExpression call = Assert.IsType<BoundCallExpression>(statement.Expression);
        Assert.Same(ArithType.I32, call.Arguments[0].Type);
        Assert.Same(ArithType.F64, call.Arguments[1].Type);
    }

    // ---- Diagnostics inside main ----------------------------------------

    [Theory]
    [InlineData("let x: i32 = 3000000000;", "ARITH3012")]      // Out of i32 range.
    [InlineData("let x = 9223372036854775808;", "ARITH3012")]  // Above i64 max without '-'.
    [InlineData("let x: i32 = -2147483649;", "ARITH3012")]
    [InlineData("let x = 3000000000i32;", "ARITH3012")]
    [InlineData("let x: f64 = 1 + 2;", "ARITH3009")]           // Categories never cross: stays i64.
    [InlineData("let x: i64 = 1.5;", "ARITH3009")]
    [InlineData("let x = 1 + 2.0;", "ARITH3010")]              // Mixed pending categories.
    [InlineData("let x = 1 + 2.0f64;", "ARITH3010")]           // Concrete other-category operand.
    [InlineData("let x = 1i32 + 1i64;", "ARITH3010")]          // Distinct numeric types don't mix.
    [InlineData("let x = 1 + true;", "ARITH3010")]
    [InlineData("let x = 5.0 % 2.0;", "ARITH3010")]            // '%' is integer-only.
    [InlineData("let x = -true;", "ARITH3011")]
    [InlineData("let x = y;", "ARITH3005")]
    [InlineData("let x = 1; let x = 2;", "ARITH3013")]
    [InlineData("x = 1;", "ARITH3005")]
    [InlineData("let x = 1; x = 1.5;", "ARITH3009")]
    [InlineData("let b = true; b += true;", "ARITH3010")]      // Compound assignment needs numeric.
    [InlineData("print();", "ARITH3008")]
    [InlineData("print(1, 2);", "ARITH3008")]
    [InlineData("let x = print(1);", "ARITH3017")]             // print produces no value.
    [InlineData("return 1;", "ARITH3014")]                     // main returns no value here.
    [InlineData("foo();", "ARITH3006")]
    public void InvalidStatement_ReportsExactlyOneDiagnostic(string body, string expectedCode)
    {
        Compilation compilation = CompileMain(body);

        Assert.Equal([expectedCode], Codes(compilation));
    }

    [Theory]
    [InlineData("if true { }", "'if'")]
    [InlineData("while true { }", "'while'")]
    [InlineData("for i in 0..10 { }", "'for'")]
    [InlineData("let x = 1 < 2;", "operator '<'")]
    [InlineData("let x = true && false;", "operator '&&'")]
    [InlineData("let x = !true;", "operator '!'")]
    [InlineData("let x = i64(1);", "explicit conversions")]
    public void NotYetImplementedConstruct_ReportsArith3901(string body, string subject)
    {
        Compilation compilation = CompileMain(body);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH3901", diagnostic.Code);
        Assert.Contains(subject, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorTypedOperand_SuppressesCascadingDiagnostics()
    {
        // 'z' is undefined; the binary operation and the let stay silent.
        Compilation compilation = CompileMain("let x = 1; let y = x + z;");

        Assert.Equal(["ARITH3005"], Codes(compilation));
    }

    [Fact]
    public void OutOfRangeLiteral_ReportsItsSpan()
    {
        Compilation compilation = CompileMain("let x: i32 = 3000000000;");

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(
            "3000000000",
            compilation.SyntaxTree.Text.ToString(diagnostic.Span));
    }

    // ---- Whole-program diagnostics --------------------------------------

    [Theory]
    [InlineData("fn helper() { }", "ARITH3003")]                             // No main.
    [InlineData("fn main(a: i64) { }", "ARITH3004")]
    [InlineData("fn main() -> i64 { return 0; }", "ARITH3004")]
    [InlineData("fn f() { } fn f() { } fn main() { }", "ARITH3001")]
    [InlineData("fn print() { } fn main() { }", "ARITH3002")]
    [InlineData("fn f(a: i64) { } fn main() { f(); }", "ARITH3008")]
    [InlineData("fn f(a: i64) { } fn main() { f(1, 2); }", "ARITH3008")]
    [InlineData("fn f(a: i64) { } fn main() { f(1.5); }", "ARITH3009")]
    [InlineData("fn f() -> i64 { return; } fn main() { }", "ARITH3015")]
    [InlineData("fn f(a: i64) { let a = 1; } fn main() { }", "ARITH3013")]   // Params share the body scope.
    [InlineData("fn f(a: i64, a: i64) { } fn main() { }", "ARITH3013")]
    [InlineData("fn g() { } fn main() { let x = g(); }", "ARITH3017")]       // Void call has no value.
    public void InvalidProgram_ReportsExactlyOneDiagnostic(string source, string expectedCode)
    {
        Compilation compilation = Compile(source);

        Assert.Equal([expectedCode], Codes(compilation));
    }

    // ---- Error accumulation, ordering, and suppression edges ------------

    [Fact]
    public void IndependentErrors_AreAllReported()
    {
        Compilation compilation = CompileMain("let x = y; let z = w;");

        Assert.Equal(["ARITH3005", "ARITH3005"], Codes(compilation));
    }

    [Fact]
    public void PrintArityError_StillBindsItsArgumentsFirst()
    {
        Compilation compilation = CompileMain("print(1, y);");

        Assert.Equal(["ARITH3005", "ARITH3008"], Codes(compilation));
    }

    [Fact]
    public void UndefinedFunctionCall_StillBindsItsArguments()
    {
        Compilation compilation = CompileMain("foo(bar);");

        Assert.Equal(["ARITH3005", "ARITH3006"], Codes(compilation));
    }

    [Fact]
    public void DuplicateFunctionBody_IsStillBoundForItsOwnDiagnostics()
    {
        Compilation compilation = Compile("fn f() { } fn f() { let x = y; } fn main() { }");

        Assert.Equal(["ARITH3001", "ARITH3005"], Codes(compilation));
        // Only the first declaration is part of the program.
        Assert.Equal(2, compilation.Program.Functions.Length);
    }

    [Fact]
    public void UseBeforeDeclaration_IsUndefined()
    {
        // Spec §1: a local may be referenced only after it has been
        // declared — the initializer binds before the name exists.
        Compilation compilation = CompileMain("let x = x;");

        Assert.Equal(["ARITH3005"], Codes(compilation));
    }

    [Fact]
    public void ParseError_DoesNotCascadeIntoBindingDiagnostics()
    {
        // The multi-diagnostic policy end to end: the missing initializer is
        // a syntax error, `x` binds with the Error type, and using it in
        // print adds nothing.
        Compilation compilation = CompileMain("let x = ; print(x);");

        Assert.Equal(["ARITH2001"], Codes(compilation));
    }

    [Fact]
    public void MissingLetIdentifier_DeclaresNothingSilently()
    {
        Compilation compilation = CompileMain("let = 1;");

        Assert.Equal(["ARITH2001"], Codes(compilation));
    }

    [Fact]
    public void MainWithUnparsableReturnType_DoesNotAlsoReportSignatureError()
    {
        // The missing type is a syntax error; the Error return type must not
        // trigger ARITH3004 on top.
        Compilation compilation = Compile("fn main() -> { }");

        Assert.Equal(["ARITH2001"], Codes(compilation));
    }

    [Fact]
    public void EmptySource_ReportsOnlyTheMissingEntryPoint()
    {
        Compilation compilation = Compile("");

        Assert.Equal(["ARITH3003"], Codes(compilation));
    }

    [Fact]
    public void RedeclaredPrint_DoesNotBreakTheBuiltin()
    {
        // The declaration is rejected, but print statements keep binding to
        // the builtin.
        Compilation compilation = Compile("fn print() { } fn main() { print(42); }");

        Assert.Equal(["ARITH3002"], Codes(compilation));
    }

    [Fact]
    public void ErrorTypedVariable_IsUsableWithoutFurtherDiagnostics()
    {
        Compilation compilation = CompileMain("let x = y; print(x);");

        Assert.Equal(["ARITH3005"], Codes(compilation));
    }

    // ---- More semantic corners ------------------------------------------

    [Theory]
    [InlineData("let x = -9223372036854775809;", "ARITH3012")] // One below i64 min.
    [InlineData("let x: i32 = -(2147483648);", "ARITH3012")]   // Parens break directness: the magnitude rule is only for the literal directly beneath '-'.
    [InlineData("let x = -\"a\";", "ARITH3011")]
    [InlineData("let x = \"a\" + \"b\";", "ARITH3010")]        // String concat arrives in step 7.
    public void SemanticCorner_ReportsExactlyOneDiagnostic(string body, string expectedCode)
    {
        Compilation compilation = CompileMain(body);

        Assert.Equal([expectedCode], Codes(compilation));
    }

    [Fact]
    public void VoidCallAsOperand_ReportsTheOperator()
    {
        Compilation compilation = Compile("fn g() { } fn main() { let x = g() + 1; }");

        Assert.Equal(["ARITH3010"], Codes(compilation));
    }

    [Theory]
    [InlineData("fn g() { } fn main() { let x: i64 = g(); }")]              // Annotated let.
    [InlineData("fn g() { } fn f(a: i64) { } fn main() { f(g()); }")]       // Argument position.
    [InlineData("fn g() { } fn f() -> i64 { return g(); } fn main() { }")]  // Return value.
    public void VoidCallInForcingContext_HasNoValueInsteadOfTypeMismatch(string source)
    {
        Compilation compilation = Compile(source);

        Assert.Equal(["ARITH3017"], Codes(compilation));
    }

    [Fact]
    public void VoidCallAsPrintArgument_HasNoValue()
    {
        Compilation compilation = Compile("fn g() { } fn main() { print(g()); }");

        Assert.Equal(["ARITH3017"], Codes(compilation));
    }

    [Fact]
    public void ReturnValueTypeMismatch_ReportsArith3009()
    {
        Compilation compilation = Compile("fn f() -> i64 { return 1.5; } fn main() { }");

        Assert.Equal(["ARITH3009"], Codes(compilation));
    }

    [Fact]
    public void NotYetImplementedStatement_DoesNotBindItsChildren()
    {
        // Pins current staging behavior: an unimplemented statement reports
        // only ARITH3901 — its condition and body wait for step 6, so the
        // undefined names inside stay undiagnosed until then.
        Compilation compilation = CompileMain("if y { let x = z; }");

        Assert.Equal(["ARITH3901"], Codes(compilation));
    }

    [Fact]
    public void MutuallyRecursiveFunctions_BindWithoutForwardDeclarations()
    {
        const string source = """
            fn main() { even(); }
            fn even() { odd(); }
            fn odd() { even(); }
            """;

        Compilation compilation = Compile(source);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(3, compilation.Program.Functions.Length);
        Assert.Equal("main", compilation.Program.EntryPoint?.Name);
    }

    [Fact]
    public void SubsetProgram_BindsCleanWithResolvedTypes()
    {
        const string source = """
            fn add(a: i64, b: i64) -> i64 {
                return a + b;
            }

            fn main() -> i32 {
                let result = add(20, 22);
                print(result);
                print("done");
                return 0;
            }
            """;

        Compilation compilation = Compile(source);

        Assert.Empty(compilation.Diagnostics);
        Assert.False(compilation.HasErrors);
        BoundBlock main = FunctionBody(compilation, "main");
        BoundLetStatement let = Assert.IsType<BoundLetStatement>(main.Statements[0]);
        Assert.Same(ArithType.I64, let.Local.Type);
        BoundCallExpression call = Assert.IsType<BoundCallExpression>(let.Initializer);
        Assert.Equal("add", call.Function.Name);
        Assert.Same(ArithType.I64, call.Arguments[0].Type);
    }
}
