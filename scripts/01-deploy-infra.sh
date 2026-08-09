#!/usr/bin/env bash
# Deploy the EntraGuard infrastructure.
#
# Reads .env.deploy so the Azure OpenAI model and remediation tier come from what
# scripts/00-preflight.sh actually found, not from a guess baked into the template.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GRN=$'\033[32m'; CYN=$'\033[36m'; RST=$'\033[0m'

if [[ ! -f "$ENV_DEPLOY" ]]; then
  echo "  .env.deploy not found. Run ./scripts/00-preflight.sh first." >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a; source "$ENV_DEPLOY"; set +a

LOCATION="${AZURE_LOCATION:-eastus}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-rg-entraguard-demo}"
DEPLOYMENT_NAME="entraguard-$(date +%Y%m%d-%H%M%S)"

if [[ -z "${AOAI_MODEL:-}" ]]; then
  echo "  AOAI_MODEL is empty in .env.deploy — preflight found no usable model." >&2
  echo "  Re-run preflight against another region, or request Azure OpenAI access." >&2
  exit 1
fi

# Granting the operator direct Log Analytics read access makes the KQL demo runnable
# from the portal by hand, which is worth having when a live demo needs a fallback.
OPERATOR_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || echo '')"

# Preserve whatever image the container apps are running.
#
# compute.bicep defaults both images to the Microsoft quickstart placeholder, because on a
# first deploy the real images do not exist yet. On a REdeploy that default silently
# reverts both running applications to the placeholder — the media service stops answering
# calls and the portal stops being EntraGuard, with a successful-looking deployment.
# Read the current images back and pass them in so infrastructure changes never take the
# apps down.
CURRENT_MEDIA_IMAGE=$(az containerapp show -n "ca-entraguard-media" -g "$RESOURCE_GROUP" \
  --query "properties.template.containers[0].image" -o tsv 2>/dev/null || echo "")
CURRENT_PORTAL_IMAGE=$(az containerapp show -n "ca-entraguard-portal" -g "$RESOURCE_GROUP" \
  --query "properties.template.containers[0].image" -o tsv 2>/dev/null || echo "")

IMAGE_PARAMS=()
if [[ -n "$CURRENT_MEDIA_IMAGE" && "$CURRENT_MEDIA_IMAGE" != *"k8se/quickstart"* ]]; then
  IMAGE_PARAMS+=("mediaServiceImage=$CURRENT_MEDIA_IMAGE")
  printf "  ${DIM}preserving media image  %s${RST}\n" "$CURRENT_MEDIA_IMAGE"
fi
if [[ -n "$CURRENT_PORTAL_IMAGE" && "$CURRENT_PORTAL_IMAGE" != *"k8se/quickstart"* ]]; then
  IMAGE_PARAMS+=("portalImage=$CURRENT_PORTAL_IMAGE")
  printf "  ${DIM}preserving portal image %s${RST}\n" "$CURRENT_PORTAL_IMAGE"
fi

printf "\n${BOLD}${CYN}Deploying EntraGuard infrastructure${RST}\n"
printf "  ${DIM}subscription  %s${RST}\n" "$AZURE_SUBSCRIPTION_ID"
printf "  ${DIM}resource grp  %s${RST}\n" "$RESOURCE_GROUP"
printf "  ${DIM}location      %s${RST}\n" "$LOCATION"
printf "  ${DIM}model         %s (%s, %s)${RST}\n" "$AOAI_MODEL" "$AOAI_MODEL_VERSION" "$AOAI_SKU"
printf "  ${DIM}risk tier     %s${RST}\n\n" "${ENTRAGUARD_RISK_TIER:-degraded}"

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "${REPO_ROOT}/infra/main.bicep" \
  --parameters \
      appName=entraguard \
      environmentName=demo \
      location="$LOCATION" \
      openAiModelName="$AOAI_MODEL" \
      openAiModelVersion="$AOAI_MODEL_VERSION" \
      openAiSkuName="$AOAI_SKU" \
      operatorObjectId="$OPERATOR_OBJECT_ID" \
      ${IMAGE_PARAMS[@]+"${IMAGE_PARAMS[@]}"} \
  --output none

printf "${GRN}  Infrastructure deployed.${RST}\n\n"

# Fold the deployment outputs into .env.deploy so downstream scripts and local runs
# share one source of truth.
OUTPUTS=$(az deployment sub show --name "$DEPLOYMENT_NAME" --query properties.outputs -o json)

read_output() { echo "$OUTPUTS" | jq -r ".${1}.value // empty"; }

{
  echo ""
  echo "# ── Written by scripts/01-deploy-infra.sh ──"
  echo "ACS_NAME=$(read_output acsName)"
  echo "ACS_ENDPOINT=$(read_output acsEndpoint)"
  echo "SPEECH_ENDPOINT=$(read_output speechEndpoint)"
  echo "AI_SERVICES_ENDPOINT=$(read_output aiServicesEndpoint)"
  echo "AOAI_ENDPOINT=$(read_output openAiEndpoint)"
  echo "AOAI_DEPLOYMENT=$(read_output openAiDeploymentName)"
  echo "LAW_RESOURCE_ID=$(read_output workspaceResourceId)"
  echo "LAW_WORKSPACE_ID=$(read_output workspaceCustomerId)"
  echo "DCE_ENDPOINT=$(read_output dceLogsIngestionEndpoint)"
  echo "DCR_IMMUTABLE_ID=$(read_output dcrImmutableId)"
  echo "STORAGE_ACCOUNT_NAME=$(read_output storageAccountName)"
  echo "MANAGED_IDENTITY_CLIENT_ID=$(read_output managedIdentityClientId)"
  echo "MANAGED_IDENTITY_PRINCIPAL_ID=$(read_output managedIdentityPrincipalId)"
  echo "MANAGED_IDENTITY_NAME=$(read_output managedIdentityName)"
  echo "MEDIA_SERVICE_NAME=$(read_output mediaServiceName)"
  echo "MEDIA_SERVICE_FQDN=$(read_output mediaServiceFqdn)"
  echo "PORTAL_NAME=$(read_output portalName)"
  echo "PORTAL_FQDN=$(read_output portalFqdn)"
  echo "ACR_NAME=$(read_output containerRegistryName)"
  echo "ACR_LOGIN_SERVER=$(read_output containerRegistryLoginServer)"
  echo "CONTAINER_APP_ENV=$(read_output containerAppEnvironmentName)"
} >> "$ENV_DEPLOY"

printf "  ${DIM}Outputs appended to .env.deploy${RST}\n"
printf "  media service  https://%s\n" "$(read_output mediaServiceFqdn)"
printf "  portal         https://%s\n\n" "$(read_output portalFqdn)"
printf "  ${BOLD}Next:${RST} ./scripts/02-entra-apps.sh\n\n"
