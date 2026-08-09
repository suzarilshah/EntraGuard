#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# EntraGuard preflight
#
# Fails fast on the three things that silently kill this build on demo day:
#   1. Azure OpenAI quota  — Sponsorship subscriptions frequently have none.
#   2. Entra ID P2         — required for identityProtection confirmCompromised.
#   3. ACS Entra ID auth   — public preview, tenant may not be eligible.
#
# Writes .env.deploy with the resolved feature flags. Everything downstream
# reads that file, so the degradation decisions are made ONCE, here, on day 1.
#
# Read-only: registers resource providers, otherwise creates nothing.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"
LOCATION="${AZURE_LOCATION:-eastus}"

# ACS Clients first-party application — constant across all tenants.
ACS_CLIENTS_APP_ID="2a04943b-b6a7-4f65-8786-2bb6131b59f6"

BOLD=$'\033[1m'; DIM=$'\033[2m'; RED=$'\033[31m'; GRN=$'\033[32m'
YLW=$'\033[33m'; CYN=$'\033[36m'; RST=$'\033[0m'

ok()   { printf "  ${GRN}✓${RST} %s\n" "$*"; }
warn() { printf "  ${YLW}!${RST} %s\n" "$*"; }
fail() { printf "  ${RED}✗${RST} %s\n" "$*"; }
info() { printf "  ${DIM}·${RST} %s\n" "$*"; }
head2(){ printf "\n${BOLD}${CYN}%s${RST}\n" "$*"; }

BLOCKERS=0

# ── 0. Authentication ────────────────────────────────────────────────────────
head2 "0. Azure authentication"

if ! az account show >/dev/null 2>&1; then
  fail "Not signed in to Azure CLI."
  printf "\n    Run:  ${BOLD}az login --scope https://management.core.windows.net//.default${RST}\n\n"
  exit 1
fi

# A cached token can exist yet be unusable for ARM. Prove it with a real call.
if ! az group list --query "[0].name" -o tsv >/dev/null 2>&1; then
  fail "Signed in, but the ARM token is stale or lacks scope."
  printf "\n    Run:  ${BOLD}az login --scope https://management.core.windows.net//.default${RST}\n\n"
  exit 1
fi

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
SUB_NAME=$(az account show --query name -o tsv)
SIGNED_IN_USER=$(az account show --query user.name -o tsv)

ok "Subscription: ${SUB_NAME} (${SUBSCRIPTION_ID})"

# EntraGuard is developed and demoed on the MCT subscription, which carries the monthly
# credit. Deploying to another subscription duplicates the whole stack and bills it
# separately — which is exactly what happened once already, on Sponsorship PAYG.
EXPECTED_SUBSCRIPTION="${ENTRAGUARD_SUBSCRIPTION:-<AZURE_SUBSCRIPTION_ID>}"
if [[ "$SUBSCRIPTION_ID" != "$EXPECTED_SUBSCRIPTION" ]]; then
  fail "This is not the EntraGuard subscription."
  printf "       ${DIM}expected %s\n" "$EXPECTED_SUBSCRIPTION"
  printf "       got      %s${RST}\n\n" "$SUBSCRIPTION_ID"
  printf "       ${BOLD}az account set --subscription %s${RST}\n" "$EXPECTED_SUBSCRIPTION"
  printf "       ${DIM}Override deliberately with ENTRAGUARD_SUBSCRIPTION=<id> if you mean it.${RST}\n\n"
  exit 1
fi
ok "Tenant:       ${TENANT_ID}"
ok "Signed in as: ${SIGNED_IN_USER}"

# ── 1. Resource providers ────────────────────────────────────────────────────
head2 "1. Resource provider registration"

PROVIDERS=(
  Microsoft.Communication
  Microsoft.CognitiveServices
  Microsoft.EventGrid
  Microsoft.App
  Microsoft.OperationalInsights
  Microsoft.SecurityInsights
  # Sentinel onboarding fails with MissingSubscriptionRegistration without this one —
  # and the error names OperationsManagement, which appears nowhere in the template.
  Microsoft.OperationsManagement
  Microsoft.Insights
  Microsoft.Storage
  Microsoft.ManagedIdentity
  Microsoft.ResourceGraph
)

PENDING=()
for p in "${PROVIDERS[@]}"; do
  state=$(az provider show -n "$p" --query registrationState -o tsv 2>/dev/null || echo "Unknown")
  case "$state" in
    Registered)  ok "$p" ;;
    Registering) warn "$p (registering — will settle on its own)"; PENDING+=("$p") ;;
    *)           info "$p → registering now"
                 az provider register -n "$p" >/dev/null 2>&1 && PENDING+=("$p") \
                   || fail "$p could not be registered (insufficient permission?)" ;;
  esac
done

    # Registration is asynchronous, and 01-deploy-infra.sh runs seconds later. Firing the
    # request and moving on races the deployment — which surfaces as a confusing
    # MissingSubscriptionRegistration failure deep inside a module, naming a provider that
    # appears nowhere in the Bicep. Block here instead.
if (( ${#PENDING[@]} > 0 )); then
  info "Waiting for ${#PENDING[@]} provider(s) to finish registering…"
  DEADLINE=$((SECONDS + 300))

  while (( ${#PENDING[@]} > 0 && SECONDS < DEADLINE )); do
    sleep 10
    STILL_PENDING=()
    for p in "${PENDING[@]}"; do
      state=$(az provider show -n "$p" --query registrationState -o tsv 2>/dev/null || echo "Unknown")
      if [[ "$state" == "Registered" ]]; then
        ok "$p registered"
      else
        STILL_PENDING+=("$p")
      fi
    done
    # macOS ships bash 3.2, where "${EMPTY[@]}" is an unbound-variable error under
    # `set -u`. The ${arr[@]+"${arr[@]}"} form expands to nothing when empty instead.
    PENDING=(${STILL_PENDING[@]+"${STILL_PENDING[@]}"})
  done

  if (( ${#PENDING[@]} > 0 )); then
    fail "Still not registered after 5 minutes: ${PENDING[*]}"
    fail "Deploying now would fail with MissingSubscriptionRegistration."
    BLOCKERS=$((BLOCKERS + 1))
  fi
fi

# ── 2. Azure OpenAI quota — GO/NO-GO for the AI layer ───────────────────────
head2 "2. Azure OpenAI model availability in ${LOCATION}"

# Preference order: cheapest model that still reasons well over a transcript.
# The Analyst runs once every ~3s per live call, so cost-per-call matters.
PREFERRED_MODELS=(gpt-5-mini gpt-5-nano gpt-5 gpt-4o-mini gpt-4o)
CHOSEN_MODEL=""
CHOSEN_MODEL_VERSION=""
CHOSEN_SKU="GlobalStandard"

MODELS_JSON=$(az cognitiveservices model list -l "$LOCATION" -o json 2>/dev/null || echo "[]")

if [[ "$MODELS_JSON" == "[]" || -z "$MODELS_JSON" ]]; then
  warn "Could not enumerate models in ${LOCATION}."
  warn "Usually means the subscription has no Cognitive Services access yet."
else
  for m in "${PREFERRED_MODELS[@]}"; do
    match=$(echo "$MODELS_JSON" | jq -r --arg m "$m" '
      [ .[]
        | select(.model.name == $m)
        | { name: .model.name,
            version: .model.version,
            skus: [ .model.skus[]?.name ] }
      ] | first // empty' 2>/dev/null)

    if [[ -n "$match" ]]; then
      CHOSEN_MODEL=$(echo "$match" | jq -r '.name')
      CHOSEN_MODEL_VERSION=$(echo "$match" | jq -r '.version')
      # GlobalStandard is preferred; fall back to whatever SKU is offered.
      if echo "$match" | jq -e '.skus | index("GlobalStandard")' >/dev/null 2>&1; then
        CHOSEN_SKU="GlobalStandard"
      else
        CHOSEN_SKU=$(echo "$match" | jq -r '.skus[0] // "Standard"')
      fi
      ok "Selected ${BOLD}${CHOSEN_MODEL}${RST} (version ${CHOSEN_MODEL_VERSION}, SKU ${CHOSEN_SKU})"
      break
    else
      info "${m} — not offered in ${LOCATION}"
    fi
  done
fi

if [[ -z "$CHOSEN_MODEL" ]]; then
  fail "No usable Azure OpenAI model found in ${LOCATION}."
  printf "       ${DIM}The Analyst agent cannot run. Options:\n"
  printf "         · try another region:  AZURE_LOCATION=swedencentral %s\n" "$0"
  printf "         · request Azure OpenAI access for this subscription\n"
  printf "         · check quota:  az cognitiveservices usage list -l %s${RST}\n" "$LOCATION"
  BLOCKERS=$((BLOCKERS + 1))
fi

# Quota check: a model can be *offered* while your TPM allowance is zero.
QUOTA_JSON=$(az cognitiveservices usage list -l "$LOCATION" -o json 2>/dev/null || echo "[]")
if [[ -n "$CHOSEN_MODEL" && "$QUOTA_JSON" != "[]" ]]; then
  avail=$(echo "$QUOTA_JSON" | jq -r --arg sku "$CHOSEN_SKU" --arg m "$CHOSEN_MODEL" '
    [ .[] | select((.name.value // "") | ascii_downcase | contains($m))
          | { limit: .limit, current: .currentValue } ] | first // empty' 2>/dev/null)
  if [[ -n "$avail" ]]; then
    lim=$(echo "$avail" | jq -r '.limit'); cur=$(echo "$avail" | jq -r '.current')
    if (( $(echo "$lim <= 0" | bc -l 2>/dev/null || echo 0) )); then
      fail "Quota for ${CHOSEN_MODEL} is 0 in ${LOCATION} — deployment will fail."
      BLOCKERS=$((BLOCKERS + 1))
    else
      ok "Quota: ${cur}/${lim} TPM-thousands used for ${CHOSEN_MODEL}"
    fi
  fi
fi

# ── 3. Entra ID P2 — decides the remediation tier ───────────────────────────
head2 "3. Entra ID licensing (Identity Protection write access)"

RISK_TIER="degraded"
SKUS_JSON=$(az rest --method GET \
  --url "https://graph.microsoft.com/v1.0/subscribedSkus?\$select=skuPartNumber,servicePlans,prepaidUnits" \
  -o json 2>/dev/null || echo "")

if [[ -z "$SKUS_JSON" ]]; then
  warn "Could not read subscribedSkus (needs Organization.Read.All or Directory.Read.All)."
  warn "Assuming no P2 → remediation runs in degraded tier."
else
  # AAD_PREMIUM_P2 is the service plan that unlocks riskyUsers read/write.
  if echo "$SKUS_JSON" | jq -e '
      [ .value[]?.servicePlans[]?
        | select(.servicePlanName == "AAD_PREMIUM_P2" and .provisioningStatus == "Success") ]
      | length > 0' >/dev/null 2>&1; then
    RISK_TIER="graph"
    ok "AAD_PREMIUM_P2 present — ${BOLD}confirmCompromised will run for real${RST}."
  else
    warn "No AAD_PREMIUM_P2 service plan found."
    info "Remediation degrades to: revokeSignInSessions → CA quarantine → Sentinel incident."
    info "This is a designed path, not a failure. The portal will label it honestly."
    HAVE=$(echo "$SKUS_JSON" | jq -r '[ .value[]?.skuPartNumber ] | join(", ")' 2>/dev/null)
    [[ -n "$HAVE" ]] && info "Tenant SKUs: ${HAVE}"
  fi
fi

# Probe the actual endpoint — licensing inference can be wrong either way.
RISKY_PROBE=$(az rest --method GET \
  --url "https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?\$top=1" \
  -o json 2>&1 || echo "PROBE_FAILED")
if [[ "$RISKY_PROBE" == *"PROBE_FAILED"* || "$RISKY_PROBE" == *"error"* ]]; then
  if [[ "$RISKY_PROBE" == *"insufficient"* || "$RISKY_PROBE" == *"Authorization"* ]]; then
    info "riskyUsers probe: signed-in user lacks the delegated permission (expected)."
    info "The service principal gets its own app-only grant in 02-entra-apps.sh."
  else
    info "riskyUsers probe returned an error — consistent with no P2."
  fi
else
  ok "riskyUsers endpoint is readable in this tenant."
  RISK_TIER="graph"
fi

# ── 4. ACS Entra ID direct auth (public preview) ────────────────────────────
head2 "4. ACS direct Entra ID user authentication (public preview)"

ACS_ENTRA_DIRECT="false"
SP_EXISTS=$(az ad sp show --id "$ACS_CLIENTS_APP_ID" --query id -o tsv 2>/dev/null || echo "")

if [[ -n "$SP_EXISTS" ]]; then
  ok "'Azure Communication Services Clients' service principal exists (${SP_EXISTS})"
  ACS_ENTRA_DIRECT="true"
else
  warn "ACS Clients service principal not yet in this tenant."
  info "02-entra-apps.sh creates it:  az ad sp create --id ${ACS_CLIENTS_APP_ID}"
  info "Requires Application Administrator or Global Administrator."
  # Not a blocker — the token-broker fallback is GA and equivalent for the demo.
fi

# ── 5. Toolchain ─────────────────────────────────────────────────────────────
head2 "5. Local toolchain"

check_tool() {
  if command -v "$1" >/dev/null 2>&1; then
    ok "$1 $(eval "$2" 2>/dev/null | head -1)"
  else
    fail "$1 not found — $3"
    BLOCKERS=$((BLOCKERS + 1))
  fi
}

check_tool az      "az version --query '\"azure-cli\"' -o tsv" "brew install azure-cli"
check_tool jq      "jq --version"                              "brew install jq"
check_tool node    "node --version"                            "brew install node"
check_tool dotnet  "dotnet --version"                          "brew install dotnet@9"

if az bicep version >/dev/null 2>&1; then
  ok "bicep $(az bicep version 2>/dev/null | head -1)"
else
  info "bicep not installed → installing"
  az bicep install >/dev/null 2>&1 && ok "bicep installed" || { fail "bicep install failed"; BLOCKERS=$((BLOCKERS+1)); }
fi

# ── 6. Emit resolved configuration ───────────────────────────────────────────
head2 "6. Resolved deployment configuration"

cat > "$ENV_DEPLOY" <<EOF
# Generated by scripts/00-preflight.sh — do not edit by hand, do not commit.
# Regenerate after any tenant/subscription/licensing change.
AZURE_SUBSCRIPTION_ID=${SUBSCRIPTION_ID}
AZURE_TENANT_ID=${TENANT_ID}
AZURE_LOCATION=${LOCATION}
AZURE_RESOURCE_GROUP=${AZURE_RESOURCE_GROUP:-rg-entraguard-demo}

# Azure OpenAI — chosen by availability probe, not hardcoded.
AOAI_MODEL=${CHOSEN_MODEL}
AOAI_MODEL_VERSION=${CHOSEN_MODEL_VERSION}
AOAI_SKU=${CHOSEN_SKU}

# Remediation tier: graph = real confirmCompromised; degraded = revoke + Sentinel.
ENTRAGUARD_RISK_TIER=${RISK_TIER}

# ACS direct Entra ID user auth (preview) vs server-side token broker (GA).
ACS_ENTRA_DIRECT=${ACS_ENTRA_DIRECT}
EOF

printf "  ${DIM}written to %s${RST}\n\n" "${ENV_DEPLOY/#$HOME/\~}"
sed 's/^/    /' "$ENV_DEPLOY" | grep -v '^\s*#' | grep -v '^\s*$'

# ── Verdict ──────────────────────────────────────────────────────────────────
printf "\n"
if (( BLOCKERS > 0 )); then
  printf "${RED}${BOLD}  PREFLIGHT FAILED — %d blocker(s).${RST}\n" "$BLOCKERS"
  printf "  ${DIM}Resolve the ✗ items above, then re-run. Do not deploy past this.${RST}\n\n"
  exit 1
fi

printf "${GRN}${BOLD}  PREFLIGHT PASSED${RST}\n"
printf "  ${DIM}Next:  ./scripts/01-deploy-infra.sh${RST}\n\n"

if [[ "$RISK_TIER" == "degraded" ]]; then
  printf "  ${YLW}Note:${RST} running in ${BOLD}degraded${RST} remediation tier (no Entra ID P2).\n"
  printf "  ${DIM}The demo still works end to end and the portal states the limitation\n"
  printf "  explicitly rather than faking a successful risk elevation.${RST}\n\n"
fi
