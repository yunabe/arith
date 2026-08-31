using System.Diagnostics;
using System.Security;

namespace Arith.Cli.Experiments;

/// <summary>
/// Compiles an IL assembly into a native executable with the official NativeAOT
/// toolchain (ILC + the platform linker), without ever running the C# compiler.
///
/// The trick: NativeAOT is driven by MSBuild targets that consume whatever IL
/// assembly the build produced. This class generates a throwaway project whose
/// `CoreCompile` target — the step that normally invokes the C# compiler — is
/// replaced by a plain copy of the already-emitted assembly, then runs
/// `dotnet publish`. The official pipeline handles the ILC invocation and the
/// platform-specific native link, which are too version- and OS-dependent to
/// reimplement by hand.
/// </summary>
internal static class NativeAotPublisher
{
    /// <summary>
    /// Publishes <paramref name="ilAssembly"/> as a native executable named
    /// <paramref name="assemblyName"/> in <paramref name="outputDirectory"/> and
    /// returns the path of the executable. Requires the platform's native linker
    /// (Xcode Command Line Tools on macOS, clang/binutils on Linux); the first run
    /// downloads the ILCompiler NuGet packages.
    /// </summary>
    internal static string Publish(
        byte[] ilAssembly, string assemblyName, string outputDirectory, TextWriter log)
    {
        DirectoryInfo workDirectory = Directory.CreateTempSubdirectory("arith-nativeaot-");
        try
        {
            string ilAssemblyPath = Path.Combine(workDirectory.FullName, assemblyName + ".il.dll");
            File.WriteAllBytes(ilAssemblyPath, ilAssembly);

            string projectPath = Path.Combine(workDirectory.FullName, assemblyName + ".csproj");
            File.WriteAllText(projectPath, StubProject(assemblyName, ilAssemblyPath));

            string publishDirectory = Path.Combine(workDirectory.FullName, "publish");
            log.WriteLine("running dotnet publish (NativeAOT); the first run downloads the ILCompiler packages...");
            RunDotnetPublish(workDirectory.FullName, publishDirectory);

            string executableName = assemblyName + (OperatingSystem.IsWindows() ? ".exe" : "");
            string publishedExecutable = Path.Combine(publishDirectory, executableName);
            if (!File.Exists(publishedExecutable))
            {
                throw new InvalidOperationException(
                    $"dotnet publish succeeded but '{publishedExecutable}' was not produced.");
            }

            Directory.CreateDirectory(outputDirectory);
            string destination = Path.Combine(outputDirectory, executableName);
            File.Copy(publishedExecutable, destination, overwrite: true);
            return destination;
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A minimal NativeAOT project around a prebuilt IL assembly. `Sdk.targets` is
    /// imported explicitly so that the `CoreCompile` target defined below overrides
    /// the SDK's C#-compiler one (last definition wins in MSBuild).
    /// </summary>
    private static string StubProject(string assemblyName, string ilAssemblyPath) =>
        $"""
        <Project>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>{SecurityElement.Escape(assemblyName)}</AssemblyName>
            <PublishAot>true</PublishAot>
            <EnableDefaultItems>false</EnableDefaultItems>
            <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
            <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
            <InvariantGlobalization>true</InvariantGlobalization>
          </PropertyGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <!-- Replace the C# compiler step: the input IL was already emitted by arith. -->
          <Target Name="CoreCompile">
            <Copy SourceFiles="{SecurityElement.Escape(ilAssemblyPath)}" DestinationFiles="@(IntermediateAssembly)" />
          </Target>
        </Project>
        """;

    private static void RunDotnetPublish(string projectDirectory, string publishDirectory)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(publishDirectory);
        startInfo.ArgumentList.Add("--nologo");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit code {process.ExitCode}. " +
                $"A working native toolchain (e.g. Xcode Command Line Tools on macOS) is required.\n" +
                $"{output}\n{error}");
        }
    }
}
