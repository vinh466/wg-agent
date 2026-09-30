using WgAgent.Platform;

namespace WgAgent.Tests;

/// <summary>Deterministic, well-formed keys: 32 bytes of one value, in standard base64.</summary>
internal static class TestKeys
{
    public static string Base64(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());
    public static PublicKey Public(byte fill) => PublicKey.FromBytes(Enumerable.Repeat(fill, 32).ToArray());
    public static SecretKey Secret(byte fill) => SecretKey.FromBytes(Enumerable.Repeat(fill, 32).ToArray());
}
