# Treasury security and persistence migration

September 2026 implementation. This changes the authentication contract: deploy media and both frontend instances together in a call-free maintenance window. This document is not a report of an Azure deployment or a live-call test.

## Delivered stages

| Priority | Implementation |
|---|---|
| 1 | Validated Entra API tokens establish revocable server sessions; owner APIs derive tenant/object identity from claims. HttpOnly cookies, same-origin mutations, operator-only diagnostics/SignalR, signed ACS callback/media capabilities |
| 2 | Conditional Table transactions persist receipts, reverse-time history and telemetry work. Workspace grants consume receipts for the requesting session. Interrupted calls fail by deadline |
| 3 | Actual asked/correct question provenance, versioned tenant policy, full-source readiness, and server-validated fresh-MFA recovery for `StepUpRequired` |
| 4 | Approver role, exact payment digest, current session/policy, one-time receipt consumption and idempotent conditional approval of demo payments |
| 5 | Versioned channel/notification preferences, registered-device revocation, and a durable in-app inbox with read state |

## Entra registration and roles

Retain `ENTRA_RP_CLIENT_ID` and its existing delegated scope **`VoiceProfile.Manage`**. The legacy scope name now authenticates the broader owner API as well as profile management. Treasury requests it during sign-in. Graph tokens and ID tokens are not accepted as bearer credentials.

Allow the intended `/app` redirect URI on both Treasury and operator hostnames, plus explicitly approved local development origins. Define and assign user/group app roles on the API registration:

- `EntraGuard.Operator`: operator console, diagnostics, simulations and SignalR, restricted to the configured home tenant.
- `EntraGuard.TenantAdmin`: edit policy in the caller's own tenant.
- `EntraGuard.PaymentApprover`: request and approve transaction-bound **demo** payments.

Role assignment does not replace delegated-scope consent. Obtain a new token/session after changing roles. `ENTRAGUARD_OPERATOR_IDS` is an optional immutable home-tenant object-ID allowlist for operators; it does not confer payment approval rights.

Configure ID tokens with **`amr` and `auth_time`** for step-up. The server validates signature, audience, exact tenant issuer and same subject, then requires `mfa` and authentication no earlier than the verification start. A browser receiving any token is not sufficient. Coercion and wrong-answer refusals cannot be overridden by this recovery path.

## Runtime configuration

- `STORAGE_ACCOUNT_NAME`: managed identity needs Table Data Contributor access.
- `CALLBACK_SIGNING_KEY`: random base64-encoded key material, at least 32 bytes.
- `EVENTGRID_WEBHOOK_KEY`: separate random secret, at least 32 characters.
- `APP_PUBLIC_ORIGIN`: exact external origin on each frontend, including HTTPS behind ingress. This anchors CSRF checks.
- `ALLOWED_ORIGINS`: explicit frontend origins for direct browser SignalR access.
- `TREASURY_DEMO_LEDGER=true`: enables server-managed sample records. False disables them; no banking connector is implied.
- Existing ACS, Speech, model, ingestion, Entra and voiceprint settings remain required.

The deploy script checks the identity/callback settings before building, uses Container App secret references for callback keys, sets the public frontend origins and pins media to one replica. Re-run Event Grid wiring after the service is reachable: old unsigned webhooks intentionally fail. Keep keys stable during normal deployments; rotating them invalidates outstanding callback capabilities. Never commit populated environment files.

## Storage and compatibility

`EntraGuardState` is declared in Bicep and initialized lazily by the application. Tenant GUIDs are partition keys; owner rows include immutable object IDs. It contains sessions, devices/presence, receipts and history, outbox/recovery work, policy audit, preferences, notifications, demo payments and approval records.

The new receipt schema omits match codes, viewer tokens, expected answers and transcripts. Existing knowledge and voice stores remain; readable registered answers are a separate remaining hardening item.

Old in-memory attempts are not imported or granted. Users must sign in again and reconnect ACS browser/phone endpoints. Legacy UPN-based identity mappings are not adopted as ownership proof. No existing knowledge or voice-profile table is deleted.

Expired sessions remain non-authorizing even while their rows exist. Durable-history/session/inbox retention and cleanup operations still need a production policy; this migration does not silently delete audit data.

## Authorization contracts

- Sessions expire by the source access-token expiry and no later than one hour after creation.
- Default policy preserves the former baseline: minimum **Low**, freshness **10 minutes**, Teams/browser/phone allowed, Analyst assessment not mandatory. Administrators can strengthen these settings.
- Workspace grants require a persisted successful receipt for the exact owner/session, current policy, sufficient assurance, freshness and no outstanding step-up. Consumption and grant update commit atomically.
- Policy changes invalidate older policy-version grants on protected operations.
- Demo approval requires a payment-approver session and a fresh receipt bound to payment ID, version, amount, currency, beneficiary, action and policy. Payment update, receipt consumption and approval record share a conditional transaction that also checks policy/session versions.
- Approval changes only the demo status to `Approved`; bank execution, settlement and dual-control approval are not implemented.

## Delivery, recovery and scale

The telemetry outbox retries every 15 seconds. Delivery is **at least once**: a crash after Azure accepts a row and before acknowledgment can duplicate it. Deduplicate analytical queries by verification/event identity; the durable receipt is the authorization authority.

Unfinished attempts fail after a 15-minute deadline. Calls are not resumed. Sockets and coordinators are process-local, so media's supported replica count is **one** until a distributed call router and SignalR backplane are implemented. Deploy only with no calls in flight.

Device revocation invalidates its web session and registration, then revokes ACS tokens. A downstream revocation error is reported rather than labeled complete. New Microsoft authentication may register again; this is not global revocation of every Entra credential.

Notifications are delivered to the user's **in-app inbox**. Email/SMS/push delivery is not configured or claimed.

## Rollout validation

1. Run backend tests and the frontend production build.
2. Validate the Table schema/access and session setup in a non-production environment.
3. Exercise wrong-user reads, logout, expiry, missing operator/approver roles and policy-edit denial.
4. Reconnect endpoints and rehearse real Teams/ACS calls with authenticated callbacks.
5. Complete a call, reload Treasury and confirm durable history plus grant expiry.
6. Rehearse actual MFA recovery with `amr`/`auth_time` claims.
7. Change payment details after requesting verification: approval must fail. Retry a successful identical approval: no second commit.
8. Verify preference saving, device revocation, inbox read state and outbox delivery.

`smoke-test.sh` requires an ephemeral `ENTRAGUARD_OPERATOR_ACCESS_TOKEN` for authenticated configuration checks. Local HTTP tests use signed test tokens and a test store; they do not validate live Azure/Entra consent, storage, ACS or model connectivity.

The checkout's optional [External Authentication Method](external-auth-method.md) has separate signing/Key Vault/client setup. It is not enabled or live-validated merely by completing this Treasury migration.
