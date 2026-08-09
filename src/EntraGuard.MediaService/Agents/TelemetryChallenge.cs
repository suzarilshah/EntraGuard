using System.Text;
using System.Text.Json;
using EntraGuard.MediaService.Tools;

namespace EntraGuard.MediaService.Agents;

/// <summary>One question and the facts that make an answer correct.</summary>
/// <param name="Question">Asked aloud, as written.</param>
/// <param name="ExpectedFacts">
/// What a correct answer must contain. Plural because a sign-in has several true
/// descriptions — "London", "the UK", "the office" — and refusing all but one of them
/// refuses the genuine user.
/// </param>
public sealed record TelemetryQuestion(string Question, IReadOnlyList<string> ExpectedFacts);

/// <summary>
/// Builds an authentication challenge from what the tenant already knows about this user,
/// minutes ago.
///
/// Replaces stored security questions, which NIST SP 800-63-3 says are not an acceptable
/// authenticator: personal information "does not constitute an acceptable secret for
/// digital authentication". Mother's maiden name and first pet are researchable, breached,
/// and permanent — a stolen answer is stolen forever.
///
/// This is different in the ways that matter:
///
///   * Nothing is stored, so there is nothing to breach. The facts are read live from
///     Entra sign-in logs at the moment of the call and discarded after it.
///   * The answers expire on their own. "Where did you sign in from this morning?" is
///     useless to an attacker tomorrow, which caps the value of any single compromise.
///   * The facts are not public. This is tenant telemetry, not a credit bureau or a social
///     profile — the objection that sinks conventional dynamic KBA does not apply.
///   * An attacker who has phoned the victim cannot research it in advance, because the
///     question did not exist until the call started.
///
/// It is still knowledge, and knowledge is still the weakest kind of factor. It sits
/// AFTER the number match, which proves possession, and underneath the coercion analysis,
/// which can veto the whole call. That layering is the point: this is the check an
/// attacker cannot prepare for, not the check that stands alone.
/// </summary>
public sealed class TelemetryChallenge(GraphClient graph, ILogger<TelemetryChallenge> logger)
{
    /// <summary>
    /// Compose questions from this user's recent sign-ins.
    /// </summary>
    /// <returns>
    /// Up to <paramref name="count"/> questions, or an empty list when the directory has
    /// nothing usable to ask about.
    /// </returns>
    /// <remarks>
    /// An empty list is a normal outcome, not an error: a brand-new account, or a tenant
    /// that has not granted AuditLog.Read.All, genuinely has nothing to ask. The caller
    /// treats that as "no second challenge", never as a failed one — inventing a question
    /// nobody can answer would lock out exactly the users this cannot see.
    /// </remarks>
    public async Task<IReadOnlyList<TelemetryQuestion>> BuildAsync(
        string objectId, string? tenantId, int count, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return [];
        }

        try
        {
            // Recent interactive sign-ins only. Service and token-refresh events are
            // invisible to the user, and asking about something they never saw is a
            // question with no correct answer.
            var path =
                $"/v1.0/auditLogs/signIns?$filter=userId eq '{objectId}'"
              + "&$top=25&$orderby=createdDateTime desc";

            // Against the user's own directory. Ours holds nothing about them, and asking
            // it returns an empty list that is indistinguishable from a new account.
            var (status, body) = await graph.GetForTenantAsync(path, tenantId, cancellationToken);

            if (status != System.Net.HttpStatusCode.OK)
            {
                logger.LogInformation(
                    "Sign-in telemetry unavailable ({Status}); skipping the telemetry challenge.", status);
                return [];
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("value", out var events))
            {
                return [];
            }

            var signIns = events.EnumerateArray()
                .Select(Read)
                .Where(s => s is not null)
                .Select(s => s!)
                // EntraGuard's own service sign-ins are not things the user did. An admin
                // consent or a background token refresh is invisible to them, and building a
                // question from one asks about an event they never experienced.
                .Where(s => s.App is null || !s.App.StartsWith("EntraGuard", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Compose(signIns, count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not build a telemetry challenge for {ObjectId}.", objectId);
            return [];
        }
    }

    private sealed record SignIn(
        DateTimeOffset At, string? City, string? State, string? Country,
        string? App, string? Os, string? Browser);

    private static SignIn? Read(JsonElement e)
    {
        if (!e.TryGetProperty("createdDateTime", out var when)
            || !when.TryGetDateTimeOffset(out var at))
        {
            return null;
        }

        var location = e.TryGetProperty("location", out var l) ? l : default;
        var device = e.TryGetProperty("deviceDetail", out var d) ? d : default;

        return new SignIn(
            at,
            Text(location, "city"),
            Text(location, "state"),
            Text(location, "countryOrRegion"),
            Text(e, "appDisplayName"),
            Text(device, "operatingSystem"),
            Text(device, "browser"));
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>
    /// Turn sign-in records into questions a person can answer out loud.
    /// </summary>
    /// <remarks>
    /// Deliberately built in code rather than by asking a model to invent questions. A model
    /// improvising here would occasionally produce something with no single correct answer,
    /// or leak the answer inside the question, and every such case is a legitimate user
    /// refused. The model's judgement is used where it is genuinely better than code —
    /// deciding whether a spoken answer means the same thing — and nowhere else.
    /// </remarks>
    private static List<TelemetryQuestion> Compose(List<SignIn> signIns, int count)
    {
        var questions = new List<TelemetryQuestion>();
        if (signIns.Count == 0)
        {
            return questions;
        }

        var latest = signIns[0];

        // Where. The strongest of these: a coercer on the phone rarely knows where their
        // victim physically was this morning, and it changes constantly.
        if (latest.City is not null || latest.State is not null || latest.Country is not null)
        {
            // City, state AND country all count as correct.
            //
            // Entra records the city an IP resolves to — "Petaling Jaya" — and almost
            // nobody answers that question with their exact suburb. They say the nearest
            // big city, the state, or the country. All three are true statements about
            // where they were, and refusing two of them refuses the genuine user for
            // answering honestly.
            var facts = new List<string>();
            if (latest.City is not null) facts.Add(latest.City);
            if (latest.State is not null) facts.Add(latest.State);
            if (latest.Country is not null) facts.Add(CountryName(latest.Country));

            questions.Add(new TelemetryQuestion(
                "Which town, city, or country were you in the last time you signed in?", facts));
        }

        // NO question about the application.
        //
        // appDisplayName is the name of the Entra app REGISTRATION — "EntraGuard-RP",
        // "EntraGuard-Service" — which the user has never seen and would never say. They
        // know the product as "Contoso Treasury". Asking it guaranteed a refusal for
        // answering correctly in human terms, and no amount of semantic judging fixes a
        // question whose expected answer is an internal identifier.

        // What they used. Coarse on purpose — "Windows", not a build number.
        if (latest.Os is not null || latest.Browser is not null)
        {
            var facts = new List<string>();
            if (latest.Os is not null) facts.Add(latest.Os);
            if (latest.Browser is not null) facts.Add(latest.Browser);

            questions.Add(new TelemetryQuestion(
                "What kind of device or browser did you last sign in from?", facts));
        }

        // A distinct earlier location, when there is one. Two questions about the same
        // sign-in are one question asked twice.
        var earlier = signIns.Skip(1).FirstOrDefault(
            s => s.City is not null && !string.Equals(s.City, latest.City, StringComparison.OrdinalIgnoreCase));

        if (earlier is not null)
        {
            questions.Add(new TelemetryQuestion(
                "Before that, which other town or city have you signed in from recently?",
                [earlier.City!]));
        }

        return questions.Take(count).ToList();
    }

    /// <summary>
    /// Country codes are not how people speak. "MY" is a correct answer said as "Malaysia".
    /// </summary>
    private static string CountryName(string code) => code.ToUpperInvariant() switch
    {
        "MY" => "Malaysia",
        "SG" => "Singapore",
        "GB" or "UK" => "the United Kingdom",
        "US" => "the United States",
        "AU" => "Australia",
        "IN" => "India",
        "ID" => "Indonesia",
        "TH" => "Thailand",
        "PH" => "the Philippines",
        "JP" => "Japan",
        _ => code,
    };

    /// <summary>Render the facts for the judge, without handing them to anything spoken.</summary>
    public static string DescribeExpected(TelemetryQuestion question)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < question.ExpectedFacts.Count; i++)
        {
            if (i > 0) builder.Append(" OR ");
            builder.Append(question.ExpectedFacts[i]);
        }
        return builder.ToString();
    }
}
