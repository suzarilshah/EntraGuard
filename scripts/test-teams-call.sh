#!/usr/bin/env bash
#
# Place one real verification call to the Teams demo account and report what happened.
#
# Exists because "did the Teams path work?" was, for most of this build, only answerable by
# a human holding a phone. This answers the part a script can answer: whether the call was
# accepted, answered, streamed audio, and how it ended — including the ACS diagnostic code
# when it failed.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
source "${REPO_ROOT}/.env.deploy"

BOLD=$'\e[1m'; DIM=$'\e[2m'; GRN=$'\e[32m'; YLW=$'\e[33m'; RED=$'\e[31m'; RST=$'\e[0m'

MEDIA="https://${MEDIA_SERVICE_FQDN}"

printf "\n${BOLD}Teams verification call${RST}\n"
printf "  ${DIM}%s → %s${RST}\n\n" "$ACS_NAME" "$TEAMS_UPN"

RESPONSE=$(curl -fsS -X POST "${MEDIA}/api/verify/start" \
  -H 'Content-Type: application/json' \
  -d "{\"upn\":\"${TEAMS_UPN}\",\"teamsUserId\":\"${TEAMS_OBJECT_ID}\",\"applicationName\":\"Contoso Treasury\"}")

ID=$(printf '%s' "$RESPONSE" | python3 -c 'import sys,json; print(json.load(sys.stdin)["verificationId"])')
CODE=$(printf '%s' "$RESPONSE" | python3 -c 'import sys,json; print(json.load(sys.stdin)["matchCode"])')

printf "  ${GRN}✓${RST} call placed — ${BOLD}match code %s${RST}\n" "$CODE"
printf "  ${DIM}%s${RST}\n\n" "$ID"
printf "  Answer in Teams and enter ${BOLD}%s${RST} on the dial pad. Watching for 90s:\n\n" "$CODE"

LAST=""
for _ in $(seq 1 45); do
  LINE=$(curl -fsS "${MEDIA}/api/verify/${ID}" | python3 -c '
import sys, json
d = json.load(sys.stdin); v = d["verification"]; m = d.get("media") or {}
print("{:<14} {:<12} stream={:<5} audio={:<6} dtmf={}".format(
    v["callState"], v["result"],
    str(m.get("streamConnected", False)), m.get("audioFrames", 0), m.get("dtmfReceived", 0)))
')
  [ "$LINE" != "$LAST" ] && { printf "    %s\n" "$LINE"; LAST="$LINE"; }

  DONE=$(curl -fsS "${MEDIA}/api/verify/${ID}" | python3 -c 'import sys,json; print(json.load(sys.stdin)["verification"]["isComplete"])')
  [ "$DONE" = "True" ] && break
  sleep 2
done

printf "\n"
curl -fsS "${MEDIA}/api/verify/${ID}" | python3 -c '
import sys, json
v = json.load(sys.stdin)["verification"]
ok  = v["result"] == "Passed"
mark = "\033[32m✓\033[0m" if ok else "\033[31m✗\033[0m"
print(f"  {mark} \033[1m{v[\"result\"]}\033[0m")
print(f"    {v[\"reason\"]}")
print(f"    attempts={v[\"attempts\"]}  peak risk={v[\"peakRiskDuringCall\"]}  {v[\"durationMs\"]}ms")
'
printf "\n"
