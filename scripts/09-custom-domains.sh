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

  if az containerapp hostname list -n "$APP" -g "$RG" --query "[?name=='$HOST']" -o tsv 2>/dev/null | grep -q .; then
    ok "$HOST already bound to $APP"
  else
    # bind creates the managed certificate and attaches it in one step. Free, and renewed
    # by Azure — which is the reason to leave the record DNS-only rather than re-proxying
    # after the first issue.
    az containerapp hostname bind \
      --name "$APP" --resource-group "$RG" \
      --hostname "$HOST" --environment "$ENV_NAME" \
      --validation-method CNAME --output none 2>/dev/null \
    || az containerapp hostname bind \
      --name "$APP" --resource-group "$RG" \
      --hostname "$HOST" --environment "$ENV_NAME" \
      --validation-method HTTP --output none
    ok "$HOST bound to $APP"
  fi
done

head2 "Verify"
for MAP in "${READY[@]}"; do
  HOST="${MAP##*|}"
  printf "  curl -sI https://%s | head -1\n" "$HOST"
done
printf "\n  ${DIM}A certificate can take a few minutes to issue. Until it does, the hostname\n"
printf "  answers with a TLS warning rather than an error.${RST}\n\n"
