# EntraGuard — five-minute pitch

Six slides · about 4 minutes 45 seconds of speech, plus transitions. Add a live demo only if your slot permits it.

## 1. The code can be right. The situation can be wrong.

**00:00–00:25 · 25 seconds**

Imagine an employee enters the right MFA code while a convincing caller tells them exactly what to do. The credential is valid. The situation is not. EntraGuard brings evidence from that conversation into the verification decision.

**Presenter cue:** Pause after “the situation is not.” The number and quotation are an illustrative scenario, not a recording of a real victim.

**Evidence and Q&A:** Grounded in the repository’s outbound verification and coercion-adjudication flow. Do not describe EntraGuard as proof of intent or a guarantee that coercion is absent.

## 2. A valid sign-in can hide an unsafe conversation.

**00:25–01:05 · 40 seconds**

The gap is not just another stolen password. A help-desk impersonator, a caller coaching an MFA approval, or someone pressuring a payment change can work through the legitimate user. Identity controls see the authentication. EntraGuard adds conversation evidence at the moment an approval is being made.

**Presenter cue:** Point to one scenario that resonates with the judges. Do not spend time reading all three quotations.

**Evidence and Q&A:** Scam taxonomy in src/EntraGuard.Shared/Detection/ScamVector.cs. Scope is calls deliberately routed through monitored ACS identities and calls EntraGuard originates. It does not listen to every phone or Teams call. No breach statistics are asserted.

## 3. One call. More context. A governed decision.

**01:05–02:00 · 55 seconds**

The user signs in with a Microsoft work account and receives a verification call. They match a number and answer questions drawn from the identity or activity sources available to their tenant. During the call, the Analyst looks for coaching. Optional voice comparison adds speaker-similarity evidence. The result is governed: access, an additional check, or refusal—with an explanation.

**Presenter cue:** Emphasize “available” sources and “optional” voice. These are distinct signals, not four independently certified authentication factors.

**Evidence and Q&A:** VerificationEndpoint / VerificationLauncher / VerificationCoordinator, EvidenceAssurance and VerificationAdjudicator. Coercion refusal threshold: risk ≥60 and confidence ≥0.75. Voice observation is the default. Enforced weak voice matches require server-validated fresh MFA; no voice-clone or PAD claim.

## 4. Fits the Microsoft stack. Extends the decision.

**02:00–03:05 · 65 seconds**

There are two integration routes. An application such as Treasury can explicitly request step-up after Entra sign-in. The optional External Authentication Method uses OIDC to fit into Entra’s MFA flow, when configured by a tenant. Both use the call pipeline: ACS and Teams, AI Speech, and Azure OpenAI. Deterministic rules govern the result. The appropriate flow can return an application decision, execute permitted Graph containment, and send evidence to Microsoft Sentinel.

**Presenter cue:** Trace the diagram left to right once. Say “optional and configuration-dependent” when pointing to EAM. Distinguish verification decisions from containment actions on monitored attack calls.

**Evidence and Q&A:** Sources: README.md; docs/architecture.md; docs/external-auth-method.md; infra/modules/*.bicep. EAM is implemented but not established as a live-validated cross-tenant rollout by this deck. Requires tenant configuration/licensing and deliverable calls. Voice scorer is open-source SpeechBrain/PyTorch on Azure, not a Microsoft biometric service. Managed identity is used for Azure service access; encryption/signing keys still exist.

## 5. See why access was granted. Or why it was refused.

**03:05–04:10 · 65 seconds**

This is Contoso Treasury, our relying-party demonstration. What matters is not just the risk meter. The model supplies evidence while policy owns authority. A demo-payment approval can require verification bound to the exact payment details. And the outcome is explainable through receipts, policy versions and owner-scoped history. The decisive demo beat is a correct number match that is still refused when coaching is detected.

**Presenter cue:** The screenshot is the Treasury prototype with illustrative data. For a five-minute slot, show a short rehearsed recording or stay on this slide; a full verification call can take substantially longer. In a longer slot, switch to a prepared benign/coerced pair and show the actual returned receipt. Never promise a particular live model verdict.

**Evidence and Q&A:** Sources: TreasuryDashboard.tsx; GrantService.cs; PaymentService.cs; VerificationLedger.cs; TenantPolicyService.cs. Approval is implemented for a protected demo ledger, not a bank connector. Local tests cover ownership, replay, concurrency and policy checks; local tests are not proof of live Azure/Entra deployment readiness. The screenshot demonstrates UI design, not the completion of a specific transaction.

## 6. Protect the approval. Not just the credential.

**04:10–04:45 · 35 seconds, then pause for questions**

We have built the verification pipeline and the trust controls around it: sessions, durable receipts, policy and transaction-bound demo approvals. The next step is a controlled tenant pilot. We want to measure completion, false refusals and time to intervention on real calling channels. We are looking for an identity or SOC partner and a test tenant. Protect the approval—not just the credential.

**Presenter cue:** End on the ask. The QR code opens the public handbook. Keep any live-demo segment separate from this five-minute pitch. Do not call the prototype production-ready.

**Evidence and Q&A:** Q&A: This is not voice-clone detection; ECAPA compares speaker similarity and has no PAD. It does not monitor every call. Tenant policies and permissions constrain actions. EAM is opt-in and has deployment prerequisites. Demo approvals move no funds. Media remains single-replica until distributed live-call routing is implemented. Repository sources: docs/security-migration.md, docs/standards-and-threat-model.md, docs/external-auth-method.md. No unvalidated breach, accuracy, latency or ROI statistic appears on these slides.
