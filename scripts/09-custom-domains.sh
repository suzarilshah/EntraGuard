#!/usr/bin/env bash
#
# Attach custom domains to the Container Apps, with free managed certificates.
#
# Prints the exact DNS records first, checks they are in place, and only then binds. It
# will not bind a hostname whose DNS is proxied, because a proxied record cannot pass
# Azure's certificate validation and the failure is confusing rather than obvious.
#
# Safe to re-run. Anything already bound is left alone.
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
RG="$AZURE_RESOURCE_GROUP"
ENV_NAME="${CONTAINER_APP_ENVIRONMENT:-cae-entraguard-demo}"

# app|hostname
MAPPINGS=(
  "ca-contoso-treasury|${TREASURY_DOMAIN:-placeholders.my}"
  "ca-entraguard-portal|${PORTAL_DOMAIN:-entraguard.my}"
  "ca-entraguard-docs|${DOCS_DOMAIN:-docs.entraguard.my}"
  "ca-entraguard-media|${MEDIA_DOMAIN:-api.entraguard.my}"
)

STATIC_IP=$(az containerapp env show -n "$ENV_NAME" -g "$RG" --query "properties.staticIp" -o tsv)

head2 "1. DNS records to create"

printf "  ${DIM}Azure verifies ownership with a TXT record and routes by Host header.\n"
printf "  An apex domain needs an A record; a subdomain should use a CNAME.${RST}\n\n"

for MAP in "${MAPPINGS[@]}"; do
  APP="${MAP%%|*}"; HOST="${MAP##*|}"
  VERIFY=$(az containerapp show -n "$APP" -g "$RG" --query "properties.customDomainVerificationId" -o tsv 2>/dev/null || true)
  FQDN=$(az containerapp show -n "$APP" -g "$RG" --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || true)
  [[ -z "$VERIFY" ]] && { warn "$APP not found — skipping $HOST"; continue; }

  # An apex has no dot beyond the registrable name; anything longer is a subdomain.
  if [[ "$(grep -o '\.' <<<"$HOST" | wc -l | tr -d ' ')" -le 1 ]]; then
    printf "  ${BOLD}%s${RST}  → %s\n" "$HOST" "$APP"
    printf "    TXT    asuid            %s\n" "$VERIFY"
    printf "    A      @                %s\n" "$STATIC_IP"
  else
    SUB="${HOST%%.*}"
    printf "  ${BOLD}%s${RST}  → %s\n" "$HOST" "$APP"
    printf "    TXT    asuid.%-10s %s\n" "$SUB" "$VERIFY"
    printf "    CNAME  %-16s %s\n" "$SUB" "$FQDN"
  fi
  printf "    ${DIM}Proxy status: DNS only (grey cloud)${RST}\n\n"
done

head2 "2. Checking DNS"

READY=()
for MAP in "${MAPPINGS[@]}"; do
  APP="${MAP%%|*}"; HOST="${MAP##*|}"
  VERIFY=$(az containerapp show -n "$APP" -g "$RG" --query "properties.customDomainVerificationId" -o tsv 2>/dev/null || true)
  [[ -z "$VERIFY" ]] && continue

  if [[ "$(grep -o '\.' <<<"$HOST" | wc -l | tr -d ' ')" -le 1 ]]; then
    TXT_NAME="asuid.${HOST}"
  else
    TXT_NAME="asuid.${HOST}"
  fi

  TXT=$(dig +short TXT "$TXT_NAME" 2>/dev/null | tr -d '"' | head -1)
  IP=$(dig +short A "$HOST" 2>/dev/null | head -1)

  # Cloudflare's proxy terminates TLS, so Azure's certificate validation never reaches this
  # service and the managed certificate cannot be issued. The record has to be DNS-only at
  # least until the certificate exists — and, because managed certificates renew
  # automatically, it is simplest to leave it DNS-only.
  PROXIED=false
  case "$IP" in 104.16.*|104.17.*|104.18.*|104.19.*|104.20.*|104.21.*|172.64.*|172.65.*|172.66.*|172.67.*|188.114.*|162.159.*|198.41.*) PROXIED=true;; esac

  if [[ -z "$TXT" ]]; then
    fail "$HOST — no TXT at ${TXT_NAME}"
  elif [[ "$TXT" != "$VERIFY" ]]; then
    fail "$HOST — TXT does not match the verification ID"
  elif [[ "$PROXIED" == true ]]; then
    fail "$HOST — resolves to a Cloudflare proxy IP (${IP})."
    printf "      ${DIM}Turn the record to DNS only. A proxied record cannot pass Azure's\n"
    printf "      certificate validation, and the error will not say so.${RST}\n"
  else
    ok "$HOST — DNS ready"
    READY+=("$MAP")
  fi
done

if [[ ${#READY[@]} -eq 0 ]]; then
  printf "\n  ${BOLD}Nothing to bind yet.${RST} Create the records above, then run this again.\n"
  printf "  ${DIM}DNS changes can take a few minutes to become visible.${RST}\n\n"
  exit 0
fi

head2 "3. Binding hostnames and issuing certificates"

for MAP in "${READY[@]}"; do
  APP="${MAP%%|*}"; HOST="${MAP##*|}"

  # An apex is reached by an A record, so there is no CNAME for Azure to follow and CNAME
  # validation cannot succeed. HTTP validation works for both, and for an apex it is the
  # only one that does.
  if [[ "$(grep -o '\.' <<<"$HOST" | wc -l | tr -d ' ')" -le 1 ]]; then
    METHOD="HTTP"
  else
    METHOD="CNAME"
  fi

  # ADD first, then BIND. They are two operations and the order is not optional: Azure
  # refuses to create a managed certificate for a hostname the environment has never been
  # told about — "RequireCustomHostnameInEnvironment", which reads like a precondition on
  # DNS and is in fact a precondition on this script.
  if az containerapp hostname list -n "$APP" -g "$RG" --query "[?name=='$HOST']" -o tsv 2>/dev/null | grep -q .; then
    ok "$HOST already added to $APP"
  else
    if az containerapp hostname add --name "$APP" --resource-group "$RG" \
         --hostname "$HOST" --output none 2>/tmp/eg-hostname.err; then
      ok "$HOST added to $APP"
    else
      fail "$HOST — could not add: $(tail -1 /tmp/eg-hostname.err | cut -c1-120)"
      continue
    fi
  fi

  # The certificate is separate, free, and renewed by Azure — which is why the record
  # should stay DNS-only rather than being re-proxied once the first one issues.
  if az containerapp hostname list -n "$APP" -g "$RG" \
       --query "[?name=='$HOST' && bindingType=='SniEnabled']" -o tsv 2>/dev/null | grep -q .; then
    ok "$HOST already has a certificate"
  else
    printf "  ${DIM}  issuing a certificate for %s (up to 20 minutes)…${RST}\n" "$HOST"
    if az containerapp hostname bind --name "$APP" --resource-group "$RG" \
         --hostname "$HOST" --environment "$ENV_NAME" \
         --validation-method "$METHOD" --output none 2>/tmp/eg-bind.err; then
      ok "$HOST bound with a managed certificate"
    else
      warn "$HOST added, certificate not issued yet: $(tail -1 /tmp/eg-bind.err | cut -c1-110)"
    fi
  fi
done

head2 "4. Sign-in redirect URIs"

# Binding a hostname is half the job. MSAL asks Entra to return the user to
# `${origin}/app`, and Entra refuses any origin the registration has not been told about —
# AADSTS50011, which names the URI it rejected but not the fact that nothing added it.
#
# Merged, never overwritten: the localhost and Container Apps entries stay, because local
# development and the FQDNs both still have to work.
if [[ -z "${ENTRA_RP_CLIENT_ID:-}" ]]; then
  warn "ENTRA_RP_CLIENT_ID is not set — skipping redirect URIs."
else
  EXISTING=$(az ad app show --id "$ENTRA_RP_CLIENT_ID" --query "spa.redirectUris" -o tsv 2>/dev/null || true)
  WANTED=()
  for MAP in "${MAPPINGS[@]}"; do
    APP="${MAP%%|*}"; HOST="${MAP##*|}"
    # Only hostnames that actually serve a sign-in page, and only once bound.
    [[ "$APP" == "ca-entraguard-media" || "$APP" == "ca-entraguard-docs" ]] && continue
    az containerapp hostname list -n "$APP" -g "$RG" \
      --query "[?name=='$HOST' && bindingType=='SniEnabled']" -o tsv 2>/dev/null | grep -q . || continue
    WANTED+=("https://${HOST}/app")
  done

  MISSING=()
  for URI in ${WANTED[@]+"${WANTED[@]}"}; do
    grep -qxF "$URI" <<<"$EXISTING" || MISSING+=("$URI")
  done

  if [[ ${#MISSING[@]} -eq 0 ]]; then
    ok "Redirect URIs already cover every bound hostname"
  else
    MERGED=$(printf '%s\n' ${EXISTING:+$EXISTING} "${MISSING[@]}" | awk 'NF' | sort -u)

    # PATCHed through Graph rather than `az ad app update`, which has flags for web and
    # public-client redirect URIs and none for the SPA block these live in. --set on a
    # nested array silently does not do what it looks like it does.
    OBJ=$(az ad app show --id "$ENTRA_RP_CLIENT_ID" --query id -o tsv)
    BODY=$(printf '%s\n' $MERGED | python3 -c 'import sys,json; print(json.dumps({"spa":{"redirectUris":[l.strip() for l in sys.stdin if l.strip()]}}))')
    if az rest --method PATCH \
         --url "https://graph.microsoft.com/v1.0/applications/${OBJ}" \
         --headers "Content-Type=application/json" \
         --body "$BODY" --output none 2>/tmp/eg-redirect.err; then
      for URI in "${MISSING[@]}"; do ok "Added $URI"; done
      printf "  ${DIM}A new token is needed for the change to take effect — sign out and in again.${RST}\n"
    else
      fail "Could not update the redirect URIs ($(tail -1 /tmp/eg-redirect.err | cut -c1-90)). Add by hand:"
      for URI in "${MISSING[@]}"; do printf "      %s\n" "$URI"; done
    fi
  fi
fi

head2 "After the media service has a certificate"

cat <<'EOF'
  The media hostname is the only one that is more than cosmetic. PUBLIC_BASE_URL is what
  ACS callback URLs, the media WebSocket URL and the External Authentication Method's
  issuer are all built from, so moving it changes what Entra must be configured with.

    1. Confirm https://api.entraguard.my/health/ready answers 200 with a valid certificate.
    2. Set PUBLIC_BASE_URL=https://api.entraguard.my in .env.deploy and redeploy.
    3. Re-run ./scripts/04-eventgrid-subscribe.sh so the webhook points at the new host.
    4. If the EAM method is already configured in a tenant, its discovery URL and issuer
       BOTH change. Update the method in that tenant, or sign-ins fail with AADSTS50012 —
       the issuer must match character for character.

  Do this before adding the method to any tenant, not after.
EOF

head2 "Verify"
for MAP in "${READY[@]}"; do
  HOST="${MAP##*|}"
  printf "  curl -sI https://%s | head -1\n" "$HOST"
done
printf "\n  ${DIM}A certificate can take a few minutes to issue. Until it does, the hostname\n"
printf "  answers with a TLS warning rather than an error.${RST}\n\n"
