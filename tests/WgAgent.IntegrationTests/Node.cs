using System.Diagnostics;

namespace WgAgent.IntegrationTests;

/// <summary>
/// One node: a privileged container running its distribution's systemd, with the published
/// wg-agent installed. A test helper, so it may start docker, bash and wg freely.
/// </summary>
public sealed class Node : IDisposable
{
    private const string Image = "wgagent-it:debian13";
    public string Container { get; } = "wgagent-it-" + Guid.NewGuid().ToString("N")[..8];

    public Node()
    {
        var binary = Environment.GetEnvironmentVariable("WGAGENT_TEST_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(Path.Combine(binary, "wg-agent")))
            throw new InvalidOperationException("WGAGENT_TEST_BINARY must name the directory of a published wg-agent; run `make test-integration`.");

        Docker(TimeSpan.FromMinutes(10), "build", "-q", "-t", Image, Path.Combine(AppContext.BaseDirectory, "Node"));
        Docker(TimeSpan.FromMinutes(1), "run", "-d", "--name", Container, "--privileged", "--tmpfs", "/run", "--tmpfs", "/run/lock", Image);
        WaitForSystemd();
        Docker(TimeSpan.FromMinutes(1), "cp", binary, $"{Container}:/opt/wg-agent");
        Must("ln -s /opt/wg-agent/wg-agent /usr/local/bin/wg-agent");
    }

    public void Dispose() => Docker(TimeSpan.FromMinutes(1), "rm", "-f", Container);

    public sealed record Result(int Code, string Out, string Error);

    /// <summary>Runs a bash script in the container.</summary>
    public Result Run(string script, TimeSpan? timeout = null) =>
        Start(timeout ?? TimeSpan.FromMinutes(1), "exec", Container, "bash", "-c", script);

    /// <summary>Runs a script that must succeed, and returns its standard output.</summary>
    public string Must(string script, TimeSpan? timeout = null)
    {
        var result = Run(script, timeout);
        if (result.Code != 0) throw new InvalidOperationException($"`{script}` exited {result.Code}: {result.Error}{result.Out}");
        return result.Out;
    }

    /// <summary>Restarts the container, so systemd boots it again.</summary>
    public void Reboot()
    {
        Docker(TimeSpan.FromMinutes(2), "restart", "-t", "30", Container);
        WaitForSystemd();
    }

    private void WaitForSystemd()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(2))
        {
            var state = Run("systemctl is-system-running").Out.Trim();
            if (state is "running" or "degraded") return;
            Thread.Sleep(500);
        }
        throw new InvalidOperationException($"systemd in {Container} did not finish booting.");
    }

    private static void Docker(TimeSpan timeout, params string[] arguments)
    {
        var result = Start(timeout, arguments);
        if (result.Code != 0) throw new InvalidOperationException($"docker {string.Join(' ', arguments)} exited {result.Code}: {result.Error}");
    }

    private static Result Start(TimeSpan timeout, params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"docker {string.Join(' ', arguments)} ran longer than {timeout}.");
        }
        return new Result(process.ExitCode, output.Result, error.Result);
    }
}
