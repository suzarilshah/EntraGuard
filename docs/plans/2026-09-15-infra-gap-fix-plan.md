# Fixing the telemetry gap, and the landmine under it

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Get the five follow-up columns into Log Analytics without taking the demo down,
and repair the reason taking it down was ever a risk.

**Status:** The columns are declared in `infra/modules/observability.bicep` and committed. The
running workspace does not have them, so probe telemetry is written and discarded at
ingestion with no error. `EntraGuard_Verification_CL` currently has 23 columns and none of
`FollowUps`, `FollowUpsAsked`, `FollowUpsConfirmed`, `FollowUpsUnanswered`, `Register`.

---

## The landmine — read this before running any infra script

`scripts/01-deploy-infra.sh` **cannot run today**, and that is the only thing protecting the
demo.

It reads the running container images and passes them as `mediaServiceImage=` and
`portalImage=` so an infrastructure change never reverts the apps. `infra/main.bicep` no
longer declares those parameters. `grep -n "Image" infra/main.bicep` returns nothing. So the
script fails with:

```
ERROR: unrecognized template parameter 'mediaServiceImage'.
Allowed parameters: appName, environmentName, location, openAiCapacity,
openAiModelName, openAiModelVersion, openAiSkuName, operatorObjectId, tags
```

It fails closed, which is luck rather than design. The obvious way to "fix" that error is to
drop the two parameters — and `az deployment sub what-if` says what happens then:

| Container app | Current image | After |
|---|---|---|
| `ca-entraguard-media` | `entraguard-media:20260914062625` | `mcr.microsoft.com/k8se/quickstart:latest` |
| `ca-entraguard-portal` | `entraguard-portal:20260914062625` | `mcr.microsoft.com/k8se/quickstart:latest` |
| `ca-contoso-treasury` | `entraguard-portal:20260914062625` | `mcr.microsoft.com/k8se/quickstart:latest` |
| `ca-entraguard-voiceprint` | `entraguard-voiceprint:20260810205641` | `mcr.microsoft.com/k8se/quickstart:latest` |

All four, because `compute.bicep` defaults every image to the placeholder and `main.bicep`
passes none of them. The deployment reports success. This is precisely the failure the
script's own comment describes preventing, and the guard against it is now dead code.

**Voiceprint is the worst of the four.** `deploy-apps.sh` skips it unless
`VOICEPRINT=rebuild`, so a normal redeploy would not bring it back — and the script never
preserved it in the first place, so even a working guard would have lost it.

---

## Task 1: Get the columns in, touching nothing else

Deploy the observability module on its own. It has no dependency on `compute.bicep`, so the
images cannot be affected by it at all.

**Verified by `az deployment group what-if`:** 8 Modify, 0 Create, 0 Delete, no container
apps in scope. The only substantive deltas are columns 23-27 added to
`EntraGuard_Verification_CL` and to the DCR's `Custom-EntraGuard_Verification_CL` stream.
Nothing is removed. `plan: 'Analytics' -> None` and `isTroubleshootingAllowed: True -> None`
on the other tables are what-if noise for properties the template does not declare.

**Step 1: Dry run again and read it, rather than trusting this document**

```bash
az deployment group what-if -g rg-entraguard-demo \
  --name "obs-$(date +%s)" \
  --template-file infra/modules/observability.bicep \
  --parameters appName=entraguard environmentName=demo location=eastus \
      uniqueSuffix=<suffix> \
      ingestorPrincipalId=8b1e5b4e-0bd7-464f-af98-b9946fe712c7 \
      operatorObjectId="$(az ad signed-in-user show --query id -o tsv)" \
      tags='{}'
```

`tags='{}'` matches the live workspace, which carries no tags. Passing `main.bicep`'s
defaults instead would tag every resource in the module — harmless, but it is a change
nobody asked for and it would clutter the diff.

Expected: no `Create`, no `Delete`, and no resource whose id contains `containerApps`.
**If any container app appears, stop.**

**Step 2: Apply**

Same command with `create` instead of `what-if`.

**Step 3: Verify the columns exist**

```bash
az monitor log-analytics workspace table show \
  -g rg-entraguard-demo --workspace-name log-entraguard-demo \
  -n EntraGuard_Verification_CL --query "schema.columns[].name" -o tsv | sort
```

Expected: 28 names including `FollowUps`, `FollowUpsAsked`, `FollowUpsConfirmed`,
`FollowUpsUnanswered`, `Register`.

**Step 4: Verify ingestion end to end, which the schema alone does not prove**

A declared column still drops silently if the DCR stream declaration disagrees with it.

```bash
curl -s -X POST "https://$MEDIA_SERVICE_FQDN/api/verify/simulate" \
  -H 'Content-Type: application/json' \
  -d '{"scenario":"pass","upn":"column.check@contoso.com"}'
```

Then, after a few minutes — Log Analytics ingestion is not instant, and an empty result
before then means nothing:

```kusto
EntraGuard_Verification_CL
| where SubjectUpn == "column.check@contoso.com"
| project TimeGenerated, Register, FollowUpsAsked, FollowUps
```

Expected: a row with `Register` = `Warm` and `FollowUpsAsked` = 0. A row whose `Register`
is blank means the DCR is still dropping the column.

---

## Task 2: Restore the image plumbing in main.bicep

Until this is done, `01-deploy-infra.sh` is a loaded gun.

**Files:** `infra/main.bicep`

**Step 1:** Add three parameters, each defaulting to the placeholder so a genuine first
deploy still works:

```bicep
@description('''
Image for the media service. Defaults to the Microsoft placeholder because on a FIRST deploy
the real image does not exist yet.

On a redeploy the default is destructive: it reverts a running application to a sample app
while reporting success. 01-deploy-infra.sh reads the running image and passes it here for
exactly that reason, and passed it to a parameter that had been removed — so the script
failed outright, and the obvious way to make it run again was to drop the argument and lose
all four applications.
''')
param mediaServiceImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param portalImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param voiceprintImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
```

**Step 2:** Pass all three in the `compute` module block, next to `tags`.

**Step 3:** Confirm the fix with what-if, passing the current images. Every container app
should read `NoChange`:

```bash
az deployment sub what-if --location eastus --template-file infra/main.bicep \
  --parameters appName=entraguard environmentName=demo location=eastus \
      openAiModelName=gpt-5-mini openAiModelVersion=2025-08-07 openAiSkuName=GlobalStandard \
      mediaServiceImage="$(az containerapp show -n ca-entraguard-media -g rg-entraguard-demo --query 'properties.template.containers[0].image' -o tsv)" \
      portalImage="$(az containerapp show -n ca-entraguard-portal -g rg-entraguard-demo --query 'properties.template.containers[0].image' -o tsv)" \
      voiceprintImage="$(az containerapp show -n ca-entraguard-voiceprint -g rg-entraguard-demo --query 'properties.template.containers[0].image' -o tsv)"
```

---

## Task 3: Make the script preserve all four apps, and fail rather than guess

**Files:** `scripts/01-deploy-infra.sh`

Two defects beyond the missing parameters:

**It never preserved voiceprint.** Only media and portal are read. Add it.

**It cannot tell "no such app" from "could not read".** Both currently collapse to an empty
string, which omits the parameter, which silently selects the placeholder. A transient ARM
error during a redeploy therefore reverts a live application. Distinguish them: if the app
exists and its image cannot be read, abort. Treasury runs the portal image and is not
separately parameterised, so it follows `portalImage`.

**Step: add a guard that cannot be argued with.** Before `az deployment sub create`, run
`what-if` and refuse to proceed if any container app image would become the placeholder:

```bash
if az deployment sub what-if ... --no-pretty-print 2>/dev/null \
   | grep -q '"after": "mcr.microsoft.com/k8se/quickstart'; then
  echo "  REFUSING: this deployment would revert a container app to the placeholder." >&2
  exit 1
fi
```

A comment describing a hazard is worth what its test is worth, and this one had a comment and
no test for however long the parameters have been missing.

---

## Task 4: Recovery, if someone has already run it

```bash
./scripts/deploy-apps.sh                      # media, portal, treasury
VOICEPRINT=rebuild ./scripts/deploy-apps.sh   # voiceprint — NOT rebuilt by the line above
```

---

## Done when

- `EntraGuard_Verification_CL` lists 28 columns.
- A simulated verification appears in KQL with `Register` populated.
- `what-if` on `main.bicep` with current images reports `NoChange` for all four apps.
- `01-deploy-infra.sh` runs to completion without the unrecognized-parameter error.
