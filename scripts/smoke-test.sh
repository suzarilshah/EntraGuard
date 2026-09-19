#!/usr/bin/env bash
# Post-deploy verification. Run before every demo.
#
# Checks the things that fail silently: a media service that is up but not answering,
# an Event Grid subscription pointing at a stale FQDN, a managed identity missing a role.
# Each of those produces a demo where nothing happens and no error appears anywhere.
set -uo pipefail

# az 2.89 on Python 3.14 emits SyntaxWarnings from its own vendored packages. They are
# harmless, but they pollute command output that gets parsed downstream.
export PYTHONWARNINGS=ignore

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GRN=$'\033[32m'; YLW=$'\033[33m'; RED=$'\033[31m'; CYN=$'\033[36m'; RST=$'\033[0m'
ok()   { printf "  ${GRN}✓${RST} %s\n" "$*"; }
warn() { printf "  ${YLW}!${RST} %s\n" "$*"; WARNINGS=$((WARNINGS+1)); }
bad()  { printf "  ${RED}✗${RST} %s\n" "$*"; FAILURES=$((FAILURES+1)); }
head2(){ printf "\n${BOLD}${CYN}%s${RST}\n" "$*"; }

FAILURES=0
WARNINGS=0

[[ -f "$ENV_DEPLOY" ]] || { echo "Run the deploy scripts first." >&2; exit 1; }
# shellcheck disable=SC1090
set -a; source "$ENV_DEPLOY"; set +a

RG="${AZURE_RESOURCE_GROUP:-rg-entraguard-demo}"

# ── Media service ───────────────────────────────────────────────────────────
head2 "1. Media service"

if curl -fsS --max-time 10 "https://${MEDIA_SERVICE_FQDN}/health/live" >/dev/null 2>&1; then
  ok "Liveness responding"
else
  bad "Liveness not responding at https://${MEDIA_SERVICE_FQDN}/health/live"
fi

READY=$(curl -fsS --max-time 10 "https://${MEDIA_SERVICE_FQDN}/health/ready" 2>/dev/null || echo "")
if echo "$READY" | jq -e '.status == "ready"' >/dev/null 2>&1; then
  ok "Readiness: all required configuration present"
else
  MISSING=$(echo "$READY" | jq -r '.missing // [] | join(", ")' 2>/dev/null || echo "unknown")
  bad "Readiness failing — missing: ${MISSING}"
fi

CONFIG=$(curl -fsS --max-time 10 -H "Authorization: Bearer ${ENTRAGUARD_OPERATOR_ACCESS_TOKEN:-}" "https://${MEDIA_SERVICE_FQDN}/api/config" 2>/dev/null || echo "")
if [[ -n "$CONFIG" ]]; then
  TIER=$(echo "$CONFIG" | jq -r '.riskTier')
  MODEL=$(echo "$CONFIG" | jq -r '.model')
  ok "Analyst deployment: ${MODEL}"
  if [[ "$TIER" == "Graph" ]]; then
    ok "Remediation tier: Graph (confirmCompromised will run for real)"
  else
    warn "Remediation tier: Degraded (no Entra ID P2 — this is a designed path, not a fault)"
  fi
else
  bad "Could not read /api/config — provide an ephemeral ENTRAGUARD_OPERATOR_ACCESS_TOKEN with the EntraGuard API scope."
fi

# ── Portal ──────────────────────────────────────────────────────────────────
head2 "2. Portal"

if curl -fsS --max-time 15 "https://${PORTAL_FQDN}/" >/dev/null 2>&1; then
  ok "Portal serving at https://${PORTAL_FQDN}"
else
  bad "Portal not responding at https://${PORTAL_FQDN}"
fi

# ── Event Grid ──────────────────────────────────────────────────────────────
head2 "3. IncomingCall subscription"

SUB_ENDPOINT=$(az eventgrid system-topic event-subscription show \
  --name entraguard-incoming-call \
  --system-topic-name egst-entraguard-demo \
  --resource-group "$RG" \
  --query "destination.endpointBaseUrl" -o tsv 2>/dev/null || echo "")

if [[ -z "$SUB_ENDPOINT" ]]; then
  bad "No IncomingCall subscription. Run ./scripts/04-eventgrid-subscribe.sh"
elif [[ "$SUB_ENDPOINT" == *"${MEDIA_SERVICE_FQDN}"* ]]; then
  ok "Subscribed and pointing at the current media service"
else
  # The classic silent failure: the container app was recreated and the subscription
  # still points at the old FQDN. Calls ring, nothing is intercepted, no error surfaces.
  bad "Subscription points at a STALE endpoint: ${SUB_ENDPOINT}"
  bad "Re-run ./scripts/04-eventgrid-subscribe.sh"
fi

# ── Azure dependencies ──────────────────────────────────────────────────────
head2 "4. Azure dependencies"

# The az CLI intermittently writes native-loader warnings to stdout, which corrupts a
# bare command substitution. Inside [[ x -gt y ]] bash evaluates operands arithmetically
# and treats a non-numeric string as a VARIABLE NAME — so under `set -u` the whole script
# dies with "unbound variable", naming a token from an unrelated warning. Reduce every
# count to digits before comparing.
# Match only lines that are ENTIRELY a number, and take the LAST one. Stripping
# non-digits instead would happily scrape "3.14" and "1028" out of a Python warning and
# concatenate them into a nonsense count — which is exactly what a naive version did.
num() {
  local raw
  raw=$(printf '%s\n' "${1:-}" \
        | tr -d '\r' \
        | grep -E '^[[:space:]]*[0-9]+[[:space:]]*$' \
        | tail -1 \
        | tr -cd '0-9')
  echo "${raw:-0}"
}

check_count() {
  local label="$1" count
  count=$(num "$2")
  (( count > 0 )) && ok "${label} (${count})" || bad "${label} — none found"
}

# Queried through the core ARM resource API rather than per-service commands. Several of
# those (az communication, az iot) live in CLI extensions, and a broken extension or a
# mismatched Python on the operator's machine makes them fail in a way that looks
# identical to "the resource was never deployed". This path has no extension dependency.
count_type() {
  az resource list -g "$RG" --resource-type "$1" --query 'length(@)' -o tsv 2>/dev/null
}

check_count "Communication Services" "$(count_type 'Microsoft.Communication/communicationServices')"
check_count "Cognitive Services"     "$(count_type 'Microsoft.CognitiveServices/accounts')"
check_count "Log Analytics"          "$(count_type 'Microsoft.OperationalInsights/workspaces')"
check_count "Container Apps"         "$(count_type 'Microsoft.App/containerApps')"

OPENAI_ACCOUNT=$(az cognitiveservices account list -g "$RG" --query "[?kind=='OpenAI'] | [0].name" -o tsv 2>/dev/null | tr -d '[:space:]')
DEPLOYMENTS=$(num "$(az cognitiveservices account deployment list \
  -g "$RG" -n "$OPENAI_ACCOUNT" --query "length(@)" -o tsv 2>/dev/null)")
(( DEPLOYMENTS > 0 )) && ok "Azure OpenAI deployment present (${OPENAI_ACCOUNT})" || bad "No Azure OpenAI deployment"

# ── Sentinel tables ─────────────────────────────────────────────────────────
head2 "5. Sentinel"

if [[ -n "${LAW_WORKSPACE_ID:-}" ]]; then
  ROWS=$(az monitor log-analytics query \
    --workspace "$LAW_WORKSPACE_ID" \
    --analytics-query "EntraGuard_CallAnalysis_CL | where TimeGenerated > ago(7d) | count" \
    --query "[0].Count" -o tsv 2>/dev/null || echo "")

  if [[ -n "$ROWS" ]]; then
    ROWS=$(num "$ROWS")
    ok "EntraGuard_CallAnalysis_CL readable (${ROWS} rows in 7d)"
    (( ROWS == 0 )) && warn "No rows yet — expected before the first intercepted call"
  else
    warn "Could not query the custom table. It may still be provisioning (can take ~15 min after deploy)."
  fi
else
  warn "LAW_WORKSPACE_ID not set"
fi

# ── Verdict ─────────────────────────────────────────────────────────────────
printf "\n"
if (( FAILURES > 0 )); then
  printf "${RED}${BOLD}  %d check(s) failed, %d warning(s).${RST}\n" "$FAILURES" "$WARNINGS"
  printf "  ${DIM}Do not demo until the ✗ items are resolved.${RST}\n\n"
  exit 1
fi

printf "${GRN}${BOLD}  All checks passed${RST}"
(( WARNINGS > 0 )) && printf " ${YLW}(%d warning(s))${RST}" "$WARNINGS"
printf "\n\n  ${DIM}Demo runbook: docs/demo-runbook.md${RST}\n"
printf "  ${DIM}Portal:       https://%s/live${RST}\n\n" "$PORTAL_FQDN"
