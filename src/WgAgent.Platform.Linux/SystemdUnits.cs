namespace WgAgent.Platform.Linux;

/// <summary><see cref="IUnitManager"/> through <c>systemctl</c>, for the unit wg-quick@&lt;name&gt;.</summary>
public sealed class SystemdUnits : IUnitManager
{
    private readonly IProcessRunner _runner;

    public SystemdUnits(TimeSpan timeout, ApplyDeadline? deadline = null) : this(new ProcessRunner(timeout, deadline)) { }
    internal SystemdUnits(IProcessRunner runner) => _runner = runner;

    public void Enable(string interfaceName) => Systemctl("enable", interfaceName);
    public void Disable(string interfaceName) => Systemctl("disable", interfaceName);
    public void Start(string interfaceName) => Systemctl("start", interfaceName);
    public void Stop(string interfaceName) => Systemctl("stop", interfaceName);

    public bool IsActive(string interfaceName) =>
        _runner.Run("systemctl", ["is-active", "--quiet", Unit(interfaceName)]).ExitCode == 0;

    private void Systemctl(string verb, string interfaceName)
    {
        var result = _runner.Run("systemctl", [verb, "--quiet", Unit(interfaceName)]);
        if (result.ExitCode != 0)
            throw new PlatformException($"systemctl {verb} {Unit(interfaceName)}: {result.StandardError.Trim()}");
    }

    private static string Unit(string interfaceName) => $"wg-quick@{interfaceName}.service";
}
