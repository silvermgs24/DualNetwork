using System.Text.Json;
using System.Text.Json.Serialization;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;

namespace DualNetwork.Core.Control;

public sealed record CaptureOptions(bool ProbeInternet = true, bool IncludeNrpt = true);

/// <summary>Reads the current networking state from the OS.</summary>
public interface INetworkStateProvider
{
    Task<NetworkState> CaptureAsync(CaptureOptions options, CancellationToken ct = default);
}

public enum WriteOutcome { Created, AlreadyExists, Removed, NotFound }

/// <summary>Adds/removes individual routes. Implementations must validate input and never touch routes other than the one given.</summary>
public interface IRouteWriter
{
    /// <summary>False when the process lacks the rights to change routes (not elevated).</summary>
    bool CanModify { get; }
    WriteOutcome AddRoute(RouteSpec spec, NetworkState current);
    WriteOutcome RemoveRoute(RouteSpec spec);
}

/// <summary>Adds/removes NRPT rules created by this application.</summary>
public interface IDnsPolicyWriter
{
    Task<NrptRuleSpec> AddRuleAsync(NrptRuleSpec spec, CancellationToken ct = default);
    Task<WriteOutcome> RemoveRuleAsync(NrptRuleSpec spec, CancellationToken ct = default);
    Task<IReadOnlyList<NrptRule>> ListRulesAsync(CancellationToken ct = default);
    Task FlushCacheAsync(CancellationToken ct = default);
}

public interface IStateStore
{
    Journal LoadJournal();
    void SaveJournal(Journal journal);
    string SaveSnapshot(ConfigurationSnapshot snapshot);
    ConfigurationSnapshot? LoadSnapshot(string path);
}

public enum LogLevel { Detail, Info, Success, Warning, Error }

public interface IActivityLog
{
    void Write(LogLevel level, string message);
}

/// <summary>Every change this application has made and not yet undone. Persisted after each individual change.</summary>
public sealed class Journal
{
    public bool Enabled { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public string? SnapshotPath { get; set; }
    public int ProcessId { get; set; }
    public List<RouteSpec> Routes { get; set; } = new();
    public List<NrptRuleSpec> NrptRules { get; set; } = new();

    [JsonIgnore] public bool HasChanges => Routes.Count > 0 || NrptRules.Count > 0;

    public OwnedChanges ToOwned() =>
        new(Routes.Select(r => r.Key).ToList(), NrptRules.Where(n => n.RuleName is not null).Select(n => n.RuleName!).ToList());
}

/// <summary>The configuration captured right before Dual Network Mode made its first change.</summary>
public sealed record ConfigurationSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string MachineName { get; init; } = Environment.MachineName;
    public string Reason { get; init; } = "";
    public NetworkState State { get; init; } = new();
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        o.Converters.Add(new IPAddressJsonConverter());
        o.Converters.Add(new IPv4PrefixJsonConverter());
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

public enum VerificationIssue { None, ClientDnsUnusable }

public sealed record VerificationResult(bool Ok, string Message, VerificationIssue Issue = VerificationIssue.None);

/// <summary>Checks, after changes were applied, that the OS really routes as planned (ground truth, not simulation).</summary>
public interface IRouteVerifier
{
    IReadOnlyList<VerificationResult> Verify(RoutingPlan plan, IReadOnlyList<RouteSpec> applied);
}
