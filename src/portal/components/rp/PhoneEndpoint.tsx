'use client';

import { useEffect, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import { useSoftPhone } from './useSoftPhone';
import { useEntraSignIn } from './useEntraSignIn';

/**
 * The handset UI.
 *
 * Deliberately spare and large-touch: this is read at arm's length on a phone, one-handed,
 * while a voice is talking. Anything that is not the keypad or the call state is noise.
 */
export function PhoneEndpoint() {
  const params = useSearchParams();
  const auth = useEntraSignIn();
  const upn = auth.identity?.upn ?? params.get('u') ?? '';

  const phone = useSoftPhone();
  const [entered, setEntered] = useState('');
  const [ready, setReady] = useState(false);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Registration is explicit rather than automatic: browsers only grant microphone access
  // in response to a user gesture, so a silent auto-register would be denied on most
  // phones and the device would appear registered while being unable to answer.
  const connect = async () => {
    await phone.checkDevice();
    const registered = await phone.register(upn, auth.identity?.objectId, 'phone');
    setReady(Boolean(registered));
  };

  useEffect(() => {
    if (phone.state === 'connected') setEntered('');
  }, [phone.state]);

  // Find the verification this call belongs to, so the digits can be reported directly as
  // well as sent as DTMF. Polled only while a call is up.
  useEffect(() => {
    if (phone.state !== 'connected') { setActiveId(null); return; }

    const poll = async () => {
      try {
        const response = await fetch('/api/verify', { cache: 'no-store' });
        if (!response.ok) return;
        const list = await response.json();
        const live = (list ?? []).find(
          (v: { isComplete: boolean; endpointKind: string }) => !v.isComplete && v.endpointKind === 'phone',
        );
        setActiveId(live?.verificationId ?? null);
      } catch {
        // Retried on the next tick.
      }
    };

    void poll();
    const timer = setInterval(poll, 2000);
    return () => clearInterval(timer);
  }, [phone.state, upn]);

  const press = async (digit: string) => {
    if (entered.length >= 2 || submitting) return;

    const next = entered + digit;
    setEntered(next);

    // Send as DTMF on the call leg — the path that would be used with a real handset.
    await phone.sendDigit(digit);

    // And report the completed entry directly. DTMF from a browser soft-phone is not
    // guaranteed to reach ACS's PSTN-oriented recogniser, and "I pressed the keys and
    // nothing happened" is the failure that makes this factor worthless. Belt and braces;
    // whichever arrives first wins and the adjudication is identical either way.
    if (next.length === 2 && activeId) {
      setSubmitting(true);
      try {
        await fetch(`/api/verify/${activeId}/digits`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ digits: next }),
        });
      } catch {
        // The DTMF path may still land it.
      } finally {
        setSubmitting(false);
      }
    }
  };

  return (
    <div className="rp" style={{ minHeight: '100vh' }}>
      <header className="rp-header">
        <span className="rp-logo">EG</span>
        <span>
          <span className="rp-brandname">EntraGuard</span>
          <span className="rp-brandsub">Your device</span>
        </span>
      </header>

      <main className="rp-center" style={{ paddingTop: 28 }}>
        <div className="rp-card">
          {!ready ? (
            <>
              <h1 className="rp-h1">Use this device for verification</h1>
              <p className="rp-sub">
                EntraGuard will ring this device when you sign in to a protected application.
                Keep this page open.
              </p>

              <div className="rp-field">
                <span className="rp-label">Account</span>
                <div className="mono" style={{ fontSize: 13, color: 'var(--rp-text-2)' }}>{upn}</div>
              </div>

              <button className="rp-btn block" onClick={auth.state === 'signed-in' ? connect : () => void auth.signIn()} type="button"
                disabled={auth.state === 'loading' || auth.state === 'signing-in'}>
                {auth.state === 'signed-in' ? 'Connect this device' : 'Sign in with Microsoft'}
              </button>
              {auth.error && <p role="alert" className="rp-result err">{auth.error}</p>}

              <p className="rp-hint">
                Allow microphone access when asked. EntraGuard listens to the verification call
                to detect whether someone is coaching you through it — that check is what
                distinguishes this from an ordinary approve-the-prompt factor.
              </p>
            </>
          ) : (
            <>
              {phone.state === 'ringing' ? (
                <div className="rp-incoming">
                  <div className="rp-incoming-avatar" aria-hidden="true">EG</div>
                  <div className="rp-incoming-from">EntraGuard</div>
                  <div className="rp-incoming-sub">Identity verification · incoming call</div>

                  <div className="rp-incoming-actions">
                    <button
                      className="rp-answer"
                      onClick={() => void phone.answer()}
                      type="button"
                      aria-label="Answer call"
                    >
                      <svg width="26" height="26" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.6">
                        <path d="M6.5 3h-2A1.5 1.5 0 0 0 3 4.6C3 11 9 17 15.4 17a1.5 1.5 0 0 0 1.6-1.5v-2l-3.5-1.2-1.6 1.7a11 11 0 0 1-4.9-4.9l1.7-1.6L6.5 3Z" />
                      </svg>
                      Answer
                    </button>

                    <button
                      className="rp-decline"
                      onClick={() => void phone.decline()}
                      type="button"
                      aria-label="Decline call"
                    >
                      <svg width="26" height="26" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.6">
                        <path d="M6.5 3h-2A1.5 1.5 0 0 0 3 4.6C3 11 9 17 15.4 17a1.5 1.5 0 0 0 1.6-1.5v-2l-3.5-1.2-1.6 1.7a11 11 0 0 1-4.9-4.9l1.7-1.6L6.5 3Z" />
                        <path d="M3 17 17 3" strokeWidth="1.8" />
                      </svg>
                      Decline
                    </button>
                  </div>

                  <p className="rp-hint" style={{ textAlign: 'center', marginTop: 18 }}>
                    Answering grants microphone access so EntraGuard can hear whether anyone
                    is coaching you. Your browser will show a microphone indicator while the
                    call is live — if it does not, the call has no audio.
                  </p>
                </div>
              ) : (
              <>
              <h1 className="rp-h1">
                {phone.state === 'connected' ? 'Verification call' : 'Waiting for a call'}
              </h1>

              {phone.state === 'connected' && (
                <div className={`rp-mic ${phone.device.microphone ? 'live' : 'off'}`}>
                  <span className="rp-mic-dot" aria-hidden="true" />
                  {phone.device.microphone
                    ? 'Microphone live — EntraGuard is listening to this call'
                    : 'Microphone off — coercion monitoring is not active'}
                </div>
              )}

              <div className="rp-callstate" style={{ marginTop: 14 }}>
                {/* 'ringing' is handled by the incoming-call screen above and cannot
                    reach here. */}
                {phone.state === 'connected' ? (
                  submitting ? <>Checking…</> : <>Enter the number shown on your computer</>
                ) : phone.state === 'error' ? (
                  <>Could not answer</>
                ) : (
                  <><span className="rp-ring" /> Connected — this device will ring</>
                )}
              </div>

              {!phone.device.microphone && phone.device.checked && (
                <div className="rp-result warn" style={{ marginTop: 14 }}>
                  <div className="rp-result-title">Coercion monitoring unavailable</div>
                  <div className="rp-result-body">
                    {phone.device.reason} Number matching will still work, but EntraGuard cannot
                    hear the call, so it cannot tell whether someone is coaching you.
                  </div>
                </div>
              )}

              {phone.error && (
                <div className="rp-result err" style={{ marginTop: 14 }}>
                  <div className="rp-result-title">Problem with this device</div>
                  <div className="rp-result-body">{phone.error}</div>
                </div>
              )}

              <div className="rp-entered" aria-live="polite">{entered || ' '}</div>

              <div className="rp-keypad">
                {['1','2','3','4','5','6','7','8','9','*','0','#'].map((key) => (
                  <button
                    key={key}
                    className="rp-key"
                    type="button"
                    style={{ height: 58, fontSize: 22 }}
                    onClick={() => press(key)}
                    disabled={phone.state !== 'connected' || entered.length >= 2}
                  >
                    {key}
                  </button>
                ))}
              </div>

              {entered.length > 0 && (
                <button
                  className="rp-btn secondary block"
                  style={{ marginTop: 14 }}
                  onClick={() => setEntered('')}
                  type="button"
                >
                  Clear
                </button>
              )}

              <p className="rp-hint">
                Never enter a number because a caller asked you to. EntraGuard will never phone
                you and ask you to read one out.
              </p>
              </>
              )}
            </>
          )}
        </div>
      </main>
    </div>
  );
}
