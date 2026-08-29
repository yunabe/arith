using System.Reflection;

namespace Arith.Cli;

internal static class BuildInfo
{
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var informationalVersion = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var metadataIndex = informationalVersion.IndexOf('+');
        return metadataIndex >= 0
            ? informationalVersion[..metadataIndex]
            : informationalVersion;
    }
}
