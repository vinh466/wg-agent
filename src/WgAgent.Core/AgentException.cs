namespace WgAgent.Core;

/// <summary>A failure the agent reports by reason code (REQ-API-041), never by message alone.</summary>
public sealed class AgentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
