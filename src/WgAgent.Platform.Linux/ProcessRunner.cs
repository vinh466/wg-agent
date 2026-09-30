using System.Diagnostics;

namespace WgAgent.Platform.Linux;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IProcessRunner
{
    ProcessResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null);
}

/// <summary>
/// Starts the agent's child processes: <c>wg</c> and <c>systemctl</c> and nothing else
/// (REQ-SEC-087), each with a fixed argument vector and never through a shell (REQ-SEC-088). A key
/// travels on standard input, never in the argument vector, which every account can read through
/// /proc (REQ-SEC-089).
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The programs, by absolute path, so no PATH entry can stand in for them.</summary>
    public static readonly IReadOnlyDictionary<string, string> AgentPrograms = new Dictionary<string, string>
    {
        ["wg"] = "/usr/bin/wg",
        ["systemctl"] = "/usr/bin/systemctl",
    };

    private readonly IReadOnlyDictionary<string, string> _programs;
    private readonly TimeSpan _timeout;
    private readonly ApplyDeadline? _deadline;

    /// <param name="timeout">The longest any one program may run.</param>
    /// <param name="deadline">The deadline of the change being applied, which shortens that (REQ-API-022).</param>
    public ProcessRunner(TimeSpan timeout, ApplyDeadline? deadline = null) : this(AgentPrograms, timeout, deadline) { }

    /// <summary>For the tests of the runner itself, which need programs of their own.</summary>
    internal ProcessRunner(IReadOnlyDictionary<string, string> programs, TimeSpan timeout, ApplyDeadline? deadline = null)
    {
        _programs = programs;
        _timeout = timeout;
        _deadline = deadline;
    }

    public ProcessResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null)
    {
        if (!_programs.TryGetValue(program, out var path))
            throw new InvalidOperationException($"'{program}' is not a program the agent may start.");

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        var allowed = _deadline?.Remaining(_timeout) ?? _timeout;
        if (allowed <= TimeSpan.Zero)
            throw new PlatformException($"The change ran out of time before {program} {string.Join(' ', arguments)}.");

        using var process = Process.Start(info) ?? throw new PlatformException($"{program} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (standardInput is not null) process.StandardInput.Write(standardInput);
        process.StandardInput.Close();

        if (!process.WaitForExit(allowed))
        {
            process.Kill(entireProcessTree: true);
            throw new PlatformException($"{program} {string.Join(' ', arguments)} did not finish within {allowed.TotalSeconds:0.#} s.");
        }
        return new ProcessResult(process.ExitCode, output.Result, error.Result);
    }
}
