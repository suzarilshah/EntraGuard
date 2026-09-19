#!/usr/bin/env bash
#
# Register EntraGuard as an Entra ID External Authentication Method.
#
# This is what turns EntraGuard from "the Treasury demo calls an API" into a second factor
# every application in a consenting tenant can use. It creates two things the Bicep cannot:
# the multitenant app registration Entra authenticates us with, and the RS256 certificate we
# sign responses with.
#
# Safe to re-run. Everything is created only if absent.
set -euo pipefail

cd "$(dirname "$0")/.."
# shellcheck disable=SC1091
[[ -f .env.deploy ]] && { set -a; source .env.deploy; set +a; }

BOLD=$'\e[1m'; DIM=$'\e[2m'; GRN=$'\e[32m'; YLW=$'\e[33m'; RED=$'\e[31m'; RST=$'\e[0m'
head2() { printf "\n${BOLD}\e[36m%s${RST}\n" "$1"; }
ok()    { printf "  ${GRN}✓${RST} %s\n" "$1"; }
warn()  { printf "  ${YLW}!${RST} %s\n" "$1"; }
fail()  { printf "  ${RED}✗${RST} %s\n" "$1"; }

: "${AZURE_RESOURCE_GROUP:?set AZURE_RESOURCE_GROUP in .env.deploy}"
: "${MEDIA_SERVICE_FQDN:?set MEDIA_SERVICE_FQDN in .env.deploy}"

APP_NAME="${EAM_APP_NAME:-EntraGuard-ExternalAuthMethod}"
CERT_NAME="${EAM_SIGNING_CERT:-eam-signing}"

# The issuer MUST equal this exactly, everywhere: in the discovery document, in the `iss` of
# every token, and in what the tenant admin types. Microsoft lists the ways it goes wrong —
# an explicit :443, a trailing slash, a query string — and each fails every sign-in with a
# signature error naming none of them.
ISSUER="https://${MEDIA_SERVICE_FQDN}"
AUTHORIZE_URL="${ISSUER}/api/eam/authorize"
DISCOVERY_URL="${ISSUER}/.well-known/openid-configuration"

head2 "1. Multitenant app registration"

# Multitenant, because a provider serves many tenants and a single-tenant registration would
# make every consumer create their own. Microsoft's guidance is explicit that one multitenant
# application reduces per-tenant misconfiguration.
EAM_APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)

if [[ -z "$EAM_APP_ID" ]]; then
  EAM_APP_ID=$(az ad app create \
    --display-name "$APP_NAME" \
    --sign-in-audience AzureADMultipleOrgs \
    --query appId -o tsv)
  ok "Created ${APP_NAME} (${EAM_APP_ID})"
else
  ok "${APP_NAME} already exists (${EAM_APP_ID})"
fi

# The authorization endpoint must be a reply URL on this registration. Without it Entra
# refuses with AADSTS50161, "Failed to validate authorization url of external claims
# provider" — which does not mention reply URLs at all.
CURRENT_REPLIES=$(az ad app show --id "$EAM_APP_ID" --query "web.redirectUris" -o tsv 2>/dev/null || true)
if ! grep -qxF "$AUTHORIZE_URL" <<<"$CURRENT_REPLIES"; then
  MERGED=$(printf '%s\n%s\n' "$CURRENT_REPLIES" "$AUTHORIZE_URL" | awk 'NF' | sort -u)
  # shellcheck disable=SC2086
  az ad app update --id "$EAM_APP_ID" --web-redirect-uris $MERGED --output none
  ok "Added the authorization endpoint as a reply URL"
else
  ok "Reply URL already present"
fi

head2 "2. Signing certificate"

: "${EAM_KEYVAULT_URI:=$(az keyvault list -g "$AZURE_RESOURCE_GROUP" --query "[0].properties.vaultUri" -o tsv 2>/dev/null || true)}"

if [[ -z "${EAM_KEYVAULT_URI}" ]]; then
  fail "No Key Vault found. Run ./scripts/01-deploy-infra.sh first."
  exit 1
fi

VAULT_NAME="${EAM_KEYVAULT_URI#https://}"; VAULT_NAME="${VAULT_NAME%%.*}"
ok "Vault ${VAULT_NAME}"

if az keyvault certificate show --vault-name "$VAULT_NAME" --name "$CERT_NAME" >/dev/null 2>&1; then
  ok "Certificate ${CERT_NAME} already exists"
  warn "To ROTATE, add a version and wait before removing the old one — see below."
else
  # RS256 is the only algorithm Microsoft supports here, so RSA 2048 and SHA-256.
  POLICY=$(mktemp)
  cat > "$POLICY" <<'JSON'
{
  "issuerParameters": { "name": "Self" },
  "keyProperties": { "exportable": false, "keySize": 2048, "keyType": "RSA", "reuseKey": false },
  "secretProperties": { "contentType": "application/x-pkcs12" },
  "x509CertificateProperties": {
    "subject": "CN=EntraGuard External Authentication Method",
    "validityInMonths": 24,
    "keyUsage": ["digitalSignature"]
  }
}
JSON
  az keyvault certificate create \
    --vault-name "$VAULT_NAME" --name "$CERT_NAME" --policy "@$POLICY" --output none
  rm -f "$POLICY"
  ok "Created ${CERT_NAME} (RSA 2048, non-exportable, 24 months)"
fi

head2 "3. Configuration"

printf "  ${DIM}Add to .env.deploy and redeploy:${RST}\n\n"
printf "    EAM_KEYVAULT_URI=%s\n" "$EAM_KEYVAULT_URI"
printf "    EAM_CLIENT_ID=%s\n" "$EAM_APP_ID"
printf "    EAM_SIGNING_CERT=%s\n\n" "$CERT_NAME"

head2 "4. What the tenant admin does"

cat <<EOF
  ${DIM}Once per tenant that wants EntraGuard as a factor. Needs Entra ID P1 or P2.${RST}

  a. Consent to the application, as a Privileged Role Administrator:
     ${BOLD}https://login.microsoftonline.com/common/adminconsent?client_id=${EAM_APP_ID}${RST}

     ${DIM}Without consent, adding the method fails with AADSTS900491,
     "Service principal ${EAM_APP_ID} not found".${RST}

  b. Entra admin centre → Authentication methods → Add external method:
       Name          EntraGuard
       Client ID     ${EAM_APP_ID}
       Discovery URL ${DISCOVERY_URL}
       App ID        ${EAM_APP_ID}

  c. Conditional Access → new policy → Grant →
     ${BOLD}Require multifactor authentication${RST}

     ${DIM}NOT an authentication-strength grant. External methods do not satisfy
     authentication strengths, including the built-in MFA strength — a policy
     configured that way will never be satisfied and users will be locked out.${RST}

  ${BOLD}Reach is not coverage.${RST} This makes EntraGuard available to every application,
  but it still has to phone the user. Teams delivery needs that tenant to allow-list
  this Communication Services resource — see docs/teams-setup.md. A tenant that
  consents here but skips that gets a method that cannot call anybody.

${BOLD}Rotating the signing certificate${RST}

  The service publishes every enabled version and signs with the ${BOLD}oldest${RST}, so the
  order cannot be got wrong:

    1. az keyvault certificate create --vault-name ${VAULT_NAME} --name ${CERT_NAME} --policy @policy.json
       ${DIM}(published immediately; still not signing)${RST}
    2. Wait 48 hours for Entra's JWKS cache to turn over.
    3. az keyvault certificate set-attributes --vault-name ${VAULT_NAME} --name ${CERT_NAME} \\
         --version <old-version> --enabled false
       ${DIM}(disabling the old version is what promotes the new one)${RST}

  ${DIM}Doing step 3 early fails every sign-in behind the policy, in every tenant.${RST}
EOF

head2 "Verify"
printf "  curl -s %s | python3 -m json.tool\n" "$DISCOVERY_URL"
printf "  curl -s %s/.well-known/jwks | python3 -m json.tool\n\n" "$ISSUER"
