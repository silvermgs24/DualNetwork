using System.Net;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using static DualNetwork.Core.Tests.Scenario;

namespace DualNetwork.Core.Tests;

/// <summary>In-memory "Windows": a mutable state whose routes/NRPT rules change when the fakes write.</summary>
internal sealed class FakeSystem : INetworkStateProvider, IRouteWriter, IDnsPolicyWriter, IStateStore, IActivityLog
{
    public NetworkState State;
    public Journal Saved = new();
    public readonly List<string> Log = new();
    public readonly List<string> Writes = new();
    public bool CanModify { get; set; } = true;
    public Func<RouteSpec, bool>? FailAdd;

    public FakeSystem(NetworkState state) => State = state;

    public Task<NetworkState> CaptureAsync(CaptureOptions options, CancellationToken ct = default) => Task.FromResult(State);

    public WriteOutcome AddRoute(RouteSpec spec, NetworkState current)
    {
        if (FailAdd?.Invoke(spec) == true) throw new InvalidOperationException("boom");
        if (State.Routes.Any(r => r.Key == spec.Key)) return WriteOutcome.AlreadyExists;
        State = State with { Routes = State.Routes.Append(new RouteEntry(spec.Destination, spec.NextHop, spec.InterfaceIndex, spec.Metric)).ToList() };
        Writes.Add("+" + spec.Key);
        return WriteOutcome.Created;
    }

    public WriteOutcome RemoveRoute(RouteSpec spec)
    {
        var before = State.Routes.Count;
        State = State with { Routes = State.Routes.Where(r => r.Key != spec.Key).ToList() };
        Writes.Add("-" + spec.Key);
        return State.Routes.Count < before ? WriteOutcome.Removed : WriteOutcome.NotFound;
    }

    public Task<NrptRuleSpec> AddRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        var name = Guid.NewGuid().ToString("B");
        State = State with { NrptRules = (State.NrptRules ?? Array.Empty<NrptRule>()).Append(new NrptRule(name, spec.Namespace, spec.NameServers, NrptTag.Comment)).ToList() };
        Writes.Add("+nrpt " + spec.Namespace);
        return Task.FromResult(spec with { RuleName = name });
    }

    public Task<WriteOutcome> RemoveRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        var rules = State.NrptRules ?? Array.Empty<NrptRule>();
        var target = rules.FirstOrDefault(r => r.Name == spec.RuleName);
        if (target is { IsOurs: false }) throw new InvalidOperationException("not ours");
        State = State with { NrptRules = rules.Where(r => r.Name != spec.RuleName).ToList() };
        Writes.Add("-nrpt " + spec.Namespace);
        return Task.FromResult(target is null ? WriteOutcome.NotFound : WriteOutcome.Removed);
    }

    public Task<IReadOnlyList<NrptRule>> ListRulesAsync(CancellationToken ct = default) =>
        Task.FromResult(State.NrptRules ?? (IReadOnlyList<NrptRule>)Array.Empty<NrptRule>());

    public Task FlushCacheAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Journal LoadJournal() => Clone(Saved);
    public void SaveJournal(Journal journal) => Saved = Clone(journal);
    public ConfigurationSnapshot? Snapshot;
    public string SaveSnapshot(ConfigurationSnapshot snapshot) { Snapshot = snapshot; return "snapshot.json"; }
    public ConfigurationSnapshot? LoadSnapshot(string path) => Snapshot;

    public void Write(LogLevel level, string message) => Log.Add($"{level}: {message}");

    private static Journal Clone(Journal j) => new()
    {
        Enabled = j.Enabled, EnabledAt = j.EnabledAt, SnapshotPath = j.SnapshotPath, ProcessId = j.ProcessId,
        Routes = j.Routes.ToList(), NrptRules = j.NrptRules.ToList(),
    };

    public IEnumerable<RouteEntry> RoutesOn(int index, int prefixLength) =>
        State.Routes.Where(r => r.InterfaceIndex == index && r.Destination.Length == prefixLength);

    public void ReplaceAdapter(int index, Func<AdapterInfo, AdapterInfo> change, Func<RouteEntry, bool>? dropRoutes = null)
    {
        State = State with
        {
            Adapters = State.Adapters.Select(a => a.Index == index ? change(a) : a).ToList(),
            Routes = State.Routes.Where(r => dropRoutes?.Invoke(r) != true).ToList(),
        };
    }
}

public class ControllerTests
{
    private static (DualNetworkController C, FakeSystem Sys) Make(NetworkState s, PlannerSettings? settings = null)
    {
        var sys = new FakeSystem(s);
        var c = new DualNetworkController(sys, sys, sys, sys, sys, () => settings ?? new PlannerSettings());
        return (c, sys);
    }

    private static NetworkState Hijacked() =>
        Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "10.10.10.10" }, suffix: "corp.local").Build();

    [Fact]
    public async Task Enable_snapshots_applies_and_journals_every_change()
    {
        var (c, sys) = Make(Hijacked());
        var plan = await c.PreviewAsync();

        var result = await c.EnableAsync(plan);

        Assert.True(result.Success, result.Message);
        Assert.True(c.IsEnabled);
        Assert.NotNull(sys.Snapshot);
        Assert.DoesNotContain(sys.Snapshot!.State.Routes, r => r.Destination.Length == 1); // snapshot is pre-change
        Assert.Equal(plan.Routes.Count, sys.Saved.Routes.Count);
        Assert.Single(sys.Saved.NrptRules);
        Assert.NotNull(sys.Saved.NrptRules[0].RuleName);
        Assert.Equal(2, sys.RoutesOn(WiFi, 1).Count());
    }

    [Fact]
    public async Task Disable_removes_exactly_what_was_added_and_verifies_against_snapshot()
    {
        var original = Hijacked();
        var (c, sys) = Make(original);
        await c.EnableAsync(await c.PreviewAsync());

        var result = await c.DisableAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(c.IsEnabled);
        Assert.Equal(original.Routes.Select(r => r.Key).OrderBy(k => k.ToString()), sys.State.Routes.Select(r => r.Key).OrderBy(k => k.ToString()));
        Assert.Empty(sys.State.NrptRules!);
        Assert.Empty(result.DetailLines); // no differences from the snapshot
        Assert.False(sys.Saved.HasChanges);
        Assert.Contains(sys.Log, l => l.Contains("matches the snapshot"));
    }

    [Fact]
    public async Task Pre_existing_identical_route_is_never_taken_over_or_removed()
    {
        // Someone (the client) already has a host route identical to one we'd plan... simulate by pre-creating our /1 on add.
        var (c, sys) = Make(Hijacked());
        var plan = await c.PreviewAsync();
        var clientRange = plan.Routes.First(r => r.Purpose == RoutePurpose.ClientRange);
        sys.FailAdd = null;
        // Insert the identical route right before apply (race with another tool).
        sys.State = sys.State with { Routes = sys.State.Routes.Append(new RouteEntry(clientRange.Destination, clientRange.NextHop, clientRange.InterfaceIndex, 1)).ToList() };

        // Fingerprint changed (planner now sees the route as the client's) → requires review.
        var first = await c.EnableAsync(plan);
        Assert.False(first.Success);
        Assert.True(first.NeedsReview);

        var second = await c.EnableAsync(await c.PreviewAsync());
        Assert.True(second.Success);
        Assert.DoesNotContain(sys.Saved.Routes, r => r.Key == clientRange.Key);

        await c.DisableAsync();
        Assert.Contains(sys.State.Routes, r => r.Key == clientRange.Key); // still there
    }

    [Fact]
    public async Task Failure_midway_rolls_back_everything()
    {
        var (c, sys) = Make(Hijacked());
        var original = sys.State.Routes.Select(r => r.Key).ToHashSet();
        sys.FailAdd = spec => spec.Purpose == RoutePurpose.ClientRange;

        var result = await c.EnableAsync(await c.PreviewAsync());

        Assert.False(result.Success);
        Assert.False(c.IsEnabled);
        Assert.True(sys.State.Routes.Select(r => r.Key).ToHashSet().SetEquals(original));
        Assert.False(sys.Saved.HasChanges);
    }

    [Fact]
    public async Task Enable_requires_admin()
    {
        var (c, sys) = Make(Hijacked());
        sys.CanModify = false;

        var result = await c.EnableAsync(await c.PreviewAsync());

        Assert.False(result.Success);
        Assert.Empty(sys.Writes);
    }

    [Fact]
    public async Task Refused_plan_is_not_applied()
    {
        var (c, sys) = Make(Create().WithWiFi("192.168.1.2/24").WithEthernet("192.168.1.3/24", gateway: "192.168.1.254").Build());

        var result = await c.EnableAsync(await c.PreviewAsync());

        Assert.False(result.Success);
        Assert.Empty(sys.Writes);
        Assert.Null(sys.Snapshot);
    }

    [Fact]
    public async Task Ethernet_unplugged_removes_all_changes_and_replug_restores_them()
    {
        var (c, sys) = Make(Hijacked());
        await c.EnableAsync(await c.PreviewAsync());
        var enabledRoutes = sys.State.Routes.Select(r => r.Key).ToHashSet();

        sys.ReplaceAdapter(Eth, a => a with { IsUp = false }, r => r.InterfaceIndex == Eth);
        await c.ReconcileAsync("Ethernet disconnected");

        Assert.True(c.IsEnabled);                       // still on, standing by
        Assert.False(sys.Saved.HasChanges);
        Assert.Empty(sys.RoutesOn(WiFi, 1));            // plain Wi-Fi routing
        Assert.Contains(sys.Log, l => l.Contains("Standing by"));

        sys.State = Hijacked() with { NrptRules = sys.State.NrptRules };
        await c.ReconcileAsync("Ethernet connected");

        Assert.True(sys.State.Routes.Select(r => r.Key).ToHashSet().SetEquals(enabledRoutes));
        Assert.Single(sys.Saved.NrptRules);
    }

    [Fact]
    public async Task Wifi_network_change_moves_the_override_to_the_new_gateway()
    {
        var (c, sys) = Make(Hijacked());
        await c.EnableAsync(await c.PreviewAsync());

        // Laptop roams to a different Wi-Fi: new subnet and gateway; Windows drops the old routes.
        sys.ReplaceAdapter(WiFi, a => a with
        {
            Addresses = new[] { new AdapterAddress(IPAddress.Parse("172.20.10.5"), 28) },
            Gateways = new[] { IPAddress.Parse("172.20.10.1") },
        }, r => r.InterfaceIndex == WiFi && r.Destination.Length != 1);
        sys.State = sys.State with
        {
            Routes = sys.State.Routes
                .Append(new RouteEntry(IPv4Prefix.Parse("172.20.10.0/28"), IPAddress.Any, WiFi, 256))
                .Append(new RouteEntry(IPv4Prefix.Default, IPAddress.Parse("172.20.10.1"), WiFi, 0)).ToList(),
        };

        await c.ReconcileAsync("Wi-Fi changed");

        var halves = sys.RoutesOn(WiFi, 1).ToList();
        Assert.Equal(2, halves.Count);
        Assert.All(halves, r => Assert.Equal("172.20.10.1", r.NextHop.ToString()));
        Assert.All(sys.Saved.Routes.Where(r => r.Purpose == RoutePurpose.InternetOverride), r => Assert.Equal("172.20.10.1", r.NextHop.ToString()));
        Assert.Contains(sys.Log, l => l.Contains("Internet route → Wi-Fi"));
    }

    [Fact]
    public async Task Full_tunnel_vpn_starting_makes_us_stand_down()
    {
        var (c, sys) = Make(Hijacked());
        await c.EnableAsync(await c.PreviewAsync());

        sys.State = sys.State with
        {
            Adapters = sys.State.Adapters.Append(new AdapterInfo
            {
                Index = Vpn, Name = "GlobalProtect", Kind = AdapterKind.Vpn, Product = "GlobalProtect", IsUp = true,
                Addresses = new[] { new AdapterAddress(IPAddress.Parse("10.200.0.9"), 32) },
            }).ToList(),
            Routes = sys.State.Routes.Append(new RouteEntry(IPv4Prefix.Default, IPAddress.Parse("10.200.0.1"), Vpn, 1)).ToList(),
        };

        await c.ReconcileAsync("VPN connected");

        Assert.Empty(sys.RoutesOn(WiFi, 1));
        Assert.Empty(sys.Saved.Routes);
        Assert.Contains(sys.Log, l => l.Contains("Full-tunnel VPN"));
    }

    [Fact]
    public async Task Route_dropped_by_windows_is_re_added()
    {
        var (c, sys) = Make(Hijacked());
        await c.EnableAsync(await c.PreviewAsync());
        var victim = sys.Saved.Routes[0];
        sys.State = sys.State with { Routes = sys.State.Routes.Where(r => r.Key != victim.Key).ToList() };

        await c.ReconcileAsync("Interface bounced");

        Assert.Contains(sys.State.Routes, r => r.Key == victim.Key);
        Assert.Single(sys.Saved.Routes, r => r.Key == victim.Key);
    }

    [Fact]
    public async Task Reconcile_without_changes_writes_nothing()
    {
        var (c, sys) = Make(Hijacked());
        await c.EnableAsync(await c.PreviewAsync());
        sys.Writes.Clear();

        await c.ReconcileAsync("Periodic check");
        await c.ReconcileAsync("Periodic check");

        Assert.Empty(sys.Writes);
    }

    [Fact]
    public async Task Crash_leftovers_are_detected_and_restored_on_next_start()
    {
        var (c1, sys) = Make(Hijacked());
        await c1.EnableAsync(await c1.PreviewAsync());
        // Process dies here. Next start:
        var c2 = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings());

        Assert.True(c2.HasLeftovers);
        Assert.False(c2.IsEnabled);
        Assert.False((await c2.EnableAsync(await c2.PreviewAsync())).Success); // must restore or resume first

        var r = await c2.DisableAsync();

        Assert.True(r.Success);
        Assert.Empty(sys.RoutesOn(WiFi, 1));
        Assert.Empty(sys.State.NrptRules!);
        Assert.False(c2.HasLeftovers);
    }

    [Fact]
    public async Task Resume_takes_over_leftovers_without_duplicating()
    {
        var (c1, sys) = Make(Hijacked());
        await c1.EnableAsync(await c1.PreviewAsync());
        var c2 = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings());
        sys.Writes.Clear();

        await c2.ResumeAsync();

        Assert.True(c2.IsEnabled);
        Assert.Empty(sys.Writes);
        Assert.Equal(2, sys.RoutesOn(WiFi, 1).Count());
    }

    [Fact]
    public async Task Orphan_tagged_nrpt_rules_are_cleaned_but_foreign_rules_are_kept()
    {
        var s = Hijacked() with
        {
            NrptRules = new[]
            {
                new NrptRule("{11111111-1111-1111-1111-111111111111}", ".old.client", new[] { "10.1.1.1" }, NrptTag.Comment),
                new NrptRule("{22222222-2222-2222-2222-222222222222}", ".corp.example", new[] { "10.2.2.2" }, "Set by IT"),
            },
        };
        var (c, sys) = Make(s);

        await c.DisableAsync();

        Assert.Single(sys.State.NrptRules!);
        Assert.Equal(".corp.example", sys.State.NrptRules![0].Namespace);
    }
}

public class ReconcilerTests
{
    private static RouteSpec R(string prefix, string hop = "192.168.1.1", int ifIndex = WiFi) =>
        new(IPv4Prefix.Parse(prefix), IPAddress.Parse(hop), ifIndex, "x", 5, RoutePurpose.InternetOverride, "");

    [Fact]
    public void Removes_stale_adds_new_keeps_present()
    {
        var applied = new[] { R("0.0.0.0/1"), R("10.0.0.0/8", "10.0.0.1", Eth) };
        var desired = new[] { R("0.0.0.0/1"), R("172.16.0.0/12", "10.0.0.1", Eth) };
        var state = new NetworkState { Routes = applied.Select(a => new RouteEntry(a.Destination, a.NextHop, a.InterfaceIndex, 5)).ToList() };

        var d = Reconciler.Diff(applied, Array.Empty<NrptRuleSpec>(), desired, Array.Empty<NrptRuleSpec>(), state);

        Assert.Equal("10.0.0.0/8", Assert.Single(d.RoutesToRemove).Destination.ToString());
        Assert.Equal("172.16.0.0/12", Assert.Single(d.RoutesToAdd).Destination.ToString());
        Assert.Equal("0.0.0.0/1", Assert.Single(d.RoutesKept).Destination.ToString());
    }

    [Fact]
    public void Never_proposes_removing_routes_that_are_not_in_the_journal()
    {
        var state = Scenario.Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").Build();

        var d = Reconciler.Diff(Array.Empty<RouteSpec>(), Array.Empty<NrptRuleSpec>(), Array.Empty<RouteSpec>(), Array.Empty<NrptRuleSpec>(), state);

        Assert.True(d.IsEmpty);
    }
}

public class AutomaticModeTests
{
    private sealed class FakeVerifier : IRouteVerifier
    {
        public bool Fail;
        public int Calls;
        public IReadOnlyList<VerificationResult> Verify(RoutingPlan plan, IReadOnlyList<RouteSpec> applied)
        {
            Calls++;
            return new[] { new VerificationResult(!Fail, Fail ? "Internet → Ethernet (expected Wi-Fi)" : "Internet → Wi-Fi") };
        }
    }

    private static NetworkState WifiOnly() => Create().WithWiFi().Build();
    private static NetworkState Hijacked() =>
        Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "10.10.10.10" }, suffix: "corp.local").Build();

    [Fact]
    public async Task Activation_without_client_network_waits_then_applies_when_cable_is_plugged_in()
    {
        var sys = new FakeSystem(WifiOnly());
        var verifier = new FakeVerifier();
        var c = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings(), verifier);

        var first = await c.ActivateAsync("App started");

        Assert.True(c.IsEnabled);
        Assert.Equal(PlanStatus.Refused, first.Plan!.Status);   // standby: nothing to do yet
        Assert.Empty(sys.Writes);
        Assert.Null(sys.Snapshot);

        sys.State = Hijacked() with { NrptRules = sys.State.NrptRules };
        var second = await c.ReconcileAsync("Network interface changed");

        Assert.True(second.ChangesMade);
        Assert.NotNull(sys.Snapshot);
        Assert.DoesNotContain(sys.Snapshot!.State.Routes, r => r.Destination.Length == 1); // snapshot taken before the change
        Assert.Equal(2, sys.RoutesOn(WiFi, 1).Count());
        Assert.Equal(1, verifier.Calls);

        var restore = await c.DisableAsync();
        Assert.True(restore.Success);
        Assert.Empty(restore.DetailLines);
    }

    [Fact]
    public async Task Activation_is_idempotent()
    {
        var sys = new FakeSystem(Hijacked());
        var c = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings());

        await c.ActivateAsync("App started");
        sys.Writes.Clear();
        var again = await c.ActivateAsync("Enable clicked");

        Assert.Empty(sys.Writes);
        Assert.False(again.ChangesMade);
    }

    [Fact]
    public async Task Failed_self_check_is_reported()
    {
        var sys = new FakeSystem(Hijacked());
        var c = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings(), new FakeVerifier { Fail = true });

        var r = await c.ActivateAsync("App started");

        Assert.False(r.Success);
        Assert.Contains(sys.Log, l => l.StartsWith("Error: Self-check ✗"));
    }

    [Fact]
    public async Task Activation_requires_admin_rights()
    {
        var sys = new FakeSystem(Hijacked()) { CanModify = false };
        var c = new DualNetworkController(sys, sys, sys, sys, sys, () => new PlannerSettings());

        Assert.False((await c.ActivateAsync("App started")).Success);
        Assert.False(c.IsEnabled);
    }
}
