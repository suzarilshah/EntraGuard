#!/usr/bin/env bash
#
# Grant yourself the two roles that custom security attributes require, in the TEAMS tenant.
#
# You have to run this; it is a privileged directory-role grant and that decision is yours.
#
# Two things this gets right that are easy to get wrong by hand:
#
#   * The TENANT. `az rest` uses your default context, which is the Azure subscription
#     tenant, not the tenant your Teams users live in. Running it there activates a role
#     nobody needed and leaves the real tenant untouched — silently, with a 200 response.
#
#   * Activation is not assignment. POST /directoryRoles activates a role DEFINITION so the
#     directory knows about it. It grants nothing. Membership is a separate call, and
#     without it Graph keeps returning Authorization_RequestDenied while the role appears
#     to exist.
#
# Global Administrator deliberately does NOT include these roles: custom security
# attributes are designed so that even a GA cannot read them without opting in visibly.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
source "${REPO_ROOT}/.env.deploy"

BOLD=$'\e[1m'; DIM=$'\e[2m'; GRN=$'\e[32m'; RED=$'\e[31m'; RST=$'\e[0m'

GRAPH="https://graph.microsoft.com/v1.0"

printf "\n${BOLD}Custom security attribute roles${RST}\n"
printf "  ${DIM}tenant %s${RST}\n\n" "${TEAMS_TENANT_ID}"

TOKEN=$(az account get-access-token \
  --tenant "${TEAMS_TENANT_ID}" \
  --resource https://graph.microsoft.com \
  --query accessToken -o tsv)

ME=$(curl -fsS -H "Authorization: Bearer ${TOKEN}" "${GRAPH}/me?\$select=id,userPrincipalName" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["id"], d["userPrincipalName"])')

PRINCIPAL_ID=${ME%% *}
PRINCIPAL_UPN=${ME##* }
printf "  granting to ${BOLD}%s${RST}\n\n" "$PRINCIPAL_UPN"

grant() {
  local template="$1" label="$2"

  # Activate the definition. Already-activated returns a conflict, which is success here.
  curl -s -o /dev/null -X POST \
    -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' \
    "${GRAPH}/directoryRoles" -d "{\"roleTemplateId\":\"${template}\"}"

  local role_id
  role_id=$(curl -fsS -H "Authorization: Bearer ${TOKEN}" "${GRAPH}/directoryRoles" \
    | python3 -c "
import sys, json
for r in json.load(sys.stdin)['value']:
    if r['roleTemplateId'] == '${template}':
        print(r['id']); break")

  if [ -z "$role_id" ]; then
    printf "  ${RED}✗${RST} %s — could not activate\n" "$label"
    return 1
  fi

  # Assignment. This is the part that actually grants anything.
  local response
  response=$(curl -s -X POST \
    -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' \
    "${GRAPH}/directoryRoles/${role_id}/members/\$ref" \
    -d "{\"@odata.id\":\"https://graph.microsoft.com/v1.0/directoryObjects/${PRINCIPAL_ID}\"}")

  if [ -z "$response" ] || printf '%s' "$response" | grep -q "already exist"; then
    printf "  ${GRN}✓${RST} %s\n" "$label"
  else
    printf "  ${RED}✗${RST} %s — %s\n" "$label" "$(printf '%s' "$response" | head -c 200)"
  fi
}

grant 8424c6f0-a189-499e-bbd0-26c1753c96d4 "Attribute Definition Administrator"
grant 58a13ea3-c632-46ae-9ee0-9c0d43cd7f3d "Attribute Assignment Administrator"

printf "\n  ${BOLD}Verifying by actually reading attribute sets${RST} — the only proof that counts:\n"

# Role assignments can take a moment to reach the token service, and a stale token still
# carries the old claims. Retried rather than reported as a failure on the first miss.
for attempt in 1 2 3 4 5 6; do
  TOKEN=$(az account get-access-token --tenant "${TEAMS_TENANT_ID}" \
    --resource https://graph.microsoft.com --query accessToken -o tsv)

  if curl -fsS -H "Authorization: Bearer ${TOKEN}" "${GRAPH}/directory/attributeSets" >/dev/null 2>&1; then
    printf "  ${GRN}✓${RST} attribute sets readable\n\n"
    exit 0
  fi

  printf "  ${DIM}not yet (attempt %s/6)…${RST}\n" "$attempt"
  sleep 15
done

printf "\n  ${RED}✗${RST} Still denied. Sign out and back in so a fresh token picks up the roles:\n"
printf "      ${DIM}az login --tenant %s --allow-no-subscriptions${RST}\n\n" "${TEAMS_TENANT_ID}"
exit 1
