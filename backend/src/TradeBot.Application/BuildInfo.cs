using System.Reflection;

namespace TradeBot.Application;

/// <summary>
/// The git commit this process was built from ("adfeb66"): the .NET SDK stamps it into the informational version
/// ("1.0.0+adfeb66…"). Alerts and /health show it, so a message can be traced to the release that sent it.
/// </summary>
public static class BuildInfo
{
    public static string Revision { get; } = Read(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Read(string? informationalVersion) =>
        informationalVersion?.Split('+', 2) is [_, { Length: > 0 } sha] ? sha[..Math.Min(7, sha.Length)] : "dev";
}
