using System.Net;
using System.Text.Json;
using EntraGuard.MediaService.Tools;
using EntraGuard.Shared.Supportability;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Questions built from who the caller is and what they have been doing.
///
/// <para>
/// Separate from <c>TelemetryChallenge</c>, which owns sign-in history alone. This owns
/// everything else: the directory record, the calendar, the mailbox, chat, and recent files.
/// Each source is fetched independently and is allowed to fail on its own — a tenant that has
/// not consented to <c>Mail.Read</c> loses the mail questions and keeps the rest, which is the
/// only tolerable behaviour when the alternative is refusing a caller because an unrelated
/// permission is missing.
/// </para>
///
/// <para>
/// <b>Metadata only, never content.</b> A question may use the name of who emailed, the
/// subject line, the title of a meeting or the name of a file. Nothing reads a message body or
/// a document, and nothing from any of them is spoken aloud — the caller is asked to recall,
/// and the expected answer stays inside the judge. The distinction matters: "who emailed you
/// most recently" needs one name, while the alternative reads somebody's correspondence to
/// generate a quiz.
/// </para>
///
/// <para>
/// <b>Every question is also a disclosure.</b> This product exists to catch coercion — someone
/// beside the caller, coaching them. Asking "who are you meeting at three" tells a listener
/// that the caller has a three o'clock and invites them to say with whom. Sign-in facts do not
/// have that problem, which is why <see cref="ChallengeSelection"/> guarantees an expiring
/// fact rather than guaranteeing an activity one, and why nothing here is ever the sole
/// question on a call.
/// </para>
/// </summary>
public sealed class ProfileChallenge(GraphClient graph, Sinks.FaultRecorder faults, ILogger<ProfileChallenge> logger)
{
    /// <summary>How far back the activity sources look. Recent enough that a person remembers.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(30);

    /// <summary>Per-source failures from the last build, for diagnostics.</summary>
    public IReadOnlyList<string> LastSkipped { get; private set; } = [];

    /// <summary>
    /// Build every question this caller's directory and activity can support.
    /// </summary>
    /// <remarks>
    /// Returns candidates, not a challenge. Which of them a call actually asks is
    /// <see cref="ChallengeSelection"/>'s decision, so that the "one per facet" and "never all
    /// directory" rules live in one tested place rather than being re-implemented per source.
    /// </remarks>
    public async Task<IReadOnlyList<ChallengeCandidate>> BuildAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return [];
        }

        var skipped = new List<string>();

        // Fired together: five independent Graph reads, and the slowest must not decide how
        // long a caller sits in silence.
        var sources = await Task.WhenAll(
            Safe("directory", () => DirectoryAsync(objectId, tenantId, cancellationToken), skipped),
            Safe("calendar", () => CalendarAsync(objectId, tenantId, cancellationToken), skipped),
            Safe("mail", () => MailAsync(objectId, tenantId, cancellationToken), skipped),
            Safe("files", () => FilesAsync(objectId, tenantId, cancellationToken), skipped),
            Safe("chat", () => ChatAsync(objectId, tenantId, cancellationToken), skipped));

        LastSkipped = skipped;

        if (skipped.Count > 0)
        {
            logger.LogInformation(
                "Profile questions: {Built} built, sources unavailable: {Skipped}.",
                sources.Sum(s => s.Count), string.Join(", ", skipped));
        }

        return sources.SelectMany(s => s).ToList();
    }

    /// <summary>
    /// Run one source, and let it fail alone.
    /// </summary>
    /// <remarks>
    /// The whole point of the per-source split. A missing consent, a throttle or a malformed
    /// payload costs its own questions and nothing else; the caller is never refused because
    /// an unrelated permission was absent.
    /// </remarks>
    private async Task<IReadOnlyList<ChallengeCandidate>> Safe(
        string name,
        Func<Task<IReadOnlyList<ChallengeCandidate>>> build,
        List<string> skipped)
    {
        try
        {
            var built = await build();

            if (built.Count == 0)
            {
                skipped.Add($"{name}(nothing to ask)");
            }

            return built;
        }
        catch (Exception ex)
        {
            skipped.Add($"{name}({ex.GetType().Name})");
            logger.LogWarning(ex, "Profile source {Source} failed; its questions are skipped.", name);
            return [];
        }
    }

    // ── Directory ───────────────────────────────────────────────────────────
    //
    // Free: User.Read.All is already granted. Also the weakest of the sources, because most
    // of it is on the caller's public professional profile — hence Strength 1 and never a
    // primary question.
    private async Task<IReadOnlyList<ChallengeCandidate>> DirectoryAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken)
    {
        var candidates = new List<ChallengeCandidate>();

        var (status, body) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}?$select=jobTitle,department,officeLocation,city",
            tenantId, cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            Record("directory", "User.Read.All", status, objectId, tenantId);
            return candidates;
        }

        using var user = JsonDocument.Parse(body);

        Add(candidates, "department", "Which team or department are you in?",
            Text(user.RootElement, "department"), FactSource.Directory, 1);

        Add(candidates, "office", "Which office are you based out of?",
            Text(user.RootElement, "officeLocation") ?? Text(user.RootElement, "city"),
            FactSource.Directory, 1);

        // Deliberately no job-title question. It is the most public fact of the lot and the
        // one a caller is most likely to phrase differently from the directory on any given
        // day — "engineer" against "Senior Software Engineer II" is a refusal waiting to
        // happen, for a fact that proves almost nothing.

        var (managerStatus, managerBody) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/manager?$select=displayName,givenName",
            tenantId, cancellationToken);

        if (managerStatus != HttpStatusCode.OK)
        {
            Record("manager", "User.Read.All", managerStatus, objectId, tenantId);
        }
        else
        {
            using var manager = JsonDocument.Parse(managerBody);
            var full = Text(manager.RootElement, "displayName");
            var first = Text(manager.RootElement, "givenName");

            // Both spellings accepted: people say their manager's first name.
            var facts = new[] { full, first }.Where(f => f is not null).Select(f => f!).ToList();
            if (facts.Count > 0)
            {
                candidates.Add(new ChallengeCandidate(
                    "manager", "Who do you report to?", facts, FactSource.Directory, 2));
            }
        }

        // Who works for them.
        //
        // Better than the manager question in one specific way: a manager is a single fact
        // that a researcher reads off an org chart in one go, whereas naming any one of
        // several reports requires knowing the team rather than the line above it. Any single
        // name is accepted — somebody with six reports should not have to recite all six, and
        // which one comes to mind first is not a security property.
        var (reportsStatus, reportsBody) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/directReports?$select=displayName,givenName&$top=20",
            tenantId, cancellationToken);

        if (reportsStatus != HttpStatusCode.OK)
        {
            Record("directreports", "User.Read.All", reportsStatus, objectId, tenantId);
        }
        else
        {
            using var reports = JsonDocument.Parse(reportsBody);

            if (reports.RootElement.TryGetProperty("value", out var people))
            {
                var names = people.EnumerateArray()
                    .SelectMany(person => new[] { Text(person, "displayName"), Text(person, "givenName") })
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Only asked when somebody actually reports to them. A question whose true
                // answer is "nobody" is one an impostor wins by guessing "nobody" — it would
                // lengthen the call and prove close to nothing.
                if (names.Count > 0)
                {
                    candidates.Add(new ChallengeCandidate(
                        "directreports",
                        "Can you name someone who reports to you?",
                        names,
                        FactSource.Directory,
                        2));
                }
            }
        }

        return candidates;
    }

    // ── Calendar ────────────────────────────────────────────────────────────
    //
    // Strong: a meeting yesterday is recent, unguessable and expires. Asked about the
    // ORGANISER rather than the subject, because a subject line is often confidential and
    // repeating one aloud on a call somebody may be listening to is exactly the disclosure
    // this class warns about.
    private async Task<IReadOnlyList<ChallengeCandidate>> CalendarAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken)
    {
        var from = DateTimeOffset.UtcNow - Window;
        var to = DateTimeOffset.UtcNow.AddHours(6);

        // "yyyy-MM-ddTHH:mm:ssZ", not round-trip "o".
        //
        // The o format renders the offset as +00:00, and a literal + in a query string is a
        // SPACE once decoded — so Graph received a malformed date and answered 400 on every
        // call. Z says the same thing with no character that means something else in a URL.
        var (status, body) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/calendarView"
          + $"?startDateTime={from:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={to:yyyy-MM-ddTHH:mm:ssZ}"
          + "&$select=subject,organizer,start&$orderby=start/dateTime desc&$top=10",
            tenantId, cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            Record("calendar", "Calendars.Read", status, objectId, tenantId);
            return [];
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var events))
        {
            return [];
        }

        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("organizer", out var organizer)
                || !organizer.TryGetProperty("emailAddress", out var email))
            {
                continue;
            }

            var name = Text(email, "name");
            if (name is null)
            {
                continue;
            }

            return
            [
                new ChallengeCandidate(
                    "meeting",
                    "Thinking about your most recent meeting — who organised it?",
                    [name, name.Split(' ')[0]],
                    FactSource.Activity,
                    3),
            ];
        }

        return [];
    }

    // ── Mail ────────────────────────────────────────────────────────────────
    //
    // The sender's name only. Never the body, never a subject read aloud.
    private async Task<IReadOnlyList<ChallengeCandidate>> MailAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken)
    {
        var (status, body) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/messages"
          + "?$select=from,receivedDateTime&$orderby=receivedDateTime desc&$top=5",
            tenantId, cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            Record("mail", "Mail.Read", status, objectId, tenantId);
            return [];
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var messages))
        {
            return [];
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("receivedDateTime", out var when)
                && when.TryGetDateTimeOffset(out var at)
                && DateTimeOffset.UtcNow - at > Window)
            {
                continue;
            }

            if (!message.TryGetProperty("from", out var from)
                || !from.TryGetProperty("emailAddress", out var email))
            {
                continue;
            }

            var name = Text(email, "name");
            if (name is null)
            {
                continue;
            }

            return
            [
                new ChallengeCandidate(
                    "mail",
                    "Who sent you the most recent email in your inbox?",
                    [name, name.Split(' ')[0]],
                    FactSource.Activity,
                    3),
            ];
        }

        return [];
    }

    // ── Files ───────────────────────────────────────────────────────────────
    //
    // File NAMES only. Chosen over content for the obvious reason, and kept to one question
    // because a caller who works in many documents a day will not reliably name the last one.
    private async Task<IReadOnlyList<ChallengeCandidate>> FilesAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken)
    {
        // No $top: /drive/recent does not accept it and answers 400 if given one. The result
        // is already ordered most-recent-first, and only the first usable item is taken.
        var (status, body) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/drive/recent", tenantId, cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            Record("files", "Files.Read.All", status, objectId, tenantId);
            return [];
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var items))
        {
            return [];
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = Text(item, "name");
            if (name is null)
            {
                continue;
            }

            // Strip the extension: nobody says "dot docx" out loud, and judging them for it
            // would be judging the format rather than the memory.
            var spoken = System.IO.Path.GetFileNameWithoutExtension(name);

            return
            [
                new ChallengeCandidate(
                    "file",
                    "What was the last document you opened called?",
                    [spoken, name],
                    FactSource.Activity,
                    2),
            ];
        }

        return [];
    }

    // ── Chat ────────────────────────────────────────────────────────────────
    //
    // Who, not what. The participant's name in the most recent chat.
    private async Task<IReadOnlyList<ChallengeCandidate>> ChatAsync(
        string objectId, string? tenantId, CancellationToken cancellationToken)
    {
        var (status, body) = await graph.GetForTenantAsync(
            $"/v1.0/users/{objectId}/chats"
          + "?$expand=members&$orderby=lastMessagePreview/createdDateTime desc&$top=5",
            tenantId, cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            Record("chat", "Chat.Read.All", status, objectId, tenantId);
            return [];
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var chats))
        {
            return [];
        }

        foreach (var chat in chats.EnumerateArray())
        {
            if (!chat.TryGetProperty("members", out var members))
            {
                continue;
            }

            // The other person, not the caller. A question answered by saying your own name
            // proves nothing.
            var other = members.EnumerateArray()
                .Select(m => (Name: Text(m, "displayName"), Id: Text(m, "userId")))
                .FirstOrDefault(m => m.Name is not null
                    && !string.Equals(m.Id, objectId, StringComparison.OrdinalIgnoreCase));

            if (other.Name is null)
            {
                continue;
            }

            return
            [
                new ChallengeCandidate(
                    "chat",
                    "Who did you most recently exchange Teams messages with?",
                    [other.Name, other.Name.Split(' ')[0]],
                    FactSource.Activity,
                    3),
            ];
        }

        return [];
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    private static void Add(
        List<ChallengeCandidate> into, string facet, string question,
        string? fact, FactSource source, int strength)
    {
        if (!string.IsNullOrWhiteSpace(fact))
        {
            into.Add(new ChallengeCandidate(facet, question, [fact], source, strength));
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>
    /// Name the missing consent rather than the status code.
    /// </summary>
    /// <remarks>
    /// A 403 here is nearly always "this tenant has not consented to that permission", and
    /// saying so is the difference between a fix taking a minute and taking an afternoon —
    /// the lesson of the telemetry fallback, which spent a day looking like a deleted feature.
    /// </remarks>
    private void Record(string source, string permission, HttpStatusCode status, string objectId, string? tenantId)
    {
        // Every non-success, not only 403.
        //
        // The first version recorded consent failures alone, so a 404 — no mailbox, no
        // OneDrive, no manager assigned — produced an empty source and complete silence.
        // "Not consented" and "this account has no mailbox" need opposite fixes and looked
        // identical from every surface, which is the same conflation that has cost this
        // project a day at a time.
        var (cause, fix) = status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                ($"The tenant has not granted admin consent for {permission}.",
                 $"Grant {permission} to EntraGuard-Service in the caller's tenant."),

            HttpStatusCode.NotFound =>
                ("The resource does not exist for this account — most often no mailbox or "
                 + "OneDrive, which means no Exchange or SharePoint licence, or no manager set.",
                 "Assign the relevant licence if these questions are wanted, or accept the "
                 + "smaller pool. Consent is not the problem here and granting more will not help."),

            HttpStatusCode.TooManyRequests =>
                ("Graph is throttling this tenant.",
                 "Transient. The source returns on a later call with no action needed."),

            _ => ($"Graph returned {(int)status}.",
                  "Check the Graph response for this path; it is neither a consent nor a "
                  + "licensing problem."),
        };

        faults.Record(new Fault(
            FaultComponent.Graph,
            $"profile.{source}_unavailable",
            FaultSeverity.Degraded,
            $"The {source} source returned {(int)status}, so its questions were not built.",
            "The caller was asked from a smaller pool of questions. Nothing failed for them, "
            + "and the challenge is weaker than it could have been.",
            cause,
            fix + " Every other source still works without it.",
            CorrelationId: objectId,
            Detail: $"tenant={tenantId} status={(int)status} permission={permission}"));
    }
}
