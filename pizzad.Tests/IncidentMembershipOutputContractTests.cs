namespace pizzad.Tests;

public sealed class IncidentMembershipOutputContractTests
{
    [Fact]
    public void SourceBindingsMapChoicesWithoutGeneratedIdentity()
    {
        var session = Session((17, "source-alpha", "same transcript"), (29, "source-beta", "same transcript"));
        var hypothesis = session.BeginHypothesis();
        hypothesis.RecordChoice(session.Sources[1], IncidentMembershipCellChoice.Member);
        hypothesis.RecordChoice(session.Sources[0], IncidentMembershipCellChoice.NotMember);
        hypothesis.Complete();
        session.RecordResidualDisposition(session.Sources[0], IncidentMembershipResidualDisposition.Unresolved);

        var result = session.Complete();

        var member = Assert.Single(Assert.Single(result.Hypotheses).Sources);
        Assert.Equal(new IncidentMembershipSourceIdentity(29, "source-beta"), member);
        Assert.Equal(new IncidentMembershipSourceIdentity(17, "source-alpha"), Assert.Single(result.UnresolvedSources));
        Assert.DoesNotContain("source-alpha", session.RenderModelEvidence(), StringComparison.Ordinal);
        Assert.DoesNotContain("source-beta", session.RenderModelEvidence(), StringComparison.Ordinal);
        Assert.DoesNotContain("17", session.RenderModelEvidence(), StringComparison.Ordinal);
        Assert.DoesNotContain("29", session.RenderModelEvidence(), StringComparison.Ordinal);
    }

    [Fact]
    public void MappingIsStableWhenSourceOrderChanges()
    {
        var first = Resolve(Session((1, "observation-a", "Alpha"), (2, "observation-b", "Bravo")), "Bravo");
        var second = Resolve(Session((2, "observation-b", "Bravo"), (1, "observation-a", "Alpha")), "Bravo");

        Assert.Equal(first, second);
        Assert.Equal(new IncidentMembershipSourceIdentity(2, "observation-b"), second);
    }

    [Fact]
    public void CompleteRejectsSourceMissingFromEveryDisposition()
    {
        var session = Session((1, "observation-a", "Alpha"), (2, "observation-b", "Bravo"));
        var hypothesis = session.BeginHypothesis();
        hypothesis.RecordChoice(session.Sources[0], IncidentMembershipCellChoice.Member);
        hypothesis.RecordChoice(session.Sources[1], IncidentMembershipCellChoice.NotMember);
        hypothesis.Complete();

        var error = Assert.Throws<IncidentMembershipContractException>(() => session.Complete());

        Assert.Contains("observation-b", error.Message, StringComparison.Ordinal);
        Assert.Contains("no final disposition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteRejectsDoubleMembershipAndResidualMembership()
    {
        var session = Session((1, "observation-a", "Alpha"), (2, "observation-b", "Bravo"));
        AddHypothesis(session, session.Sources[0]);
        AddHypothesis(session, session.Sources[0]);
        session.RecordResidualDisposition(session.Sources[0], IncidentMembershipResidualDisposition.Unresolved);
        session.RecordResidualDisposition(session.Sources[1], IncidentMembershipResidualDisposition.NonIncident);

        var error = Assert.Throws<IncidentMembershipContractException>(() => session.Complete());

        Assert.Contains("more than one hypothesis", error.Message, StringComparison.Ordinal);
        Assert.Contains("both a hypothesis member and residual evidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HypothesisRequiresEveryBoundCellEvenWhenEvidenceIsDuplicate()
    {
        var session = Session((1, "observation-a", "duplicate"), (2, "observation-b", "duplicate"));
        var hypothesis = session.BeginHypothesis();
        hypothesis.RecordChoice(session.Sources[0], IncidentMembershipCellChoice.Member);

        var error = Assert.Throws<IncidentMembershipContractException>(hypothesis.Complete);

        Assert.Contains("missing 1 source-bound decision cell", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingFromAnotherSessionFailsClosed()
    {
        var session = Session((1, "observation-a", "Alpha"));
        var other = Session((1, "observation-a", "Alpha"));
        var hypothesis = session.BeginHypothesis();

        var error = Assert.Throws<IncidentMembershipContractException>(() =>
            hypothesis.RecordChoice(other.Sources[0], IncidentMembershipCellChoice.Member));

        Assert.Contains("another contract session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySourceCanCarryForwardWithoutCreatingSingletons()
    {
        var session = Session((1, "observation-a", "Alpha"), (2, "observation-b", "Bravo"));
        foreach (var source in session.Sources)
            session.RecordResidualDisposition(source, IncidentMembershipResidualDisposition.Unresolved);

        var result = session.Complete();

        Assert.Empty(result.Hypotheses);
        Assert.Equal(2, result.UnresolvedSources.Count);
        Assert.Empty(result.NonIncidentSources);
    }

    private static IncidentMembershipSourceIdentity Resolve(
        IncidentMembershipContractSession session,
        string memberTranscript)
    {
        var hypothesis = session.BeginHypothesis();
        foreach (var source in session.Sources)
        {
            hypothesis.RecordChoice(
                source,
                source.Evidence.Transcript == memberTranscript
                    ? IncidentMembershipCellChoice.Member
                    : IncidentMembershipCellChoice.NotMember);
        }
        hypothesis.Complete();
        foreach (var source in session.Sources.Where(source => source.Evidence.Transcript != memberTranscript))
            session.RecordResidualDisposition(source, IncidentMembershipResidualDisposition.Unresolved);
        return Assert.Single(Assert.Single(session.Complete().Hypotheses).Sources);
    }

    private static void AddHypothesis(
        IncidentMembershipContractSession session,
        IncidentMembershipSourceBinding member)
    {
        var hypothesis = session.BeginHypothesis();
        foreach (var source in session.Sources)
            hypothesis.RecordChoice(source, ReferenceEquals(source, member) ? IncidentMembershipCellChoice.Member : IncidentMembershipCellChoice.NotMember);
        hypothesis.Complete();
    }

    private static IncidentMembershipContractSession Session(params (long CallId, string ObservationId, string Transcript)[] sources) =>
        new(sources.Select(source => (
            new IncidentMembershipSourceIdentity(source.CallId, source.ObservationId),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
                source.Transcript,
                "OT",
                "Dispatch",
                TimeSpan.FromSeconds(4)))));
}
