# EntraGuard admin console

## Scope

The operator console uses an Azure Portal-inspired resource shell: blue command header, resource navigation, breadcrumbs, command bars, Essentials, neutral cards and sortable detail lists. Light/dark presentation settings are scoped to `.admin-portal`; Treasury and the handbook retain their own design.

The frontend image is shared by the operator portal, Treasury and handbook. A frontend build does not itself deploy any container. The full `deploy-apps.sh` script updates more than the admin app; do not use it as an admin-only deployment shortcut.

## Views

| Route | Purpose | Data source |
|---|---|---|
| `/` | Full-window verification totals, latest outcomes, live-call activity and hosting-directory capability context | Deduplicated KQL, media service, Graph |
| `/verification` | Latest 100 verification records with reasons, assurance and row details | KQL; current in-flight records from media |
| `/live` | Existing live transcript, assessments and decisions; simulation tools are collapsed by default | Existing SignalR and simulation APIs |
| `/identity` | Directory users; conditional sign-in and risky-user views | Microsoft Graph v1.0 |
| `/incidents` | Latest 100 Sentinel incidents, with title-based EntraGuard or workspace scope | ARM Sentinel incidents API |
| `/voice-insights` | Voice-comparison distributions, follow-ups and biometric lifecycle | KQL |
| `/health` | Resource-group inventory, Container App images, ready revisions, ingress and replica settings | Azure Resource Graph |

`lib/adminNavigation.ts` is shared with the operator route guard so new blades do not bypass authentication. It does not change the existing backend role checks or Treasury/docs allowlists.

## Graph capabilities and boundaries

- Directory users: `User.Read.All` or the existing `Directory.Read.All` application permission.
- Subscription discovery: `LicenseAssignment.Read.All` or a supported broader read permission such as `Directory.Read.All`.
- Sign-in logs: `AuditLog.Read.All`, with the applicable P1/P2 tenant subscription.
- Risky users: appropriate IdentityRiskyUser permissions and Entra ID P2.
- These are hosting-tenant reads through the existing server credential, not an automatic cross-tenant directory browser.
- Subscription discovery is an availability signal, not proof of every user's entitlement or API access.
- No directory edits, risk dismissal, consent grants or Conditional Access policy mutations are offered here.

The directory tab is useful without premium licensing. If Graph reports no P1/P2 plan, the premium tabs explain the limitation instead of presenting a false zero. If subscription discovery fails, capability is unknown and an explicitly opened premium view can still attempt its own read.

## Correct interpretation of metrics

- Retry/outbox duplicates are collapsed with `arg_max(TimeGenerated, *) by VerificationId`.
- Summary counts cover the selected time window; record tables and Graph/Sentinel lists are explicitly bounded samples.
- A `Passed` challenge is not counted as a completed application grant or payment approval.
- Failed/timeout/unanswered calls are not labeled prevented attacks.
- `StepUpRequired` is displayed separately. Legacy voice refusals are not interpreted as proof of an impostor.
- Voice comparison absence is not evidence that no user has enrolled. A zero similarity is a valid score and is retained when the outcome was assessed.
- Known `(simulated)` verification labels are excluded by default. Legacy unlabelled test events cannot be identified reliably from this schema.
- Data-source errors suppress the dependent metric/table body; unavailable data is not rendered as zero or a clear queue.
- Resource provisioning success and `Running` metadata do not certify application or dependency health.

## Working interactions

- Ctrl/Cmd+K page search; keyboard navigation and Enter to open a match.
- Desktop rail collapse and mobile navigation drawer.
- Persisted display theme; real operator account identity and existing sign-out flow.
- Refresh, optional auto-refresh, URL time range and simulation scope.
- Table sorting, loaded-row filtering, CSV export and full row-detail dialogs.
- CSV formula prefixes are neutralized; external table links are restricted to approved Microsoft control-plane hosts.
- Azure/Entra/handbook links open their real owning services rather than duplicating unsupported management controls.

## Read-only deployment inspection

On 20 September 2026, the inspected `rg-entraguard-demo` estate contained five Container Apps. `ca-entraguard-portal` reported `Running` / `Succeeded`, ready revision `ca-entraguard-portal--0000101`, using the same `20260920054544` portal image tag as Treasury and the handbook. Media was limited to one replica; voiceprint ingress was internal; the handbook minimum was zero.

The managed identity had Directory.Read.All, AuditLog.Read.All and IdentityRiskyUser.ReadWrite.All among its Graph roles. The hosting tenant's returned SKUs did not advertise P1/P2. Some resources, including the separate realtime OpenAI account, lacked the application tag; the resource-group inventory now includes such objects instead of relying exclusively on tags.

These are inspection-time observations, not hardcoded UI data or a claim about future deployment state. No Azure resources, permissions, scaling settings or application images were changed by that inspection.

## Verification

In `src/portal`, run `npm run test:admin` (Node 22.6+ with TypeScript stripping support) and `npm run build`.

Contract checks cover time-range validation, KQL deduplication/full-window totals, route authorization coverage, OData escaping, error-versus-zero handling, CSV injection prevention and approved links. Browser checks use fixture data for layout and interaction coverage; they do not bypass authentication on the live portal. The summary KQL was additionally checked against the real workspace using a read-only query.
