'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { useSoftPhone } from './useSoftPhone';
import { Enrollment, type Endpoint } from './Enrollment';
import { useEntraSignIn } from './useEntraSignIn';

type Stage = 'login' | 'enroll' | 'verifying' | 'stepup' | 'granted' | 'denied';

interface MediaTelemetry {
  streamConnected: boolean;
  audioFrames: number;
  dtmfReceived: number;
  secondsSinceAudio: number | null;
}

interface Verification {
  verificationId: string;
  callState: string;
  matchCode: string | null;
  result: string;
  reason: string;
  grantsAccess: boolean;
  isComplete: boolean;
  attempts: number;
  peakRiskDuringCall: number;
  /** Cosine similarity against the enrolled voice, or null when not compared. */
  voiceScore?: number | null;
  voiceOutcome?: string;
  /**
   * The voice check wants a stronger factor before access is released.
   *
   * Always false while VOICE_MODE=observe, so this changes nothing until thresholds have
   * been earned against real calls.
   */
  requiresStepUp?: boolean;
}

const APP_NAME = 'Contoso Treasury';

/**
 * Evidence that ACS is genuinely streaming this call to EntraGuard.
 *
 * "Is anything actually listening?" is a fair question, and until now the UI could not
 * answer it — a call that connected and a call that connected AND is being monitored
 * looked identical. These counters come from the media WebSocket: frames only increment
 * because ACS is sending real audio, so a rising number is proof rather than assertion.
 */
function MediaEvidence({ media }: { media: MediaTelemetry | null }) {
  if (!media) return null;

  const listening = media.streamConnected && media.audioFrames > 0;

  return (
    <div className={`rp-mic ${listening ? 'live' : 'off'}`} style={{ marginTop: 14 }}>
      <span className="rp-mic-dot" aria-hidden="true" />
      <span>
        {listening ? (
          <>
            EntraGuard is listening — {media.audioFrames.toLocaleString()} audio frames received
            from Azure Communication Services
            {media.dtmfReceived > 0 && <>, {media.dtmfReceived} keypad tone(s)</>}
          </>
        ) : media.streamConnected ? (
          <>Media stream open, waiting for audio from the call…</>
        ) : (
          <>Waiting for Azure Communication Services to open the media stream…</>
        )}
      </span>
    </div>
  );
}

/**
 * Live progress of the verification call.
 *
 * Each step corresponds to a real ACS event, not a guess or a timer. The previous UI showed
 * one static "Calling your device…" for every state including stalled, so when the call
 * silently failed there was nothing to distinguish "still ringing" from "never going to
 * ring". If this tracker stops advancing, the step it stopped on is the fault.
 */
function CallProgress({
  callState,
  endpoint,
  attempts,
}: {
  callState: string;
  endpoint: Endpoint;
  attempts: number;
}) {
  const steps = [
    {
      key: 'Placing',
      label: endpoint === 'teams'
        ? 'Calling you on Microsoft Teams'
        : endpoint === 'phone'
          ? 'Calling your phone'
          : 'Calling this browser',
    },
    { key: 'Connected', label: 'Answered' },
    { key: 'AwaitingDigits', label: 'Listening for your number' },
    { key: 'Adjudicating', label: 'Checking' },
  ];
  const current = Math.max(0, steps.findIndex((s) => s.key === callState));

  return (
    <div style={{ marginTop: 18 }}>
      <ol className="rp-progress">
        {steps.map((step, index) => (
          <li
            key={step.key}
            className={index < current ? 'done' : index === current ? 'active' : ''}
          >
            <span className="rp-progress-dot" aria-hidden="true" />
            <span>{step.label}</span>
          </li>
        ))}
      </ol>

      {attempts > 0 && (
        <p className="rp-hint" style={{ marginTop: 10 }}>
          Attempt {attempts} of 3 — the previous number was not correct.
        </p>
      )}

      {callState === 'Placing' && (
        <p className="rp-hint" style={{ marginTop: 10 }}>
          {endpoint === 'teams'
            ? 'Teams should ring within a few seconds. The caller shows as the EntraGuard Communication Services resource, because that is literally who is calling.'
            : endpoint === 'phone'
              ? 'Your phone should ring within a few seconds. Keep the EntraGuard page open on it.'
              : 'This tab should answer automatically.'}
        </p>
      )}
    </div>
  );
}

/**
 * Find the ACS identity of the device the user actually enrolled.
 *
 * Read from PRESENCE, not from the token broker. The broker mints an identity on demand,
 * so asking it "what is this user's identity" always succeeds and tells you nothing about
 * which device is listening. Presence is reported by the device itself, so the identity it
 * returns is one that provably has a live CallAgent attached to it.
 */
async function resolveEnrolledDevice(upn: string, deviceKind: Endpoint): Promise<string | null> {
  try {
    const response = await fetch(`/api/presence/${encodeURIComponent(upn)}`, { cache: 'no-store' });
    if (!response.ok) return null;
    const { endpoints } = await response.json();
    const match = (endpoints ?? []).find(
      (e: { deviceKind: string }) => e.deviceKind === deviceKind,
    );
    return match?.acsUserId ?? null;
  } catch {
    return null;
  }
}

export function TreasuryApp() {
  const [stage, setStage] = useState<Stage>('login');
  const [busy, setBusy] = useState(false);
  const [verification, setVerification] = useState<Verification | null>(null);
  const [media, setMedia] = useState<MediaTelemetry | null>(null);
  const [entered, setEntered] = useState('');
  const [startError, setStartError] = useState<string | null>(null);
  const [endpoint, setEndpoint] = useState<Endpoint>('browser');

  const phone = useSoftPhone();
  const auth = useEntraSignIn();
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Identity comes from the token, not from a form. Everything downstream — who is called,
  // whose Teams rings, whose name appears in the audit row — derives from these claims.
  const upn = auth.identity?.upn ?? '';

  // Password is no longer a stage: Entra owns the first factor now, including whatever
  // policy that tenant enforces on it. EntraGuard is strictly the step-up.
  useEffect(() => {
    if (auth.state === 'signed-in' && stage === 'login') setStage('enroll');
    if (auth.state === 'signed-out' && stage !== 'login') setStage('login');
  }, [auth.state, stage]);

  // ── Step 2: choose voice verification, place the call ─────────────────────
  const startVerification = async (chosen: Endpoint, teamsUserId?: string) => {
    setBusy(true);
    setStartError(null);
    setEntered('');
    setEndpoint(chosen);

    try {
      // The browser only needs to register as a call endpoint when the browser IS the
      // endpoint. When the user enrolled their phone, that handset has already registered
      // the same ACS identity, and registering here too would just ring both.
      // Each device holds its OWN ACS identity, so the call goes to exactly the endpoint
      // the user chose. Previously both shared one identity, ACS forked the call to both,
      // and the desktop's automatic accept() answered before the phone could ring.
      //
      // A Teams endpoint needs neither of those: the account is reachable through Teams
      // itself, so there is no ACS identity to mint and no presence to wait for.
      const acsId = chosen === 'teams'
        ? null
        : chosen === 'browser'
          ? (phone.acsUserId ?? (await phone.register(upn, undefined, 'browser')))
          : await resolveEnrolledDevice(upn, 'phone');

      if (chosen !== 'teams' && !acsId) {
        throw new Error(
          chosen === 'browser'
            ? 'Could not register this browser to receive the call.'
            : 'Your phone has not connected yet. Open the link on your phone and tap Connect.',
        );
      }

      const response = await fetch('/api/verify', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(
          chosen === 'teams'
            ? {
                upn,
                teamsUserId,
                tenantId: auth.identity?.tenantId,
                objectId: auth.identity?.objectId,
                applicationName: APP_NAME,
              }
            : { upn, calleeAcsId: acsId, applicationName: APP_NAME },
        ),
      });

      const data = await response.json();
      if (!response.ok) {
        throw new Error(data.error ?? `Verification could not be started (${response.status}).`);
      }

      setVerification(data);
      setStage('verifying');
    } catch (error) {
      setStartError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  };

  // ── Poll for the verdict ──────────────────────────────────────────────────
  // Polled rather than pushed: the verdict is an authorization decision, and the RP should
  // read it from the authority that issued it rather than trust a message the browser
  // received over a socket it also controls.
  useEffect(() => {
    if (stage !== 'verifying' || !verification) return;

    pollRef.current = setInterval(async () => {
      try {
        const response = await fetch(`/api/verify/${verification.verificationId}`);
        if (!response.ok) return;

        const payload = await response.json();
        // The status endpoint returns { verification, media }. Older builds returned the
        // verification flat, and a shape mismatch here would leave isComplete undefined and
        // the user staring at a screen that never resolves — so accept either.
        const data: Verification = payload.verification ?? payload;
        setVerification(data);
        setMedia(payload.media ?? null);

        if (data.isComplete) {
          if (pollRef.current) clearInterval(pollRef.current);
          // Voice never denies on its own. A weak match asks Entra for a stronger factor,
          // and only failing THAT refuses access — the model is not accurate enough over a
          // phone line to lock somebody out of their own money by itself.
          if (data.grantsAccess && data.requiresStepUp) {
            setStage('stepup');
          } else {
            setStage(data.grantsAccess ? 'granted' : 'denied');
          }
        }
      } catch {
        // Transient — the next tick retries.
      }
    }, 1500);

    return () => {
      if (pollRef.current) clearInterval(pollRef.current);
    };
  }, [stage, verification?.verificationId]);

  const pressKey = useCallback(async (digit: string) => {
    if (entered.length >= 2) return;
    setEntered((current) => current + digit);
    await phone.sendDigit(digit);
  }, [entered.length, phone]);

  const reset = () => {
    void phone.hangUp();
    setStage('enroll');
    setVerification(null);
    setEntered('');
    setStartError(null);
  };

  return (
    <div className="rp">
      <header className="rp-header">
        <span className="rp-logo">CT</span>
        <span>
          <span className="rp-brandname">Contoso Treasury</span>
          <span className="rp-brandsub">Payment Operations</span>
        </span>
        {auth.state === 'signed-in' && (
          <span className="rp-header-right">
            <span>{auth.identity?.displayName ?? upn}</span>
            <a className="rp-btn secondary" href="/settings">Settings</a>
            <button className="rp-btn secondary" onClick={() => void auth.signOut()} type="button">
              Sign out
            </button>
          </span>
        )}
      </header>

      {stage === 'login' && (
        <main className="rp-center">
          <div className="rp-card">
            <h1 className="rp-h1">Sign in</h1>
            <p className="rp-sub">Continue to Contoso Treasury with your work account.</p>

            <button
              className="rp-btn block"
              type="button"
              onClick={() => void auth.signIn()}
              disabled={auth.state === 'signing-in' || auth.state === 'loading'}
            >
              {auth.state === 'signing-in'
                ? 'Waiting for Microsoft…'
                : auth.state === 'loading'
                  ? 'Loading…'
                  : 'Sign in with Microsoft'}
            </button>

            {auth.error && (
              <div className="rp-result err" style={{ marginTop: 14 }}>
                <div className="rp-result-title">Could not sign in</div>
                <div className="rp-result-body">{auth.error}</div>
              </div>
            )}

            <p className="rp-hint">
              Entra ID handles the first factor, including whatever policy your tenant
              enforces on it. Treasury then requires a second factor before releasing access
              — and that second factor is what EntraGuard provides.
            </p>
          </div>
        </main>
      )}

      {stage === 'enroll' && (
        <main className="rp-center">
          <Enrollment
            upn={upn}
            device={phone.device}
            onCheckDevice={phone.checkDevice}
            onEnrolled={startVerification}
            busy={busy}
            teamsObjectId={auth.identity?.objectId}
            teamsUpn={auth.identity?.upn}
            tenantId={auth.identity?.tenantId}
            getAccessToken={auth.getAccessToken}
            getTokens={auth.getTokens}
          />
          {startError && (
            <div className="rp-result err" style={{ marginTop: 14 }}>
              <div className="rp-result-title">Could not start verification</div>
              <div className="rp-result-body">{startError}</div>
            </div>
          )}
        </main>
      )}

      {stage === 'verifying' && verification && (
        <main className="rp-center">
          <div className="rp-card">
            <div className="rp-steps">
              <span className="rp-step done" /><span className="rp-step done" /><span className="rp-step active" />
            </div>

            <h1 className="rp-h1">Enter this number on the call</h1>
            <p className="rp-sub">
              EntraGuard is calling you now. Enter the number below using the keypad.
            </p>

            <div className="rp-code-label">Your number</div>
            <div className="rp-code">{verification.matchCode ?? '··'}</div>

            {phone.state === 'error' ? (
              <div className="rp-result err" style={{ marginTop: 18 }}>
                <div className="rp-result-title">Could not answer the call</div>
                <div className="rp-result-body">
                  {phone.error ?? 'This browser could not open a microphone.'}
                  <br />
                  Voice verification needs microphone access so EntraGuard can hear whether you
                  are being coached. Allow it and try again.
                </div>
              </div>
            ) : (
              <>
                <CallProgress
                  callState={verification.callState}
                  endpoint={endpoint}
                  attempts={verification.attempts}
                />
                <MediaEvidence media={media} />
              </>
            )}

            {/* The keypad belongs to whichever device is on the call. When the user
                enrolled their phone the digits are pressed there, and a second keypad here
                would invite them to type into the wrong device and wonder why nothing
                happened. */}
            {endpoint === 'teams' ? (
              <p className="rp-sub" style={{ textAlign: 'center', marginTop: 18 }}>
                Answer the Teams call, open its dial pad, and enter the number above. The
                digits travel on the call itself — that is what proves you hold the account,
                rather than merely this browser tab.
              </p>
            ) : endpoint === 'phone' ? (
              <p className="rp-sub" style={{ textAlign: 'center', marginTop: 18 }}>
                Answer your phone and enter the number on its keypad.
              </p>
            ) : (
              <>
                <div className="rp-entered" aria-live="polite">{entered || ' '}</div>

                <div className="rp-keypad">
                  {['1','2','3','4','5','6','7','8','9','*','0','#'].map((key) => (
                    <button
                      key={key}
                      className="rp-key"
                      type="button"
                      onClick={() => pressKey(key)}
                      disabled={phone.state !== 'connected' || entered.length >= 2}
                    >
                      {key}
                    </button>
                  ))}
                </div>
              </>
            )}

            <div style={{ display: 'flex', gap: 8, marginTop: 18 }}>
              <button className="rp-btn secondary" onClick={reset} type="button">Cancel</button>
              <button className="rp-btn secondary" onClick={() => setEntered('')} type="button"
                      disabled={entered.length === 0}>
                Clear
              </button>
            </div>

            <p className="rp-hint">
              Never enter this number because someone on a call asked you to. Contoso will never
              phone you and ask you to read it out.
            </p>
          </div>
        </main>
      )}

      {stage === 'stepup' && verification && (
        <main className="rp-center">
          <div className="rp-card">
            <div className="rp-result warn">
              <div className="rp-result-title">One more check</div>
              <div className="rp-result-body">
                Your answers were correct, but your voice did not clearly match the profile
                on file{typeof verification.voiceScore === 'number'
                  && <> (score {verification.voiceScore.toFixed(2)})</>}.
                <br /><br />
                That is not proof of anything — voice matching over a phone line is
                imperfect, which is why it asks rather than refuses. Confirm with your
                authenticator or passkey to continue.
              </div>
            </div>

            <button
              className="rp-btn block"
              style={{ marginTop: 14 }}
              type="button"
              disabled={busy}
              onClick={async () => {
                setBusy(true);
                try {
                  // A fresh interactive authentication, not a cached token. The point is a
                  // second human interaction with a factor voice cannot imitate.
                  const token = await auth.getAccessToken(true);
                  setStage(token ? 'granted' : 'denied');
                } finally {
                  setBusy(false);
                }
              }}
            >
              {busy ? 'Waiting for Microsoft…' : 'Confirm with Microsoft'}
            </button>

            <button className="rp-btn secondary block" style={{ marginTop: 8 }}
                    type="button" onClick={reset}>
              Cancel
            </button>

            <p className="rp-hint">
              If you did not start this sign-in, cancel and contact your IT help desk on a
              number you already know.
            </p>
          </div>
        </main>
      )}

      {stage === 'denied' && verification && (
        <main className="rp-center">
          <div className="rp-card">
            <div className={`rp-result ${verification.result === 'BlockedCoercion' ? 'warn' : 'err'}`}>
              <div className="rp-result-title">
                {verification.result === 'BlockedCoercion'
                  ? 'Blocked for your protection'
                  : verification.result === 'BlockedVoiceMismatch'
                    ? 'Blocked — voice not recognised'
                    : 'Verification failed'}
              </div>
              <div className="rp-result-body">{verification.reason}</div>
            </div>

            {verification.result === 'BlockedVoiceMismatch' && (
              <p className="rp-sub">
                The code you entered was correct, but the voice on the call did not match the
                voice profile registered to this account. Access to payment runs was refused.
                {' '}
                If this was you, your enrolled profile may need re-recording — a poor line, a
                cold, or a noisy room can all lower the match. You can re-record it in
                Settings after signing in from a trusted device, or contact your IT help desk
                on a number you already know.
              </p>
            )}

            {verification.result === 'BlockedCoercion' && (
              <p className="rp-sub">
                The number you entered was correct. EntraGuard refused the sign-in anyway because
                it detected you were being coached through the call. If someone asked you to do
                this, hang up and contact your IT help desk on a number you already know.
              </p>
            )}

            <button className="rp-btn block" onClick={reset} type="button">Try again</button>
          </div>
        </main>
      )}

      {stage === 'granted' && (
        <main className="rp-main">
          <div className="rp-appbar">
            <div>
              <h1 className="rp-h1" style={{ marginBottom: 2 }}>Payment runs</h1>
              <span className="rp-protected">
                <svg width="13" height="13" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.8">
                  <path d="M10 2.5 4 5v4.5c0 3.6 2.4 6.9 6 8 3.6-1.1 6-4.4 6-8V5l-6-2.5Z" />
                  <path d="M7.5 10.2 9.3 12l3.4-3.6" />
                </svg>
                Verified by EntraGuard voice challenge
              </span>
            </div>
          </div>

          <div className="rp-tiles">
            <div className="rp-tile">
              <div className="rp-tile-label">Pending approval</div>
              <div className="rp-tile-value">£2,481,900</div>
            </div>
            <div className="rp-tile">
              <div className="rp-tile-label">Runs this week</div>
              <div className="rp-tile-value">14</div>
            </div>
            <div className="rp-tile">
              <div className="rp-tile-label">Awaiting your review</div>
              <div className="rp-tile-value">3</div>
            </div>
          </div>

          <div style={{ background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 4, overflow: 'hidden' }}>
            <table className="rp-table">
              <thead>
                <tr><th>Reference</th><th>Beneficiary</th><th>Amount</th><th>Status</th></tr>
              </thead>
              <tbody>
                <tr><td>PR-40192</td><td>Northwind Logistics Ltd</td><td>£812,400</td><td><span className="rp-badge warn">Awaiting approval</span></td></tr>
                <tr><td>PR-40191</td><td>Fabrikam Industrial</td><td>£1,204,000</td><td><span className="rp-badge warn">Awaiting approval</span></td></tr>
                <tr><td>PR-40188</td><td>Tailwind Freight</td><td>£465,500</td><td><span className="rp-badge warn">Awaiting approval</span></td></tr>
                <tr><td>PR-40184</td><td>Contoso Payroll</td><td>£1,940,220</td><td><span className="rp-badge ok">Released</span></td></tr>
              </tbody>
            </table>
          </div>

          <p className="rp-hint">
            This is the asset the step-up protects. Access was released only after a voice
            challenge that confirmed the person at this browser is the person on the phone —
            and that nobody was standing over them.
          </p>
        </main>
      )}
    </div>
  );
}
