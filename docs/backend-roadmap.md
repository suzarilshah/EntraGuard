# Backend enhancement roadmap

Recommendations from the 20 September 2026 source review. **Proposed, not implemented by the Treasury redesign.** The current UI must not expose controls that pretend these capabilities already exist.

## 1. Trusted relying-party session and authorization boundary — first

Validate the caller and tenant on every user-facing API. Derive subject IDs from validated claims instead of request bodies; enforce ownership for verification, knowledge, presence and history. Authenticate callbacks/media upgrades and scope SignalR subscriptions. Bind completed verification to a server-side relying-party session with expiration and replay protection.

**UI benefit:** a real “Session security” center, reliable return navigation and genuine tenant-isolated settings. Test cross-user/cross-tenant denials and replay rejection, not just happy-path sign-in.

## 2. Durable verification ledger and reliable delivery

Persist verification lifecycle, explicit assessed/not-assessed state and result evidence. Add an owner-scoped, paginated history API. Make completion and telemetry publication idempotent, with retry/outbox handling. Replace replica-local call routing assumptions and introduce shared SignalR fan-out before scaling out.

**UI benefit:** searchable audit history, receipt downloads, accurate timelines and refresh/restart continuity. Current settings history reads recent in-memory attempts only.

## 3. Versioned assurance and tenant policy

Record the exact question sources and outcomes instead of using a broad `telemetry` label. Reconcile readiness with profile candidates and unify voice refusal versus additional-authentication behavior. Add server-enforced minimum assurance, freshness, allowed channels and escalation policy, versioned and auditable.

**UI benefit:** an actionable readiness checklist, precise explanations, and administrator-managed policies rather than cosmetic toggles. Do not promote the current informational scale to an access rule until its provenance is correct.

## 4. Transaction-bound step-up

When a real payment backend is introduced, bind verification to immutable transaction details: amount, currency, beneficiary, requested action and expiration. Re-check those details and authorization server-side before execution. Add idempotency keys and dual approval where required by the business.

**UI benefit:** a genuine payment-review drawer and approval workflow. Current CSV/detail controls operate on samples; there is no transfer API.

## 5. Settings capabilities with persistence

After authorization is in place, consider:

- Owner-managed endpoint preferences and registered-device lifecycle.
- Authenticated readiness summaries without exposing expected answers.
- Notification preferences and delivery-status tracking.
- Consent history, profile-key rotation and data deletion lifecycle.
- Tenant-admin policy settings with audit trails and role checks.

Prefer explicit save results, last-updated times, validation and rollback over auto-saving security policy. A setting needs an API contract, authorization and persistence before it needs a switch in the portal.

## 6. Detection quality and operations

Measure false accept/reject rates and response latency on real calling channels, accents and noisy environments. Add replay/liveness and PAD evaluation as separate signals. Correlate repeated failed/coerced verifications in Sentinel; add dependency health, latency budgets and operational alerts. Manage biometric encryption keys and reduce/remove readable stored answers.

**UI benefit:** evidence-backed trust indicators, meaningful incident reporting and tenant capability status. A green availability badge must come from measured service state, not configuration alone.

## Recommended delivery order

Authorization/session binding → durable history → source-aware assurance/readiness → tenant policy → transaction approval → optional device/preferences features. Keep the current sign-in/call contracts stable during incremental migration, with compatibility tests and live-call rehearsals before rollout.
