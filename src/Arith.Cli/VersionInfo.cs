using System.Reflection;

namespace Arith.Cli;

internal static class VersionInfo
{
    /// <summary>
    /// The version shown by `arith version`, without build metadata (the `+...` suffix
    /// that official builds append to the informational version).
    /// </summary>
    internal static string DisplayVersion { get; } = ComputeDisplayVersion();

    private static string ComputeDisplayVersion()
    {
        Assembly assembly = typeof(VersionInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is null)
        {
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        int metadataStart = informational.IndexOf('+', StringComparison.Ordinal);
        return metadataStart >= 0 ? informational[..metadataStart] : informational;
    }
}
