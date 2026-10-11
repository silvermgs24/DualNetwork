using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;

namespace DualNetwork.Windows;

/// <summary>
/// Manages NRPT (Name Resolution Policy Table) rules through the DnsClient PowerShell module.
/// Every rule we create carries the comment "DualNetwork"; we refuse to remove any rule without it.
/// </summary>
public sealed class NrptManager : IDnsPolicyWriter
{
    private static readonly Regex NamespacePattern = new(@"^\.[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$", RegexOptions.Compiled);
    private static readonly Regex GuidPattern = new(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<NrptRule>> ListRulesAsync(CancellationToken ct = default)
    {
        const string script =
            "$rules = @(Get-DnsClientNrptRule | ForEach-Object { [pscustomobject]@{ " +
            "Name = [string]$_.Name; Namespace = @($_.Namespace); NameServers = @($_.NameServers); Comment = [string]$_.Comment } }); " +
            "ConvertTo-Json -InputObject $rules -Compress -Depth 4";
        var r = await PowerShellRunner.RunAsync(script, ct: ct);
        if (!r.Ok) throw new InvalidOperationException($"Get-DnsClientNrptRule failed: {r.StdErr}");
        if (string.IsNullOrWhiteSpace(r.StdOut)) return Array.Empty<NrptRule>();

        var list = new List<NrptRule>();
        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        foreach (var e in items)
        {
            var name = e.GetProperty("Name").GetString() ?? "";
            var comment = e.TryGetProperty("Comment", out var c) ? c.GetString() : null;
            var servers = Strings(e, "NameServers");
            // A rule can list several namespaces; flatten to one entry per namespace (same rule name).
            foreach (var ns in Strings(e, "Namespace"))
                list.Add(new NrptRule(name, ns, servers, comment));
        }
        return list;
    }

    private static List<string> Strings(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return new();
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
            JsonValueKind.String => new() { v.GetString()! },
            _ => new(),
        };
    }

    public async Task<NrptRuleSpec> AddRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        var ns = spec.Namespace.Trim().ToLowerInvariant();
        if (!NamespacePattern.IsMatch(ns)) throw new ArgumentException($"Invalid DNS namespace '{spec.Namespace}'");
        var servers = spec.NameServers.Select(s => IPAddress.Parse(s).ToString()).ToList();
        if (servers.Count == 0) throw new ArgumentException("An NRPT rule needs at least one DNS server");

        var existing = await ListRulesAsync(ct);
        var conflict = existing.FirstOrDefault(r => r.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase) && !r.IsOurs);
        if (conflict is not null)
            throw new InvalidOperationException($"An NRPT rule for {ns} already exists and was not created by Dual Network; not modifying it.");

        var serverList = string.Join(",", servers.Select(s => $"'{s}'"));
        var script =
            $"$r = Add-DnsClientNrptRule -Namespace '{ns}' -NameServers @({serverList}) -Comment '{NrptTag.Comment}' " +
            $"-DisplayName 'Dual Network: {ns}' -PassThru; if ($r) {{ [string]$r.Name }}";
        var res = await PowerShellRunner.RunAsync(script, ct: ct);
        if (!res.Ok) throw new InvalidOperationException($"Add-DnsClientNrptRule failed: {res.StdErr}");

        var name = res.StdOut.Split('\n').Select(s => s.Trim()).LastOrDefault(s => GuidPattern.IsMatch(s));
        if (name is null)
        {
            // Older builds don't return the rule from -PassThru: find it by namespace + tag.
            var known = existing.Where(r => r.IsOurs).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            name = (await ListRulesAsync(ct)).FirstOrDefault(r => r.IsOurs && r.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase) && !known.Contains(r.Name))?.Name;
        }
        if (name is null) throw new InvalidOperationException($"NRPT rule for {ns} was not found after creating it");
        return spec with { RuleName = name };
    }

    public async Task<WriteOutcome> RemoveRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        if (spec.RuleName is null || !GuidPattern.IsMatch(spec.RuleName))
            throw new ArgumentException($"Cannot remove NRPT rule for {spec.Namespace}: unknown rule name");

        var rules = await ListRulesAsync(ct);
        var rule = rules.FirstOrDefault(r => r.Name.Equals(spec.RuleName, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return WriteOutcome.NotFound;
        if (!rule.IsOurs) throw new InvalidOperationException($"NRPT rule {spec.RuleName} is not tagged as a Dual Network rule; refusing to remove it.");

        var res = await PowerShellRunner.RunAsync($"Remove-DnsClientNrptRule -Name '{spec.RuleName}' -Force", ct: ct);
        if (!res.Ok) throw new InvalidOperationException($"Remove-DnsClientNrptRule failed: {res.StdErr}");
        return WriteOutcome.Removed;
    }

    public async Task FlushCacheAsync(CancellationToken ct = default)
    {
        var res = await PowerShellRunner.RunAsync("Clear-DnsClientCache", ct: ct);
        if (!res.Ok) throw new InvalidOperationException(res.StdErr);
    }
}
