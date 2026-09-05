using System.Reflection;

namespace Rot.App.Services;

internal static class BuildIdentity
{
    private static readonly Assembly Assembly = typeof(BuildIdentity).Assembly;

    public static string InformationalVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public static string Version { get; } = InformationalVersion.Split('+')[0];

    public static string Revision { get; } = InformationalVersion.Contains('+')
        ? InformationalVersion[(InformationalVersion.IndexOf('+') + 1)..]
        : string.Empty;
}
