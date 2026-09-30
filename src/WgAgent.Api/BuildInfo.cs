using System.Reflection;

namespace WgAgent.Api;

/// <summary>The version and commit the running binary was built from (REQ-API-078, REQ-CLI-033).</summary>
public static class BuildInfo
{
    public static (string Version, string Commit) Current()
    {
        var informational = (Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? (informational, "unknown") : (informational[..plus], informational[(plus + 1)..]);
    }
}
