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
    internal static int Build(
        string sourcePath, string? outputDirectory, bool aot, bool debug, TextWriter output, TextWriter error)
    {
        if (aot && debug)
        {
            error.WriteLine("error: --debug cannot be combined with --aot; debug mode currently supports managed output only");
            return 1;
        }

        if (ValidateProgramName(sourcePath, error) is not { } name)
        {
            return 1;
        }

        // Defense in depth beyond the name rule: refuse to overwrite the
        // input with any planned output, whatever the paths involved.
        string resolvedOutputDirectory = outputDirectory ?? Directory.GetCurrentDirectory();
        string sourceFullPath;
        try
        {
            sourceFullPath = Path.GetFullPath(sourcePath);
            foreach (string planned in ArtifactWriter.PlannedPaths(resolvedOutputDirectory, name))
            {
                if (string.Equals(Path.GetFullPath(planned), sourceFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine($"error: output file '{planned}' would overwrite the source file");
                    return 1;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            error.WriteLine($"error: cannot resolve input or output path: {exception.Message}");
            return 1;
        }

        if (CompileFile(sourcePath, name, debug, error, sourceFullPath) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, sourcePath, error);
        if (!result.Success)
        {
            return 1;
        }

        // AOT is packaging, not a second emission path (design §4.6): the
        // NativeAOT toolchain consumes the same PE bytes the JIT path would.
        if (aot)
        {
            try
            {
                string executable = NativeAotPublisher.Publish(
                    [.. result.PeImage], name, resolvedOutputDirectory, output);
                output.WriteLine($"wrote {executable}");
                return 0;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: NativeAOT publish failed: {exception.Message}");
                return 1;
            }
        }

        IReadOnlyList<string> written;
        try
        {
            written = ArtifactWriter.Write(resolvedOutputDirectory, name, result.PeImage, result.PdbImage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: cannot write artifacts to '{resolvedOutputDirectory}': {exception.Message}");
            return 1;
        }

        foreach (string path in written)
        {
            output.WriteLine($"wrote {path}");
        }

        return 0;
    }

    /// <summary>Builds into a temporary directory, runs via the dotnet host, and forwards the exit code.</summary>
    internal static int Run(
        string sourcePath, string[] programArguments, bool debug, TextWriter output, TextWriter error)
    {
        if (ValidateProgramName(sourcePath, error) is not { } name)
        {
            return 1;
        }

        if (CompileFile(sourcePath, name, debug, error) is not ({ } result, { } source))
        {
            return 1;
        }

        PrintDiagnostics(result, source, sourcePath, error);
        if (!result.Success)
        {
            return 1;
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "arith-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                ArtifactWriter.Write(temporaryDirectory, name, result.PeImage, result.PdbImage);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: cannot write artifacts to '{temporaryDirectory}': {exception.Message}");
                return 1;
            }

            ProcessStartInfo startInfo = new(
                "dotnet", [Path.Combine(temporaryDirectory, name + ".dll"), .. programArguments]);
            return ProcessRunner.Run(startInfo, output, error);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        string sourcePath, string assemblyName, bool debug, TextWriter error, string? sourceFullPath = null)
    {
        SourceText source;
        try
        {
            // Resolve at the CLI boundary, once; the compiler treats paths as
            // document names and never interprets them against the process CWD.
            sourceFullPath ??= Path.GetFullPath(sourcePath);
            source = SourceText.FromBytes(File.ReadAllBytes(sourceFullPath), sourceFullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"error: cannot read '{sourcePath}': {exception.Message}");
            return null;
        }

        Compilation compilation = Compilation.Create(SyntaxTree.Parse(source));
        return (compilation.Emit(assemblyName, debug), source);
    }

    /// <summary>Renders diagnostics as `path:line:col: severity CODE: message` (design §4.6).</summary>
    private static void PrintDiagnostics(EmitResult result, SourceText source, string diagnosticPath, TextWriter error)
    {
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            LinePosition position = source.GetLinePosition(diagnostic.Span.Start);
            string severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            error.WriteLine($"{diagnosticPath}:{position}: {severity} {diagnostic.Code}: {diagnostic.Message}");
        }
    }
}
