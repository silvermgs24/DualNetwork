using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;

namespace DualNetwork.Core.Control;

public sealed record OperationResult(bool Success, string Message, RoutingPlan? Plan = null, IReadOnlyList<string>? Details = null)
{
    /// <summary>The network changed between preview and apply; the user must review the new plan.</summary>
    public bool NeedsReview { get; init; }
    /// <summary>Routes or DNS rules were added or removed by this operation.</summary>
    public bool ChangesMade { get; init; }
    public IReadOnlyList<string> DetailLines => Details ?? Array.Empty<string>();
}

/// <summary>
/// Orchestrates enable / reconcile / disable / recovery. Owns the journal, so it is the only component
/// that decides what may be removed. Independent of the UI and of Windows (everything OS-specific is injected).
/// </summary>
public sealed class DualNetworkController
{
    private readonly INetworkStateProvider _provider;
    private readonly IRouteWriter _routes;
    private readonly IDnsPolicyWriter _dns;
    private readonly IStateStore _store;
    private readonly IActivityLog _log;
    private readonly Func<PlannerSettings> _settings;
    private readonly IRouteVerifier? _verifier;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DualNetworkController(INetworkStateProvider provider, IRouteWriter routes, IDnsPolicyWriter dns,
        IStateStore store, IActivityLog log, Func<PlannerSettings> settings, IRouteVerifier? verifier = null)
    {
        _provider = provider;
        _routes = routes;
        _dns = dns;
        _store = store;
        _log = log;
        _settings = settings;
        _verifier = verifier;
        Journal = store.LoadJournal();
        // A journal from another process means the app was closed or crashed while changes were active.
        HasLeftovers = Journal.HasChanges || Journal.Enabled;
        if (HasLeftovers) Journal.Enabled = false;
    }

    public Journal Journal { get; private set; }
    public bool IsEnabled => Journal.Enabled;
    public bool CanModify => _routes.CanModify;
    /// <summary>True when a previous session left changes in place (crash, kill, or "keep" on exit).</summary>
    public bool HasLeftovers { get; private set; }
    public RoutingPlan? LastPlan { get; private set; }
    public NetworkState? LastState { get; private set; }
    public DateTimeOffset? LastReconcile { get; private set; }

    public event EventHandler? Changed;

    // ------------------------------------------------------------------ preview

    public async Task<RoutingPlan> PreviewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await _provider.CaptureAsync(new CaptureOptions(), ct);
            return Plan(state);
        }
        finally { _gate.Release(); }
    }

    private RoutingPlan Plan(NetworkState state)
    {
        var plan = RoutingPlanner.Build(state, _settings(), Journal.ToOwned());
        foreach (var e in NetworkChangeDescriber.Describe(LastState, state)) _log.Write(LogLevel.Info, e);
        LastState = state;
        LastPlan = plan;
        return plan;
    }

    // ------------------------------------------------------------------ enable

    public async Task<OperationResult> EnableAsync(RoutingPlan reviewed, CancellationToken ct = default)
    {
        if (!_routes.CanModify) return new OperationResult(false, "Administrator rights are required to change routes.");
        await _gate.WaitAsync(ct);
        try
        {
            if (Journal.Enabled) return new OperationResult(true, "Dual Network Mode is already enabled.", LastPlan);
            if (HasLeftovers) return new OperationResult(false, "Changes from a previous session are still active. Restore or resume them first.");

            _log.Write(LogLevel.Info, "Enabling Dual Network Mode — capturing current configuration");
            var state = await _provider.CaptureAsync(new CaptureOptions(), ct);
            var plan = Plan(state);
            foreach (var t in plan.Trace) _log.Write(LogLevel.Detail, t);

            if (plan.Status == PlanStatus.Refused)
            {
                foreach (var r in plan.RefusalReasons) _log.Write(LogLevel.Warning, r);
                return new OperationResult(false, "Dual Network Mode cannot be enabled safely right now.", plan, plan.RefusalReasons);
            }
            if (plan.Fingerprint != reviewed.Fingerprint)
            {
                _log.Write(LogLevel.Warning, "The network changed while the changes were being reviewed — nothing applied");
                return new OperationResult(false, "The network changed since you reviewed the changes. Please review the updated plan.", plan) { NeedsReview = true };
            }

            var snapshotPath = _store.SaveSnapshot(new ConfigurationSnapshot { State = state, Reason = "Before enabling Dual Network Mode" });
            _log.Write(LogLevel.Detail, $"Configuration snapshot saved: {snapshotPath}");

            Journal = new Journal
            {
                Enabled = true,
                EnabledAt = DateTimeOffset.Now,
                SnapshotPath = snapshotPath,
                ProcessId = Environment.ProcessId,
            };
            _store.SaveJournal(Journal);

            var diff = Reconciler.Diff(Array.Empty<RouteSpec>(), Array.Empty<NrptRuleSpec>(), plan.Routes, plan.NrptRules, state);
            try
            {
                await ApplyAsync(diff, state, ct);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, $"Applying changes failed: {ex.Message} — rolling back");
                await RemoveEverythingAsync(ct);
                Journal.Enabled = false;
                _store.SaveJournal(Journal);
                return new OperationResult(false, $"Applying changes failed and was rolled back: {ex.Message}", plan);
            }

            LogOutcome(plan);
            _log.Write(LogLevel.Success, "Dual Network Mode enabled");
            LastReconcile = DateTimeOffset.Now;
            return new OperationResult(true, "Dual Network Mode enabled.", plan);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ automatic mode

    /// <summary>
    /// Turns on automatic management without a reviewed plan: from now on every network change is reconciled,
    /// changes are applied only when the plan is safe, and the mode waits in standby otherwise.
    /// </summary>
    public async Task<OperationResult> ActivateAsync(string reason, CancellationToken ct = default)
    {
        if (!_routes.CanModify) return new OperationResult(false, "Administrator rights are required to change routes.");
        await _gate.WaitAsync(ct);
        try
        {
            if (HasLeftovers) return new OperationResult(false, "Changes from a previous session are still active. Restore or resume them first.");
            if (!Journal.Enabled)
            {
                Journal.Enabled = true;
                Journal.EnabledAt = DateTimeOffset.Now;
                Journal.ProcessId = Environment.ProcessId;
                _store.SaveJournal(Journal);
                _log.Write(LogLevel.Success, $"Dual Network Mode on ({reason}) — routing is managed automatically");
                LastPlan = null; // force the outcome to be logged on the first reconcile
            }
        }
        finally { _gate.Release(); }
        return await ReconcileAsync(reason, ct);
    }

    private bool Verify(RoutingPlan plan)
    {
        if (_verifier is null) return true;
        IReadOnlyList<VerificationResult> results;
        try { results = _verifier.Verify(plan, Journal.Routes); }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, $"Self-check could not run: {ex.Message}");
            return true;
        }
        foreach (var r in results) _log.Write(r.Ok ? LogLevel.Success : LogLevel.Error, (r.Ok ? "Self-check ✓ " : "Self-check ✗ ") + r.Message);
        return results.All(r => r.Ok);
    }

    // ------------------------------------------------------------------ reconcile

    /// <summary>Recompute the plan after a network change and converge the applied changes to it.</summary>
    public async Task<OperationResult> ReconcileAsync(string trigger, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await _provider.CaptureAsync(new CaptureOptions(), ct);
            var previous = LastPlan;
            var plan = Plan(state);
            if (!Journal.Enabled) return new OperationResult(true, "Not enabled", plan);

            var desiredRoutes = plan.Status == PlanStatus.Refused ? Array.Empty<RouteSpec>() : plan.Routes;
            var desiredNrpt = plan.Status == PlanStatus.Refused ? Array.Empty<NrptRuleSpec>() : plan.NrptRules;
            var diff = Reconciler.Diff(Journal.Routes, Journal.NrptRules, desiredRoutes, desiredNrpt, state);
            var statusChanged = previous is null || previous.Status != plan.Status;

            if (diff.IsEmpty && !statusChanged)
            {
                LastReconcile = DateTimeOffset.Now;
                return new OperationResult(true, "No changes needed", plan);
            }

            _log.Write(LogLevel.Info, $"{trigger} — re-evaluating routing");
            foreach (var t in plan.Trace) _log.Write(LogLevel.Detail, t);

            if (!_routes.CanModify)
            {
                _log.Write(LogLevel.Error, "Cannot adjust routes: not running as Administrator");
                return new OperationResult(false, "Administrator rights are required.", plan);
            }

            if (plan.Status == PlanStatus.Refused)
                foreach (var r in plan.RefusalReasons) _log.Write(LogLevel.Warning, "Standing by: " + r);

            // Snapshot right before the first change of an active period, so restore is verified against
            // the configuration that existed just before anything was touched.
            if (!Journal.HasChanges && (diff.RoutesToAdd.Count > 0 || diff.NrptToAdd.Count > 0))
            {
                Journal.SnapshotPath = _store.SaveSnapshot(new ConfigurationSnapshot { State = state, Reason = $"Before applying changes ({trigger})" });
                _store.SaveJournal(Journal);
                _log.Write(LogLevel.Detail, $"Configuration snapshot saved: {Journal.SnapshotPath}");
            }

            try
            {
                await ApplyAsync(diff, state, ct);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, $"Re-evaluation failed: {ex.Message}");
                return new OperationResult(false, ex.Message, plan) { ChangesMade = true };
            }

            var verified = true;
            if (plan.Status == PlanStatus.Refused)
                _log.Write(LogLevel.Warning, Journal.HasChanges
                    ? "Standing by — some earlier changes could not be removed (see above)"
                    : "Standing by — all Dual Network changes removed; Windows default routing is in effect");
            else
            {
                LogOutcome(plan);
                verified = Verify(plan);
            }
            LastReconcile = DateTimeOffset.Now;
            return new OperationResult(verified, verified ? "Re-evaluated" : "Applied, but the self-check found a problem (see log)", plan)
            {
                ChangesMade = !diff.IsEmpty,
            };
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ disable

    public async Task<OperationResult> DisableAsync(CancellationToken ct = default)
    {
        if (!_routes.CanModify && Journal.HasChanges)
            return new OperationResult(false, "Administrator rights are required to remove the changes.");
        await _gate.WaitAsync(ct);
        try
        {
            _log.Write(LogLevel.Info, "Disabling Dual Network Mode — restoring previous configuration");
            var errors = await RemoveEverythingAsync(ct);
            await RemoveOrphanNrptAsync(ct);
            Journal.Enabled = false;
            _store.SaveJournal(Journal);
            HasLeftovers = Journal.HasChanges;

            var details = new List<string>(errors);
            var snapshot = Journal.SnapshotPath is null ? null : _store.LoadSnapshot(Journal.SnapshotPath);
            var after = await _provider.CaptureAsync(new CaptureOptions(ProbeInternet: false), ct);
            LastState = after;
            if (snapshot is not null)
            {
                var diffs = SnapshotComparer.Compare(snapshot.State, after);
                if (diffs.Count == 0)
                    _log.Write(LogLevel.Success, "Verified: routing configuration matches the snapshot taken before enabling");
                else
                {
                    _log.Write(LogLevel.Info, $"Differences from the snapshot ({diffs.Count}) — caused by network changes, not by Dual Network:");
                    foreach (var d in diffs) _log.Write(LogLevel.Info, "  " + d);
                    details.AddRange(diffs);
                }
            }

            if (errors.Count > 0)
            {
                _log.Write(LogLevel.Error, "Some changes could not be removed; they stay in the journal and will be retried");
                return new OperationResult(false, "Some changes could not be removed.", null, details);
            }
            Journal = new Journal();
            _store.SaveJournal(Journal);
            _log.Write(LogLevel.Success, "Dual Network Mode disabled — previous configuration restored");
            return new OperationResult(true, "Previous configuration restored.", null, details);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ recovery

    /// <summary>Resume monitoring leftover changes from a previous session instead of removing them.</summary>
    public async Task<OperationResult> ResumeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!HasLeftovers) return new OperationResult(true, "Nothing to resume.");
            await RemoveOrphanNrptAsync(ct);
            Journal.Enabled = true;
            Journal.ProcessId = Environment.ProcessId;
            _store.SaveJournal(Journal);
            HasLeftovers = false;
            _log.Write(LogLevel.Info, "Resumed Dual Network Mode from the previous session");
        }
        finally { _gate.Release(); }
        return await ReconcileAsync("Resumed");
    }

    /// <summary>Removes tagged NRPT rules that are not in the journal (left behind by a crash).</summary>
    private async Task RemoveOrphanNrptAsync(CancellationToken ct)
    {
        if (!_routes.CanModify) return;
        IReadOnlyList<NrptRule> rules;
        try { rules = await _dns.ListRulesAsync(ct); }
        catch (Exception ex) { _log.Write(LogLevel.Warning, $"Could not list NRPT rules: {ex.Message}"); return; }

        var known = Journal.NrptRules.Select(n => n.RuleName).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules.Where(r => r.IsOurs && !known.Contains(r.Name)))
        {
            try
            {
                await _dns.RemoveRuleAsync(new NrptRuleSpec(r.Namespace, r.NameServers, "orphan") { RuleName = r.Name }, ct);
                _log.Write(LogLevel.Info, $"Removed leftover DNS rule {r.Namespace} from an earlier session");
            }
            catch (Exception ex) { _log.Write(LogLevel.Warning, $"Could not remove leftover DNS rule {r.Namespace}: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ apply helpers

    private async Task ApplyAsync(ReconcileResult diff, NetworkState state, CancellationToken ct)
    {
        foreach (var r in diff.RoutesToRemove)
        {
            var outcome = _routes.RemoveRoute(r);
            Journal.Routes.RemoveAll(x => x.Key == r.Key);
            _store.SaveJournal(Journal);
            _log.Write(LogLevel.Info, outcome == WriteOutcome.NotFound
                ? $"Route {r} was already gone"
                : $"Removed route {r} (no longer needed)");
        }

        var dnsChanged = false;
        foreach (var n in diff.NrptToRemove)
        {
            await _dns.RemoveRuleAsync(n, ct);
            Journal.NrptRules.RemoveAll(x => x.Key == n.Key);
            _store.SaveJournal(Journal);
            dnsChanged = true;
            _log.Write(LogLevel.Info, $"Removed DNS rule {n} (no longer needed)");
        }

        foreach (var r in diff.RoutesToAdd)
        {
            var wasOwned = Journal.Routes.Any(x => x.Key == r.Key);
            var outcome = _routes.AddRoute(r, state);
            if (outcome == WriteOutcome.Created)
            {
                Journal.Routes.RemoveAll(x => x.Key == r.Key);
                Journal.Routes.Add(r);
                _store.SaveJournal(Journal);
                _log.Write(LogLevel.Success, $"{(wasOwned ? "Re-added" : "Added")} route {r.Destination} → {r.InterfaceName} via {r.NextHop}");
                _log.Write(LogLevel.Detail, $"  {r.ToPowerShell()}");
            }
            else if (!wasOwned)
            {
                // Someone else's identical route: never take ownership, so it is never removed by us.
                _log.Write(LogLevel.Info, $"Route {r} already exists — left as is and not taken over");
            }
        }

        foreach (var n in diff.NrptToAdd)
        {
            var created = await _dns.AddRuleAsync(n, ct);
            Journal.NrptRules.RemoveAll(x => x.Key == n.Key);
            Journal.NrptRules.Add(created);
            _store.SaveJournal(Journal);
            dnsChanged = true;
            _log.Write(LogLevel.Success, $"Added DNS rule {n}");
            _log.Write(LogLevel.Detail, $"  {n.ToPowerShell()}");
        }

        if (dnsChanged)
        {
            try { await _dns.FlushCacheAsync(ct); }
            catch (Exception ex) { _log.Write(LogLevel.Warning, $"DNS cache flush failed: {ex.Message}"); }
        }
    }

    /// <returns>Error messages for changes that could not be removed (they remain in the journal).</returns>
    private async Task<List<string>> RemoveEverythingAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var r in Journal.Routes.AsEnumerable().Reverse().ToList())
        {
            try
            {
                var outcome = _routes.RemoveRoute(r);
                Journal.Routes.Remove(r);
                _store.SaveJournal(Journal);
                _log.Write(LogLevel.Success, outcome == WriteOutcome.NotFound
                    ? $"Route {r.Destination} on {r.InterfaceName} was already gone"
                    : $"Removed route {r.Destination} → {r.InterfaceName} via {r.NextHop}");
            }
            catch (Exception ex)
            {
                errors.Add($"Could not remove route {r}: {ex.Message}");
                _log.Write(LogLevel.Error, errors[^1]);
            }
        }

        var hadDns = Journal.NrptRules.Count > 0;
        foreach (var n in Journal.NrptRules.AsEnumerable().Reverse().ToList())
        {
            try
            {
                await _dns.RemoveRuleAsync(n, ct);
                Journal.NrptRules.Remove(n);
                _store.SaveJournal(Journal);
                _log.Write(LogLevel.Success, $"Removed DNS rule {n.Namespace}");
            }
            catch (Exception ex)
            {
                errors.Add($"Could not remove DNS rule {n.Namespace}: {ex.Message}");
                _log.Write(LogLevel.Error, errors[^1]);
            }
        }
        if (hadDns)
        {
            try { await _dns.FlushCacheAsync(ct); } catch { /* best effort */ }
        }
        return errors;
    }

    private void LogOutcome(RoutingPlan plan)
    {
        if (plan.ClientAdapter is null || plan.EffectiveInternetAdapter is null) return;
        foreach (var w in plan.Warnings.Where(w => w.Severity >= Severity.Warning)) _log.Write(LogLevel.Warning, w.Text);
        foreach (var p in plan.ClientNetworks) _log.Write(LogLevel.Success, $"✓ Client network {p} → {plan.ClientAdapter.Name}");
        if (plan.Preserved.Count > 0) _log.Write(LogLevel.Success, $"✓ Client routes preserved ({plan.Preserved.Count})");
        _log.Write(LogLevel.Success, $"✓ Internet route → {plan.EffectiveInternetAdapter.Name}");
        _log.Write(LogLevel.Success, plan.NrptRules.Count > 0
            ? $"✓ Internal DNS ({string.Join(", ", plan.NrptRules.Select(n => n.Namespace.TrimStart('.')))}) → client DNS"
            : "✓ DNS configuration preserved");
    }
}
