using static EntraGuard.MediaService.Endpoints.SimulationEndpoint;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Built-in conversations for exercising the detection pipeline.
///
/// The benign scenarios matter as much as the malicious ones. A detector you only ever
/// watch fire is a detector you cannot evaluate, and EntraGuard's false positives land on
/// people who have done nothing wrong — locked out of their account mid-workday by an
/// automated decision. Every scenario here states what it SHOULD score, so a run that
/// disagrees is a visible regression rather than an interesting result.
/// </summary>
internal static class Scenarios
{
    internal sealed record Scenario(
        string Name,
        string Description,
        string Expectation,
        IReadOnlyList<SimulatedLine> Lines);

    private static SimulatedLine Caller(string text) => new() { Speaker = "caller", Text = text };
    private static SimulatedLine User(string text) => new() { Speaker = "user", Text = text };

    public static readonly IReadOnlyDictionary<string, Scenario> All = new Dictionary<string, Scenario>
    {
        ["helpdesk-fraud"] = new(
            "Help desk impersonation",
            "Caller poses as IT, manufactures urgency, and coaches the user through an MFA approval they did not initiate.",
            "High risk (80+). Should reach about_to_approve and trigger containment.",
            [
                Caller("Hi, am I speaking with Sam? This is Daniel from the IT service desk, I'm calling about your account."),
                User("Yes, speaking. What's this about?"),
                Caller("We've flagged some unusual sign-in activity on your Microsoft 365 account overnight. Someone outside the organisation tried to authenticate as you. I need to secure the account before it escalates."),
                User("Okay, I did just try to log in a minute ago."),
                Caller("Right, that would be the reset I've already started on my side, don't worry about it. I need to stay on the line while we push the security update through. If you hang up the session drops and we'd have to start the lockout process from the beginning."),
                User("How long does this take?"),
                Caller("Two minutes at most. You should be seeing a notification from Microsoft Authenticator on your phone right about now. Do you see it?"),
                User("Yeah, there's a prompt. It's asking me to approve a sign-in."),
                Caller("Perfect, that's the one I just triggered from our end. Go ahead and tap approve for me."),
                User("It's showing a number, twenty-eight?"),
                Caller("That's the verification code. Just read that out to me and I'll confirm it matches on my console."),
                Caller("And once you've done that I'll need you to add a backup authentication method so this doesn't happen again. I'll walk you through registering it while we're on the call."),
                Caller("Sam? Just tap approve for me. I've got a queue building and I really don't want to have to lock the account out."),
            ]),

        ["benign-helpdesk"] = new(
            "Legitimate help desk call",
            "User calls the help desk about a password reset. The agent actively discourages every behaviour an attacker relies on.",
            "LOW risk (under 40). No remediation should fire. This is the false-positive control.",
            [
                User("Hi, I called about not being able to get into my email. Ticket four one nine two."),
                Caller("Got it, thanks, I can see the ticket. Before we go further, can you confirm you called us on the number from the intranet page and not one someone gave you?"),
                User("Yeah, I looked it up on the portal."),
                Caller("Good, that's the right habit. So your password expired Sunday night and the reminder emails went to the mailbox you can't get into, which is why this is annoying."),
                User("That would do it."),
                Caller("Right. I'm not going to reset it for you over the phone, and I will never ask you for a code. You'll go to the self-service reset page yourself, aka.ms slash sspr, and it'll verify you through your existing Authenticator. I'll stay on if you get stuck but I won't see anything you enter."),
                User("Okay, I've got the page up."),
                Caller("Take your time. And if any prompt shows up on your phone that you didn't just cause yourself, don't approve it, tell me and we'll stop."),
                User("That's gone through, I'm back in."),
                Caller("Great, I'll close the ticket. One thing worth knowing: nobody from this desk will ever ring you and ask you to approve a prompt or read out a code. If that happens, hang up and call us on the intranet number."),
            ]),

        ["remote-access"] = new(
            "Remote access tooling",
            "Caller impersonates support and pushes the user to install remote-control software.",
            "High risk (80+). Should flag remote_access_tooling and authority_impersonation.",
            [
                Caller("Good afternoon, this is the Microsoft security team. We've detected malware communicating from your workstation."),
                User("Really? I haven't noticed anything wrong."),
                Caller("It's designed to be silent, that's why we monitor for it centrally. I need to get onto the machine to clear it before it spreads to the file shares."),
                User("How do you do that?"),
                Caller("Open the Start menu and type Quick Assist. It's built into Windows so it's completely safe. I'll give you a six digit code to connect."),
                User("Okay, it's open. It's asking for a code."),
                Caller("The code is four eight two nine one seven. Type that in and click Allow. Don't close the window while I'm working, and I'd rather you didn't mention this to your IT team yet, we're still assessing which machines are affected."),
            ]),

        ["tap-request"] = new(
            "Temporary Access Pass solicitation",
            "Caller impersonates a new-starter onboarding flow to solicit a Temporary Access Pass.",
            "High risk. Should flag temporary_access_pass_request and helpdesk_reset_fraud.",
            [
                Caller("Hi, this is Priya from identity operations. I'm setting up device enrolment for your new laptop."),
                User("I wasn't expecting a new laptop."),
                Caller("It's part of the refresh programme, it'll make sense when the shipment notice comes through. To pre-register the device I need a Temporary Access Pass generated against your account."),
                User("I don't know what that is."),
                Caller("It's a one-time onboarding credential, completely routine. I've raised the request on my side. You'll get it in your authenticator in a moment, and I just need you to read the pass back to me so I can complete the enrolment."),
                Caller("It does expire quite quickly, so if you could read it out as soon as it arrives that would help."),
            ]),

        ["ambiguous-support"] = new(
            "Ambiguous support call",
            "A real support agent under time pressure, discussing MFA without ever soliciting a credential action.",
            "MODERATE at most (under 60), and crucially LOW confidence. Tests that pressure alone is not treated as evidence.",
            [
                Caller("Hi, it's Marcus from the service desk, following up on the mailbox migration."),
                User("Oh right, is that happening today?"),
                Caller("It's happening now, which is why I'm chasing. Your mailbox is one of the last ones in the batch and the window closes at five."),
                User("What do you need from me?"),
                Caller("Nothing urgent, just that you might get signed out of Outlook and have to sign back in normally. Your usual MFA prompt will come up as it always does."),
                User("That's fine. Should I do anything now?"),
                Caller("No, honestly nothing. If it's still broken tomorrow morning raise a ticket and it'll come back to me. Sorry to chase, it's just the batch deadline."),
            ]),
    };
}
