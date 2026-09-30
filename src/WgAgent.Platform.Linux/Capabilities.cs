using System.Globalization;

namespace WgAgent.Platform.Linux;

/// <summary>The process's Linux capabilities, read from <c>/proc/self/status</c> (REQ-SEC-086).</summary>
public static class Capabilities
{
    private const int CapNetAdmin = 12;   // <linux/capability.h>

    /// <summary>Whether the effective set holds CAP_NET_ADMIN, the one capability the agent needs.</summary>
    public static bool HasNetAdmin()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("CapEff:", StringComparison.Ordinal)) continue;
            var value = ulong.Parse(line["CapEff:".Length..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (value & (1UL << CapNetAdmin)) != 0;
        }
        return false;
    }
}
