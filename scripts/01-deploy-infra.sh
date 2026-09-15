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
# compute.bicep defaults every image to the Microsoft quickstart placeholder, because on a
# first deploy the real images do not exist yet. On a REdeploy that default silently reverts
# the running applications to the placeholder — the media service stops answering calls and
# the portal stops being EntraGuard, with a successful-looking deployment.
#
# This guard has failed once already, in a way worth recording. It passed mediaServiceImage=
# and portalImage= to parameters that main.bicep no longer declared, so every run died on
# "unrecognized template parameter". It failed closed, which was luck — the obvious way to
# make it run again was to delete the two arguments, and what-if confirmed that reverts all
# FOUR container apps at once. A comment describing a hazard is worth exactly what its test
# is worth, so the hazard now has a test: see the what-if gate below.
#
# read_image NAME -> prints the running image, or exits the script.
#
# The distinction that matters is "this app does not exist yet" (a first deploy — the
# placeholder default is correct) versus "the app exists and I could not read it" (a
# transient ARM error — proceeding would revert a LIVE application). The previous version
# collapsed both to an empty string, which silently selected the placeholder.
read_image() {
  local name="$1" img
  if ! az containerapp show -n "$name" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
    echo ""            # genuinely absent: first deploy, let the template default apply
    return 0
  fi
  img=$(az containerapp show -n "$name" -g "$RESOURCE_GROUP" \
        --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
  if [[ -z "$img" ]]; then
    printf "  ${BOLD}Refusing to deploy.${RST} %s exists but its image could not be read.\n" "$name" >&2
    printf "  Deploying now would revert a running application to the placeholder.\n" >&2
    exit 1
  fi
  echo "$img"
}

CURRENT_MEDIA_IMAGE=$(read_image "ca-entraguard-media")
CURRENT_PORTAL_IMAGE=$(read_image "ca-entraguard-portal")
# Contoso Treasury deliberately has no parameter of its own: it runs the PORTAL image with
# APP_MODE=treasury, so portalImage governs both and a separate one could only drift.
CURRENT_VOICEPRINT_IMAGE=$(read_image "ca-entraguard-voiceprint")

IMAGE_PARAMS=()
add_image_param() {
  local param="$1" value="$2" label="$3"
  if [[ -n "$value" && "$value" != *"k8se/quickstart"* ]]; then
    IMAGE_PARAMS+=("${param}=${value}")
    printf "  ${DIM}preserving %-10s %s${RST}\n" "$label" "$value"
  fi
}
add_image_param mediaServiceImage "$CURRENT_MEDIA_IMAGE"     "media"
add_image_param portalImage       "$CURRENT_PORTAL_IMAGE"    "portal"
# Voiceprint was missing from this list entirely, and it is the worst one to lose:
# deploy-apps.sh skips it unless VOICEPRINT=rebuild, so a normal redeploy does not bring it
# back. Recovering it needs a deliberate VOICEPRINT=rebuild ./scripts/deploy-apps.sh.
add_image_param voiceprintImage   "$CURRENT_VOICEPRINT_IMAGE" "voiceprint"

printf "\n${BOLD}${CYN}Deploying EntraGuard infrastructure${RST}\n"
printf "  ${DIM}subscription  %s${RST}\n" "$AZURE_SUBSCRIPTION_ID"
printf "  ${DIM}resource grp  %s${RST}\n" "$RESOURCE_GROUP"
printf "  ${DIM}location      %s${RST}\n" "$LOCATION"
printf "  ${DIM}model         %s (%s, %s)${RST}\n" "$AOAI_MODEL" "$AOAI_MODEL_VERSION" "$AOAI_SKU"
printf "  ${DIM}risk tier     %s${RST}\n\n" "${ENTRAGUARD_RISK_TIER:-degraded}"

DEPLOY_PARAMS=(
  appName=entraguard
  environmentName=demo
  location="$LOCATION"
  openAiModelName="$AOAI_MODEL"
  openAiModelVersion="$AOAI_MODEL_VERSION"
  openAiSkuName="$AOAI_SKU"
  operatorObjectId="$OPERATOR_OBJECT_ID"
  ${IMAGE_PARAMS[@]+"${IMAGE_PARAMS[@]}"}
)

# ── The test for the hazard described above ─────────────────────────────────
#
# Ask ARM what this deployment would actually do, and refuse if the answer includes setting
# any container app to the placeholder. This catches the whole family of causes rather than
# the one that bit us — a renamed parameter, a new container app nobody added here, a
# read that silently returned the wrong thing — because it checks the OUTCOME rather than
# the reasoning that leads to it.
printf "  ${DIM}checking what this would change…${RST}\n"
WHATIF=$(az deployment sub what-if \
  --name "${DEPLOYMENT_NAME}-whatif" \
  --location "$LOCATION" \
  --template-file "${REPO_ROOT}/infra/main.bicep" \
  --parameters "${DEPLOY_PARAMS[@]}" \
  --no-pretty-print 2>&1) || {
    printf "  ${BOLD}Refusing to deploy.${RST} what-if failed, so the blast radius is unknown:\n" >&2
    printf "%s\n" "$WHATIF" | tail -5 >&2
    exit 1
  }

if grep -q '"after": *"mcr.microsoft.com/k8se/quickstart' <<<"$WHATIF"; then
  printf "\n  ${BOLD}Refusing to deploy.${RST}\n" >&2
  printf "  This deployment would revert a container app to the Microsoft placeholder,\n" >&2
  printf "  taking the application down while reporting success.\n\n" >&2
  printf "  Usually this means main.bicep no longer accepts one of the image parameters,\n" >&2
  printf "  or a new container app was added to compute.bicep without being preserved here.\n" >&2
  exit 1
fi
printf "  ${GRN}✓${RST} no container app would be reverted\n\n"

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "${REPO_ROOT}/infra/main.bicep" \
  --parameters "${DEPLOY_PARAMS[@]}" \
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
