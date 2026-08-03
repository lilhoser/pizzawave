using System.Collections.ObjectModel;
using System.Text;

namespace pizzad;

/// <summary>
/// Application-owned identity for one source observation. This type must never be
/// serialized into the model's generated token stream.
/// </summary>
public sealed record IncidentMembershipSourceIdentity(long CallId, string ObservationId);

/// <summary>
/// Evidence the model may inspect. Identity is deliberately absent.
/// </summary>
public sealed record IncidentMembershipModelEvidence(
    DateTimeOffset ObservedAt,
    string Transcript,
    string SystemName,
    string TalkgroupName,
    TimeSpan? AudioDuration);

/// <summary>
/// An application-owned decision cell. The inference adapter passes this object
/// back to the capture API; the model generates only the choice value.
/// </summary>
public sealed class IncidentMembershipSourceBinding
{
    internal IncidentMembershipSourceBinding(
        IncidentMembershipSourceIdentity identity,
        IncidentMembershipModelEvidence evidence)
    {
        Identity = identity;
        Evidence = evidence;
    }

    internal IncidentMembershipSourceIdentity Identity { get; }

    public IncidentMembershipModelEvidence Evidence { get; }
}

public enum IncidentMembershipCellChoice
{
    NotMember,
    Member
}

public enum IncidentMembershipResidualDisposition
{
    Unresolved,
    NonIncident
}

public sealed record IncidentMembershipHypothesis(
    IReadOnlyList<IncidentMembershipSourceIdentity> Sources);

public sealed record IncidentMembershipContractResult(
    IReadOnlyList<IncidentMembershipHypothesis> Hypotheses,
    IReadOnlyList<IncidentMembershipSourceIdentity> UnresolvedSources,
    IReadOnlyList<IncidentMembershipSourceIdentity> NonIncidentSources);

public sealed class IncidentMembershipContractException : InvalidOperationException
{
    public IncidentMembershipContractException(string message) : base(message)
    {
    }
}

/// <summary>
/// Captures one source-bound constrained generation. Generated choices are
/// recorded against application-owned binding objects, never parsed back to a
/// source by identifier, array position, transcript copy, or hash.
/// </summary>
public sealed class IncidentMembershipContractSession
{
    private readonly IReadOnlyList<IncidentMembershipSourceBinding> _sources;
    private readonly HashSet<IncidentMembershipSourceBinding> _sourceSet;
    private readonly List<CapturedHypothesis> _hypotheses = [];
    private readonly Dictionary<IncidentMembershipSourceBinding, IncidentMembershipResidualDisposition> _residuals = [];
    private bool _completed;

    public IncidentMembershipContractSession(
        IEnumerable<(IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceList = sources
            .Select(source => new IncidentMembershipSourceBinding(source.Identity, source.Evidence))
            .ToList();
        if (sourceList.Count == 0)
            throw new ArgumentException("At least one source observation is required.", nameof(sources));
        if (sourceList.Any(source => string.IsNullOrWhiteSpace(source.Identity.ObservationId)))
            throw new ArgumentException("Every source observation must have an application identity.", nameof(sources));

        var duplicateIdentity = sourceList
            .GroupBy(source => source.Identity)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateIdentity is not null)
        {
            throw new ArgumentException(
                $"Duplicate source identity for call {duplicateIdentity.CallId} and observation '{duplicateIdentity.ObservationId}'.",
                nameof(sources));
        }

        _sources = new ReadOnlyCollection<IncidentMembershipSourceBinding>(sourceList);
        _sourceSet = sourceList.ToHashSet();
    }

    public IReadOnlyList<IncidentMembershipSourceBinding> Sources => _sources;

    /// <summary>
    /// Renders only evidence fields. Delimiters are forced application text and
    /// carry no source key, ordinal, position label, or opaque token.
    /// </summary>
    public string RenderModelEvidence()
    {
        var builder = new StringBuilder();
        foreach (var source in _sources)
        {
            builder.AppendLine("<evidence>");
            builder.Append("observed_at_utc: ").AppendLine(source.Evidence.ObservedAt.ToUniversalTime().ToString("O"));
            if (!string.IsNullOrWhiteSpace(source.Evidence.SystemName))
                builder.Append("system: ").AppendLine(source.Evidence.SystemName.Trim());
            if (!string.IsNullOrWhiteSpace(source.Evidence.TalkgroupName))
                builder.Append("talkgroup: ").AppendLine(source.Evidence.TalkgroupName.Trim());
            if (source.Evidence.AudioDuration is { } duration)
                builder.Append("audio_duration_seconds: ").AppendLine(duration.TotalSeconds.ToString("0.###"));
            builder.Append("transcript: ").AppendLine(source.Evidence.Transcript.Trim());
            builder.AppendLine("</evidence>");
        }
        return builder.ToString();
    }

    public IncidentMembershipHypothesisCapture BeginHypothesis()
    {
        EnsureOpen();
        return new IncidentMembershipHypothesisCapture(this);
    }

    public void RecordResidualDisposition(
        IncidentMembershipSourceBinding source,
        IncidentMembershipResidualDisposition disposition)
    {
        EnsureOpen();
        RequireOwnedSource(source);
        if (_residuals.ContainsKey(source))
            throw new IncidentMembershipContractException("A source has more than one residual disposition.");
        _residuals[source] = disposition;
    }

    public IncidentMembershipContractResult Complete()
    {
        EnsureOpen();
        var memberCounts = _sources.ToDictionary(source => source, _ => 0);
        foreach (var hypothesis in _hypotheses)
        {
            foreach (var source in hypothesis.Members)
                memberCounts[source]++;
        }

        var errors = new List<string>();
        foreach (var source in _sources)
        {
            var memberCount = memberCounts[source];
            var hasResidual = _residuals.ContainsKey(source);
            if (memberCount > 1)
                errors.Add($"Source '{source.Identity.ObservationId}' belongs to more than one hypothesis.");
            if (memberCount >= 1 && hasResidual)
                errors.Add($"Source '{source.Identity.ObservationId}' is both a hypothesis member and residual evidence.");
            if (memberCount == 0 && !hasResidual)
                errors.Add($"Source '{source.Identity.ObservationId}' has no final disposition.");
        }
        if (errors.Count > 0)
            throw new IncidentMembershipContractException(string.Join(Environment.NewLine, errors));

        _completed = true;
        return new IncidentMembershipContractResult(
            _hypotheses.Select(hypothesis => new IncidentMembershipHypothesis(
                hypothesis.Members.Select(source => source.Identity).ToList())).ToList(),
            _sources.Where(source => _residuals.TryGetValue(source, out var disposition) &&
                                     disposition == IncidentMembershipResidualDisposition.Unresolved)
                .Select(source => source.Identity).ToList(),
            _sources.Where(source => _residuals.TryGetValue(source, out var disposition) &&
                                     disposition == IncidentMembershipResidualDisposition.NonIncident)
                .Select(source => source.Identity).ToList());
    }

    internal void CommitHypothesis(
        IReadOnlyDictionary<IncidentMembershipSourceBinding, IncidentMembershipCellChoice> choices)
    {
        EnsureOpen();
        var missingCount = _sources.Count(source => !choices.ContainsKey(source));
        if (missingCount > 0)
            throw new IncidentMembershipContractException($"A hypothesis is missing {missingCount} source-bound decision cell(s).");
        var unknownCount = choices.Keys.Count(source => !_sourceSet.Contains(source));
        if (unknownCount > 0)
            throw new IncidentMembershipContractException("A hypothesis contains a decision cell from another contract session.");

        var members = _sources.Where(source => choices[source] == IncidentMembershipCellChoice.Member).ToList();
        if (members.Count == 0)
            throw new IncidentMembershipContractException("An active hypothesis must contain at least one source.");
        _hypotheses.Add(new CapturedHypothesis(members));
    }

    internal void RequireOwnedSource(IncidentMembershipSourceBinding source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_sourceSet.Contains(source))
            throw new IncidentMembershipContractException("The decision cell belongs to another contract session.");
    }

    private void EnsureOpen()
    {
        if (_completed)
            throw new IncidentMembershipContractException("The contract session is already complete.");
    }

    private sealed record CapturedHypothesis(IReadOnlyList<IncidentMembershipSourceBinding> Members);
}

public sealed class IncidentMembershipHypothesisCapture
{
    private readonly IncidentMembershipContractSession _session;
    private readonly Dictionary<IncidentMembershipSourceBinding, IncidentMembershipCellChoice> _choices = [];
    private bool _completed;

    internal IncidentMembershipHypothesisCapture(
        IncidentMembershipContractSession session)
    {
        _session = session;
    }

    public void RecordChoice(IncidentMembershipSourceBinding source, IncidentMembershipCellChoice choice)
    {
        if (_completed)
            throw new IncidentMembershipContractException("The hypothesis capture is already complete.");
        _session.RequireOwnedSource(source);
        if (!_choices.TryAdd(source, choice))
            throw new IncidentMembershipContractException("A source-bound decision cell was recorded more than once.");
    }

    public void Complete()
    {
        if (_completed)
            throw new IncidentMembershipContractException("The hypothesis capture is already complete.");
        _session.CommitHypothesis(_choices);
        _completed = true;
    }
}
