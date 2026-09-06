using System.Diagnostics;

using Arith.Compiler.Syntax;

namespace Arith.Compiler.Tests;

/// <summary>
/// Renders a syntax tree as a compact S-expression so tests can assert tree
/// shape in one readable string, e.g. <c>(fn main (block (return 0)))</c>.
/// Literals and names print as their raw text; parentheses in source appear
/// as an explicit <c>(paren …)</c> node so grouping is assertable.
/// </summary>
internal static class SyntaxDumper
{
    public static string Dump(SyntaxNode node) => node switch
    {
        CompilationUnitSyntax unit => string.Join(" ", unit.Functions.Select(Dump)),
        FunctionDeclarationSyntax fn =>
            $"(fn {fn.Identifier.Text}"
            + string.Concat(fn.Parameters.Select(p => " " + Dump(p)))
            + (fn.ReturnType is null ? "" : $" -> {TypeText(fn.ReturnType)}")
            + $" {Dump(fn.Body)})",
        ParameterSyntax parameter => $"(param {parameter.Identifier.Text} {TypeText(parameter.Type)})",
        BlockSyntax block => $"(block{string.Concat(block.Statements.Select(s => " " + Dump(s)))})",
        LetStatementSyntax let =>
            let.Type is null
                ? $"(let {let.Identifier.Text} {Dump(let.Initializer)})"
                : $"(let {let.Identifier.Text} : {TypeText(let.Type)} {Dump(let.Initializer)})",
        AssignmentStatementSyntax assignment =>
            $"({assignment.OperatorToken.Text} {Dump(assignment.Target)} {Dump(assignment.Value)})",
        ExpressionStatementSyntax statement => $"(expr {Dump(statement.Expression)})",
        ReturnStatementSyntax ret => ret.Value is null ? "(return)" : $"(return {Dump(ret.Value)})",
        IfStatementSyntax conditional =>
            conditional.Else is null
                ? $"(if {Dump(conditional.Condition)} {Dump(conditional.Then)})"
                : $"(if {Dump(conditional.Condition)} {Dump(conditional.Then)} {Dump(conditional.Else)})",
        WhileStatementSyntax loop => $"(while {Dump(loop.Condition)} {Dump(loop.Body)})",
        ForStatementSyntax loop =>
            $"(for {loop.Identifier.Text} {loop.RangeOperator.Text} "
            + $"{Dump(loop.Start)} {Dump(loop.End)} {Dump(loop.Body)})",
        ForEachStatementSyntax loop =>
            $"(for-in {loop.Identifier.Text} {Dump(loop.Iterable)} {Dump(loop.Body)})",
        BreakStatementSyntax => "(break)",
        ContinueStatementSyntax => "(continue)",
        ErrorStatementSyntax => "(error-stmt)",
        LiteralExpressionSyntax literal => literal.LiteralToken.Text,
        NameExpressionSyntax name => name.Identifier.Text,
        CallExpressionSyntax call =>
            $"(call {call.Callee.Text}{string.Concat(call.Arguments.Select(a => " " + Dump(a)))})",
        UnaryExpressionSyntax unary => $"({unary.OperatorToken.Text} {Dump(unary.Operand)})",
        BinaryExpressionSyntax binary =>
            $"({binary.OperatorToken.Text} {Dump(binary.Left)} {Dump(binary.Right)})",
        ParenthesizedExpressionSyntax paren => $"(paren {Dump(paren.Expression)})",
        ArrayLiteralExpressionSyntax array =>
            $"(array{string.Concat(array.Elements.Select(e => " " + Dump(e)))})",
        ArrayRepeatExpressionSyntax repeat => $"(array-repeat {Dump(repeat.Value)} {Dump(repeat.Count)})",
        IndexExpressionSyntax index => $"(index {Dump(index.Target)} {Dump(index.Index)})",
        ErrorExpressionSyntax => "(error)",
        _ => throw new UnreachableException($"unhandled node type {node.GetType().Name}"),
    };

    /// <summary>A type as written: `[]` per array depth, then the keyword, e.g. `[][]i64`.</summary>
    private static string TypeText(TypeSyntax type) =>
        string.Concat(Enumerable.Repeat("[]", type.ArrayDepth)) + type.Keyword.Text;
}
