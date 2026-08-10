#!/usr/bin/env bash
# Build and deploy both container apps, then wire the runtime configuration.
#
# Uses ACR remote build so this works from any machine without a local Docker daemon —
# which matters on a hackathon laptop and on conference wifi.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GRN=$'\033[32m'; CYN=$'\033[36m'; RST=$'\033[0m'
head2(){ printf "\n${BOLD}${CYN}%s${RST}\n" "$*"; }

[[ -f "$ENV_DEPLOY" ]] || { echo "Run ./scripts/01-deploy-infra.sh first." >&2; exit 1; }
# shellcheck disable=SC1090
set -a; source "$ENV_DEPLOY"; set +a

TAG="$(date +%Y%m%d%H%M%S)"
RG="${AZURE_RESOURCE_GROUP:-rg-entraguard-demo}"

# ── Media service ───────────────────────────────────────────────────────────
head2 "1. Building media service"

az acr build \
  --registry "$ACR_NAME" \
  --image "entraguard-media:${TAG}" \
  --file src/EntraGuard.MediaService/Dockerfile \
  "$REPO_ROOT" \
  --output none

printf "  ${GRN}✓${RST} entraguard-media:%s\n" "$TAG"

head2 "2. Deploying media service"

# The Speech SDK needs the account resource ID to build its aad# authorization token,
# and PUBLIC_BASE_URL must be the app's own external FQDN — ACS dials it from outside.
SPEECH_RESOURCE_ID=$(az cognitiveservices account list -g "$RG" \
  --query "[?kind=='SpeechServices'] | [0].id" -o tsv)

# The conversational agent is off unless VOICE_AGENT=on. It is opt-in because a model with a
# live microphone on an authentication call produced four separate failures in one day, and
# the scripted path is what has actually passed end to end.
#
# These are resolved into variables FIRST, deliberately. They were previously inline in the
# az argument list with these comments between the arguments — and a `#` comment inside a
# backslash-continued command ends the command there. The update then ran without half its
# environment, bash tried to execute the next argument as a command, and `set -e` killed the
# script before the portal and treasury stages. Both sat on a stale image for a day while
# every deploy reported success.
if [ "${VOICE_AGENT:-off}" = "on" ]; then
  REALTIME_ENDPOINT="${AOAI_REALTIME_ENDPOINT:-}"
  REALTIME_DEPLOYMENT="${AOAI_REALTIME_DEPLOYMENT:-}"
else
  REALTIME_ENDPOINT=""
  REALTIME_DEPLOYMENT=""
fi

az containerapp update \
  --name "$MEDIA_SERVICE_NAME" \
  --resource-group "$RG" \
  --image "${ACR_LOGIN_SERVER}/entraguard-media:${TAG}" \
  --set-env-vars \
      "PUBLIC_BASE_URL=https://${MEDIA_SERVICE_FQDN}" \
      "SPEECH_RESOURCE_ID=${SPEECH_RESOURCE_ID}" \
      "ENTRAGUARD_RISK_TIER=${ENTRAGUARD_RISK_TIER:-degraded}" \
      "ENTRA_QUARANTINE_GROUP_ID=${ENTRA_QUARANTINE_GROUP_ID:-}" \
      "AZURE_SUBSCRIPTION_ID=${AZURE_SUBSCRIPTION_ID}" \
      "AOAI_REALTIME_ENDPOINT=${REALTIME_ENDPOINT}" \
      "AOAI_REALTIME_DEPLOYMENT=${REALTIME_DEPLOYMENT}" \
      "ENTRA_SERVICE_CLIENT_ID=${ENTRA_SERVICE_CLIENT_ID:-}" \
      "AZURE_TENANT_ID=${AZURE_TENANT_ID}" \
      "VOICEPRINT_URL=${VOICEPRINT_URL:-}" \
      "VOICE_MODE=${VOICE_MODE:-observe}" \
      "VOICE_ACCEPT=${VOICE_ACCEPT:-}" \
      "VOICE_REJECT=${VOICE_REJECT:-}" \
      "VOICEPRINT_KEY=${VOICEPRINT_KEY:-}" \
      "ENTRA_RP_CLIENT_ID=${ENTRA_RP_CLIENT_ID:-}" \
  --output none

printf "  ${GRN}✓${RST} https://%s\n" "$MEDIA_SERVICE_FQDN"

# ── Portal ──────────────────────────────────────────────────────────────────
head2 "3. Building portal"

az acr build \
  --registry "$ACR_NAME" \
  --image "entraguard-portal:${TAG}" \
  --file src/portal/Dockerfile \
  "${REPO_ROOT}/src/portal" \
  --output none

printf "  ${GRN}✓${RST} entraguard-portal:%s\n" "$TAG"

head2 "4. Deploying portal"

az containerapp update \
  --name "$PORTAL_NAME" \
  --resource-group "$RG" \
  --image "${ACR_LOGIN_SERVER}/entraguard-portal:${TAG}" \
  --set-env-vars \
      "MEDIA_SERVICE_URL=https://${MEDIA_SERVICE_FQDN}" \
      "LAW_RESOURCE_ID=${LAW_RESOURCE_ID}" \
      "LAW_WORKSPACE_ID=${LAW_WORKSPACE_ID}" \
      "AZURE_SUBSCRIPTION_ID=${AZURE_SUBSCRIPTION_ID}" \
      "ENTRA_PORTAL_CLIENT_ID=${ENTRA_PORTAL_CLIENT_ID:-}" \
      "AZURE_TENANT_ID=${AZURE_TENANT_ID}" \
      "TEAMS_OBJECT_ID=${TEAMS_OBJECT_ID:-}" \
      "TEAMS_UPN=${TEAMS_UPN:-}" \
      "ENTRA_RP_CLIENT_ID=${ENTRA_RP_CLIENT_ID:-}" \
      "ENTRA_RP_SCOPE=${ENTRA_RP_SCOPE:-}" \
  --output none

printf "  ${GRN}✓${RST} https://%s\n" "$PORTAL_FQDN"

# ── Contoso Treasury ────────────────────────────────────────────────────────
#
# The same image on a second hostname. APP_MODE decides which product it is, and
# middleware.ts makes the admin console unreachable there — the relying-party boundary is
# the premise of a step-up factor, so it has to be a real boundary and not a nav link.
head2 "5. Deploying Contoso Treasury"

az containerapp update \
  --name "$TREASURY_NAME" \
  --resource-group "$RG" \
  --image "${ACR_LOGIN_SERVER}/entraguard-portal:${TAG}" \
  --set-env-vars \
      "APP_MODE=treasury" \
      "MEDIA_SERVICE_URL=https://${MEDIA_SERVICE_FQDN}" \
      "ENTRA_RP_CLIENT_ID=${ENTRA_RP_CLIENT_ID:-}" \
      "TEAMS_OBJECT_ID=${TEAMS_OBJECT_ID:-}" \
      "TEAMS_UPN=${TEAMS_UPN:-}" \
      "AZURE_TENANT_ID=${AZURE_TENANT_ID}" \
      "ENTRA_RP_SCOPE=${ENTRA_RP_SCOPE:-}" \
  --output none

printf "  ${GRN}✓${RST} https://%s\n" "$TREASURY_FQDN"

head2 "Deployed"
printf "  portal         ${BOLD}https://%s${RST}\n" "$PORTAL_FQDN"
printf "  treasury app   ${BOLD}https://%s${RST}\n" "$TREASURY_FQDN"
printf "  media service  ${DIM}https://%s${RST}\n\n" "$MEDIA_SERVICE_FQDN"
printf "  ${BOLD}Next:${RST} ./scripts/04-eventgrid-subscribe.sh\n\n"
