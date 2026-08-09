# Control script — legitimate help-desk call

**This should score LOW.** Run it before or after the scam script.

Demonstrating a non-detection is worth as much as demonstrating a detection. A system that
only ever fires is a system nobody can evaluate, and false positives here land on people
who have done nothing wrong — locked out of their account, mid-workday, by an automated
decision.

This call has every surface feature of the scam: an IT voice, technical vocabulary, MFA,
password resets, mild time pressure. The one thing it lacks is the caller steering the user
toward an irreversible credential action the user did not initiate. Risk should stay
**below 40** and no remediation should fire.

---

**USER:** Hi — I called about not being able to get into my email. Ticket four-one-nine-two.

**CALLER:** Got it, thanks. So I can see the ticket. Before we go any further, can you confirm you called us on the number from the intranet page, not one someone gave you?

**USER:** Yeah, I looked it up on the portal.

**CALLER:** Good, that's the right habit. So — your password expired Sunday night and the reminder emails went to the mailbox you can't get into, which is why this is annoying.

**USER:** That would do it.

**CALLER:** Right. I'm not going to reset it for you over the phone, and I won't ever ask you for a code. What you'll do is go to the self-service reset page yourself — aka.ms/sspr — and it'll verify you through your existing Authenticator. I'll stay on if you get stuck, but I won't see anything you enter.

**USER:** Okay, I've got the page up.

**CALLER:** Take your time. And if any prompt shows up on your phone that you didn't just cause yourself, don't approve it — tell me and we'll stop.

**USER:** That's gone through. I'm back in.

**CALLER:** Great. I'll close the ticket. One thing worth knowing: nobody from this desk will ever ring you and ask you to approve a prompt or read out a code. If that ever happens, hang up and call us on the intranet number.

**USER:** Good to know. Thanks.

---

## Why this scores low

The caller **actively discourages** every behaviour the attacker relies on:

- Confirms the user dialled a known-good number rather than a supplied one
- Refuses to reset the credential themselves
- States explicitly they will never ask for a code
- Directs the user to the official self-service page
- Tells the user to refuse any prompt they did not initiate
- Closes by naming the attack pattern

The user initiated the call. The credential flow runs entirely through the user's own
verified channel. Nothing irreversible is requested. Under EntraGuard's model this is
`unaware`, low risk, no vectors — no action beyond telemetry.
