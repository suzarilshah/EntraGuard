namespace EntraGuard.Shared.Verification;

/// <summary>Only provenance and outcome. Never the question or expected answer.</summary>
public sealed record QuestionEvidence(string Source, string Facet, bool Asked = false, bool Correct = false);

public static class EvidenceAssurance
{
    public static AssuranceResult Evaluate(bool successfulChallenge, IReadOnlyList<QuestionEvidence> evidence,
        IReadOnlyList<FollowUpOutcome> followUps, string voiceOutcome)
    {
        if (!successfulChallenge) return new(AssuranceLevel.None, [], []);
        var correct = evidence.Where(e => e.Asked && e.Correct).ToArray();
        var signIn = correct.Any(e => e.Source == "SignIn");
        var usefulKnowledge = correct.Any(e => e.Source is "SignIn" or "Activity" or "Registered");
        var corroborated = followUps.Any(f => f.Answered && f.Correct) || voiceOutcome == "Match";
        var level = signIn && corroborated ? AssuranceLevel.High : usefulKnowledge ? AssuranceLevel.Substantial : AssuranceLevel.Low;
        var basis = new List<string> { "Number match completed." };
        basis.AddRange(correct.Select(e => $"Answered {e.Facet} from {e.Source}.").Distinct());
        if (voiceOutcome == "Match") basis.Add("Voice matched the enrolled profile.");
        var gaps = new List<string>();
        if (!signIn) gaps.Add("No sign-in-derived question was answered correctly.");
        if (!usefulKnowledge) gaps.Add("No activity or registered knowledge was established; directory facts alone do not raise assurance.");
        if (!corroborated) gaps.Add("No confirmed follow-up or enrolled voice match corroborated the challenge.");
        return new(level, basis, gaps);
    }
}
