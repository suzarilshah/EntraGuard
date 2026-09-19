#!/usr/bin/env bash
# Subscribe the media service to ACS IncomingCall events.
#
# Deliberately NOT in Bicep: the subscription needs the Container App FQDN, which does not
# exist until compute is deployed, and Event Grid validates the endpoint at creation time —
# so the app must already be serving before this can succeed.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GRN=$'\033[32m'; RED=$'\033[31m'; CYN=$'\033[36m'; RST=$'\033[0m'

[[ -f "$ENV_DEPLOY" ]] || { echo "Run ./scripts/01-deploy-infra.sh first." >&2; exit 1; }
# shellcheck disable=SC1090
set -a; source "$ENV_DEPLOY"; set +a

RG="${AZURE_RESOURCE_GROUP:-rg-entraguard-demo}"
SYSTEM_TOPIC="egst-entraguard-demo"
SUBSCRIPTION_NAME="entraguard-incoming-call"
WEBHOOK_KEY="${EVENTGRID_WEBHOOK_KEY:-}"
[[ ${#WEBHOOK_KEY} -ge 32 ]] || { printf 'EVENTGRID_WEBHOOK_KEY must be configured before subscribing.\n' >&2; exit 1; }
WEBHOOK_CODE=$(python3 -c 'import os,urllib.parse; print(urllib.parse.quote(os.environ["EVENTGRID_WEBHOOK_KEY"], safe=""))')
# Follows the custom domain once it is bound, so the webhook and PUBLIC_BASE_URL name the
# same host. Falls back to the Container Apps FQDN, which is what a deployment without
# custom domains has.
MEDIA_HOST="$MEDIA_SERVICE_FQDN"
if [[ -n "${MEDIA_DOMAIN:-}" ]] && az containerapp hostname list \
     -n ca-entraguard-media -g "${AZURE_RESOURCE_GROUP}" \
     --query "[?name=='${MEDIA_DOMAIN}' && bindingType=='SniEnabled']" -o tsv 2>/dev/null | grep -q .; then
  MEDIA_HOST="$MEDIA_DOMAIN"
fi

ENDPOINT="https://${MEDIA_HOST}/api/events/incoming-call?code=${WEBHOOK_CODE}"

printf "\n${BOLD}${CYN}Wiring IncomingCall events${RST}\n"
printf "  ${DIM}endpoint  https://%s/api/events/incoming-call (authenticated)${RST}\n\n" "$MEDIA_HOST"

# Event Grid performs the validation handshake against this endpoint during creation.
# If the app is not serving yet, creation fails with a validation error — check readiness
# first so the failure names the real cause.
printf "  Checking the media service is serving… "
if curl -fsS --max-time 10 "https://${MEDIA_HOST}/health/live" >/dev/null 2>&1; then
  printf "${GRN}ok${RST}\n"
else
  printf "${RED}unreachable${RST}\n"
  echo "  The endpoint must answer before Event Grid will validate it." >&2
  echo "  Check:  az containerapp logs show -n ${MEDIA_SERVICE_NAME} -g ${RG} --tail 50" >&2
  exit 1
fi

if az eventgrid system-topic event-subscription show \
     --name "$SUBSCRIPTION_NAME" \
     --system-topic-name "$SYSTEM_TOPIC" \
     --resource-group "$RG" >/dev/null 2>&1; then
  printf "  ${DIM}Subscription exists — recreating so it points at the current FQDN.${RST}\n"
  az eventgrid system-topic event-subscription delete \
    --name "$SUBSCRIPTION_NAME" \
    --system-topic-name "$SYSTEM_TOPIC" \
    --resource-group "$RG" --yes --output none
fi

# Retry settings follow Microsoft's Call Automation guidance: a call only rings for ~30
# seconds, so retrying a stale event past that just answers calls nobody is on any more.
az eventgrid system-topic event-subscription create \
  --name "$SUBSCRIPTION_NAME" \
  --system-topic-name "$SYSTEM_TOPIC" \
  --resource-group "$RG" \
  --endpoint "$ENDPOINT" \
  --endpoint-type webhook \
  --included-event-types Microsoft.Communication.IncomingCall \
  --max-delivery-attempts 2 \
  --event-ttl 1 \
  --output none

printf "\n  ${GRN}✓${RST} Subscribed. Calls to this ACS resource are now intercepted.\n\n"
printf "  ${BOLD}Verify:${RST}\n"
printf "    az containerapp logs show -n %s -g %s --follow\n\n" "$MEDIA_SERVICE_NAME" "$RG"
