# Conversational Verification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Let the verification call deepen an answer it has already accepted, so that a
correct-but-coarse answer stops being worth the same as a correct-and-specific one.

**Architecture:** A deterministic `ConversationDirector` owns turn-taking and picks at most
two follow-up probes, precomputed alongside the primary questions from the same sign-in
record. Probes feed `RiskScore` and never refuse anyone. The voice agent renders one move at
a time in one of two registers and still holds no secret and no authority.

**Tech Stack:** .NET 9, xunit 2.9, FluentAssertions 6.12. Tests in
`tests/EntraGuard.UnitTests`. Run with `dotnet test`.

**Design:** [2026-09-13-conversational-verification-design.md](2026-09-13-conversational-verification-design.md)

**Read before starting:** the class comment on
[`TelemetryChallenge`](../../src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs) and on
[`VerificationRisk`](../../src/EntraGuard.Shared/Verification/VerificationRisk.cs). Both
explain constraints this plan depends on and neither is obvious from the code.

---

## Task 1: The probe record and how probes are composed

A probe deepens an answer already given. It is not a new question, and it is built in code
rather than invented by a model, for the reason `TelemetryChallenge.Compose` already
documents: a model improvising here eventually produces a question with no single correct
answer, and every such case is a legitimate user refused.

**There is no time-of-day probe, and that is deliberate.** Graph reports
`createdDateTime` in UTC. A user in Malaysia signing in at 09:00 local is 01:00 UTC, which
buckets as "night" — so the probe would expect "night", the caller would truthfully say
"morning", and a correct answer would be scored wrong. Fixing it needs a country-to-timezone
mapping that is ambiguous for large countries, which is real work for the weakest of the
three facets. Left out until someone wants it enough to do that properly.

**Files:**
- Modify: `src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs`
- Test: `tests/EntraGuard.UnitTests/FollowUpProbeTests.cs` (create)

**Step 1: Write the failing tests**

```csharp
using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What the call asks AFTER an answer it has already accepted.
///
/// The location question accepts the city, the state OR the country, because Entra records
/// the city an IP resolves to and refusing a true answer refuses the genuine user. The cost
/// of that leniency is that "Malaysia" passes and is worth almost nothing — anyone who
/// dialled a +60 number can produce it. These probes buy that precision back, and because
/// the caller has already passed, they cannot refuse anybody.
/// </summary>
public class FollowUpProbeTests
{
    private static TelemetryChallenge.SignIn Sample(
        string? city = "Petaling Jaya", string? state = "Selangor",
        string? os = "Windows", string? browser = "Edge") =>
        new(DateTimeOffset.UtcNow, city, state, "MY", "EntraGuard-RP", os, browser);

    [Fact]
    public void The_location_probe_names_the_country_the_caller_would_have_said()
    {
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Should().Contain(p => p.Facet == "location");
        probes.Single(p => p.Facet == "location").Question
            .Should().Contain("Malaysia");
    }

    [Fact]
    public void The_location_probe_wants_the_city_or_the_state_and_not_the_country()
    {
        // The whole point: the country is what the primary question already accepted.
        var probe = TelemetryChallenge.ComposeFollowUps([Sample()])
            .Single(p => p.Facet == "location");

        probe.ExpectedFacts.Should().BeEquivalentTo(["Petaling Jaya", "Selangor"]);
        probe.ExpectedFacts.Should().NotContain("Malaysia");
    }

    [Fact]
    public void Location_is_stronger_than_device()
    {
        // Budget is small. A weak probe must not crowd out the one carrying the evidence.
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Single(p => p.Facet == "location").Strength
            .Should().BeGreaterThan(probes.First(p => p.Facet == "device").Strength);
    }

    [Fact]
    public void Both_halves_of_the_device_question_get_a_probe()
    {
        // The primary accepts the OS or the browser. Whichever the caller left unsaid is
        // the one worth asking for, and which that is is not known until they speak.
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Where(p => p.Facet == "device")
            .SelectMany(p => p.ExpectedFacts)
            .Should().BeEquivalentTo(["Edge", "Windows"]);
    }

    [Fact]
    public void Nothing_is_probed_when_the_directory_holds_nothing_to_probe()
    {
        // A tenant with no location and no device detail is a normal outcome, not an error.
        TelemetryChallenge.ComposeFollowUps(
            [Sample(city: null, state: null, os: null, browser: null)])
            .Should().BeEmpty();
    }

    [Fact]
    public void No_probe_asks_about_the_time_of_day()
    {
        // Graph reports UTC. 09:00 in Kuala Lumpur is 01:00 UTC, so a time probe would
        // expect "night" and score a truthful "morning" as wrong. See the plan.
        TelemetryChallenge.ComposeFollowUps([Sample()])
            .Should().NotContain(p =>
                p.Question.Contains("time", StringComparison.OrdinalIgnoreCase));
    }
}
```

**Step 2: Run them and watch them fail**

```bash
dotnet test tests/EntraGuard.UnitTests --filter FollowUpProbeTests
```

Expected: build error — `ComposeFollowUps` does not exist.

**Step 3: Add the record and the composer**

Add to `TelemetryChallenge.cs`, immediately after the `TelemetryQuestion` record:

```csharp
/// <summary>
/// A question that deepens an answer the caller has already given correctly.
/// </summary>
/// <param name="Facet">"location" or "device". At most one probe per facet is asked.</param>
/// <param name="Question">Asked aloud, as written.</param>
/// <param name="ExpectedFacts">
/// What a correct answer contains. Same semantics as <see cref="TelemetryQuestion"/>.
/// </param>
/// <param name="AlreadyCovered">
/// Facts that make this probe pointless. When the caller has already said one of these,
/// asking reads as not having listened — which is both rude and a tell that the call is
/// scripted.
/// </param>
/// <param name="Strength">
/// What a correct answer is worth. Asked highest-first, because the budget is one or two
/// and a weak probe must not crowd out the one carrying the evidence.
/// </param>
public sealed record FollowUpProbe(
    string Facet,
    string Question,
    IReadOnlyList<string> ExpectedFacts,
    IReadOnlyList<string> AlreadyCovered,
    int Strength);
```

Add this method to the `TelemetryChallenge` class, next to `Compose`:

```csharp
/// <summary>
/// Build the probes that can deepen the answers to <see cref="Compose"/>'s questions.
/// </summary>
/// <remarks>
/// Built from the same sign-in record, at the same moment, in code. Nothing here is new
/// information to ask about — every probe narrows something the primary question already
/// accepted loosely, which is why none of them can refuse a caller who has passed.
///
/// No time-of-day probe. Graph reports createdDateTime in UTC, so 09:00 in Kuala Lumpur
/// buckets as "night" and a truthful "morning" would score wrong. Correcting for that
/// needs a country-to-timezone mapping that is ambiguous for large countries, and this is
/// the weakest of the facets. Not worth being wrong about.
/// </remarks>
internal static List<FollowUpProbe> ComposeFollowUps(List<SignIn> signIns)
{
    var probes = new List<FollowUpProbe>();
    if (signIns.Count == 0)
    {
        return probes;
    }

    var latest = signIns[0];

    // Location. The strongest by some distance, and the reason this mechanism exists: the
    // primary question accepts the country, which a caller who dialled a +60 number can
    // produce. Someone who was actually there names the place without thinking.
    var fine = new List<string>();
    if (latest.City is not null) fine.Add(latest.City);
    if (latest.State is not null) fine.Add(latest.State);

    if (fine.Count > 0)
    {
        var country = latest.Country is not null ? CountryName(latest.Country) : null;
        probes.Add(new FollowUpProbe(
            "location",
            country is null
                ? "And whereabouts was that, roughly?"
                : $"And whereabouts in {country}, roughly?",
            fine, fine, 2));
    }

    // Device. The primary accepts the operating system OR the browser, so exactly one of
    // these is worth asking — whichever the caller left unsaid. Which that is is not known
    // until they answer, so both are composed and the director picks.
    if (latest.Browser is not null)
    {
        probes.Add(new FollowUpProbe(
            "device", "And which browser were you using on it?",
            [latest.Browser], [latest.Browser], 1));
    }

    if (latest.Os is not null)
    {
        probes.Add(new FollowUpProbe(
            "device", "And what kind of machine was that on?",
            [latest.Os], [latest.Os], 1));
    }

    return probes;
}
```

**Step 4: Run the tests**

```bash
dotnet test tests/EntraGuard.UnitTests --filter FollowUpProbeTests
```

Expected: 6 passed.

**Step 5: Commit**

```bash
git add src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs tests/EntraGuard.UnitTests/FollowUpProbeTests.cs
git commit -m "Compose the probes that buy back what leniency spent"
```

---

## Task 2: The director that decides whether to probe at all

**Files:**
- Create: `src/EntraGuard.MediaService/Agents/ConversationDirector.cs`
- Test: `tests/EntraGuard.UnitTests/ConversationDirectorTests.cs`

**Step 1: Write the failing tests**

```csharp
using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Whether the call goes deeper, and how far.
///
/// Pure and deterministic on purpose, for the same reason PolicyGate is: the model conducts
/// the conversation, but it does not get to decide how long to keep somebody on the phone
/// or what to ask them next.
/// </summary>
public class ConversationDirectorTests
{
    private static readonly FollowUpProbe Location =
        new("location", "And whereabouts in Malaysia, roughly?",
            ["Petaling Jaya", "Selangor"], ["Petaling Jaya", "Selangor"], 2);

    private static readonly FollowUpProbe Browser =
        new("device", "And which browser were you using on it?", ["Edge"], ["Edge"], 1);

    [Fact]
    public void A_country_only_answer_gets_asked_where_exactly()
    {
        // The case the whole design is for. "Malaysia" is true and nearly worthless.
        ConversationDirector.NextProbe([Location, Browser], "Malaysia", [], riskElevated: false)
            .Should().Be(Location);
    }

    [Fact]
    public void Someone_who_already_named_the_city_is_not_asked_again()
    {
        // They gave the strong answer first time. Asking again reads as not listening, and
        // there is nothing left to learn.
        ConversationDirector.NextProbe(
            [Location, Browser], "I was in Petaling Jaya", [], riskElevated: false)
            .Should().Be(Browser);
    }

    [Fact]
    public void Nothing_is_asked_when_the_caller_has_covered_everything()
    {
        ConversationDirector.NextProbe(
            [Location, Browser], "Petaling Jaya, on Edge", [], riskElevated: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_calm_call_gets_one_probe_and_stops()
    {
        // A genuine user's call must not run longer because a model found them interesting.
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location"], riskElevated: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_worrying_call_buys_a_second_probe()
    {
        // Rising risk buys MORE conversation, not colder conversation. Another probe reads
        // as ordinary thoroughness, and every extra second of speech feeds the Analyst.
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location"], riskElevated: true)
            .Should().Be(Browser);
    }

    [Fact]
    public void Not_even_a_worrying_call_gets_a_third()
    {
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location", "device"], riskElevated: true)
            .Should().BeNull();
    }

    [Fact]
    public void The_stronger_probe_goes_first()
    {
        // Budget is one on a calm call, so whichever is asked is the only one asked.
        ConversationDirector.NextProbe([Browser, Location], "Malaysia", [], riskElevated: false)
            .Should().Be(Location);
    }

    [Fact]
    public void A_caller_who_said_nothing_is_still_probed()
    {
        // Silence covers nothing, so every probe is still open. Reaching here at all means
        // the primary question was judged correct, so this is a defensive case rather than
        // a live one — it must not throw.
        ConversationDirector.NextProbe([Location, Browser], "", [], riskElevated: false)
            .Should().Be(Location);
    }
}
```

**Step 2: Run and watch fail**

```bash
dotnet test tests/EntraGuard.UnitTests --filter ConversationDirectorTests
```

Expected: build error — `ConversationDirector` does not exist.

**Step 3: Write it**

```csharp
namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Decides whether the verification call goes deeper, and with which question.
///
/// The conversational agent renders what it is handed. It does not choose to keep somebody
/// on the phone, and it does not choose what to ask them — for the same reason the Analyst
/// does not choose to revoke a session. The model proposes wording; this disposes of
/// structure. Pure and free of I/O so it can be exhaustively tested, because a rule nobody
/// can test is a claim rather than a control.
/// </summary>
public static class ConversationDirector
{
    /// <summary>Probes allowed on a call that has given no cause for concern.</summary>
    /// <remarks>
    /// One, not zero. The location probe earns its place on every call, because a
    /// country-only answer is weak evidence whether or not anything else looks wrong.
    /// </remarks>
    public const int CalmBudget = 1;

    /// <summary>
    /// Probes allowed once the Analyst is worried.
    ///
    /// Two, not more. Rising risk buys more conversation, but a genuine user's call must
    /// still end in about the time they expect it to.
    /// </summary>
    public const int ElevatedBudget = 2;

    public static int Budget(bool riskElevated) => riskElevated ? ElevatedBudget : CalmBudget;

    /// <summary>
    /// The next probe to ask, or null to stop.
    /// </summary>
    /// <param name="candidates">Probes composed for this call. Order is not significant.</param>
    /// <param name="heard">
    /// Everything the caller has said so far. Used to skip probes whose ground they have
    /// already covered — asking for something somebody just told you reads as not listening.
    /// </param>
    /// <param name="facetsAsked">Facets already probed. One probe per facet, at most.</param>
    /// <param name="riskElevated">Whether the Analyst has crossed the elevated threshold.</param>
    public static FollowUpProbe? NextProbe(
        IReadOnlyList<FollowUpProbe> candidates,
        string heard,
        IReadOnlyCollection<string> facetsAsked,
        bool riskElevated)
    {
        if (facetsAsked.Count >= Budget(riskElevated))
        {
            return null;
        }

        return candidates
            .Where(p => !facetsAsked.Contains(p.Facet))
            .Where(p => !Covers(heard, p.AlreadyCovered))
            .OrderByDescending(p => p.Strength)
            .FirstOrDefault();
    }

    /// <summary>
    /// Has the caller already said one of these facts?
    /// </summary>
    /// <remarks>
    /// Substring, not equality, and deliberately generous. People answer in sentences —
    /// "I was in Petaling Jaya this morning" — and the cost of the two mistakes is not
    /// symmetric. A missed probe loses one weak signal. A probe asked for something the
    /// caller has just said makes the call sound like it is not listening, which is
    /// precisely the impression this whole feature exists to remove.
    /// </remarks>
    internal static bool Covers(string heard, IReadOnlyList<string> facts) =>
        !string.IsNullOrWhiteSpace(heard)
        && facts.Any(f => heard.Contains(f, StringComparison.OrdinalIgnoreCase));
}
```

**Step 4: Run**

```bash
dotnet test tests/EntraGuard.UnitTests --filter ConversationDirectorTests
```

Expected: 8 passed.

**Step 5: Commit**

```bash
git add src/EntraGuard.MediaService/Agents/ConversationDirector.cs tests/EntraGuard.UnitTests/ConversationDirectorTests.cs
git commit -m "Decide in code how deep a verification call goes"
```

---

## Task 3: Record what the probes found, and let it move the risk score

**Files:**
- Modify: `src/EntraGuard.Shared/Verification/VerificationSession.cs`
- Create: `src/EntraGuard.Shared/Verification/FollowUpOutcome.cs`
- Modify: `src/EntraGuard.Shared/Verification/VerificationRisk.cs`
- Test: `tests/EntraGuard.UnitTests/VerificationRiskTests.cs` (append)

**Step 1: Write the failing tests**

Append to `VerificationRiskTests.cs`:

```csharp
[Fact]
public void A_probe_nobody_could_answer_raises_the_score()
{
    var clean = VerificationRisk.Score(0, "Match", 1, "teams");
    var vague = VerificationRisk.Score(0, "Match", 1, "teams",
        followUps: [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false)]);

    vague.Score.Should().BeGreaterThan(clean.Score);
}

[Fact]
public void A_probe_answered_well_costs_nothing()
{
    var clean = VerificationRisk.Score(0, "Match", 1, "teams");
    var sharp = VerificationRisk.Score(0, "Match", 1, "teams",
        followUps: [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)]);

    sharp.Score.Should().Be(clean.Score);
}

[Fact]
public void Missing_every_probe_is_not_on_its_own_enough_to_flag_a_call()
{
    // A probe cannot refuse anybody and must not be able to flag anybody either. People
    // forget which browser they used. The signal is worth having only in company.
    var result = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
    [
        new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false),
        new FollowUpOutcome("device", "Which browser?", Answered: false, Correct: false),
    ]);

    result.Band.Should().Be(RiskBand.Low);
}

[Fact]
public void A_missed_probe_says_which_one_it_was()
{
    // "Why was I flagged?" must have an answer that survives being asked a second time.
    var result = VerificationRisk.Score(0, "Match", 1, "teams",
        followUps: [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false)]);

    result.Contributors.Should().Contain(c =>
        c.Contains("location", StringComparison.OrdinalIgnoreCase));
}
```

**Step 2: Run and watch fail**

```bash
dotnet test tests/EntraGuard.UnitTests --filter VerificationRiskTests
```

Expected: build error — `FollowUpOutcome` does not exist.

**Step 3: Write `FollowUpOutcome.cs`**

```csharp
namespace EntraGuard.Shared.Verification;

/// <summary>
/// One follow-up probe, and what came back.
/// </summary>
/// <param name="Facet">"location" or "device".</param>
/// <param name="Question">Exactly what was asked aloud. Never the expected answer.</param>
/// <param name="Answered">Whether anything was heard at all.</param>
/// <param name="Correct">Whether what was heard matched.</param>
/// <remarks>
/// Recorded, never fatal. A probe follows a question the caller has ALREADY answered
/// correctly, so a wrong answer here cannot mean they are the wrong person — it means they
/// could not recall a detail, which is ordinary. Voice already works this way: it never
/// denies access alone, it asks for a stronger factor.
///
/// Kept so the audit trail can answer "why did this call take ninety seconds?" without
/// anyone reading container logs from the right replica at the right moment.
/// </remarks>
public sealed record FollowUpOutcome(string Facet, string Question, bool Answered, bool Correct);
```

**Step 4: Add the field to `VerificationSession`**

Next to `KnowledgeAttempts`:

```csharp
/// <summary>
/// Probes asked after a correct answer, and how each went. See <see cref="FollowUpOutcome"/>.
///
/// Empty is the normal case on a calm call with a specific first answer, and means "nothing
/// needed asking" rather than "nothing was recorded".
/// </summary>
public IReadOnlyList<FollowUpOutcome> FollowUps { get; set; } = [];

/// <summary>
/// How the agent is speaking: "Warm" or "Protective".
///
/// Two states rather than a gradient, and the agent chooses neither. Warmth that tracked the
/// risk score would turn the call into a live readout of the detector — a scammer runs it
/// three times, learns which phrasing turns the voice cold, and stops using it. Two states
/// with an externally authorised transition leak only at the moment the gate has already
/// decided to act.
/// </summary>
public string Register { get; set; } = "Warm";
```

**Step 5: Score it**

In `VerificationRisk.Score`, add the parameter after `knowledgeAttempts`:

```csharp
IReadOnlyList<FollowUpOutcome>? followUps = null)
```

Document it:

```csharp
/// <param name="followUps">
/// Probes asked after a correct answer. Only the unmet ones contribute, and they are capped
/// well below the moderate threshold: a probe that cannot refuse anybody must not be able to
/// flag anybody on its own either.
/// </param>
```

And add the contribution, after the knowledge-attempts block:

```csharp
// ── Follow-up probes ────────────────────────────────────────────────
//
// Capped at 20 against a moderate threshold of 25, so probes alone never move a call out
// of Low no matter how many are missed. That is the point: this is a signal worth having
// in company with others and worth nothing on its own, because forgetting which browser
// you used at eight in the morning is what people do.
var unmet = followUps?.Where(f => !f.Correct).ToList() ?? [];
if (unmet.Count > 0)
{
    contributors.Add((
        Math.Min(unmet.Count * 10, 20),
        $"Could not confirm {string.Join(" or ", unmet.Select(f => f.Facet).Distinct())} "
      + "when asked for more detail"));
}
```

**Step 6: Run**

```bash
dotnet test tests/EntraGuard.UnitTests --filter VerificationRiskTests
```

Expected: all pass, including the pre-existing ones.

**Step 7: Commit**

```bash
git add src/EntraGuard.Shared/Verification tests/EntraGuard.UnitTests/VerificationRiskTests.cs
git commit -m "Record what a probe found, and let it weigh a little"
```

---

## Task 4: Ask the probes on the real call

The largest task, and the only one without unit tests — it is I/O against a live call. Keep
the logic in it to a minimum; everything decidable now lives in tasks 1-3.

**Files:**
- Modify: `src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs` (return type of `BuildAsync`)
- Modify: `src/EntraGuard.MediaService/Endpoints/VerificationEndpoint.cs:381`
- Modify: `src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs:371` and `RunTelemetryChallengeAsync`

**Step 1: Carry probes out of `BuildAsync`**

`TelemetryChallenge` is registered as a singleton
([`Program.cs:126`](../../src/EntraGuard.MediaService/Program.cs)), so probes must NOT be
stashed on the instance the way `LastFailure` and `LastCounts` are. Those are diagnostics
and tolerate a race; this is a decision input and does not.

Add the record:

```csharp
/// <summary>
/// The questions for a call and the probes that can deepen them.
/// </summary>
/// <remarks>
/// Returned together because they come from the same sign-in record and one Graph call.
/// Kept off the instance because this type is a singleton and two concurrent verifications
/// would otherwise read each other's probes.
/// </remarks>
public sealed record TelemetryChallengeSet(
    IReadOnlyList<TelemetryQuestion> Questions,
    IReadOnlyList<FollowUpProbe> FollowUps)
{
    public static readonly TelemetryChallengeSet Empty = new([], []);
}
```

Change `BuildAsync` to return `Task<TelemetryChallengeSet>`. Every early return becomes
`TelemetryChallengeSet.Empty`; the final return becomes:

```csharp
return new TelemetryChallengeSet(Compose(usable, count), ComposeFollowUps(usable));
```

**Step 2: Update both callers**

`VerificationEndpoint.cs:381` — it only wants the questions:

```csharp
var questions = (await telemetry.BuildAsync(objectId, tenantId, 3, cancellationToken)).Questions;
```

`VerificationCoordinator.cs:371` — keep the set, and pass it to `RunTelemetryChallengeAsync`.
Change that method's signature from `IReadOnlyList<Agents.TelemetryQuestion> questions` to
`Agents.TelemetryChallengeSet set`, and add at the top of the body:

```csharp
var questions = set.Questions;
```

so the rest of the method is untouched.

**Step 3: Hoist the caller's answer out of the retry loop**

In `RunTelemetryChallengeAsync`, the inner `for (var tries = ...)` loop scopes `spoken`. The
probe needs it. Before that loop add:

```csharp
// What they actually said, kept past the retry loop so a probe can avoid asking for
// something they have already told us.
string? lastSpoken = null;
```

and inside the loop, right after the echo check, add:

```csharp
if (spoken is not null)
{
    lastSpoken = spoken;
}
```

**Step 4: Track which facets have been probed**

Just before the `for (var index = 0; ...)` question loop:

```csharp
// One probe per facet across the whole call, not per question.
var facetsProbed = new HashSet<string>(StringComparer.Ordinal);
```

**Step 5: Probe after a correct answer**

Immediately after the `if (!correct) { ... return; }` block, inside the question loop:

```csharp
// A correct answer is not automatically a strong one. The location question accepts
// the country because refusing a true answer refuses the genuine user, which means
// "Malaysia" passes and is worth almost nothing. Ask for the precision that leniency
// spent. The caller has already passed, so this can only add.
await ProbeAsync(
    verification, monitored, set.FollowUps, facetsProbed, lastSpoken ?? string.Empty, token);
```

**Step 6: Write `ProbeAsync`**

Add it next to `RunTelemetryChallengeAsync`:

```csharp
/// <summary>
/// Ask one follow-up, if the director wants one, and record what came back.
/// </summary>
/// <remarks>
/// Never completes the verification and never throws into the challenge. A probe that could
/// end a call would be a third authority on this path, and there are already two more than
/// enough. Everything it learns arrives as a <see cref="FollowUpOutcome"/> and is weighed
/// later by <see cref="VerificationRisk"/>.
/// </remarks>
private async Task ProbeAsync(
    VerificationSession verification,
    LiveCall monitored,
    IReadOnlyList<Agents.FollowUpProbe> candidates,
    HashSet<string> facetsProbed,
    string heard,
    CancellationToken token)
{
    if (candidates.Count == 0)
    {
        return;
    }

    // The Analyst's view of the call so far. Elevated buys a second probe, nothing more —
    // it does not change a word of what is said or how it is said.
    var elevated = verification.PeakRiskDuringCall >= VerificationRisk.ElevatedThreshold;

    var probe = Agents.ConversationDirector.NextProbe(candidates, heard, facetsProbed, elevated);
    if (probe is null)
    {
        return;
    }

    facetsProbed.Add(probe.Facet);

    try
    {
        var askedAt = await SpeakAndSettleAsync(verification, monitored, probe.Question, token);
        var spoken = await ListenForAnswerAsync(monitored, askedAt, token);

        if (spoken is not null && IsEchoOf(probe.Question, spoken))
        {
            spoken = null;
        }

        var correct = spoken is not null && await judge.IsEquivalentAsync(
            probe.Question,
            string.Join(" OR ", probe.ExpectedFacts),
            spoken,
            token);

        logger.LogInformation(
            "Verification {Id}: probe [{Facet}] — {Outcome}. Asked: {Question}",
            verification.VerificationId, probe.Facet,
            spoken is null ? "nothing heard" : correct ? "confirmed" : "not confirmed",
            probe.Question);

        verification.FollowUps =
        [
            .. verification.FollowUps,
            new FollowUpOutcome(probe.Facet, probe.Question, spoken is not null, correct),
        ];
    }
    catch (OperationCanceledException)
    {
        // The call budget ran out mid-probe. The primary questions are already answered and
        // the verdict does not depend on this, so it is dropped rather than surfaced.
        logger.LogInformation(
            "Verification {Id}: probe [{Facet}] was cut short by the call budget.",
            verification.VerificationId, probe.Facet);
    }
}
```

**Step 7: Feed the outcomes into the score**

Find where `VerificationRisk.Score` is called for this session and add
`followUps: verification.FollowUps`. Locate it with:

```bash
grep -rn "VerificationRisk.Score" src --include=*.cs
```

**Step 8: Build and run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: builds clean, all tests pass.

**Step 9: Commit**

```bash
git add -A src tests
git commit -m "Ask for the detail the first answer left out"
```

---

## Task 5: Let the agent sound like a person

Until now the call is still terse — a probe is just one more flat sentence. This task is the
part the user actually hears.

**Files:**
- Modify: `src/EntraGuard.MediaService/Agents/VoiceAgent.cs` (the `Instructions` const)
- Test: `tests/EntraGuard.UnitTests/VoiceGuardrailTests.cs` (append)

**Step 1: Confirm the guardrail still holds against a chattier agent**

Append to `VoiceGuardrailTests.cs` a test that the guardrail refuses an utterance containing
a forbidden fact even when wrapped in conversational filler, e.g. `"Of course — you said
Selangor, so that's confirmed."` against forbidden `["Selangor"]`. Read the existing tests
in that file first and match their shape; the guardrail API is already covered there.

The point of the test: warmth adds words around the secret, and the check must be on
substrings rather than on the whole utterance.

**Step 2: Rewrite `Instructions`**

Replace the step-numbered script with a persona and hard limits. Keep every SECURITY RULE
line that exists today, verbatim — none of them are about tone, and the rewrite in `19cb817`
is already on record for having deleted a safety behaviour as collateral in a prompt change.

The new instructions must say:
- Speak like a colleague making a routine check: warm, brief, unhurried.
- Acknowledge what the caller said before moving on, without repeating it back in full.
- One question at a time. Never more than two sentences.
- Answer "what is this?" and "I wasn't expecting a call" plainly, then return to the question.
- Ask only the question the system supplies, in its own words if that sounds more natural,
  never changing what is being asked for.

**Step 3: Add the register**

Add a `Register` property to `VoiceAgent` with two values and a per-turn instruction:

- `Warm` — the default, described above.
- `Protective` — plain and direct: say that something about the call is concerning, tell the
  caller to make sure nobody is listening and that nobody should be helping them, and repeat
  the question once.

The agent never sets this itself. Set it from the coordinator only where the gate has
already authorised an intervention. Find that point with:

```bash
grep -rn "PolicyGate\|Decide(" src/EntraGuard.MediaService --include=*.cs | head
```

**Step 4: Build, test, commit**

```bash
dotnet build && dotnet test
git add -A src tests
git commit -m "Let the verification call sound like a colleague"
```

---

## Task 6: Show it in the admin blade

**Files:**
- Modify: `src/portal/app/(admin)/verification/page.tsx`
- Modify: `src/EntraGuard.MediaService/Endpoints/VerificationEndpoint.cs` (`Describe`)

Add `followUps` and `register` to the `Describe` projection, then render the probes on the
verification detail: the facet, whether it was confirmed, and the question as asked. Never
the expected answer — `Describe` is reachable by the portal and the expected facts are the
thing this whole subsystem exists to not say out loud.

Check what `Describe` already excludes before adding anything; `ViewerToken` and `MatchCode`
have specific reasons for how they are handled and those reasons apply here too.

```bash
git add -A src
git commit -m "Show what the call asked for beyond the first answer"
```

---

## Done when

- `dotnet test` passes.
- A verification call where the caller answers "Malaysia" is asked where exactly, and the
  answer appears in `FollowUps` on the session.
- A verification call where the caller answers "Petaling Jaya" is asked about the browser
  instead, or nothing at all.
- No call asks more than two probes.
- No probe can produce a `Failed` verdict. Verify by reading `ProbeAsync` — it must contain
  no call to `CompleteAsync`.
