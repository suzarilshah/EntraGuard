# Demo script — help-desk impersonation with MFA coaching

Read the **CALLER** lines. A second person reads **USER**, or read both.

Modelled on the tradecraft behind the 2023–2024 help-desk intrusions: no technical exploit,
just an authoritative stranger, manufactured urgency, and a request to approve something the
victim did not initiate.

Pace it. Roughly two minutes. The Analyst scores every three seconds, so rushing gives it
nothing to work with and the risk gauge stays flat while you talk over it.

---

**CALLER:** Hi, am I speaking with Sam? This is Daniel from the IT service desk. I'm calling about your account.

**USER:** Uh, yes — what's this about?

**CALLER:** We've flagged some unusual sign-in activity on your Microsoft 365 account overnight. It looks like someone outside the organisation attempted to authenticate as you. I need to secure the account before it escalates.

**USER:** Okay… I did just try to log in a minute ago.

**CALLER:** Right, that would be the reset I've already started on my side. Don't worry about it. Now, I need to stay on the line with you while we push the security update through — if you hang up, the session drops and we'd have to start the lockout process from the beginning.

> *`authority_impersonation` and `urgency_pretexting` should both register by here. Risk climbs into Elevated.*

**USER:** How long does this take?

**CALLER:** Two minutes, tops. You should be seeing a notification from Microsoft Authenticator on your phone right about now. Do you see it?

**USER:** Yeah, there's a prompt. It's asking me to approve a sign-in.

> *Stage flips to `about_to_approve`. This is the moment to point at the screen.*

**CALLER:** Perfect, that's the one I just triggered from our end. Go ahead and tap approve for me.

**USER:** It's showing a number — twenty-eight?

**CALLER:** That's the verification code, yeah. Just read that out to me and I'll confirm it matches on my console.

> *`otp_elicitation` and `mfa_fatigue_coaching`. Risk should be crossing 80. Legitimate support NEVER needs this.*

**CALLER:** And once you've done that, I'll need you to add a backup authentication method so this doesn't happen again — I'll walk you through registering it while we're on the call.

> *`mfa_method_registration`. This is the persistence step — the attacker's own MFA method.*

**CALLER:** Sam? Just tap approve for me. I've got a queue building and I really don't want to have to lock the account out.

> *EntraGuard should interrupt with the spoken warning around here.*

---

## Why this scores high

| Beat | Vector | The tell |
|---|---|---|
| "This is Daniel from the IT service desk" | `authority_impersonation` | Unverified authority claim |
| "if you hang up… lockout process" | `urgency_pretexting` | Discourages independent verification |
| "that's the one I just triggered" | `mfa_fatigue_coaching` | Prompt the user did not initiate |
| "read that out to me" | `otp_elicitation` | **The decisive one** |
| "add a backup authentication method" | `mfa_method_registration` | Attacker-controlled persistence |

The discriminator is not tone or topic — legitimate support calls sound like this too. It
is the direction of the credential flow. Real support never needs you to read back a
one-time code, and never needs you to approve a prompt *they* triggered.
