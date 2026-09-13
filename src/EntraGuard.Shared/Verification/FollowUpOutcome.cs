namespace EntraGuard.Shared.Verification;

/// <summary>
/// One follow-up probe, and what came back.
/// </summary>
/// <param name="Facet">Which facet was deepened: "location" or "device".</param>
/// <param name="Question">Exactly what was asked aloud. Never the expected answer.</param>
/// <param name="Answered">Whether anything was heard at all.</param>
/// <param name="Correct">Whether what was heard matched.</param>
/// <remarks>
/// Recorded, never fatal. A probe follows a question the caller has ALREADY answered
/// correctly, so a wrong answer here cannot mean they are the wrong person — it means they
/// could not recall a detail, which is ordinary. Voice already works this way: it never
/// denies access on its own, it asks for a stronger factor.
///
/// <para>
/// <paramref name="Answered"/> and <paramref name="Correct"/> are separate because they have
/// opposite fixes. Nothing heard is a microphone, transcription or timing problem; something
/// heard and rejected is a matching problem. The knowledge challenge already learned to
/// report those separately rather than conflating them into "not answered correctly".
/// </para>
///
/// <para>
/// Kept so the audit trail can answer "why did this call take ninety seconds?" without
/// anyone reading container logs from the right replica at the right moment.
/// </para>
/// </remarks>
public sealed record FollowUpOutcome(string Facet, string Question, bool Answered, bool Correct);
