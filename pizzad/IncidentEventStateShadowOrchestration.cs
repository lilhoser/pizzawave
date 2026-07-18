using System.Diagnostics;

namespace pizzad;

public sealed record IncidentEventStateShadowRunRequest(
    string LedgerEntryId,
    string SoftwareVersion,
    string ConfigurationIdentity);

public sealed record IncidentEventStateObservationInterpretationRunRequest(
    string ObservationId,
    string SoftwareVersion,
    string ConfigurationIdentity);

public sealed record IncidentEventStateObservationInterpretationExecution(
    string SoftwareVersion,
    string ConfigurationIdentity,
    long InterpreterElapsedMilliseconds,
    long CriticElapsedMilliseconds);

public sealed record IncidentEventStateObservationInterpretationRunResult(
    string BundleId,
    string ObservationId,
    IncidentEventStateObservationInterpretation Interpretation,
    IncidentEventStateObservationInterpretationCritique Critique,
    IncidentEventStateObservationInterpretationExecution Execution);

public interface IIncidentEventStateObservationInterpreter
{
    Task<IncidentEventStateObservationInterpretation> InterpretAsync(
        IncidentEventStateObservationBundle bundle,
        string observationId,
        CancellationToken ct);
}

public interface IIncidentEventStateObservationInterpretationCritic
{
    Task<IncidentEventStateObservationInterpretationCritique> CritiqueAsync(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateObservationInterpretation interpretation,
        CancellationToken ct);
}

public interface IIncidentEventStateProposer
{
    Task<IncidentEventStateProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        CancellationToken ct);
}

public interface IIncidentEventStateCritic
{
    Task<IncidentEventStateCritique> CritiqueAsync(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal,
        CancellationToken ct);
}

public interface IIncidentEventStateShadowStore
{
    Task<IncidentEventStateStoredLedgerEntry> AppendIncidentEventStateShadowLedgerEntryAsync(
        IncidentEventStateLedgerEntry entry,
        CancellationToken ct);
}

public sealed class IncidentEventStateObservationInterpretationCoordinator
{
    private readonly IIncidentEventStateObservationInterpreter _interpreter;
    private readonly IIncidentEventStateObservationInterpretationCritic _critic;

    public IncidentEventStateObservationInterpretationCoordinator(
        IIncidentEventStateObservationInterpreter interpreter,
        IIncidentEventStateObservationInterpretationCritic critic)
    {
        _interpreter = interpreter;
        _critic = critic;
    }

    public async Task<IncidentEventStateObservationInterpretationRunResult> RunAsync(
        IncidentEventStateObservationInterpretationRunRequest request,
        IncidentEventStateObservationBundle bundle,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ObservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SoftwareVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigurationIdentity);

        var bundleValidation = IncidentEventStateContractValidator.ValidateBundle(bundle);
        if (!bundleValidation.IsValid)
            throw new ArgumentException(string.Join("; ", bundleValidation.Errors), nameof(bundle));
        if (!bundle.Observations.Any(observation =>
                string.Equals(observation.ObservationId, request.ObservationId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Observation id '{request.ObservationId}' does not exist in bundle '{bundle.BundleId}'.",
                nameof(request));
        }

        var interpreterTimer = Stopwatch.StartNew();
        var interpretation = await _interpreter.InterpretAsync(bundle, request.ObservationId, ct);
        interpreterTimer.Stop();
        var interpretationValidation =
            IncidentEventStateContractValidator.ValidateObservationInterpretation(bundle, interpretation);
        if (!interpretationValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", interpretationValidation.Errors));
        if (!string.Equals(interpretation.ObservationId, request.ObservationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Interpretation observation id '{interpretation.ObservationId}' does not match requested observation id '{request.ObservationId}'.");
        }

        var criticTimer = Stopwatch.StartNew();
        var critique = await _critic.CritiqueAsync(bundle, interpretation, ct);
        criticTimer.Stop();
        var critiqueValidation =
            IncidentEventStateContractValidator.ValidateObservationInterpretationCritique(
                bundle,
                interpretation,
                critique);
        if (!critiqueValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", critiqueValidation.Errors));

        return new IncidentEventStateObservationInterpretationRunResult(
            bundle.BundleId,
            request.ObservationId,
            interpretation,
            critique,
            new IncidentEventStateObservationInterpretationExecution(
                request.SoftwareVersion,
                request.ConfigurationIdentity,
                interpreterTimer.ElapsedMilliseconds,
                criticTimer.ElapsedMilliseconds));
    }
}

public sealed class IncidentEventStateShadowCoordinator
{
    private readonly IIncidentEventStateProposer _proposer;
    private readonly IIncidentEventStateCritic _critic;
    private readonly IIncidentEventStateShadowStore _store;
    private readonly TimeProvider _timeProvider;

    public IncidentEventStateShadowCoordinator(
        IIncidentEventStateProposer proposer,
        IIncidentEventStateCritic critic,
        IIncidentEventStateShadowStore store,
        TimeProvider? timeProvider = null)
    {
        _proposer = proposer;
        _critic = critic;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IncidentEventStateStoredLedgerEntry> RunAsync(
        IncidentEventStateShadowRunRequest request,
        IncidentEventStateObservationBundle bundle,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LedgerEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SoftwareVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigurationIdentity);
        var bundleValidation = IncidentEventStateContractValidator.ValidateBundle(bundle);
        if (!bundleValidation.IsValid)
            throw new ArgumentException(string.Join("; ", bundleValidation.Errors), nameof(bundle));

        var proposerTimer = Stopwatch.StartNew();
        var proposal = await _proposer.ProposeAsync(bundle, ct);
        proposerTimer.Stop();
        var proposalValidation = IncidentEventStateContractValidator.ValidateProposal(bundle, proposal);
        if (!proposalValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", proposalValidation.Errors));

        var criticTimer = Stopwatch.StartNew();
        var critique = await _critic.CritiqueAsync(bundle, proposal, ct);
        criticTimer.Stop();
        var critiqueValidation = IncidentEventStateContractValidator.ValidateCritique(bundle, proposal, critique);
        if (!critiqueValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", critiqueValidation.Errors));

        var entry = new IncidentEventStateLedgerEntry(
            request.LedgerEntryId,
            _timeProvider.GetUtcNow(),
            bundle,
            proposal,
            critique,
            new IncidentEventStateExecutionContext(
                request.SoftwareVersion,
                request.ConfigurationIdentity,
                proposerTimer.ElapsedMilliseconds,
                criticTimer.ElapsedMilliseconds),
            proposal.SupersedesLedgerEntryIds);
        var validation = IncidentEventStateContractValidator.ValidateLedgerEntry(entry);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));

        return await _store.AppendIncidentEventStateShadowLedgerEntryAsync(entry, ct);
    }
}
