#!/usr/bin/env bash
# Entra ID identity plumbing — everything Bicep cannot express, because it writes to
# Microsoft Graph rather than to Azure Resource Manager.
#
# Creates:
#   · the ACS Clients first-party service principal (enables direct Entra ID user auth)
#   · the portal app registration (delegated: User.Read + ACS VoIP)
#   · Graph app-role grants for the managed identity
#   · the Conditional Access quarantine group used by the degraded remediation path
#
# The managed identity IS the service principal. Granting Graph app roles directly to it
# means there is no client secret anywhere in this system — nothing to rotate, nothing to
# leak, and nothing to accidentally commit.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_DEPLOY="${REPO_ROOT}/.env.deploy"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GRN=$'\033[32m'; YLW=$'\033[33m'; RED=$'\033[31m'; CYN=$'\033[36m'; RST=$'\033[0m'
ok()   { printf "  ${GRN}✓${RST} %s\n" "$*"; }
warn() { printf "  ${YLW}!${RST} %s\n" "$*"; }
info() { printf "  ${DIM}·${RST} %s\n" "$*"; }
head2(){ printf "\n${BOLD}${CYN}%s${RST}\n" "$*"; }

[[ -f "$ENV_DEPLOY" ]] || { echo "Run ./scripts/01-deploy-infra.sh first." >&2; exit 1; }
# shellcheck disable=SC1090
set -a; source "$ENV_DEPLOY"; set +a

ACS_CLIENTS_APP_ID="2a04943b-b6a7-4f65-8786-2bb6131b59f6"
GRAPH_APP_ID="00000003-0000-0000-c000-000000000000"

# ── 1. ACS Clients service principal ────────────────────────────────────────
head2 "1. Azure Communication Services Clients service principal"

if az ad sp show --id "$ACS_CLIENTS_APP_ID" >/dev/null 2>&1; then
  ok "Already present in this tenant."
else
  if az ad sp create --id "$ACS_CLIENTS_APP_ID" >/dev/null 2>&1; then
    ok "Created."
  else
    warn "Could not create it — needs Application Administrator or Global Administrator."
    info "Direct Entra ID user auth for ACS stays disabled; the token broker handles it instead."
  fi
fi

# ── 2. Portal app registration ──────────────────────────────────────────────
head2 "2. Portal app registration"

PORTAL_APP_NAME="EntraGuard-Portal"
PORTAL_APP_ID=$(az ad app list --display-name "$PORTAL_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || echo "")

if [[ -z "$PORTAL_APP_ID" ]]; then
  PORTAL_APP_ID=$(az ad app create \
    --display-name "$PORTAL_APP_NAME" \
    --sign-in-audience AzureADMyOrg \
    --query appId -o tsv)
  ok "Created ${PORTAL_APP_NAME} (${PORTAL_APP_ID})"
else
  ok "${PORTAL_APP_NAME} already exists (${PORTAL_APP_ID})"
fi

# SPA redirect URIs, including localhost so the demo runs without redeploying.
#
# Uses --set rather than --spa-redirect-uris: that flag is not present in every az CLI
# version (2.89 does not have it) and its absence fails silently as "could not set".
REDIRECT_JSON='["http://localhost:3000","http://localhost:3000/auth"'
[[ -n "${PORTAL_FQDN:-}" ]] && REDIRECT_JSON="${REDIRECT_JSON},\"https://${PORTAL_FQDN}\",\"https://${PORTAL_FQDN}/auth\""
REDIRECT_JSON="${REDIRECT_JSON}]"

if az ad app update --id "$PORTAL_APP_ID" \
     --set "spa={\"redirectUris\":${REDIRECT_JSON}}" >/dev/null 2>&1; then
  ok "Redirect URIs set"
else
  warn "Could not set redirect URIs — set them by hand in the Entra admin center."
fi

# ── 3. Graph app roles for the managed identity ─────────────────────────────
head2 "3. Microsoft Graph app roles for the managed identity"

if [[ -z "${MANAGED_IDENTITY_PRINCIPAL_ID:-}" ]]; then
  warn "MANAGED_IDENTITY_PRINCIPAL_ID is missing from .env.deploy — skipping."
else
  GRAPH_SP_ID=$(az ad sp show --id "$GRAPH_APP_ID" --query id -o tsv)

  # Least privilege for what EntraGuard actually does. Read-only everywhere except the
  # three write scopes remediation genuinely needs.
  declare -a ROLES=(
    "IdentityRiskyUser.ReadWrite.All:elevate user risk (rung 1, needs P2)"
    "IdentityRiskEvent.Read.All:read risk detections"
    "User.RevokeSessions.All:revoke refresh tokens (rung 2)"
    "GroupMember.ReadWrite.All:Conditional Access quarantine (rung 3)"
    "AuditLog.Read.All:read sign-in logs"
    "Directory.Read.All:resolve users and tenant"
  )

  GRANTED=0
  for entry in "${ROLES[@]}"; do
    role="${entry%%:*}"
    purpose="${entry#*:}"

    ROLE_ID=$(az ad sp show --id "$GRAPH_APP_ID" \
      --query "appRoles[?value=='${role}' && contains(allowedMemberTypes,'Application')].id | [0]" -o tsv)

    if [[ -z "$ROLE_ID" || "$ROLE_ID" == "null" ]]; then
      warn "${role} — no such app role"
      continue
    fi

    # Idempotent: re-running must not fail on an already-granted role.
    EXISTING=$(az rest --method GET \
      --url "https://graph.microsoft.com/v1.0/servicePrincipals/${MANAGED_IDENTITY_PRINCIPAL_ID}/appRoleAssignments" \
      --query "value[?appRoleId=='${ROLE_ID}'] | [0].id" -o tsv 2>/dev/null || echo "")

    if [[ -n "$EXISTING" && "$EXISTING" != "null" ]]; then
      ok "${role} — already granted"
      GRANTED=$((GRANTED + 1))
      continue
    fi

    if az rest --method POST \
      --url "https://graph.microsoft.com/v1.0/servicePrincipals/${MANAGED_IDENTITY_PRINCIPAL_ID}/appRoleAssignments" \
      --headers "Content-Type=application/json" \
      --body "{\"principalId\":\"${MANAGED_IDENTITY_PRINCIPAL_ID}\",\"resourceId\":\"${GRAPH_SP_ID}\",\"appRoleId\":\"${ROLE_ID}\"}" \
      >/dev/null 2>&1; then
      ok "${role} — granted (${purpose})"
      GRANTED=$((GRANTED + 1))
    else
      warn "${role} — could not grant (needs Privileged Role Administrator or Global Administrator)"
    fi
  done

  info "${GRANTED}/${#ROLES[@]} app roles in place."
fi

# ── 4. Quarantine group ─────────────────────────────────────────────────────
head2 "4. Conditional Access quarantine group"

GROUP_NAME="EntraGuard-Quarantine"
GROUP_ID=$(az ad group list --display-name "$GROUP_NAME" --query "[0].id" -o tsv 2>/dev/null || echo "")

if [[ -z "$GROUP_ID" ]]; then
  GROUP_ID=$(az ad group create \
    --display-name "$GROUP_NAME" \
    --mail-nickname "entraguard-quarantine" \
    --description "Users EntraGuard has flagged during a live authentication call. Bind a Conditional Access policy to this group requiring phishing-resistant MFA, or blocking outright." \
    --query id -o tsv 2>/dev/null || echo "")
  [[ -n "$GROUP_ID" ]] && ok "Created ${GROUP_NAME} (${GROUP_ID})" || warn "Could not create the quarantine group."
else
  ok "${GROUP_NAME} already exists (${GROUP_ID})"
fi

if [[ -n "$GROUP_ID" ]]; then
  warn "The group does nothing until a Conditional Access policy targets it."
  info "Entra admin center → Protection → Conditional Access → New policy"
  info "  Users: include group '${GROUP_NAME}'"
  info "  Target: All resources"
  info "  Grant: Require authentication strength → Phishing-resistant MFA"
fi

# ── 5. Persist ──────────────────────────────────────────────────────────────
{
  echo ""
  echo "# ── Written by scripts/02-entra-apps.sh ──"
  echo "ENTRA_PORTAL_CLIENT_ID=${PORTAL_APP_ID}"
  echo "ENTRA_QUARANTINE_GROUP_ID=${GROUP_ID:-}"
} >> "$ENV_DEPLOY"

# ── 6. Consent ──────────────────────────────────────────────────────────────
head2 "5. Administrator consent"

TENANT_ID=$(az account show --query tenantId -o tsv)
printf "  ${DIM}Graph app roles were granted directly to the managed identity above; if any\n"
printf "  reported a permission error, a Global Administrator must grant them.\n\n"
printf "  For the portal's delegated permissions, open:${RST}\n\n"
printf "    ${BOLD}https://login.microsoftonline.com/%s/adminconsent?client_id=%s${RST}\n\n" \
  "$TENANT_ID" "$PORTAL_APP_ID"

printf "  ${BOLD}Next:${RST} ./scripts/deploy-apps.sh\n\n"
