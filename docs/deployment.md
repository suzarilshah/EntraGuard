# Deployment and resource inventory

**Session/persistence upgrade:** follow [security-migration.md](security-migration.md) before running deployment scripts. The new contract requires API-scope consent, appropriate roles, callback/webhook secrets, exact frontend public origins, and coordinated media/portal rollout. `EntraGuardState` is now the durable application store; media is limited to one replica until live-call routing is distributed.

Reviewed 20 September 2026 against Bicep, scripts and local deployment metadata. Names below identify the recorded demo configuration; verify the target subscription's actual resources before deployment. No cloud changes are implied by this document.

## Main estate

| Resource | Recorded / template-derived name |
|---|---|
| Resource group / main region | `rg-entraguard-demo` / `eastus` |
| Container Apps environment | `cae-entraguard-demo` |
| Media / portal / Treasury / handbook / voiceprint | `ca-entraguard-media`, `ca-entraguard-portal`, `ca-contoso-treasury`, `ca-entraguard-docs`, `ca-entraguard-voiceprint` |
| Container registry | `crentraguard<suffix>.azurecr.io` |
| ACS | `acs-entraguard-<suffix>` |
| Event Grid system topic | `egst-entraguard-demo` |
| Speech | `spch-entraguard-<suffix>` |
| ACS cognitive endpoint account | `ai-entraguard-<suffix>` |
| Azure OpenAI / analyst deployment | `aoai-entraguard-<suffix>` / `entraguard-analyst` |
| Optional realtime account / deployment, referenced by configuration | `aoai-entraguard-rt-<suffix>` / `entraguard-voice` |
| Storage | `stentraguard<suffix>` |
| Log Analytics / Sentinel workspace | `log-entraguard-demo` |
| Data Collection Endpoint / Rule | `dce-entraguard-<suffix>` / `dcr-entraguard-demo` |
| Application Insights | `appi-entraguard-demo` |
| User-assigned managed identity | `id-entraguard-demo` |
| Key Vault (external authentication method signing certificate) | `kv-entraguard-<suffix>` |
| Entra portal registration / quarantine group | `EntraGuard-Portal` / `EntraGuard-Quarantine` |

The six-character suffix is derived from subscription and resource-group identity. A new subscription need not produce these same globally unique names.


### Public hostnames

Bound with free Azure managed certificates by `scripts/09-custom-domains.sh`. Records must be
**DNS-only** — a proxied record cannot pass certificate validation, and cannot renew one.

| Hostname | Container app |
|---|---|
| `placeholders.my` | `ca-contoso-treasury` |
| `entraguard.my` | `ca-entraguard-portal` |
| `docs.entraguard.my` | `ca-entraguard-docs` |
| `api.entraguard.my` | `ca-entraguard-media` |

`api.entraguard.my` is the only one that is more than cosmetic: `PUBLIC_BASE_URL` is derived
from it, and with it the ACS callback URLs, the media WebSocket URL and the external
authentication method's issuer.

## Provisioning coverage

`infra/main.bicep` composes identity, observability, storage, communication, AI and compute modules at subscription scope. All four apps keep a minimum replica. Voiceprint ingress is internal; the other three are external. Service-to-Azure calls primarily use the shared managed identity.

Not fully expressed by the Bicep deployment:

- Incoming-call Event Grid subscription: wired after a reachable media hostname exists.
- Entra app registrations, consent and Graph roles: separate identity setup.
- Teams federation: setup in the Teams tenant.
- A working multitenant relying-party application and federated service registration: runtime code expects these, but the basic portal setup script does not create the whole arrangement.
- Optional realtime OpenAI account/model: referenced in deployment configuration, not provisioned by the current AI module.
- Conditional Access policy targeting the quarantine group: administrator setup required.
- Extra Graph consent for newer mail/calendar/chat/file question sources: not all covered by the original six-role script.

## Scripts and scope

| Script | Purpose |
|---|---|
| `00-preflight.sh` | Resolves subscription/model capabilities and writes `.env.deploy` |
| `01-deploy-infra.sh` | Applies Bicep, passes existing media/portal/voiceprint images and runs a what-if guard |
| `02-entra-apps.sh` | Portal registration, managed-identity Graph roles, quarantine group |
| `deploy-apps.sh` | ACR builds and runtime updates for media, portal and Treasury; voiceprint rebuilt only when requested |
| `04-eventgrid-subscribe.sh` | Incoming-call event wiring |
| `05-teams-federation.sh` | Teams federation setup |
| `06-attribute-roles.sh` | Custom security attribute setup/roles |
| `07-voice-calibration.sh` | Synthetic-speaker calibration; not a production-accuracy test |
| `smoke-test.sh` | Deployment smoke checks |

`deploy-apps.sh` is a **whole-application deployment**, not a frontend-only or Treasury-only command. The same portal image is used on two Container Apps; `APP_MODE=treasury` selects the relying party. Deploying a Treasury UI change does not require running the infrastructure scripts, and should target only the intended app after verifying the image.

The image-preservation parameters removed in an earlier revision are restored. The historical incident plan in `docs/plans/` is not current deployment guidance. The first existence probe in `read_image` still treats any failed read as absence; the guard is not a substitute for reviewing the actual deployment diff and cloud permissions.

## Configuration sources

- [`.env.example`](../.env.example): documented variables, no real values or generated keys.
- `.env.deploy`: local script output; may contain sensitive key material. Do not commit it or source an unreviewed copy.
- Container App environment: actual deployed runtime values.
- `EntraGuardOptions` and `Program.cs`: backend defaults and bindings.

Important groups:

| Variables | Responsibility |
|---|---|
| `PUBLIC_BASE_URL`, `ACS_ENDPOINT`, `AI_SERVICES_ENDPOINT` | Call control, public callbacks/WSS and spoken ACS prompts |
| `SPEECH_ENDPOINT`, `SPEECH_REGION`, `SPEECH_RESOURCE_ID`, `SPEECH_LANGUAGE` | Streaming recognition |
| `AOAI_ENDPOINT`, `AOAI_DEPLOYMENT`, `AOAI_API_VERSION` | Analyst model/API |
| `MEDIA_SERVICE_URL` | Portal/Treasury → media backend |
| `ENTRA_RP_CLIENT_ID`, `ENTRA_RP_SCOPE`, `AZURE_TENANT_ID` | Relying-party identity and voice-profile API |
| `ENTRA_SERVICE_CLIENT_ID` | Federated cross-tenant Graph access |
| `VOICEPRINT_URL`, `VOICEPRINT_KEY`, `VOICE_MODE`, `VOICE_REQUIRE_MFA` | Voice scoring, encryption, enforcement and enrollment |
| `LAW_RESOURCE_ID`, `LAW_WORKSPACE_ID`, `DCE_ENDPOINT`, `DCR_IMMUTABLE_ID` | Log reads and ingestion |
| `ALLOWED_ORIGINS` | Explicit backend CORS origins; does not replace authorization |

`VOICE_AGENT` is interpreted by the deploy script, not directly by the backend. Empty realtime endpoint/deployment disables the agent. `VOICE_MODE=observe` is the deployed default in the scripts; do not enable enforcement based solely on synthetic calibration.

## Verification after a deployment

1. Confirm the intended images/revisions and runtime flags on each target.
2. Check media liveness/readiness. Readiness currently checks configuration presence, not all downstream dependencies.
3. Verify Treasury sign-in with an allowed redirect URI and account.
4. Rehearse an actual Teams or ACS call: prompt, digits, questions, verdict and media evidence.
5. Confirm new telemetry fields arrive populated, not merely that ingestion returned success.
6. Test recent activity, voice enrollment/deletion and failure handling using appropriate test accounts.

A frontend build or browser test with stubbed APIs does not validate Entra consent, model access, Teams reachability or live telephony.
