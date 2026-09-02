using System.Diagnostics;

using Arith.Compiler;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Cli;

/// <summary>The `arith build` and `arith run` implementations: thin drivers over Arith.Compiler.</summary>
internal static class CompilerCommands
{
    /// <summary>Compiles a source file and writes its artifacts. Returns the process exit code.</summary>
    internal static int Build(string sourcePath, string? outputDirectory, TextWriter output, TextWriter error)
    {
        if (CompileFile(sourcePath, error) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, error);
        if (!result.Success)
        {
            return 1;
        }

        string name = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (string path in ArtifactWriter.Write(
            outputDirectory ?? Directory.GetCurrentDirectory(), name, result.PeImage))
        {
            output.WriteLine($"wrote {path}");
        }

        return 0;
    }

    /// <summary>Builds into a temporary directory, runs via the dotnet host, and forwards the exit code.</summary>
    internal static int Run(string sourcePath, TextWriter output, TextWriter error)
    {
        if (CompileFile(sourcePath, error) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, error);
        if (!result.Success)
        {
            return 1;
        }

        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "arith-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            ArtifactWriter.Write(temporaryDirectory, name, result.PeImage);
            ProcessStartInfo startInfo = new("dotnet", [Path.Combine(temporaryDirectory, name + ".dll")]);
            ProcessResult processResult = ProcessRunner.Run(startInfo);
            output.Write(processResult.Output);
            error.Write(processResult.Error);
            return processResult.ExitCode;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; the OS reclaims temp files eventually.
            }
        }
    }

    /// <summary>Reads and compiles the file, or returns null (with a message) when it cannot be read.</summary>
    private static (EmitResult Result, SourceText Source)? CompileFile(string sourcePath, TextWriter error)
    {
        string text;
        try
        {
            text = File.ReadAllText(sourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: cannot read '{sourcePath}': {exception.Message}");
            return null;
        }

        SourceText source = SourceText.From(text, sourcePath);
        Compilation compilation = Compilation.Create(SyntaxTree.Parse(source));
        return (compilation.Emit(Path.GetFileNameWithoutExtension(sourcePath)), source);
    }

    /// <summary>Renders diagnostics as `path:line:col: severity CODE: message` (design §4.6).</summary>
    private static void PrintDiagnostics(EmitResult result, SourceText source, TextWriter error)
    {
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            LinePosition position = source.GetLinePosition(diagnostic.Span.Start);
            string severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            error.WriteLine($"{source.FilePath}:{position}: {severity} {diagnostic.Code}: {diagnostic.Message}");
        }
    }
}
