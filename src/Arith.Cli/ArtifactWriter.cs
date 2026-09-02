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

    /// <summary>Writes the program's files into <paramref name="outputDirectory"/> and returns their paths.</summary>
    internal static IReadOnlyList<string> Write(
        string outputDirectory, string name, ImmutableArray<byte> peImage)
    {
        Directory.CreateDirectory(outputDirectory);
        List<string> written = [];

        string assemblyPath = Path.Combine(outputDirectory, name + ".dll");
        File.WriteAllBytes(assemblyPath, [.. peImage]);
        written.Add(assemblyPath);

        string runtimeConfigPath = Path.Combine(outputDirectory, name + ".runtimeconfig.json");
        File.WriteAllText(runtimeConfigPath, RuntimeConfigJsonTemplate + Environment.NewLine);
        written.Add(runtimeConfigPath);

        string launcherPath = Path.Combine(outputDirectory, name);
        File.WriteAllText(launcherPath, $"#!/bin/sh\nexec dotnet \"$(dirname \"$0\")/{name}.dll\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                launcherPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        written.Add(launcherPath);

        string cmdLauncherPath = Path.Combine(outputDirectory, name + ".cmd");
        File.WriteAllText(cmdLauncherPath, $"@echo off\r\ndotnet \"%~dp0{name}.dll\" %*\r\n");
        written.Add(cmdLauncherPath);

        return written;
    }
}
