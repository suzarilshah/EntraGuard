# EntraGuard — current pitch

Reviewed 20 September 2026. Implementation details and limitations are in [README](../README.md) and [architecture](architecture.md).

## The problem

A valid credential does not tell you whether the person using it is being coached. Social engineering can persuade an employee to approve an authentication they would otherwise reject.

**EntraGuard brings conversation evidence into an application’s verification decision.** It can place a verification call and monitor that call for manipulation, or analyze incoming calls deliberately routed through a monitored ACS identity.

## The demonstration

1. Sign into **Contoso Treasury** with Microsoft Entra ID.
2. Receive a Teams or ACS verification call and match the number shown on screen.
3. Answer questions selected from available sign-in, directory and recent activity sources; optionally compare the caller's voice with an enrolled profile.
4. If coaching is detected with sufficient confidence, the correct code is not enough: verification can be refused.
5. Inspect the result in Treasury's session-security view and the operator's telemetry.

Treasury has a polished payment workspace with search, filters, record details and export. Those payments are explicitly illustrative. The prototype demonstrates access to a protected application, not integration with a real banking ledger.

## What differentiates it

### 1. The model supplies evidence; code controls authority

The Analyst produces risk, confidence, quotes and compliance stage. The deterministic Policy Gate decides permitted remediation. The verification adjudicator separately decides code/coercion/voice outcomes. Pure rules are testable, but their correctness still depends on requirements and input evidence; they are not infallible.

### 2. A verification channel that observes the conversation

Number matching relates the call to the browser. Knowledge questions add context. Voice comparison measures speaker similarity. Coercion analysis looks for manipulation. These are distinct signals, not a claim to four independent certified authentication factors.

### 3. Explain what happened and what was missing

Risk and assurance answer different questions. The service reports outcomes and gaps, including unavailable dependencies. Treasury displays the actual verification response; settings expose enrollment choices and recent attempts.

### 4. Azure-hosted, with explicit component boundaries

ACS supplies call automation, AI Speech supplies recognition, Azure OpenAI supplies analysis, Graph supplies identity/activity context, and Sentinel receives telemetry. Four Container Apps host media, operator portal, Treasury and an internal **open-source SpeechBrain/PyTorch** voice scorer. Azure access primarily uses managed identity; encrypted voice templates still require application key material.

## Claims to keep bounded

- Only monitored/routed calls are analyzed; this is not universal interception of Teams or PSTN.
- The External Authentication Method is implemented and off until configured. It has not yet been exercised against a live Conditional Access policy in a second tenant, so treat tenant-wide reach as built rather than proven.
- No P2 means no P2 risk elevation. Quarantine also needs an actual Conditional Access policy.
- The voice model has no PAD or real-time clone detection; default mode observes scores.
- A successful verification is not proof that coercion was absent.
- Authenticated tenant isolation, durable state, source-aware assurance and transaction binding are production work.
- The optional realtime agent is constrained to supplied prompts; a freely improvising agent is not the current product.

## Evidence, not guarantees

Previous scripted `gpt-5-mini` runs recorded attack peak risk 100 versus benign/ambiguous controls at 15/10, and mean analyst latency 6.9 seconds. These are historical sample results, not independently revalidated accuracy or latency commitments. Use a current live rehearsal to demonstrate current behavior.

## Next product milestone

Build trusted relying-party sessions and durable verification receipts, reconcile assurance/readiness with the full question pool, then add tenant policy and transaction-bound step-up. These changes let the existing Treasury experience become a real authorization workflow rather than only a compelling demonstration. See [backend roadmap](backend-roadmap.md).
