using System.Diagnostics;

namespace pizzad;

public sealed record IncidentEventStateShadowRunRequest(
    string LedgerEntryId,
    string SoftwareVersion,
    string ConfigurationIdentity);

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
