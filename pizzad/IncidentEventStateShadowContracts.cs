namespace pizzad;

public sealed record IncidentEventStateObservationBundle(
    string BundleId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<IncidentEventStateSourceObservation> Observations,
    IReadOnlyList<IncidentEventStateProjectedEvent> PriorState);

public sealed record IncidentEventStateSourceObservation(
    string ObservationId,
    long? CallId,
    long ObservedAtUnixSeconds,
    string AudioReference,
    long? AudioDurationMilliseconds,
    IReadOnlyList<IncidentEventStateTranscriptObservation> Transcripts,
    IReadOnlyDictionary<string, IncidentEventStateMetadataObservation> Metadata);

public enum IncidentEventStateMetadataOrigin
{
    SourceRecord,
    ApplicationDerived,
    RawPayload
}

public sealed record IncidentEventStateMetadataObservation(
    string Value,
    IncidentEventStateMetadataOrigin Origin);

public sealed record IncidentEventStateTranscriptObservation(
    string TranscriptId,
    string Text,
    string Producer,
    DateTimeOffset? CreatedAtUtc);

public sealed record IncidentEventStateExecutionContext(
    string SoftwareVersion,
    string ConfigurationIdentity,
    long ProposerDurationMilliseconds,
    long CriticDurationMilliseconds);

public sealed record IncidentEventStateProvenance(
    string ObservationId,
    string TranscriptId,
    string ExactQuote,
    long? AudioStartMilliseconds,
    long? AudioEndMilliseconds,
    string MetadataField);

public sealed record IncidentEventStateObservationInterpretationStatement(
    string StatementId,
    string Statement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateProvenance> Provenance);

public sealed record IncidentEventStateObservationInterpretation(
    string InterpretationId,
    string ObservationId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentEventStateObservationInterpretationStatement> PossibleReadings,
    IReadOnlyList<IncidentEventStateObservationInterpretationStatement> SharedContent,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateObservationInterpretationCritique(
    string CritiqueId,
    string InterpretationId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    string Summary,
    IReadOnlyList<IncidentEventStateCritiqueFinding> Findings);

public sealed record IncidentEventStateClaim(
    string ClaimId,
    string Statement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateProvenance> Provenance);

public sealed record IncidentEventStateRelationship(
    string RelationshipId,
    string Statement,
    double Uncertainty,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<IncidentEventStateProvenance> Provenance);

public sealed record IncidentEventStateAlternative(
    string AlternativeId,
    string Statement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateProvenance> Provenance);

public sealed record IncidentEventStateHypothesis(
    string HypothesisId,
    string Description,
    double Uncertainty,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<IncidentEventStateClaim> Claims,
    IReadOnlyList<IncidentEventStateRelationship> Relationships,
    IReadOnlyList<IncidentEventStateAlternative> Alternatives,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateProposal(
    string ProposalId,
    string BundleId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentEventStateHypothesis> Hypotheses,
    IReadOnlyList<string> SupersedesLedgerEntryIds);

public sealed record IncidentEventStateCritiqueFinding(
    string FindingId,
    string Statement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateProvenance> Provenance);

public sealed record IncidentEventStateCritique(
    string CritiqueId,
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    string Summary,
    IReadOnlyList<IncidentEventStateCritiqueFinding> Findings);

public sealed record IncidentEventStateLedgerEntry(
    string LedgerEntryId,
    DateTimeOffset RecordedAtUtc,
    IncidentEventStateObservationBundle Bundle,
    IncidentEventStateProposal Proposal,
    IncidentEventStateCritique Critique,
    IncidentEventStateExecutionContext Execution,
    IReadOnlyList<string> SupersedesLedgerEntryIds);

public sealed record IncidentEventStateStoredLedgerEntry(
    long Sequence,
    string ContentHash,
    IncidentEventStateLedgerEntry Entry);

public sealed record IncidentEventStateProjectedEvent(
    string ProjectionEventId,
    string Description,
    double Uncertainty,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<IncidentEventStateClaim> Claims,
    IReadOnlyList<IncidentEventStateAlternative> Alternatives,
    IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<string> SourceLedgerEntryIds);

public sealed record IncidentEventStateProjection(
    string ProjectionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> LedgerEntryIds,
    IReadOnlyList<IncidentEventStateProjectedEvent> Events);

public sealed record IncidentEventStateStoredProjection(
    long Sequence,
    string ContentHash,
    IncidentEventStateProjection Projection);

public sealed record IncidentEventStateContractValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

public static class IncidentEventStateContractValidator
{
    public static IncidentEventStateContractValidationResult ValidateBundle(
        IncidentEventStateObservationBundle bundle)
    {
        var errors = new List<string>();
        ValidateBundle(bundle, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal)
    {
        var errors = new List<string>();
        ValidateBundle(bundle, errors);
        ValidateProposal(bundle, proposal, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateObservationInterpretation(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateObservationInterpretation interpretation)
    {
        var errors = new List<string>();
        ValidateBundle(bundle, errors);
        ValidateObservationInterpretation(bundle, interpretation, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateObservationInterpretationCritique(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateObservationInterpretation interpretation,
        IncidentEventStateObservationInterpretationCritique critique)
    {
        var errors = new List<string>();
        ValidateBundle(bundle, errors);
        ValidateObservationInterpretation(bundle, interpretation, errors);
        ValidateObservationInterpretationCritique(bundle, interpretation, critique, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateCritique(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal,
        IncidentEventStateCritique critique)
    {
        var errors = new List<string>();
        ValidateCritique(bundle, proposal, critique, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateObservationReferences(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> observationIds,
        string owner)
    {
        var errors = new List<string>();
        ValidateObservationReferences(bundle, observationIds, owner, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProvenanceReferences(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentEventStateProvenance> provenance,
        string owner)
    {
        var errors = new List<string>();
        ValidateProvenance(bundle, provenance, owner, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal,
        IncidentEventStateCritique critique)
    {
        var errors = new List<string>();
        ValidateBundle(bundle, errors);
        ValidateProposal(bundle, proposal, errors);
        ValidateCritique(bundle, proposal, critique, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateLedgerEntry(
        IncidentEventStateLedgerEntry entry)
    {
        var result = Validate(entry.Bundle, entry.Proposal, entry.Critique);
        var errors = result.Errors.ToList();
        RequireValue(entry.LedgerEntryId, "ledger entry id", errors);
        if (entry.RecordedAtUtc == default)
            errors.Add("ledger entry recorded timestamp is required");
        RequireValue(entry.Execution.SoftwareVersion, "software version", errors);
        RequireValue(entry.Execution.ConfigurationIdentity, "configuration identity", errors);
        if (entry.Execution.ProposerDurationMilliseconds < 0)
            errors.Add("proposer duration cannot be negative");
        if (entry.Execution.CriticDurationMilliseconds < 0)
            errors.Add("critic duration cannot be negative");
        RequireUniqueValues(entry.SupersedesLedgerEntryIds, "superseded ledger entry id", errors);
        if (!entry.SupersedesLedgerEntryIds.SequenceEqual(
                entry.Proposal.SupersedesLedgerEntryIds,
                StringComparer.Ordinal))
        {
            errors.Add("ledger entry supersession references do not match the proposal");
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProjection(
        IncidentEventStateProjection projection)
    {
        var errors = new List<string>();
        RequireValue(projection.ProjectionId, "projection id", errors);
        if (projection.GeneratedAtUtc == default)
            errors.Add("projection generated timestamp is required");
        RequireUniqueValues(projection.LedgerEntryIds, "projection ledger entry id", errors);
        RequireUniqueValues(
            projection.Events.Select(projectedEvent => projectedEvent.ProjectionEventId),
            "projected event id",
            errors);
        var ledgerEntryIds = projection.LedgerEntryIds.ToHashSet(StringComparer.Ordinal);
        foreach (var projectedEvent in projection.Events)
        {
            RequireValue(projectedEvent.ProjectionEventId, "projected event id", errors);
            RequireValue(projectedEvent.Description, $"projected event description for '{projectedEvent.ProjectionEventId}'", errors);
            ValidateUncertainty(
                projectedEvent.Uncertainty,
                $"projected event '{projectedEvent.ProjectionEventId}'",
                errors);
            RequireUniqueValues(
                projectedEvent.ObservationIds,
                $"observation id in projected event '{projectedEvent.ProjectionEventId}'",
                errors);
            RequireUniqueValues(
                projectedEvent.SourceLedgerEntryIds,
                $"source ledger entry id in projected event '{projectedEvent.ProjectionEventId}'",
                errors);
            foreach (var sourceLedgerEntryId in projectedEvent.SourceLedgerEntryIds)
            {
                if (!ledgerEntryIds.Contains(sourceLedgerEntryId))
                {
                    errors.Add($"projected event '{projectedEvent.ProjectionEventId}' references ledger entry '{sourceLedgerEntryId}' outside the projection");
                }
            }

            foreach (var claim in projectedEvent.Claims)
            {
                RequireValue(claim.ClaimId, "projected claim id", errors);
                RequireValue(claim.Statement, $"projected claim statement for '{claim.ClaimId}'", errors);
                ValidateUncertainty(claim.Uncertainty, $"projected claim '{claim.ClaimId}'", errors);
                if (claim.Provenance.Count == 0)
                    errors.Add($"projected claim '{claim.ClaimId}' must include source provenance");
            }

            foreach (var alternative in projectedEvent.Alternatives)
            {
                RequireValue(alternative.AlternativeId, "projected alternative id", errors);
                RequireValue(alternative.Statement, $"projected alternative statement for '{alternative.AlternativeId}'", errors);
                ValidateUncertainty(
                    alternative.Uncertainty,
                    $"projected alternative '{alternative.AlternativeId}'",
                    errors);
                if (alternative.Provenance.Count == 0)
                    errors.Add($"projected alternative '{alternative.AlternativeId}' must include source provenance");
            }
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProjection(
        IncidentEventStateProjection projection,
        IReadOnlyList<IncidentEventStateLedgerEntry> sourceEntries)
    {
        var result = ValidateProjection(projection);
        var errors = result.Errors.ToList();
        var suppliedLedgerIds = sourceEntries
            .Select(entry => entry.LedgerEntryId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var ledgerEntryId in projection.LedgerEntryIds)
        {
            if (!suppliedLedgerIds.Contains(ledgerEntryId))
                errors.Add($"projection source ledger entry '{ledgerEntryId}' was not supplied");
        }

        var observationGroups = sourceEntries
            .SelectMany(entry => entry.Bundle.Observations)
            .GroupBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToList();
        foreach (var group in observationGroups)
        {
            var distinctPayloads = group
                .Select(observation => System.Text.Json.JsonSerializer.Serialize(observation))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count();
            if (distinctPayloads > 1)
                errors.Add($"source ledgers contain conflicting versions of observation '{group.Key}'");
        }

        var sourceBundle = new IncidentEventStateObservationBundle(
            "projection-source",
            projection.GeneratedAtUtc,
            observationGroups.Select(group => group.First()).ToList(),
            []);
        foreach (var projectedEvent in projection.Events)
        {
            ValidateObservationReferences(
                sourceBundle,
                projectedEvent.ObservationIds,
                $"projected event '{projectedEvent.ProjectionEventId}'",
                errors);
            foreach (var claim in projectedEvent.Claims)
                ValidateProvenance(sourceBundle, claim.Provenance, $"projected claim '{claim.ClaimId}'", errors);
            foreach (var alternative in projectedEvent.Alternatives)
                ValidateProvenance(sourceBundle, alternative.Provenance, $"projected alternative '{alternative.AlternativeId}'", errors);
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateBundle(
        IncidentEventStateObservationBundle bundle,
        List<string> errors)
    {
        RequireValue(bundle.BundleId, "bundle id", errors);
        if (bundle.CreatedAtUtc == default)
            errors.Add("bundle created timestamp is required");
        RequireUniqueValues(
            bundle.Observations.Select(observation => observation.ObservationId),
            "observation id",
            errors);

        foreach (var observation in bundle.Observations)
        {
            RequireValue(observation.ObservationId, "observation id", errors);
            if (observation.ObservedAtUnixSeconds < 0)
                errors.Add($"observation '{observation.ObservationId}' has a negative observed timestamp");
            if (observation.CallId is <= 0)
                errors.Add($"observation '{observation.ObservationId}' has an invalid call id");
            if (observation.AudioDurationMilliseconds is < 0)
                errors.Add($"observation '{observation.ObservationId}' has a negative audio duration");
            if (string.IsNullOrWhiteSpace(observation.AudioReference) &&
                observation.Transcripts.Count == 0 &&
                observation.Metadata.Count == 0)
            {
                errors.Add($"observation '{observation.ObservationId}' has no source material");
            }

            foreach (var (field, metadata) in observation.Metadata)
            {
                RequireValue(field, $"metadata field in observation '{observation.ObservationId}'", errors);
                if (!Enum.IsDefined(metadata.Origin))
                    errors.Add($"metadata field '{field}' in observation '{observation.ObservationId}' has an invalid origin");
            }

            RequireUniqueValues(
                observation.Transcripts.Select(transcript => transcript.TranscriptId),
                $"transcript id in observation '{observation.ObservationId}'",
                errors);
            foreach (var transcript in observation.Transcripts)
            {
                RequireValue(transcript.TranscriptId, "transcript id", errors);
                RequireValue(transcript.Producer, $"transcript producer for '{transcript.TranscriptId}'", errors);
                if (transcript.CreatedAtUtc.HasValue && transcript.CreatedAtUtc.Value == default)
                    errors.Add($"transcript '{transcript.TranscriptId}' created timestamp cannot be the default value");
            }
        }
    }

    private static void ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal,
        List<string> errors)
    {
        RequireValue(proposal.ProposalId, "proposal id", errors);
        RequireValue(proposal.ModelIdentity, "proposal model identity", errors);
        RequireValue(proposal.PromptIdentity, "proposal prompt identity", errors);
        if (!string.Equals(proposal.BundleId, bundle.BundleId, StringComparison.Ordinal))
            errors.Add("proposal bundle id does not match the observation bundle");
        if (proposal.GeneratedAtUtc == default)
            errors.Add("proposal generated timestamp is required");

        RequireUniqueValues(
            proposal.Hypotheses.Select(hypothesis => hypothesis.HypothesisId),
            "hypothesis id",
            errors);
        foreach (var hypothesis in proposal.Hypotheses)
        {
            RequireValue(hypothesis.HypothesisId, "hypothesis id", errors);
            RequireValue(hypothesis.Description, $"hypothesis description for '{hypothesis.HypothesisId}'", errors);
            ValidateUncertainty(hypothesis.Uncertainty, $"hypothesis '{hypothesis.HypothesisId}'", errors);
            if (hypothesis.ObservationIds.Count == 0)
                errors.Add($"hypothesis '{hypothesis.HypothesisId}' must reference at least one observation");
            ValidateObservationReferences(bundle, hypothesis.ObservationIds, $"hypothesis '{hypothesis.HypothesisId}'", errors);
            RequireUniqueValues(
                hypothesis.Claims.Select(claim => claim.ClaimId),
                $"claim id in hypothesis '{hypothesis.HypothesisId}'",
                errors);
            foreach (var claim in hypothesis.Claims)
            {
                RequireValue(claim.ClaimId, "claim id", errors);
                RequireValue(claim.Statement, $"claim statement for '{claim.ClaimId}'", errors);
                ValidateUncertainty(claim.Uncertainty, $"claim '{claim.ClaimId}'", errors);
                ValidateProvenance(bundle, claim.Provenance, $"claim '{claim.ClaimId}'", errors);
            }

            RequireUniqueValues(
                hypothesis.Relationships.Select(relationship => relationship.RelationshipId),
                $"relationship id in hypothesis '{hypothesis.HypothesisId}'",
                errors);
            foreach (var relationship in hypothesis.Relationships)
            {
                RequireValue(relationship.RelationshipId, "relationship id", errors);
                RequireValue(relationship.Statement, $"relationship statement for '{relationship.RelationshipId}'", errors);
                ValidateUncertainty(relationship.Uncertainty, $"relationship '{relationship.RelationshipId}'", errors);
                ValidateObservationReferences(bundle, relationship.ObservationIds, $"relationship '{relationship.RelationshipId}'", errors);
                ValidateProvenance(bundle, relationship.Provenance, $"relationship '{relationship.RelationshipId}'", errors);
            }

            RequireUniqueValues(
                hypothesis.Alternatives.Select(alternative => alternative.AlternativeId),
                $"alternative id in hypothesis '{hypothesis.HypothesisId}'",
                errors);
            foreach (var alternative in hypothesis.Alternatives)
            {
                RequireValue(alternative.AlternativeId, "alternative id", errors);
                RequireValue(alternative.Statement, $"alternative statement for '{alternative.AlternativeId}'", errors);
                ValidateUncertainty(alternative.Uncertainty, $"alternative '{alternative.AlternativeId}'", errors);
                ValidateProvenance(bundle, alternative.Provenance, $"alternative '{alternative.AlternativeId}'", errors);
            }
        }
    }

    private static void ValidateObservationInterpretation(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateObservationInterpretation interpretation,
        List<string> errors)
    {
        RequireValue(interpretation.InterpretationId, "observation interpretation id", errors);
        RequireValue(interpretation.ObservationId, "interpreted observation id", errors);
        RequireValue(interpretation.ModelIdentity, "observation interpretation model identity", errors);
        RequireValue(interpretation.PromptIdentity, "observation interpretation prompt identity", errors);
        if (interpretation.GeneratedAtUtc == default)
            errors.Add("observation interpretation generated timestamp is required");
        ValidateObservationReferences(
            bundle,
            [interpretation.ObservationId],
            $"observation interpretation '{interpretation.InterpretationId}'",
            errors);

        var statements = interpretation.PossibleReadings
            .Select(statement => (Statement: statement, Description: "possible reading"))
            .Concat(interpretation.SharedContent.Select(statement =>
                (Statement: statement, Description: "shared content")))
            .ToList();
        RequireUniqueValues(
            statements.Select(item => item.Statement.StatementId),
            $"statement id in observation interpretation '{interpretation.InterpretationId}'",
            errors);
        foreach (var (statement, description) in statements)
        {
            RequireValue(statement.StatementId, $"{description} id", errors);
            RequireValue(statement.Statement, $"{description} statement for '{statement.StatementId}'", errors);
            ValidateUncertainty(statement.Uncertainty, $"{description} '{statement.StatementId}'", errors);
            ValidateProvenance(bundle, statement.Provenance, $"{description} '{statement.StatementId}'", errors);
            foreach (var provenance in statement.Provenance)
            {
                if (!string.Equals(
                        provenance.ObservationId,
                        interpretation.ObservationId,
                        StringComparison.Ordinal))
                {
                    errors.Add($"{description} '{statement.StatementId}' cites observation '{provenance.ObservationId}' outside interpreted observation '{interpretation.ObservationId}'");
                }
            }
        }
        RequireUniqueValues(
            interpretation.UnresolvedQuestions,
            $"unresolved question in observation interpretation '{interpretation.InterpretationId}'",
            errors);
        foreach (var question in interpretation.UnresolvedQuestions)
            RequireValue(question, "observation interpretation unresolved question", errors);
    }

    private static void ValidateObservationInterpretationCritique(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateObservationInterpretation interpretation,
        IncidentEventStateObservationInterpretationCritique critique,
        List<string> errors)
    {
        RequireValue(critique.CritiqueId, "observation interpretation critique id", errors);
        RequireValue(critique.InterpretationId, "observation interpretation critique interpretation id", errors);
        RequireValue(critique.ModelIdentity, "observation interpretation critique model identity", errors);
        RequireValue(critique.PromptIdentity, "observation interpretation critique prompt identity", errors);
        RequireValue(critique.Summary, "observation interpretation critique summary", errors);
        if (!string.Equals(critique.InterpretationId, interpretation.InterpretationId, StringComparison.Ordinal))
            errors.Add("observation interpretation critique id reference does not match the interpretation");
        if (critique.GeneratedAtUtc == default)
            errors.Add("observation interpretation critique generated timestamp is required");
        RequireUniqueValues(
            critique.Findings.Select(finding => finding.FindingId),
            "observation interpretation critique finding id",
            errors);
        foreach (var finding in critique.Findings)
        {
            RequireValue(finding.FindingId, "observation interpretation critique finding id", errors);
            RequireValue(
                finding.Statement,
                $"observation interpretation critique finding statement for '{finding.FindingId}'",
                errors);
            ValidateUncertainty(
                finding.Uncertainty,
                $"observation interpretation critique finding '{finding.FindingId}'",
                errors);
            ValidateProvenance(
                bundle,
                finding.Provenance,
                $"observation interpretation critique finding '{finding.FindingId}'",
                errors);
            foreach (var provenance in finding.Provenance)
            {
                if (!string.Equals(
                        provenance.ObservationId,
                        interpretation.ObservationId,
                        StringComparison.Ordinal))
                {
                    errors.Add($"observation interpretation critique finding '{finding.FindingId}' cites observation '{provenance.ObservationId}' outside interpreted observation '{interpretation.ObservationId}'");
                }
            }
        }
    }

    private static void ValidateCritique(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateProposal proposal,
        IncidentEventStateCritique critique,
        List<string> errors)
    {
        RequireValue(critique.CritiqueId, "critique id", errors);
        RequireValue(critique.ModelIdentity, "critique model identity", errors);
        RequireValue(critique.PromptIdentity, "critique prompt identity", errors);
        RequireValue(critique.Summary, "critique summary", errors);
        if (!string.Equals(critique.ProposalId, proposal.ProposalId, StringComparison.Ordinal))
            errors.Add("critique proposal id does not match the proposal");
        if (critique.GeneratedAtUtc == default)
            errors.Add("critique generated timestamp is required");

        RequireUniqueValues(
            critique.Findings.Select(finding => finding.FindingId),
            "critique finding id",
            errors);
        foreach (var finding in critique.Findings)
        {
            RequireValue(finding.FindingId, "critique finding id", errors);
            RequireValue(finding.Statement, $"critique finding statement for '{finding.FindingId}'", errors);
            ValidateUncertainty(finding.Uncertainty, $"critique finding '{finding.FindingId}'", errors);
            ValidateProvenance(bundle, finding.Provenance, $"critique finding '{finding.FindingId}'", errors);
        }
    }

    private static void ValidateObservationReferences(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> observationIds,
        string owner,
        List<string> errors)
    {
        var known = bundle.Observations
            .Select(observation => observation.ObservationId)
            .ToHashSet(StringComparer.Ordinal);
        RequireUniqueValues(observationIds, $"observation reference in {owner}", errors);
        foreach (var observationId in observationIds)
        {
            if (!known.Contains(observationId))
                errors.Add($"{owner} references unknown observation '{observationId}'");
        }
    }

    private static void ValidateProvenance(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentEventStateProvenance> provenanceItems,
        string owner,
        List<string> errors)
    {
        if (provenanceItems.Count == 0)
        {
            errors.Add($"{owner} must include source provenance");
            return;
        }

        var observations = bundle.Observations
            .GroupBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var provenance in provenanceItems)
        {
            if (!observations.TryGetValue(provenance.ObservationId, out var observation))
            {
                errors.Add($"{owner} provenance references unknown observation '{provenance.ObservationId}'");
                continue;
            }

            var hasTranscriptSource = !string.IsNullOrWhiteSpace(provenance.TranscriptId) ||
                                      !string.IsNullOrWhiteSpace(provenance.ExactQuote);
            var hasAudioSource = provenance.AudioStartMilliseconds.HasValue || provenance.AudioEndMilliseconds.HasValue;
            var hasMetadataSource = !string.IsNullOrWhiteSpace(provenance.MetadataField);
            if (!hasTranscriptSource && !hasAudioSource && !hasMetadataSource)
            {
                errors.Add($"{owner} provenance for observation '{provenance.ObservationId}' identifies no source material");
                continue;
            }

            if (hasTranscriptSource)
                ValidateTranscriptProvenance(observation, provenance, owner, errors);
            if (hasAudioSource)
                ValidateAudioProvenance(observation, provenance, owner, errors);
            if (hasMetadataSource && !observation.Metadata.ContainsKey(provenance.MetadataField))
            {
                errors.Add($"{owner} provenance references missing metadata field '{provenance.MetadataField}' on observation '{provenance.ObservationId}'");
            }
        }
    }

    private static void ValidateTranscriptProvenance(
        IncidentEventStateSourceObservation observation,
        IncidentEventStateProvenance provenance,
        string owner,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(provenance.TranscriptId) || string.IsNullOrWhiteSpace(provenance.ExactQuote))
        {
            errors.Add($"{owner} transcript provenance requires both transcript id and exact quote");
            return;
        }

        var transcript = observation.Transcripts.FirstOrDefault(candidate =>
            string.Equals(candidate.TranscriptId, provenance.TranscriptId, StringComparison.Ordinal));
        if (transcript is null)
        {
            errors.Add($"{owner} provenance references unknown transcript '{provenance.TranscriptId}'");
            return;
        }

        if (!transcript.Text.Contains(provenance.ExactQuote, StringComparison.Ordinal))
            errors.Add($"{owner} exact quote does not occur in transcript '{provenance.TranscriptId}'");
    }

    private static void ValidateAudioProvenance(
        IncidentEventStateSourceObservation observation,
        IncidentEventStateProvenance provenance,
        string owner,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(observation.AudioReference))
            errors.Add($"{owner} audio provenance references an observation without audio");
        if (!provenance.AudioStartMilliseconds.HasValue || !provenance.AudioEndMilliseconds.HasValue)
        {
            errors.Add($"{owner} audio provenance requires both start and end offsets");
            return;
        }

        var start = provenance.AudioStartMilliseconds.Value;
        var end = provenance.AudioEndMilliseconds.Value;
        if (start < 0 || end <= start)
            errors.Add($"{owner} audio provenance has an invalid interval");
        if (observation.AudioDurationMilliseconds.HasValue && end > observation.AudioDurationMilliseconds.Value)
            errors.Add($"{owner} audio provenance exceeds the observation duration");
    }

    private static void ValidateUncertainty(double value, string owner, List<string> errors)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            errors.Add($"{owner} uncertainty must be between 0 and 1");
    }

    private static void RequireUniqueValues(
        IEnumerable<string> values,
        string description,
        List<string> errors)
    {
        var duplicates = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal);
        foreach (var duplicate in duplicates)
            errors.Add($"duplicate {description} '{duplicate}'");
    }

    private static void RequireValue(string value, string description, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{description} is required");
    }
}
