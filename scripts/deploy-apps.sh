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
      "AOAI_REALTIME_ENDPOINT=${AOAI_REALTIME_ENDPOINT:-}" \
      "AOAI_REALTIME_DEPLOYMENT=${AOAI_REALTIME_DEPLOYMENT:-}" \
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
  --output none

printf "  ${GRN}✓${RST} https://%s\n" "$PORTAL_FQDN"

head2 "Deployed"
printf "  portal         ${BOLD}https://%s${RST}\n" "$PORTAL_FQDN"
printf "  media service  ${DIM}https://%s${RST}\n\n" "$MEDIA_SERVICE_FQDN"
printf "  ${BOLD}Next:${RST} ./scripts/04-eventgrid-subscribe.sh\n\n"
