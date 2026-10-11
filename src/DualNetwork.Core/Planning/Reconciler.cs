using DualNetwork.Core.Model;

namespace DualNetwork.Core.Planning;

public sealed record ReconcileResult(
    IReadOnlyList<RouteSpec> RoutesToRemove,
    IReadOnlyList<RouteSpec> RoutesToAdd,
    IReadOnlyList<RouteSpec> RoutesKept,
    IReadOnlyList<NrptRuleSpec> NrptToRemove,
    IReadOnlyList<NrptRuleSpec> NrptToAdd,
    IReadOnlyList<NrptRuleSpec> NrptKept)
{
    public bool IsEmpty => RoutesToRemove.Count == 0 && RoutesToAdd.Count == 0 && NrptToRemove.Count == 0 && NrptToAdd.Count == 0;
}

/// <summary>
/// Computes the difference between what we already applied (journal) and what the current plan wants.
/// Only ever proposes removing things that are in the journal.
/// </summary>
public static class Reconciler
{
    public static ReconcileResult Diff(
        IReadOnlyList<RouteSpec> appliedRoutes,
        IReadOnlyList<NrptRuleSpec> appliedNrpt,
        IReadOnlyList<RouteSpec> desiredRoutes,
        IReadOnlyList<NrptRuleSpec> desiredNrpt,
        NetworkState current)
    {
        var desiredKeys = desiredRoutes.Select(r => r.Key).ToHashSet();
        var appliedKeys = appliedRoutes.Select(r => r.Key).ToHashSet();
        var presentKeys = current.Routes.Select(r => r.Key).ToHashSet();

        var remove = appliedRoutes.Where(r => !desiredKeys.Contains(r.Key)).Reverse().ToList();
        // Add what we don't own yet, plus owned routes that Windows dropped (e.g. interface bounced).
        var add = desiredRoutes.Where(r => !appliedKeys.Contains(r.Key) || !presentKeys.Contains(r.Key)).ToList();
        var kept = appliedRoutes.Where(r => desiredKeys.Contains(r.Key) && presentKeys.Contains(r.Key)).ToList();

        var desiredNrptKeys = desiredNrpt.Select(n => n.Key).ToHashSet();
        var appliedNrptKeys = appliedNrpt.Select(n => n.Key).ToHashSet();
        var presentNrpt = current.NrptRules?.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool NrptPresent(NrptRuleSpec s) => presentNrpt is null || (s.RuleName is not null && presentNrpt.Contains(s.RuleName));

        var nrptRemove = appliedNrpt.Where(n => !desiredNrptKeys.Contains(n.Key)).Reverse().ToList();
        var nrptAdd = desiredNrpt.Where(n => !appliedNrptKeys.Contains(n.Key)
                                             || !NrptPresent(appliedNrpt.First(a => a.Key == n.Key))).ToList();
        var nrptKept = appliedNrpt.Where(n => desiredNrptKeys.Contains(n.Key) && NrptPresent(n)).ToList();

        return new ReconcileResult(remove, add, kept, nrptRemove, nrptAdd, nrptKept);
    }
}
