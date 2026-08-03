# Exact Existing-Incident Membership Adapter

Status: implemented and proven locally; not enabled in production.

## Why the earlier formulation was rejected

The July 31 Qwen shadow asked one model pass to discover groups across an
entire mixed call window. The transport was reliable, but the semantic result
was not. Adding one radio-retrieved call changed the disposition of 17 of 65
otherwise identical baseline observations. Two of four grouped additions became
unsupported singleton groups, and the other grouped results mixed unrelated
traffic. That is evidence against continuing to tune the same task.

The old semantic shadow is no longer registered as a running PizzaWave service.
Its disabled experimental code remains only so the recorded trial and tests can
be understood; it is not an available production path.

## Replacement decision

The model now answers one narrow question: does one radio-retrieved candidate
call belong to one specific incident that PizzaWave has already established?

PizzaWave supplies:

- every call already in the target incident;
- the exact established call sharing the transmitting-radio identifier;
- the candidate call.

The model may return only:

- `include`;
- `do_not_include`;
- `unresolved`.

The application retains the incident and call identities. They never appear in
the prompt and the model never reproduces them. A result can only refer back to
the existing application-owned incident and candidate objects. It cannot create
a new incident, publish a singleton, split an incident, combine incidents, or
rename anything. `unresolved` leaves the candidate unchanged for later evidence.

Radio identity is retrieval evidence, not proof of membership. The prompt says
this explicitly. The complete incident is required; incidents with more than
five calls are rejected by this first adapter instead of being silently
truncated, because the frozen model input limit is six calls including the
candidate.

## Proof

`IncidentTargetMembershipAdapterTests` contains nine focused tests proving:

- all established incident calls are presented;
- the directly linked call must already belong to that incident;
- the candidate must not already belong to the incident;
- private incident, call, and observation identities do not enter the prompt;
- repeated transcript text still maps to the correct application object;
- incidents beyond the input limit fail instead of losing evidence;
- model and transport failures fail closed;
- the response schema permits only the three decisions above;
- the reported model identity must match the requested model.

The reusable read-only runner is under
`utilities/IncidentTargetMembershipReplay`.

## Smoke result

The earlier thirteen-package shadow did not contain thirteen valid examples for
this new question. Audit history showed only one candidate whose exact
radio-linked counterpart belonged to an already-established incident:

- existing incident 7863 contained five calls about a multi-vehicle crash;
- call 1578270 was the exact radio-linked established call;
- call 1578272 was the candidate;
- Qwen 3 4B Instruct 2507 returned `do_not_include` in three repeated runs;
- each successful run used 974 prompt tokens and 13 completion tokens;
- successful durations were 1.35, 1.39, and 1.45 seconds.

The first attempt waited behind model work and exceeded the original two-minute
client timeout. A retry while the endpoint was idle completed normally. The
runner now uses a five-minute ceiling so a queued offline replay is not mistaken
for malformed output. Production work was not changed, and the temporary model
was unloaded afterward.

This is a successful mechanical and semantic smoke test, not an accuracy
evaluation. One case cannot establish accuracy.

## Recommended next experiment

Keep production candidate use disabled. Run a short read-only collector that
records only source-linked candidates where the exact linked call already
belongs to an incident with no more than five calls. Stop after 25 eligible
cases or two hours, whichever happens first. Then:

1. run this adapter once per saved case;
2. select exactly five cases for the single reviewer, favoring disagreements and
   `unresolved` decisions;
3. measure include, do-not-include, and unresolved counts, model duration, and
   reviewer corrections;
4. proceed to a larger fixed evaluation only if the five-case check shows the
   task itself is coherent.

Do not treat current incident membership as gold. It supplies the target
context; the candidate decision still requires independent review.
