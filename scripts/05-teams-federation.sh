#!/usr/bin/env bash
#
# Enable ACS ↔ Microsoft Teams federation in the Teams tenant.
#
# This is the one deployment step that cannot be done with Azure CLI, Bicep or Microsoft
# Graph. The setting lives in the Teams service configuration and is only exposed through
# the MicrosoftTeams PowerShell module, so this script installs PowerShell if needed, signs
# you in interactively as a Teams Administrator, applies the two settings, and reads them
# back.
#
# Until it has run, a verification call to a Teams user is rejected at the far end with:
#
#     ACS code 403 / DiagCode 403#10124 — Forbidden
#
# which arrives as an immediate disconnect rather than an error on the outbound call. That
# is measured, not assumed: see docs/teams-setup.md.
#
# Two things here are easy to get wrong, and both were got wrong first:
#
#   * -AllowedAcsResources takes the ACS resource's IMMUTABLE RESOURCE ID (a bare GUID),
#     NOT its ARM path. The cmdlet rejects an ARM path outright.
#   * Each callable Teams user must be Enterprise Voice enabled. A Teams Phone LICENCE is
#     not the same as the flag being on, and the licence alone is not enough.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
source "${REPO_ROOT}/.env.deploy"

BOLD=$'\e[1m'; DIM=$'\e[2m'; GRN=$'\e[32m'; YLW=$'\e[33m'; RED=$'\e[31m'; RST=$'\e[0m'

# The immutable resource ID, read live from ARM rather than hard-coded — it is a distinct
# GUID from the resource name and from anything visible in the portal's overview blade.
ACS_RESOURCE_ID=$(az communication show \
  --name "$ACS_NAME" --resource-group "$AZURE_RESOURCE_GROUP" \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --query "immutableResourceId" -o tsv 2>/dev/null || true)

if [ -z "${ACS_RESOURCE_ID}" ]; then
  TOKEN=$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)
  ACS_RESOURCE_ID=$(curl -fsS -H "Authorization: Bearer ${TOKEN}" \
    "https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.Communication/CommunicationServices/${ACS_NAME}?api-version=2023-04-01" \
    | python3 -c 'import sys,json; print(json.load(sys.stdin)["properties"]["immutableResourceId"])')
fi

printf "\n${BOLD}Teams federation for EntraGuard${RST}\n"
printf "  ${DIM}Teams tenant   %s${RST}\n" "${TEAMS_TENANT_ID}"
printf "  ${DIM}ACS resource   %s${RST}\n" "${ACS_NAME}"

printf "  ${DIM}immutable id   %s${RST}\n\n" "${ACS_RESOURCE_ID}"

# ── PowerShell ───────────────────────────────────────────────────────────────
if ! command -v pwsh >/dev/null 2>&1; then
  printf "  ${YLW}!${RST} PowerShell is not installed.\n"
  if command -v brew >/dev/null 2>&1; then
    printf "    Installing it now (Homebrew cask, ~100 MB)…\n"
    brew install --cask powershell
  else
    printf "    ${RED}✗${RST} Install PowerShell first: https://aka.ms/powershell\n\n"
    exit 1
  fi
fi
printf "  ${GRN}✓${RST} pwsh %s\n" "$(pwsh -NoProfile -c '$PSVersionTable.PSVersion.ToString()')"

# ── Apply ────────────────────────────────────────────────────────────────────
# Written to a temp file rather than passed with -c so the quoting survives intact.
SCRIPT="$(mktemp -t entraguard-teams).ps1"
trap 'rm -f "$SCRIPT"' EXIT

cat > "$SCRIPT" <<PWSH
\$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable MicrosoftTeams)) {
    Write-Host '  Installing the MicrosoftTeams module…'
    Install-Module MicrosoftTeams -Scope CurrentUser -Force -AllowClobber
}
Import-Module MicrosoftTeams

Write-Host ''
Write-Host '  A browser window will open. Sign in as a Teams Administrator of the'
Write-Host '  tenant that holds the Teams licence.'
Write-Host ''

Connect-MicrosoftTeams -TenantId '${TEAMS_TENANT_ID}' | Out-Null

# Allow-list THIS ACS resource specifically rather than opening federation to every
# Communication Services resource in the world. The allow-list is the security boundary:
# without it, any ACS tenant could place calls to users here.
Set-CsTeamsAcsFederationConfiguration \`
    -Identity Global \`
    -EnableAcsUsers \$true \`
    -AllowedAcsResources @('${ACS_RESOURCE_ID}')

# Tenant-wide switch. Both are required — the allow-list names who may call, this permits
# the calls to be delivered at all.
Set-CsExternalAccessPolicy -Identity Global -EnableAcsFederationAccess \$true

# Enterprise Voice, per callable user. The Teams Phone licence provisions the capability;
# this flag is what actually makes the user reachable over interop. Without it the call is
# refused with 403#10391 — a DIFFERENT sub-code from the federation refusal above, and the
# only way to tell the two apart.
foreach (\$u in ('${TEAMS_UPN}')) {
    try {
        Set-CsPhoneNumberAssignment -Identity \$u -EnterpriseVoiceEnabled \$true
        Write-Host ("    enterprise voice enabled: {0}" -f \$u)
    } catch {
        Write-Host ("    could not enable enterprise voice for {0}: {1}" -f \$u, \$_.Exception.Message)
    }
}

Write-Host ''
Write-Host '  Read back from the service:'
\$fed = Get-CsTeamsAcsFederationConfiguration -Identity Global
Write-Host ("    EnableAcsUsers        : {0}" -f \$fed.EnableAcsUsers)
Write-Host ("    AllowedAcsResources   : {0}" -f (\$fed.AllowedAcsResources -join ', '))
\$pol = Get-CsExternalAccessPolicy -Identity Global
Write-Host ("    AcsFederationAccess   : {0}" -f \$pol.EnableAcsFederationAccess)

Disconnect-MicrosoftTeams | Out-Null
PWSH

pwsh -NoProfile -File "$SCRIPT"

printf "\n  ${GRN}✓${RST} Federation applied\n\n"
printf "  ${BOLD}Propagation takes up to a few hours.${RST} Until it lands, a Teams verification\n"
printf "  call still ends immediately with ${DIM}403#10124${RST} — that is the tenant, not the code.\n\n"
printf "  Test with:\n"
printf "    ${DIM}./scripts/test-teams-call.sh${RST}\n\n"
