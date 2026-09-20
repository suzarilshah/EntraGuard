# EntraGuard — 1:55 hackathon video script

**Target: 115 seconds; hard maximum: 120 seconds.** 175 narration words. This is a storyboard until real demo footage is supplied.

## Recommended edit

| Time | Picture | Purpose |
|---|---|---|
| 00:00–00:10 | The code is right. The situation isn't. | Use this title slide. No claim of a recorded incident. |
| 00:10–00:18 | Verify the person. Assess the pressure. | Use the solution slide; cut before it turns into a feature inventory. |
| 00:18–00:48 | A familiar sign-in. A richer verification. | Replace the entire storyboard slate with recorded sign-in, number match, one question and the actual result. Keep 4–6 seconds of original call audio. |
| 00:48–01:28 | Correct digits. Coaching still matters. | Replace with the controlled coaching test. Show correct code, a short coaching excerpt, actual assessment, returned result and reason. Keep 6–8 seconds of original audio. If no refusal occurred, revise the story rather than inventing a result. |
| 01:28–01:45 | AI supplies evidence. Code owns the decision. | Use this simplified architecture slide instead of the dense engineering diagrams. |
| 01:45–01:55 | Protect the approval. Not just the credential. | Use this closing slide. Leave a short clean tail; do not add a long logo animation. |

## Read-aloud script

### 0.4–9.7 seconds

> The code is correct. The person has the phone. But someone pretending to be IT is telling them what to do.

### 10.3–17.7 seconds

> Meet EntraGuard: verification that combines work identity, recent activity, and evidence from the conversation.

### 18.6–29.0 seconds

> In our Contoso Treasury demo, a user signs in with Microsoft Entra ID and starts a verification call.

### 32.0–46.8 seconds

> They match the number on screen, then answer questions drawn from available recent activity. A separate server check decides whether the application grants access.

### 49.0–61.8 seconds

> Now consider a controlled coaching test. The user can enter the right number while someone else guides their answers.

### 65.0–78.8 seconds

> EntraGuard analyses the transcript for manipulation. When coaching is detected with sufficient confidence, verification can be refused, even when the code is correct.

### 81.0–87.5 seconds

> The operator can inspect the assessment, evidence, and recorded verification outcome.

### 88.5–104.5 seconds

> Azure Communication Services carries the call. Speech transcribes it. Azure OpenAI assesses risk. Deterministic code controls the outcome. Microsoft Graph provides context, and Azure monitoring records the evidence.

### 105.4–114.6 seconds

> EntraGuard. The right code should not be the only question. Bring conversation evidence into the verification decision.

## Use the gaps for the real call

- Around 29–32 seconds: one short prompt/answer from the normal verification.
- Around 62–65 seconds: a short controlled coaching excerpt.
- Around 79–81 seconds: let the actual result appear before explaining it.
- Actual synthesized speech can finish before its cue window ends. `narration-timing.json` records the measured durations; use those gaps for original call audio.
- The narrator and the call should not compete. Duck original call audio while narration is active, then restore it for the evidence moments.

## What to send for the final edit

1. Original landscape MP4/MOV recordings, ideally 1920×1080 or higher, including system/call audio. Uncut clips are fine.
2. A normal verification: sign-in, visible number, incoming call, one contextual answer, and the actual result.
3. A controlled coaching attempt: correct number, coaching excerpt, assessment, returned result and reason. Send the real outcome even if it did not refuse.
4. Optional operator-console recording synchronized with the attempt; identify which clip belongs to which verification.
5. Any judging rules, mandatory team names/credits or required logo/end card.

## Editing rules

- Use 70 seconds of demonstration, 18 seconds of opening, 17 seconds of architecture and 10 seconds of closing.
- Compress waiting with visible jump cuts; keep spoken evidence and the result at normal speed. Do not make edited elapsed time look like a latency benchmark.
- Show real UI outcomes. Label simulator footage as simulated; do not substitute the verification simulator for evidence that the live Analyst detected coaching.
- Keep the demo ledger label. No bank execution, universal call interception, voice-clone protection, guaranteed detection or independent accuracy claims.
- Match contextual narration to questions actually asked in the recording. Questions depend on permissions and available data.
- Outbound verification can refuse a result; session revocation/quarantine belongs to the separate monitored-call path.
- Distinguish professional production quality of this video from claims that the application is production-certified.

## Source checks

- `src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs`: questions, fallbacks, voice and completion.
- `src/EntraGuard.MediaService/Endpoints/VerificationAdjudicator.cs`: code/coercion decision.
- `src/EntraGuard.MediaService/Sessions/GrantService.cs`: separate session grant.
- `src/EntraGuard.MediaService/Endpoints/MediaSocketEndpoint.cs`: analysis and verification-call remediation suppression.
- `src/EntraGuard.MediaService/Agents/AnalystClient.cs`: Azure OpenAI analysis.
- [Detailed verified architecture](../architecture-flows.md) and [official icon credits](../diagrams/azure-icons/README.md).
