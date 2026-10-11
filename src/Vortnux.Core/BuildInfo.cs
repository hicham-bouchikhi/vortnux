using System.Reflection;

namespace Vortnux.Core;

public static class BuildInfo
{
    private static readonly Dictionary<string, string?> Metadata = typeof(BuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    public static string Version => Metadata["VortnuxVersion"]!;
    public static string Number => Metadata["VortnuxBuildNumber"]!;
    public static string Commit => Metadata["VortnuxGitCommit"]!;
    public static string Display => $"Vortnux {Version} · build {Number} · commit {Commit}";
}
