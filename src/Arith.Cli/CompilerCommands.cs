using System.Diagnostics;
using System.Text.RegularExpressions;

using Arith.Compiler;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Cli;

/// <summary>The `arith build` and `arith run` implementations: thin drivers over Arith.Compiler.</summary>
internal static partial class CompilerCommands
{
    /// <summary>
    /// The CLI's (not the language's) rule for source files: the input is
    /// `&lt;program-name&gt;.arith` and the program name — which names every
    /// output artifact — is restricted to a filesystem- and launcher-safe
    /// shape. A future `--name` option can split output naming from the
    /// source name if richer file names are ever needed.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex ProgramNameRegex();

    /// <summary>Compiles a source file and writes its artifacts. Returns the process exit code.</summary>
    internal static int Build(string sourcePath, string? outputDirectory, TextWriter output, TextWriter error)
    {
        if (ValidateProgramName(sourcePath, error) is not { } name)
        {
            return 1;
        }

        // Defense in depth beyond the name rule: refuse to overwrite the
        // input with any planned output, whatever the paths involved.
        string resolvedOutputDirectory = outputDirectory ?? Directory.GetCurrentDirectory();
        string sourceFullPath = Path.GetFullPath(sourcePath);
        foreach (string planned in ArtifactWriter.PlannedPaths(resolvedOutputDirectory, name))
        {
            if (string.Equals(Path.GetFullPath(planned), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"error: output file '{planned}' would overwrite the source file");
                return 1;
            }
        }

        if (CompileFile(sourcePath, name, error) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, error);
        if (!result.Success)
        {
            return 1;
        }

        foreach (string path in ArtifactWriter.Write(resolvedOutputDirectory, name, result.PeImage))
        {
            output.WriteLine($"wrote {path}");
        }

        return 0;
    }

    /// <summary>Builds into a temporary directory, runs via the dotnet host, and forwards the exit code.</summary>
    internal static int Run(string sourcePath, TextWriter output, TextWriter error)
    {
        if (ValidateProgramName(sourcePath, error) is not { } name)
        {
            return 1;
        }

        if (CompileFile(sourcePath, name, error) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, error);
        if (!result.Success)
        {
            return 1;
        }

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

    /// <summary>Checks the file-name rule and returns the program name, or null after printing an error.</summary>
    private static string? ValidateProgramName(string sourcePath, TextWriter error)
    {
        string fileName = Path.GetFileName(sourcePath);
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.EndsWith(".arith", StringComparison.Ordinal) && ProgramNameRegex().IsMatch(name))
        {
            return name;
        }

        error.WriteLine(
            $"error: '{fileName}' is not a valid source file name: expected <program-name>.arith, "
            + "where <program-name> starts with a letter or '_' and contains only letters, digits, '_', and '-'");
        return null;
    }

    /// <summary>Reads and compiles the file, or returns null (with a message) when it cannot be read.</summary>
    private static (EmitResult Result, SourceText Source)? CompileFile(
        string sourcePath, string assemblyName, TextWriter error)
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
        return (compilation.Emit(assemblyName), source);
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
