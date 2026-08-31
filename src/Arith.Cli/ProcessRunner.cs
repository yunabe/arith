using System.Diagnostics;

namespace Arith.Cli;

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion with stdout and stderr captured. Both pipes are
    /// drained concurrently: reading one to EOF first would deadlock if the child
    /// blocks on the other pipe's full buffer before it can finish.
    /// </summary>
    internal static ProcessResult Run(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new ProcessResult(
            process.ExitCode,
            outputTask.GetAwaiter().GetResult(),
            errorTask.GetAwaiter().GetResult());
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
