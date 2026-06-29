namespace pizzad;

public sealed record IncidentPlanExecutionOptionsV3(
    bool Enabled,
    bool DryRun,
    bool AllowLiveUpdateCurrent = false);

public sealed record IncidentPlanExecutionOperationV3(
    string PlanAction,
    string ExecutionAction,
    bool WouldMutate,
    bool Blocked,
    string BlockedBecause,
    string TargetIncidentId,
    string Title,
    string FrameTitle,
    string Category,
    string LocationLabel,
    IReadOnlyList<long> CallIds,
    string Reason);

public sealed record IncidentPlanExecutionResultV3(
    string Mode,
    bool Enabled,
    bool DryRun,
    bool CanMutate,
    int OperationCount,
    int MutatingOperationCount,
    int BlockedOperationCount,
    IReadOnlyList<string> BlockReasons,
    IReadOnlyList<IncidentPlanExecutionOperationV3> Operations);

public sealed class IncidentPlanExecutorV3
{
    private static readonly HashSet<string> WriteActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "update_current",
        "create_new",
        "detach_create"
    };

    private static readonly HashSet<string> PhaseOneLiveWriteActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "update_current"
    };

    private static readonly string[] GenericTitles =
    [
        "Fire response",
        "Medical response",
        "Police response",
        "Public safety incident",
        "Traffic incident"
    ];

    public IncidentPlanExecutionResultV3 BuildExecutionPlan(
        IReadOnlyList<IncidentPlanDecisionV3> plans,
        IncidentPlanExecutionOptionsV3 options)
    {
        var operations = plans
            .Select(plan => BuildOperation(plan))
            .ToList();

        var duplicateWriteCallIds = operations
            .Where(operation => operation.WouldMutate)
            .SelectMany(operation => operation.CallIds.Select(callId => (operation, callId)))
            .GroupBy(row => row.callId)
            .Where(group => group.Select(row => row.operation).Distinct().Count() > 1)
            .Select(group => group.Key)
            .Order()
            .ToList();
        if (duplicateWriteCallIds.Count > 0)
        {
            var duplicateReason = $"duplicate_write_call_ids:{string.Join(",", duplicateWriteCallIds)}";
            operations = operations
                .Select(operation => operation.WouldMutate
                    ? operation with
                    {
                        Blocked = true,
                        BlockedBecause = AppendReason(operation.BlockedBecause, duplicateReason)
                    }
                    : operation)
                .ToList();
        }

        if (options.Enabled)
        {
            operations = operations
                .Select(operation => ApplyLiveWriteGuards(operation, options))
                .ToList();
        }

        var blockReasons = operations
            .Where(operation => operation.Blocked)
            .Select(operation => operation.BlockedBecause)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mode = !options.Enabled
            ? "disabled"
            : options.DryRun
                ? "dry_run"
                : LiveMode(operations, blockReasons);
        var canMutate = options.Enabled &&
                        !options.DryRun &&
                        operations.Any(operation => operation.WouldMutate && !operation.Blocked);

        return new IncidentPlanExecutionResultV3(
            mode,
            options.Enabled,
            options.DryRun,
            canMutate,
            operations.Count,
            operations.Count(operation => operation.WouldMutate),
            operations.Count(operation => operation.Blocked),
            blockReasons,
            operations);
    }

    private static IncidentPlanExecutionOperationV3 BuildOperation(IncidentPlanDecisionV3 plan)
    {
        var action = Normalize(plan.Action);
        var wouldMutate = WriteActions.Contains(action);
        var blockedBecause = string.Empty;

        if (wouldMutate && plan.CallIds.Count == 0)
            blockedBecause = AppendReason(blockedBecause, "missing_call_ids");
        if (string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(plan.TargetIncidentId))
        {
            blockedBecause = AppendReason(blockedBecause, "missing_target_incident_id");
        }
        if ((string.Equals(action, "create_new", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(action, "detach_create", StringComparison.OrdinalIgnoreCase)) &&
            IsGenericTitle(plan.Title))
        {
            blockedBecause = AppendReason(blockedBecause, "generic_title");
        }

        return new IncidentPlanExecutionOperationV3(
            plan.Action,
            ExecutionAction(action),
            wouldMutate,
            !string.IsNullOrWhiteSpace(blockedBecause),
            blockedBecause,
            plan.TargetIncidentId,
            plan.Title,
            plan.FrameTitle,
            plan.Category,
            plan.LocationLabel,
            plan.CallIds,
            plan.Reason);
    }

    private static IncidentPlanExecutionOperationV3 ApplyLiveWriteGuards(
        IncidentPlanExecutionOperationV3 operation,
        IncidentPlanExecutionOptionsV3 options)
    {
        if (!operation.WouldMutate)
            return operation;

        var action = Normalize(operation.PlanAction);
        var blockedBecause = operation.BlockedBecause;
        if (!PhaseOneLiveWriteActions.Contains(action))
            blockedBecause = AppendReason(blockedBecause, $"live_{action}_not_implemented");
        if (string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase) &&
            !options.AllowLiveUpdateCurrent)
        {
            blockedBecause = AppendReason(blockedBecause, "live_update_current_not_approved");
        }
        if (string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase) &&
            !IsActiveIncidentTarget(operation.TargetIncidentId))
        {
            blockedBecause = AppendReason(blockedBecause, "unsupported_target_incident_id");
        }

        return operation with
        {
            Blocked = !string.IsNullOrWhiteSpace(blockedBecause),
            BlockedBecause = blockedBecause
        };
    }

    private static string LiveMode(
        IReadOnlyList<IncidentPlanExecutionOperationV3> operations,
        IReadOnlyList<string> blockReasons)
    {
        var hasUnblockedWrite = operations.Any(operation => operation.WouldMutate && !operation.Blocked);
        if (hasUnblockedWrite)
        {
            return blockReasons.Count > 0
                ? "live_update_current_partial"
                : "live_update_current";
        }

        return blockReasons.Count > 0
            ? "blocked"
            : "live_noop";
    }

    private static string ExecutionAction(string action) =>
        action switch
        {
            "update_current" => "upsert_current_incident",
            "create_new" => "create_incident",
            "detach_create" => "create_detached_incident",
            "drop_ambiguous" => "drop_ambiguous_state",
            "hold_pending" => "no_op_hold_pending",
            "hold_ambiguous" => "no_op_hold_ambiguous",
            "suppress_stale" => "no_op_suppress_stale",
            _ => "no_op"
        };

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsActiveIncidentTarget(string? targetIncidentId)
    {
        var value = (targetIncidentId ?? string.Empty).Trim();
        if (!value.StartsWith("active:", StringComparison.OrdinalIgnoreCase))
            return false;
        return long.TryParse(value["active:".Length..], out var id) && id > 0;
    }

    private static bool IsGenericTitle(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return true;

        foreach (var generic in GenericTitles)
        {
            if (string.Equals(value, generic, StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.StartsWith($"{generic} at ", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.StartsWith($"{generic} near ", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string AppendReason(string existing, string reason)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return reason;
        if (existing.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(reason, StringComparer.OrdinalIgnoreCase))
        {
            return existing;
        }

        return $"{existing};{reason}";
    }
}
