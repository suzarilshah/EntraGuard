# Enabling Teams calling for EntraGuard

> **Current-status note (20 September 2026):** the account, subscription, licence counts and live-call results below are historical setup evidence, not a current inventory. Treasury now targets the account selected by Microsoft sign-in (`oid`/`tid`), not a prefilled demo GUID. Confirm federation and a reachable signed-in Teams client for that user's tenant. See [deployment](deployment.md) and the [current runbook](demo-runbook.md).

EntraGuard's ACS resource lives in the **MCT** subscription; the Teams users live in the
**suzaril / example.com** tenant (`<TEAMS_TENANT_ID>`). Cross-tenant is
the supported design — the Teams tenant allowlists the ACS resource, and nothing moves.

Verified before writing this:

| Check | Result |
|---|---|
| Teams Phone (`MCOEV`) | Present in `DEVELOPERPACK_E5` |
| Spare licences | 8 of 25 |
| Your rights in that tenant | **Global Administrator** |
| SDK support | `CallInvite(MicrosoftTeamsUserIdentifier)` exists in Call Automation 1.6.0 |

---

## 1. Demo account — done

Created and verified against Microsoft Graph:

| Field | Value |
|---|---|
| UPN | `entraguard@example.onmicrosoft.com` |
| **Object ID** | `<TEAMS_DEMO_OBJECT_ID>` |
| Type | Member (not Guest — guests cannot be called as Teams users) |
| Enabled | yes |
| Licence | `DEVELOPERPACK_E5` + `FLOW_FREE` |
| `TEAMS1` | Success |
| `MCOEV` (Teams Phone) | Success |
| Usage location | MY |

The object ID is what a call targets, not the UPN. The historical test account is still useful
for explicitly configured test scripts. Treasury's current UI derives its target from the
signed-in user's identity, rather than the `TEAMS_OBJECT_ID` fallback in runtime config.

## 2. Enable ACS ↔ Teams federation

Three settings, all in the Teams tenant. Each one, when missing, produces a call that is
*accepted by ACS, dialled, and then killed at the far end within two seconds* — never an
error on the outbound call. The sub-code is the only thing that tells them apart:

| Sub-code | Missing | Fix |
|---|---|---|
| `403#10124` | ACS resource not allow-listed | `Set-CsTeamsAcsFederationConfiguration -EnableAcsUsers $true -AllowedAcsResources @(<immutable id>)` |
| `403#10391` | Teams user not Enterprise Voice enabled | `Set-CsPhoneNumberAssignment -Identity <upn> -EnterpriseVoiceEnabled $true` |
| — | `EnableAcsFederationAccess` off | `Set-CsExternalAccessPolicy -Identity Global -EnableAcsFederationAccess $true` |

Run it all with:

```bash
./scripts/05-teams-federation.sh
```

### Two things that are easy to get wrong

**`-AllowedAcsResources` wants the ACS resource's *immutable resource ID* — a bare GUID.**
Not the ARM path, not the resource name. The cmdlet rejects an ARM path with *"must be a
valid GUID"*. It is not shown on the portal's overview blade; read it from ARM:

```bash
az communication show -n <acs-name> -g <rg> --query immutableResourceId -o tsv
```

**A Teams Phone licence is not the same as Enterprise Voice being enabled.** The demo
account had `MCOEV` provisioned and `EnterpriseVoiceEnabled: False`, and the call was
refused. Microsoft's prerequisites say "Teams Phone license *and* Enterprise Voice enabled"
— they are two separate things.

Propagation is fast in practice: each fix changed the sub-code within minutes, not hours.

### Set a source display name on the invite

`CallInvite.SourceDisplayName` is set on the Teams leg — without it the invite arrives in
Teams as a call from an unnamed external party, which is exactly the shape of the attack
this system defends against. The first successful call had it set; whether it was also
*required* for delivery was not isolated from the settings above, so treat it as
recommended rather than proven-mandatory.

## Verified working

Live call to `user@example.com`, 2026-08-09:

```
AwaitingDigits | stream True | audio 1306 frames | dtmf 2
Ended | Passed — Number match confirmed. No coercion detected.
```

Teams rang, the call was answered, ACS opened the media WebSocket and streamed 1,306 audio
frames to the Analyst, both keypad digits arrived, and the adjudicator passed the match.

**This also settles the open question:** DTMF *does* reach ACS over Teams interop. No
speech-recognition fallback is needed, so the code stays out of the mouths of anyone
listening in the room.

Reproduce with:

```bash
./scripts/test-teams-call.sh
```

## 3. Sign in on your phone

Install Teams on the handset and sign in as `entraguard@example.onmicrosoft.com`. Nothing
else to install — Teams is the endpoint. No browser page, no QR code, no microphone prompt,
no presence heartbeat: Teams is a real app with real push notifications, so the call rings
even with the phone locked.

## 4. Run the demo

Sign in to Contoso Treasury with the account whose Teams client is reachable, choose
**Microsoft Teams**, confirm the displayed identity, and continue. If no endpoint is registered,
the call can fail (including 480). The app does not currently preflight Graph Teams presence.

---

## What this removes

Four of the six failures we hit are structurally impossible with Teams:

| Failure | Browser VoIP | Teams |
|---|---|---|
| Page must stay open | yes | no |
| Microphone permission | yes | already granted |
| Rings when locked | no | yes |
| Shared ACS identity race | yes | no |

## What was hard about this

Every failure in this integration looked identical from the outside: the call was placed,
nothing rang, and the flow reported a timeout. Three distinct causes — federation
allow-list, Enterprise Voice, and earlier the missing AI Services link — all presented as
"the call ended".

The fix was not any one setting. It was making the system *say which one*: logging
`ResultInformation` on every Call Automation event, and separating "ended before it was
answered" from "answered but nobody entered a code". Once the sub-code was visible, each
cause took minutes. Before that, they were indistinguishable from a bug.
