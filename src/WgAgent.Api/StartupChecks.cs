using WgAgent.Core;
using WgAgent.Core.Store;
using WgAgent.Platform.Linux;

namespace WgAgent.Api;

/// <summary>
/// The checks of REQ-API-050, run before the listener serves and again for each health probe. The
/// first failure is returned with the reason code the table assigns it; the caller ends the process.
/// </summary>
public static class StartupChecks
{
    public sealed record Failure(string Reason, string Message);

    /// <param name="hasNetAdmin">CAP_NET_ADMIN probe; injected so the checks run without privilege in a test.</param>
    public static Failure? Run(StateStore store, TokenFile token, Func<bool> hasNetAdmin)
    {
        // 1 and 2: the store opens, is a schema this build understands, and its directory is writable.
        try
        {
            store.Load();
        }
        catch (AgentException error)   // STORE_CORRUPT or STORE_SCHEMA_TOO_NEW
        {
            return new Failure(error.Code, error.Message);
        }
        if (!DirectoryWritable(store.Path, out var why))
            return new Failure(ReasonCodes.StoreCorrupt, why);

        // 3: a token is configured (REQ-SEC-072).
        try
        {
            if (token.Read() is null)
                return new Failure(ReasonCodes.TokenInvalid, $"No token is configured in {token.Path}; the agent will not serve without one.");
        }
        catch (TokenFile.Invalid invalid)
        {
            return new Failure(ReasonCodes.TokenInvalid, invalid.Message);
        }

        // 4: CAP_NET_ADMIN is held (REQ-SEC-086).
        if (!hasNetAdmin())
            return new Failure(ReasonCodes.MissingCapNetAdmin, "The agent does not hold CAP_NET_ADMIN; it cannot manage interfaces.");

        return null;
    }

    private static bool DirectoryWritable(string storePath, out string why)
    {
        why = "";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(storePath))!;
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".writable.{Environment.ProcessId}.{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"The store directory of {storePath} is not writable: {e.Message}";
            return false;
        }
    }
}
