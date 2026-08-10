#!/usr/bin/env bash
#
# Measure whether the speaker model separates people, and where the thresholds belong.
#
# Two sources of evidence, in order of how much they are worth:
#
#   1. SYNTHETIC — the calibrate endpoint synthesises several Azure neural voices, treats
#      each as a speaker, and scores same-speaker pairs against different-speaker pairs.
#      Runs in a minute and needs nobody. It is an OPTIMISTIC bound: synthesised voices are
#      cleaner than telephony and more distinct from one another than two colleagues who
#      share an accent.
#
#   2. REAL — scores recorded from actual verification calls, in EntraGuard_Verification_CL.
#      This is the evidence that should actually move a threshold. It accumulates only while
#      VOICE_MODE=observe, which is why observe is the default.
#
# Thresholds copied from a published benchmark would be measured on studio recordings of
# other people; used here they would refuse real users. This is how they get earned.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
source "${REPO_ROOT}/.env.deploy"

BOLD=$'\e[1m'; DIM=$'\e[2m'; GRN=$'\e[32m'; YLW=$'\e[33m'; RST=$'\e[0m'

printf "\n${BOLD}Voice threshold calibration${RST}\n\n"

printf "${BOLD}1. Synthetic separation${RST} ${DIM}(optimistic bound)${RST}\n"
curl -fsS -X POST "https://${MEDIA_SERVICE_FQDN}/api/voice-profile/calibrate" --max-time 300 \
  | python3 -c '
import sys, json
d = json.load(sys.stdin)
if not d.get("ran"):
    print("   could not run:", d.get("detail")); raise SystemExit
r = d["recommendation"]
if not r.get("usable"):
    print("   ", r.get("detail")); raise SystemExit
g, i = r["genuine"], r["impostor"]
sep = "separated" if r["separated"] else "OVERLAPPING"
print("   speakers    {}".format(len(d["speakers"])))
print("   genuine     n={:<3} min={:.3f} mean={:.3f} max={:.3f}".format(g["count"], g["min"], g["mean"], g["max"]))
print("   impostor    n={:<3} min={:.3f} mean={:.3f} max={:.3f}".format(i["count"], i["min"], i["mean"], i["max"]))
print("   margin      {:.3f}  ({})".format(r["margin"], sep))
print("   suggests    accept={}  reject={}".format(r["recommendedAccept"], r["recommendedReject"]))
'

printf "\n${BOLD}2. Real calls${RST} ${DIM}(what should actually set the threshold)${RST}\n"
TOKEN=$(az account get-access-token --resource https://api.loganalytics.io --query accessToken -o tsv)
QUERY='EntraGuard_Verification_CL
| where TimeGenerated > ago(30d)
// isnotnull, not "!= 0": rows written before the VoiceScore column existed carry null,
// and null passes an inequality in KQL — they showed up as counted rows with NaN scores,
// which reads as "we measured something" when nothing was measured.
| where isnotnull(VoiceScore) and VoiceOutcome != "NotAssessed"
| summarize n=count(), min=round(min(VoiceScore),3), mean=round(avg(VoiceScore),3), max=round(max(VoiceScore),3) by Result'

curl -fsS -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' \
  "https://api.loganalytics.io/v1/workspaces/${LAW_WORKSPACE_ID}/query" \
  -d "$(python3 -c "import json,sys;print(json.dumps({'query':sys.argv[1]}))" "$QUERY")" \
  | python3 -c '
import sys, json
t = json.load(sys.stdin)["tables"][0]
if not t["rows"]:
    print("   no scored calls yet — run verifications in observe mode first")
    print("   (a threshold set without this is still a guess)")
else:
    print("   " + "  ".join(c["name"] for c in t["columns"]))
    for r in t["rows"]:
        print("   " + "  ".join(str(v) for v in r))
'

printf "\n${YLW}Before enabling enforcement${RST}\n"
printf "  Genuine scores from real calls must sit clearly above impostor ones. Until an\n"
printf "  impostor has actually been recorded, there is no measured false-accept rate and\n"
printf "  no basis for enforcing anything.\n\n"
printf "  Then: ${DIM}VOICE_MODE=enforce VOICE_ACCEPT=<x> VOICE_REJECT=<y> ./scripts/deploy-apps.sh${RST}\n\n"
