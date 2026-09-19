# EntraGuard as an Entra External Authentication Method

How EntraGuard becomes a second factor for **any** application in a tenant, rather than only
for the bundled Contoso Treasury demo.

Reviewed 20 September 2026. Implemented and off until configured; not yet exercised against
a live Conditional Access policy in a second tenant.

## Why this and not SAML

The obvious framing is "SAML SSO with EntraGuard as a second factor". Microsoft built a
different and better-fitting slot for it: an **External Authentication Method** (EAM), which
is OIDC.

| | SAML IdP proxy | External Authentication Method |
|---|---|---|
| Applications to change | every one | **none** |
| EntraGuard in the sign-in path | always | only when a policy asks for a second factor |
| If EntraGuard is down | nobody signs in | the policy fails; first factor untouched |
| Configuration | per application | one Conditional Access policy, tenant-wide |

A tenant adds the method and points one policy at it. Microsoft 365, the Azure portal,
gallery SaaS and the tenant's own applications all get the verification call, and none of
them know EntraGuard exists.

Requires **Entra ID P1 or P2** (P2 includes P1).

## The flow

```
User signs in to ANY app
   │  first factor (password) completes with Entra
   ▼
Entra: Conditional Access requires MFA; user picks EntraGuard
   │  POST  https://<host>/api/eam/authorize
   │        id_token_hint (signed by Microsoft, deliberately EXPIRED)
   │        nonce · state · client_id · redirect_uri · claims
   ▼
EntraGuard validates the hint — signature, issuer, audience
   │  reads oid + tid, rings that user, runs the normal verification
   ▼
POST back to Entra's published federation endpoint
       id_token { iss, aud, sub, nonce, exp, acr, amr:["tel"] }   → MFA satisfied
       error=access_denied                                        → sign-in refused
```

The hint arrives already expired. That is Microsoft's design — it stops the hint being
replayed as a credential anywhere else — so EntraGuard validates its signature, issuer and
audience but **not** its lifetime.

## What EntraGuard asserts, and what it will not

`amr: ["tel"]` — *confirmation by telephone*, which Entra maps to **possession**. EntraGuard
rings an endpoint enrolled to the directory account and the caller keys a number shown on a
screen only they can see.

**Not `vbm`** ("biometric with voiceprint"), though it describes the product almost too well.
Voice runs in `VOICE_MODE=observe`: it is scored and recorded, and it cannot refuse anybody.
The same enrolled speaker has scored 0.278 on one call and 0.7401 on another. Sending `vbm`
would tell Entra an inherence factor was verified and grant MFA on that basis, while nothing
about the voice can fail a sign-in. That moves to `vbm` when voice enforces, and not before.

One consequence worth stating plainly: a sign-in whose **first** factor was already
possession-based — a FIDO key, a passkey — makes Entra ask for inherence. A phone call
cannot supply it, so EntraGuard **declines before ringing anybody** rather than interrupting
someone for a verification whose result could never be accepted.

## Setup

```bash
./scripts/01-deploy-infra.sh        # creates the Key Vault
./scripts/08-external-auth-method.sh
```

The script creates the multitenant app registration and the RS256 signing certificate, then
prints the three values for `.env.deploy` (`EAM_KEYVAULT_URI`, `EAM_CLIENT_ID`,
`EAM_SIGNING_CERT`) and the steps a tenant admin takes. Re-runnable.

Per consuming tenant:

1. Admin-consent the application. Skipping this fails with `AADSTS900491: Service principal
   <app id> not found`.
2. Entra admin centre → Authentication methods → Add external method, with the client ID and
   the discovery URL.
3. Conditional Access → Grant → **Require multifactor authentication**. *Not* an
   authentication-strength grant — external methods do not satisfy authentication strengths,
   including the built-in MFA strength, and a policy configured that way can never be
   satisfied.

## Reach is not coverage

EAM makes EntraGuard available to every *application*. It still has to **phone the user**.

Teams delivery requires that tenant to allow-list this Communication Services resource
(`docs/teams-setup.md`). A tenant that consents to the EAM application but skips the ACS
federation gets an authentication method that cannot call anybody. This is the main adoption
constraint, and it is per-tenant.

## Timing

Entra abandons the sign-in about **five minutes** after redirecting. Measured across 23 real
calls in this deployment: median **108s**, worst **156s**.

EntraGuard stops at **240s** and returns `temporarily_unavailable`, so the user gets an
outcome Entra surfaces rather than a page whose sign-in was discarded at the other end.

## Rotating the signing certificate

The dangerous operation, so the rule is structural: **publish every enabled version, sign
with the oldest.**

1. Create a new certificate version. It is published in JWKS immediately and does **not**
   start signing.
2. Wait 48 hours for Entra's JWKS cache to turn over.
3. Disable the old version. That is what promotes the new one.

Doing step 3 early fails every sign-in behind the policy, in every tenant. The
newest-signs alternative is the obvious implementation and cannot be sequenced safely at all,
because a new version would sign against a cache that has never seen it.

## Proving it works before a tenant depends on it

```
GET /.well-known/openid-configuration     # issuer, endpoints
GET /.well-known/jwks                     # must contain x5c
GET /api/diagnostics/eam-selftest         # operator only
```

The self-test is the one that matters, because **reading the certificate and using it are
two different Key Vault roles over two different planes**. A vault granting Reader but not
Crypto User publishes a perfectly good JWKS and then fails every signature — so a healthy
JWKS is not evidence that anything can sign. The self-test signs a throwaway payload and
verifies it against the published certificate, closing the loop from outside.

It also reports `signingIsOldestPublished`, which is the invariant that makes rotation safe.

## Failure modes worth recognising

| Symptom | Cause |
|---|---|
| `AADSTS50161` failed to validate authorization url | The authorization endpoint is not a reply URL on the app registration |
| `AADSTS900491` service principal not found | The tenant has not admin-consented |
| `AADSTS50012` failed signature verification | Issuer mismatch, or a key Entra's cache has not seen — see rotation |
| Discovery returns 404 | `EAM_KEYVAULT_URI` is unset; the feature is off |
| The method never appears to users | Policy uses an authentication-strength grant instead of Require MFA |
| Sign-in fails, no call arrives | Teams federation not granted in that tenant |

The issuer must match **character for character** across the discovery document, the `iss` of
every token, and what the admin configured. An explicit `:443`, a trailing slash or a query
string each fail every sign-in with an error that names none of them.
