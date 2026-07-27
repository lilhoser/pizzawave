using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentBatchGraphVerificationProposal(
    IncidentBatchConfirmationProposal RelationshipProposal,
    IReadOnlyDictionary<string, IncidentBatchStandaloneVerificationProposal> StandaloneProposals);

public static class IncidentBatchGraphVerification
{
    public const string ConfigurationToken = "verification=two-pass-graph-v1";
    public const string PromptIdentity = "incident-batch-graph-verifier-v2-compact-evidence-indices";

    public static bool IsEnabled(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ConfigurationToken, StringComparer.Ordinal);

    public static IncidentBatchPromptPayload BuildPrompt(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests)
    {
        var relationshipRows = requests
            .Where(item => item.Kind == IncidentBatchVerificationKind.Relationship)
            .Select((request, index) =>
            {
                var context = IncidentBatchVerificationQueueContract.BuildContext(entry, request);
                return new RelationshipRow(index + 1, request, context);
            })
            .ToList();
        var standaloneRows = requests
            .Where(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent)
            .Select((request, index) =>
            {
                var context = IncidentBatchVerificationQueueContract.BuildStandaloneContext(entry, request);
                return new StandaloneRow(index + 1, request, context.Event);
            })
            .ToList();
        if (relationshipRows.Count == 0 && standaloneRows.Count == 0)
            throw new ArgumentException("graph verification requires at least one request", nameof(requests));

        var evidence = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle);
        var indexedEvidence = evidence.Select((item, index) => new { item, index }).ToList();
        var evidenceIndicesByObservation = indexedEvidence
            .GroupBy(item => item.item.ObservationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.index).ToList(),
                StringComparer.Ordinal);
        var observations = entry.Bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var input = new
        {
            evidence = indexedEvidence.Select(item => new object[]
            {
                item.index,
                item.item.ObservationId,
                item.item.TranscriptId,
                item.item.ExactQuote
            }).ToList(),
            observations = entry.Bundle.Observations.Select(item => new object[]
            {
                item.ObservationId,
                item.ObservedAtUnixSeconds,
                evidenceIndicesByObservation.GetValueOrDefault(item.ObservationId, [])
            }).ToList(),
            proposed_relationships = relationshipRows.Select(item => new object[]
            {
                item.Row,
                item.Request.RequestId,
                item.Context.Relationship.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership ? "membership" : "association",
                item.Context.Relationship.RelationshipStatement,
                item.Context.Source.NewObservationIds,
                item.Context.Candidate.ObservationIds,
                item.Context.Source.NewObservationIds.SelectMany(observationId => evidenceIndicesByObservation[observationId]).ToList(),
                item.Context.Candidate.ObservationIds.SelectMany(observationId => evidenceIndicesByObservation[observationId]).ToList()
            }).ToList(),
            standalone_candidates = standaloneRows.Select(item => new object[]
            {
                item.Row,
                item.Request.RequestId,
                item.Event.NewObservationIds,
                item.Event.NewObservationIds.SelectMany(observationId => evidenceIndicesByObservation[observationId]).ToList()
            }).ToList()
        };

        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("This is the independent second pass. The earlier relationship proposer is untrusted. Evaluate every relationship and standalone row using the complete evidence window.");
        user.AppendLine("Evidence is [evidence_index, observation_id, transcript_id, exact_quote] and is encoded once. Observations are [observation_id, timestamp, evidence_indices]. Relationship rows end with source_allowed_evidence_indices and candidate_allowed_evidence_indices; standalone rows end with allowed_evidence_indices. Copy only indices allowed by that output row.");
        user.AppendLine("For a relationship, verify only when both boundaries describe one unfolding real-world event and no material conflict remains. Review means a concrete connection exists but membership is unsafe. Reject unsupported resemblance. Timing, category, responder, talkgroup, shared words, or broad location alone are insufficient.");
        user.AppendLine("Evaluate overlapping verified relationships as a whole component. Set component_coherent false if the combined component could contain separate events, even when one edge looks plausible in isolation. A false merge is the most serious error.");
        user.AppendLine("For a standalone candidate, verify only when its own evidence establishes a self-contained operator-worthy real-world event and it is not merely a fragment, status, response, continuation, or update of other evidence in this window. Being unmatched is never evidence of being standalone.");
        user.AppendLine("A legitimate single-call event may be verified. Missing location alone does not invalidate it, but never invent a location. Use review when a concrete event is supported but its identity remains materially unresolved; review is non-persistent.");
        user.AppendLine($"For verify, return a concise evidence-grounded title of at most {IncidentBatchConfirmationContract.MaximumDisplayTitleLength} characters. A verified relationship title must describe the complete connected component, not only that edge. A standalone review also requires a grounded title for inspector continuity; relationship review and all reject rows use an empty title. Do not copy radio preambles, silently repair ASR, or invent names, locations, agencies, diagnoses, conditions, or status.");
        user.AppendLine("Every relationship decision requires evidence from both sides. Every standalone decision requires evidence from its own observations. Verify rows must have no counterevidence or unresolved questions. Review rows must explain the uncertainty.");
        user.AppendLine();
        user.AppendLine("Graph input:");
        user.AppendLine(JsonSerializer.Serialize(input, EngineConfig.JsonOptions()));

        return new IncidentBatchPromptPayload(
            "You independently verify a proposed incident relationship graph. Application code owns evidence, graph construction, unresolved observations, and persistence. You cannot create unproposed relationships or publish unmatched observations by default.",
            user.ToString(),
            ResponseFormat(relationshipRows, standaloneRows, evidenceIndicesByObservation));
    }

    public static IncidentBatchGraphVerificationProposal Parse(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        string json,
        string model,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var relationshipRequests = requests.Where(item => item.Kind == IncidentBatchVerificationKind.Relationship).ToList();
        var standaloneRequests = requests.Where(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent).ToList();
        var relationshipRows = root.GetProperty("relationship_decisions");
        var standaloneRows = root.GetProperty("standalone_decisions");
        if (relationshipRows.GetArrayLength() != relationshipRequests.Count ||
            standaloneRows.GetArrayLength() != standaloneRequests.Count)
        {
            throw new InvalidDataException("Graph verifier did not return complete request coverage.");
        }

        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle);
        var relationshipDecisions = new List<IncidentBatchConfirmationDecision>();
        for (var index = 0; index < relationshipRequests.Count; index++)
        {
            var row = relationshipRows[index];
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 9 || row[0].GetInt32() != index + 1)
                throw new InvalidDataException($"Graph relationship row {index + 1} has an invalid identity or shape.");
            var context = IncidentBatchVerificationQueueContract.BuildContext(entry, relationshipRequests[index]);
            var proposed = ParseDecision(row[1].GetString());
            var componentCoherent = row[2].GetBoolean();
            var counter = ReadStrings(row[7]);
            var unresolved = ReadStrings(row[8]);
            var decision = proposed == IncidentBatchConfirmationDecisionKind.Verify && !componentCoherent
                ? IncidentBatchConfirmationDecisionKind.Review
                : proposed;
            if (decision == IncidentBatchConfirmationDecisionKind.Review && counter.Count == 0 && unresolved.Count == 0)
                unresolved = ["The proposed combined component is not established as one real-world event."];
            relationshipDecisions.Add(new IncidentBatchConfirmationDecision(
                context.Relationship.SourceProposalToken,
                context.Relationship.CandidateToken,
                decision,
                row[4].GetString() ?? string.Empty,
                IncidentBatchConfirmationEvidenceCatalog.ResolveIndices(ReadInts(row[5]), catalog),
                IncidentBatchConfirmationEvidenceCatalog.ResolveIndices(ReadInts(row[6]), catalog),
                decision == IncidentBatchConfirmationDecisionKind.Verify ? [] : counter,
                decision == IncidentBatchConfirmationDecisionKind.Verify ? [] : unresolved,
                decision == IncidentBatchConfirmationDecisionKind.Verify
                    ? IncidentTitlePresentation.Normalize(row[3].GetString())
                    : string.Empty));
        }

        relationshipDecisions = NormalizeRelationshipForest(entry, relationshipRequests, relationshipDecisions);
        var relationshipProposal = new IncidentBatchConfirmationProposal(
            $"model:incident-batch-graph:{Guid.NewGuid():N}:relationships",
            now,
            model,
            PromptIdentity,
            relationshipDecisions);

        var standalone = new Dictionary<string, IncidentBatchStandaloneVerificationProposal>(StringComparer.Ordinal);
        for (var index = 0; index < standaloneRequests.Count; index++)
        {
            var row = standaloneRows[index];
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 6 || row[0].GetInt32() != index + 1)
                throw new InvalidDataException($"Graph standalone row {index + 1} has an invalid identity or shape.");
            var context = IncidentBatchVerificationQueueContract.BuildStandaloneContext(entry, standaloneRequests[index]);
            var decision = ParseDecision(row[1].GetString());
            var reason = row[5].GetString() ?? string.Empty;
            var proposal = new IncidentBatchStandaloneVerificationProposal(
                $"model:incident-batch-graph:{Guid.NewGuid():N}:standalone:{index + 1}",
                now,
                model,
                PromptIdentity,
                new IncidentBatchStandaloneVerificationDecision(
                    context.Event.ProposalToken,
                    decision,
                    decision != IncidentBatchConfirmationDecisionKind.Reject,
                    decision is IncidentBatchConfirmationDecisionKind.Verify or IncidentBatchConfirmationDecisionKind.Review
                        ? IncidentTitlePresentation.Normalize(row[2].GetString())
                        : string.Empty,
                    row[3].GetString() ?? string.Empty,
                    IncidentBatchConfirmationEvidenceCatalog.ResolveIndices(ReadInts(row[4]), catalog),
                    decision == IncidentBatchConfirmationDecisionKind.Review ? [reason] : [],
                    []));
            standalone.Add(standaloneRequests[index].RequestId, proposal);
        }
        return new IncidentBatchGraphVerificationProposal(relationshipProposal, standalone);
    }

    public static IReadOnlySet<string> TerminalPersistenceRequests(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        IReadOnlyDictionary<string, IncidentBatchVerificationResult> results)
    {
        var edges = RelationshipApplicationOrder(entry, requests, results)
            .Where(item => item.Kind == IncidentBatchVerificationKind.Relationship)
            .Where(item => results.TryGetValue(item.RequestId, out var result) && result.Outcome == IncidentBatchVerificationOutcome.Verified)
            .Select(item => (Request: item, Context: IncidentBatchVerificationQueueContract.BuildContext(entry, item)))
            .ToList();
        var graph = new DisjointSet();
        foreach (var edge in edges)
            graph.Union(SourceProjection(entry, edge.Context.Source), edge.Context.Candidate.ProjectionEventId);
        return edges
            .GroupBy(item => graph.Find(SourceProjection(entry, item.Context.Source)), StringComparer.Ordinal)
            .Select(group => group.Last().Request.RequestId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<IncidentBatchVerificationRequest> ApplicationOrder(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        IReadOnlyDictionary<string, IncidentBatchVerificationResult> results) =>
        requests.Where(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent)
            .Concat(RelationshipApplicationOrder(entry, requests, results))
            .ToList();

    public static IReadOnlySet<string> VerifiedRelationshipObservationIds(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        IReadOnlyDictionary<string, IncidentBatchVerificationResult> results)
    {
        var observationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests.Where(item =>
                     item.Kind == IncidentBatchVerificationKind.Relationship &&
                     results.TryGetValue(item.RequestId, out var result) &&
                     result.Outcome == IncidentBatchVerificationOutcome.Verified))
        {
            var context = IncidentBatchVerificationQueueContract.BuildContext(entry, request);
            observationIds.UnionWith(context.Source.NewObservationIds);
            observationIds.UnionWith(context.Candidate.ObservationIds);
        }
        return observationIds;
    }

    public static bool IsOwnedByVerifiedRelationship(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request,
        IReadOnlySet<string> verifiedRelationshipObservationIds) =>
        request.Kind == IncidentBatchVerificationKind.StandaloneEvent &&
        IncidentBatchVerificationQueueContract.BuildStandaloneContext(entry, request).Event.NewObservationIds
            .Any(verifiedRelationshipObservationIds.Contains);

    private static List<IncidentBatchConfirmationDecision> NormalizeRelationshipForest(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        IReadOnlyList<IncidentBatchConfirmationDecision> decisions)
    {
        var result = new List<IncidentBatchConfirmationDecision>();
        var graph = new DisjointSet();
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            if (decision.Decision != IncidentBatchConfirmationDecisionKind.Verify)
            {
                result.Add(decision);
                continue;
            }
            var context = IncidentBatchVerificationQueueContract.BuildContext(entry, requests[index]);
            if (graph.Union(SourceProjection(entry, context.Source), context.Candidate.ProjectionEventId))
            {
                result.Add(decision);
                continue;
            }
            result.Add(decision with
            {
                Decision = IncidentBatchConfirmationDecisionKind.Review,
                DisplayTitle = string.Empty,
                CounterEvidence = [],
                UnresolvedQuestions = ["This edge is redundant inside an already connected proposed component."]
            });
        }
        return result;
    }

    private static IReadOnlyList<IncidentBatchVerificationRequest> RelationshipApplicationOrder(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        IReadOnlyDictionary<string, IncidentBatchVerificationResult> results)
    {
        var timestamps = entry.Bundle.Observations.ToDictionary(
            item => item.ObservationId,
            item => item.ObservedAtUnixSeconds,
            StringComparer.Ordinal);
        return requests
            .Where(item => item.Kind == IncidentBatchVerificationKind.Relationship)
            .OrderBy(item => results.TryGetValue(item.RequestId, out var result) &&
                             result.Outcome == IncidentBatchVerificationOutcome.Verified ? 1 : 0)
            // Eligible same-batch edges point from a later source to an earlier
            // candidate. Applying newest sources first collapses C into B before B
            // is collapsed into A, so no target projection disappears mid-component.
            .ThenByDescending(item => IncidentBatchVerificationQueueContract.BuildContext(entry, item)
                .Source.NewObservationIds.Max(observationId => timestamps[observationId]))
            .ThenBy(item => item.RequestId, StringComparer.Ordinal)
            .ToList();
    }

    private static string SourceProjection(IncidentBatchLedgerEntry entry, IncidentBatchRelationshipSource source)
    {
        var projections = source.NewObservationIds
            .Select(observationId => entry.SingletonEvents.Single(item => item.ObservationId == observationId).ProjectionEventId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (projections.Count != 1)
            throw new InvalidDataException("Graph relationship source must resolve to one application-owned component.");
        return projections[0];
    }

    private static IncidentBatchConfirmationDecisionKind ParseDecision(string? value) => value switch
    {
        "verify" => IncidentBatchConfirmationDecisionKind.Verify,
        "review" => IncidentBatchConfirmationDecisionKind.Review,
        "reject" => IncidentBatchConfirmationDecisionKind.Reject,
        _ => throw new InvalidDataException($"Unsupported graph verification decision '{value}'.")
    };

    private static List<string> ReadStrings(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

    private static List<int> ReadInts(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetInt32()).ToList();

    private static object ResponseFormat(
        IReadOnlyList<RelationshipRow> relationshipRows,
        IReadOnlyList<StandaloneRow> standaloneRows,
        IReadOnlyDictionary<string, List<int>> evidenceIndicesByObservation)
    {
        object EvidenceIndices(IEnumerable<string> observationIds)
        {
            var allowed = observationIds
                .SelectMany(observationId => evidenceIndicesByObservation[observationId])
                .Distinct()
                .Order()
                .ToArray();
            return new
            {
                type = "array",
                minItems = 1,
                maxItems = Math.Min(IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide, allowed.Length),
                uniqueItems = true,
                items = new { type = "integer", @enum = allowed }
            };
        }
        object Strings() => new
        {
            type = "array",
            maxItems = IncidentBatchRelationshipContract.MaximumAlternatives,
            items = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength }
        };
        object RelationshipRowSchema(RelationshipRow relationshipRow) => new
        {
            type = "array",
            minItems = 9,
            maxItems = 9,
            prefixItems = new object[]
            {
                new { type = "integer", @const = relationshipRow.Row },
                new { type = "string", @enum = new[] { "verify", "review", "reject" } },
                new { type = "boolean" },
                new { type = "string", maxLength = IncidentBatchConfirmationContract.MaximumDisplayTitleLength },
                new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength },
                EvidenceIndices(relationshipRow.Context.Source.NewObservationIds),
                EvidenceIndices(relationshipRow.Context.Candidate.ObservationIds),
                Strings(),
                Strings()
            }
        };
        object StandaloneRowSchema(StandaloneRow standaloneRow) => new
        {
            type = "array",
            minItems = 6,
            maxItems = 6,
            prefixItems = new object[]
            {
                new { type = "integer", @const = standaloneRow.Row },
                new { type = "string", @enum = new[] { "verify", "review", "reject" } },
                new { type = "string", maxLength = IncidentBatchConfirmationContract.MaximumDisplayTitleLength },
                new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength },
                EvidenceIndices(standaloneRow.Event.NewObservationIds),
                new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength }
            }
        };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_graph_verifier_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        relationship_decisions = new
                        {
                            type = "array",
                            minItems = relationshipRows.Count,
                            maxItems = relationshipRows.Count,
                            prefixItems = relationshipRows.Select(RelationshipRowSchema).ToArray()
                        },
                        standalone_decisions = new
                        {
                            type = "array",
                            minItems = standaloneRows.Count,
                            maxItems = standaloneRows.Count,
                            prefixItems = standaloneRows.Select(StandaloneRowSchema).ToArray()
                        }
                    },
                    required = new[] { "relationship_decisions", "standalone_decisions" }
                }
            }
        };
    }

    private sealed record RelationshipRow(int Row, IncidentBatchVerificationRequest Request, IncidentBatchVerificationContext Context);
    private sealed record StandaloneRow(int Row, IncidentBatchVerificationRequest Request, IncidentBatchEventProposal Event);

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

        public string Find(string value)
        {
            if (!_parent.TryGetValue(value, out var parent))
                return _parent[value] = value;
            if (parent == value)
                return value;
            return _parent[value] = Find(parent);
        }

        public bool Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return false;
            _parent[rightRoot] = leftRoot;
            return true;
        }
    }
}

public sealed class OpenAiIncidentBatchGraphVerifier(
    EngineConfig config,
    EngineDatabase database,
    ILogger logger,
    string runId)
{
    public async Task<IncidentBatchGraphVerificationProposal> VerifyAsync(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        CancellationToken ct)
    {
        var prompt = IncidentBatchGraphVerification.BuildPrompt(entry, requests);
        var model = config.AiInsights.OpenAiModel;
        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0.1,
            max_tokens = 3000,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        }, EngineConfig.JsonOptions());
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, config.AiInsights.TimeoutMs)) };
        if (!string.IsNullOrWhiteSpace(config.AiInsights.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.AiInsights.OpenAiApiKey);
        var endpoint = $"{config.AiInsights.OpenAiBaseUrl.TrimEnd('/')}/chat/completions";
        var responseText = string.Empty;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Batch graph verifier returned HTTP {(int)response.StatusCode}.");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Batch graph model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batch graph response content was empty.");
            var proposal = IncidentBatchGraphVerification.Parse(entry, requests, json, responseModel, DateTimeOffset.UtcNow);
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, started, ct);
            return proposal;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, false, ex.GetBaseException().Message, started, CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(
        string responseText,
        string endpoint,
        string requestedModel,
        int payloadChars,
        bool success,
        string error,
        long started,
        CancellationToken ct)
    {
        var usage = ReadUsage(responseText);
        try
        {
            await database.AddLmUsageAsync(new TokenUsageEntryDto(
                0,
                DateTime.UtcNow,
                $"incident batch graph verification:{runId}",
                "chat.completions",
                success,
                error,
                endpoint,
                requestedModel,
                usage.ResponseModel,
                usage.FinishReason,
                payloadChars,
                payloadChars,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not record incident batch graph verification usage");
        }
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, string ResponseModel, string FinishReason) ReadUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, 0, string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
            return (
                usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
                usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
                usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : 0,
                root.TryGetProperty("model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("finish_reason", out var finish) ? finish.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return (0, 0, 0, string.Empty, string.Empty);
        }
    }
}
