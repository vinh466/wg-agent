using WgAgent.Core.Model;
using WgAgent.Core.Render;
using WgAgent.Core.Store;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Core.Apply;

/// <summary>What applying a change did: whether the interface's sessions were interrupted (REQ-APL-007).</summary>
public sealed record ApplyResult(bool Restarted);

/// <summary>
/// Takes an interface from one stored state to the next through its file and its unit
/// (REQ-APL-001). A peer change synchronises (REQ-APL-005); addresses, MTU, hooks and site-to-site
/// peers restart (REQ-APL-006); a unit is stopped before its file is replaced (REQ-APL-010); a
/// failure restores the previous file and running state (REQ-APL-008).
/// </summary>
/// <param name="deadline">
/// The deadline of the change, shared with the adapters; a restoration ends it, so each program the
/// restoration runs has its own bound (REQ-APL-012).
/// </param>
public sealed class Applier(IWireGuardTool wg, IUnitManager units, IConfigDirectory files, ApplyDeadline? deadline = null)
{
    public ApplyResult Apply(string name, StoredInterface? before, StoredInterface? after)
    {
        if (before is null && after is null) return new ApplyResult(false);
        if (before is null) return Create(name, after!);
        if (after is null) return Delete(name, before);
        return Update(name, before, after);
    }

    private ApplyResult Create(string name, StoredInterface after)
    {
        try
        {
            files.Write(name, Render(after));
            if (IsEnabled(after))
            {
                units.Enable(name);
                units.Start(name);
            }
            return new ApplyResult(false);
        }
        catch (PlatformException failure)
        {
            var restore = Attempt(() => { units.Stop(name); units.Disable(name); files.Delete(name); });
            throw Failed(name, failure, restore);
        }
    }

    private ApplyResult Delete(string name, StoredInterface before)
    {
        try
        {
            units.Stop(name);
            units.Disable(name);
            files.Delete(name);
            return new ApplyResult(false);
        }
        catch (PlatformException failure)
        {
            var restore = Attempt(() =>
            {
                files.Write(name, Render(before));
                if (IsEnabled(before)) { units.Enable(name); units.Start(name); }
            });
            throw Failed(name, failure, restore);
        }
    }

    private ApplyResult Update(string name, StoredInterface before, StoredInterface after)
    {
        var oldText = Render(before);
        var newText = Render(after);
        bool wasEnabled = IsEnabled(before), isEnabled = IsEnabled(after);

        void RestoreRunning()
        {
            if (wasEnabled) Attempt(() => units.Stop(name));
            files.Write(name, oldText);
            if (wasEnabled) { units.Enable(name); units.Start(name); }
            else { Attempt(() => units.Stop(name)); units.Disable(name); }
        }

        try
        {
            if (wasEnabled && !isEnabled)
            {
                units.Stop(name);
                units.Disable(name);
                files.Write(name, newText);
                return new ApplyResult(false);
            }
            if (!wasEnabled && isEnabled)
            {
                files.Write(name, newText);
                units.Enable(name);
                units.Start(name);
                return new ApplyResult(false);
            }
            if (!isEnabled)
            {
                if (newText != oldText) files.Write(name, newText);
                return new ApplyResult(false);
            }
            if (NeedsRestart(before, after))
            {
                units.Stop(name);
                files.Write(name, newText);
                units.Start(name);
                return new ApplyResult(true);
            }
            if (newText != oldText) Synchronise(name, before, after, oldText, newText);
            return new ApplyResult(false);
        }
        catch (PlatformException failure)
        {
            var restore = Attempt(RestoreRunning);
            throw Failed(name, failure, restore);
        }
    }

    /// <summary>
    /// A peer change, applied without a restart. A failure is undone the same way — the old file and
    /// the old configuration synchronised back — so the other peers keep their sessions; only when
    /// that fails too is the unit restarted on the old file.
    /// </summary>
    private void Synchronise(string name, StoredInterface before, StoredInterface after, string oldText, string newText)
    {
        try
        {
            files.Write(name, newText);
            wg.SyncConfig(name, ConfigRenderer.WireGuardOnly(after.Spec, after.Peers));
        }
        catch (PlatformException failure)
        {
            var restore = Attempt(() =>
            {
                files.Write(name, oldText);
                wg.SyncConfig(name, ConfigRenderer.WireGuardOnly(before.Spec, before.Peers));
            });
            if (restore is not null)
                restore = Attempt(() => { Attempt(() => units.Stop(name)); files.Write(name, oldText); units.Start(name); });
            throw Failed(name, failure, restore);
        }
    }

    /// <summary>REQ-APL-006: what <c>wg syncconf</c> cannot apply.</summary>
    public static bool NeedsRestart(StoredInterface before, StoredInterface after)
    {
        if (!(before.Spec.Addresses ?? []).SequenceEqual(after.Spec.Addresses ?? [])) return true;
        if ((before.Spec.Mtu ?? Defaults.Mtu) != (after.Spec.Mtu ?? Defaults.Mtu)) return true;
        if (!(before.Spec.PostUp ?? []).SequenceEqual(after.Spec.PostUp ?? [])) return true;
        if (!(before.Spec.PostDown ?? []).SequenceEqual(after.Spec.PostDown ?? [])) return true;

        var subnets = Networks.SubnetsOf(after.Spec.Addresses ?? []);
        Dictionary<string, string> SiteToSite(StoredInterface state) => state.Peers
            .Where(p => (p.Value.AllowedIps ?? []).Any(e => Cidr.TryParse(e, out var c) && !Networks.IsWithin(c, subnets)))
            .ToDictionary(p => p.Key, p => ConfigRenderer.PeerSection(p.Key, p.Value));
        var old = SiteToSite(before);
        var next = SiteToSite(after);
        return old.Count != next.Count || old.Any(p => !next.TryGetValue(p.Key, out var text) || text != p.Value);
    }

    private static bool IsEnabled(StoredInterface state) => state.Spec.Enabled ?? Defaults.Enabled;

    private static string Render(StoredInterface state) => ConfigRenderer.File(state.Spec, state.Peers);

    /// <summary>Runs a restoration step, returning its failure rather than throwing it.</summary>
    private string? Attempt(Action step)
    {
        deadline?.End();
        try { step(); return null; }
        catch (PlatformException e) { return e.Message; }
    }

    private static AgentException Failed(string name, PlatformException failure, string? restoreFailure) =>
        new(ReasonCodes.ApplyFailed, restoreFailure is null
            ? $"Applying the change to '{name}' failed, and the previous configuration was restored: {failure.Message}"
            : $"Applying the change to '{name}' failed: {failure.Message}. Restoring the previous configuration failed too: {restoreFailure}");
}
