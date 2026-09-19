using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Sessions;

public sealed class ReadinessService(TelemetryChallenge telemetry, ProfileChallenge profiles, KnowledgeStore knowledge,
    VoiceprintStore voices, TenantPolicyService policies)
{
    public async Task<object> GetAsync(Owner owner, CancellationToken ct)
    {
        var signIns = telemetry.BuildAsync(owner.ObjectId, owner.TenantId, 3, ct);
        var profile = profiles.BuildAsync(owner.ObjectId, owner.TenantId, ct);
        var stored = knowledge.GetAsync(owner.TenantId, owner.ObjectId, ct);
        var voice = voices.GetAsync(owner.TenantId, owner.ObjectId, ct);
        await Task.WhenAll(signIns, profile, stored, voice);
        var evidence = (await signIns).Questions.Select(_ => new QuestionEvidence("SignIn", "sign-in", true, true))
            .Concat((await profile).Select(p => new QuestionEvidence(p.Source.ToString(), p.Facet, true, true))).ToList();
        if (await stored is not null) evidence.Add(new QuestionEvidence("Registered", "stored", true, true));
        var enrolled = await voice is not null;
        var ceiling = EvidenceAssurance.Evaluate(true, evidence, (await signIns).FollowUps.Count > 0
            ? [new FollowUpOutcome("sign-in", "", true, true)] : [], enrolled ? "Match" : "NotAssessed");
        var policy = await policies.GetAsync(owner.TenantId, ct);
        return new { ceiling = ceiling.Level.ToString(), sources = evidence.Select(e => e.Source).Distinct(),
            candidateCount = evidence.Count, registeredQuestion = await stored is not null, enrolledVoice = enrolled,
            policy, gaps = ceiling.Gaps, canMeetMinimum = ceiling.Level >= Enum.Parse<AssuranceLevel>(policy.MinimumAssurance),
            note = "An upper bound from currently available sources, not a promised verdict. Questions and expected answers are never returned." };
    }
}
