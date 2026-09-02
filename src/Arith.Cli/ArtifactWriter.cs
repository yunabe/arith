using System.Collections.Immutable;

namespace Arith.Cli;

/// <summary>
/// Turns an emitted PE image into on-disk artifacts: the assembly, the
/// runtimeconfig naming the shared framework for the `dotnet` host, and
/// convenience launchers. The compiler library never writes files itself
/// (design §4.6); every packaging mode consumes the same emitted bytes.
/// </summary>
internal static class ArtifactWriter
{
    private const string RuntimeConfigJsonTemplate = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "10.0.0"
            }
          }
        }
        """;

    /// <summary>The paths <see cref="Write"/> will create, so callers can check for collisions before writing.</summary>
    internal static IReadOnlyList<string> PlannedPaths(string outputDirectory, string name) =>
    [
        Path.Combine(outputDirectory, name + ".dll"),
        Path.Combine(outputDirectory, name + ".runtimeconfig.json"),
        Path.Combine(outputDirectory, name),
        Path.Combine(outputDirectory, name + ".cmd"),
    ];

    /// <summary>Writes the program's files into <paramref name="outputDirectory"/> and returns their paths.</summary>
    internal static IReadOnlyList<string> Write(
        string outputDirectory, string name, ImmutableArray<byte> peImage)
    {
        Directory.CreateDirectory(outputDirectory);
        IReadOnlyList<string> paths = PlannedPaths(outputDirectory, name);
        string assemblyPath = paths[0];
        File.WriteAllBytes(assemblyPath, [.. peImage]);
        File.WriteAllText(paths[1], RuntimeConfigJsonTemplate + Environment.NewLine);

        // The launchers derive the dll path from their own location instead
        // of embedding the program name, so a renamed launcher fails loudly
        // rather than running some other program's dll.
        string launcherPath = paths[2];
        File.WriteAllText(launcherPath, "#!/bin/sh\nexec dotnet \"$0.dll\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                launcherPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        File.WriteAllText(paths[3], "@echo off\r\ndotnet \"%~dpn0.dll\" %*\r\n");
        return paths;
    }
}
