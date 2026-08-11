'use client';

import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import type { DeviceCheck } from './useSoftPhone';
import { KnowledgeSetup } from './KnowledgeSetup';
import { VoiceEnrollment } from './VoiceEnrollment';

export type Endpoint = 'browser' | 'phone' | 'teams';

/**
 * A Teams user is addressed by their Entra ID **object ID**, not their UPN.
 *
 * ACS's Teams interop identifier takes a GUID; handing it a UPN produces a call invite
 * that is accepted by the service and then rings nobody, which is indistinguishable from
 * a network problem. Validating the shape here turns that into a message the user can act on.
 */
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Verification enrollment.
 *
 * Real MFA has a setup step, and the first version of this skipped it — the soft-phone was
 * registered invisibly and the user had no way to see, choose, or test anything. That
 * produced the worst possible failure: the call went out, the browser could not answer it,
 * and the screen said "Calling your device…" indefinitely.
 *
 * So enrollment now does the two things enrollment exists to do: let the user choose where
 * they are reachable, and PROVE that endpoint works before it is depended on. Nothing here
 * is cosmetic — the device probe runs a real getUserMedia, and the phone option is only
 * marked ready once that handset has actually registered with ACS.
 */
export function Enrollment({
  upn,
  device,
  onCheckDevice,
  onEnrolled,
  busy,
  teamsObjectId,
  teamsUpn,
  tenantId,
  getAccessToken,
}: {
  upn: string;
  device: DeviceCheck;
  onCheckDevice: () => Promise<DeviceCheck>;
  onEnrolled: (endpoint: Endpoint, teamsUserId?: string) => void;
  busy: boolean;
  /** Entra object ID of the signed-in user. The Teams call target — not editable. */
  teamsObjectId?: string;
  /** Signed-in account, shown so the user can see who will be rung. */
  teamsUpn?: string;
  /** Home tenant, needed to find a knowledge question in the user's own directory. */
  tenantId?: string;
  /** Obtains a token for the voice-profile API. Voice enrolment is authenticated. */
  getAccessToken?: (forceMfa?: boolean) => Promise<string | null>;
}) {
  const [choice, setChoice] = useState<Endpoint | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [phoneReady, setPhoneReady] = useState(false);
  const [checking, setChecking] = useState(false);
  // Taken from the ID token, never typed. A second factor whose destination the user can
  // edit is a second factor that can be pointed at someone else's phone.
  const teamsId = teamsObjectId ?? '';
  const teamsValid = GUID.test(teamsId);

  const phoneUrl =
    typeof window === 'undefined'
      ? ''
      : `${window.location.origin}/phone?u=${encodeURIComponent(upn)}`;

  useEffect(() => {
    if (choice !== 'phone' || !phoneUrl) return;
    QRCode.toDataURL(phoneUrl, { width: 200, margin: 1, color: { dark: '#0f4c81', light: '#ffffff' } })
      .then(setQr)
      .catch(() => setQr(null));
  }, [choice, phoneUrl]);

  // Poll real presence — has a device actually registered and reported itself able to
  // answer? Asking the token broker whether an ACS identity exists is useless: it ALWAYS
  // does, because the broker mints one on demand, so it reported "connected"
  // unconditionally and the call went out to a device that was never listening.
  useEffect(() => {
    if (choice !== 'phone') return;

    const poll = async () => {
      try {
        const response = await fetch(`/api/presence/${encodeURIComponent(upn)}`, { cache: 'no-store' });
        if (!response.ok) return;
        const { endpoints } = await response.json();
        setPhoneReady(
          Array.isArray(endpoints) && endpoints.some((e: { deviceKind: string }) => e.deviceKind === 'phone'),
        );
      } catch {
        // Leave it false. An unreachable check must not read as ready.
      }
    };

    void poll();
    const timer = setInterval(poll, 3000);
    return () => clearInterval(timer);
  }, [choice, upn]);

  const runCheck = async () => {
    setChecking(true);
    await onCheckDevice();
    setChecking(false);
  };

  return (
    <div className="rp-card">
      <div className="rp-steps">
        <span className="rp-step done" /><span className="rp-step active" /><span className="rp-step" />
      </div>

      <h1 className="rp-h1">Set up verification</h1>
      <p className="rp-sub">
        Choose where EntraGuard should reach you when a protected application asks you to
        verify. You can test it here before it is used for real.
      </p>

      <div className="rp-methods">
        <button
          className="rp-method"
          aria-pressed={choice === 'browser'}
          onClick={() => setChoice('browser')}
          type="button"
        >
          <span className="rp-method-icon" aria-hidden="true">
            <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.5">
              <rect x="2.5" y="4" width="15" height="11" rx="1.5" /><path d="M7 17.5h6" />
            </svg>
          </span>
          <span className="rp-method-body">
            <span className="rp-method-title">
              This computer
              {device.checked && (
                <span className={`rp-tag ${device.microphone ? 'rec' : ''}`}>
                  {device.microphone ? 'Ready' : 'Limited'}
                </span>
              )}
            </span>
            <span className="rp-method-desc">
              The verification call rings in this browser tab. Needs microphone access.
            </span>
          </span>
        </button>

        <button
          className="rp-method"
          aria-pressed={choice === 'phone'}
          onClick={() => setChoice('phone')}
          type="button"
        >
          <span className="rp-method-icon" aria-hidden="true">
            <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.5">
              <rect x="6" y="2.5" width="8" height="15" rx="1.5" /><path d="M9 15h2" />
            </svg>
          </span>
          <span className="rp-method-body">
            <span className="rp-method-title">
              My phone
              {phoneReady && <span className="rp-tag rec">Connected</span>}
            </span>
            <span className="rp-method-desc">
              Scan a code to open EntraGuard on your phone. The call rings there, on the
              handset in your hand.
            </span>
          </span>
        </button>
        <button
          className="rp-method"
          aria-pressed={choice === 'teams'}
          onClick={() => setChoice('teams')}
          type="button"
        >
          <span className="rp-method-icon" aria-hidden="true">
            <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.5">
              <rect x="2.5" y="4.5" width="10" height="11" rx="1.5" /><path d="M5.5 7.5h4M7.5 7.5v5" />
              <path d="M13.5 7.5h3a1 1 0 0 1 1 1v3.5a1 1 0 0 1-1 1h-3" />
            </svg>
          </span>
          <span className="rp-method-body">
            <span className="rp-method-title">
              Microsoft Teams
              {teamsValid && <span className="rp-tag rec">Ready</span>}
            </span>
            <span className="rp-method-desc">
              EntraGuard calls your Teams account. It rings in the Teams app on your phone or
              desktop — nothing to keep open in a browser.
            </span>
          </span>
        </button>
      </div>

      {choice === 'teams' && (
        <div style={{ marginBottom: 16 }}>
          <div className="rp-field">
            <span className="rp-label">Teams account</span>
            <div className="mono" style={{ fontSize: 13, color: 'var(--rp-text-2)' }}>
              {teamsUpn || 'not signed in'}
            </div>
            <div className="mono" style={{ fontSize: 11, color: 'var(--rp-text-3)', marginTop: 4 }}>
              {teamsId || '—'}
            </div>
          </div>

          <div className={`rp-result ${teamsValid ? 'ok' : 'warn'}`}>
            <div className="rp-result-title">
              {teamsValid ? 'Teams endpoint ready' : 'No callable Teams identity'}
            </div>
            <div className="rp-result-body">
              {teamsValid
                ? 'EntraGuard will call the account you signed in as. Nothing to keep open — ' +
                  'answer in Teams on your phone or desktop.'
                : 'This account has no directory object ID, so it cannot be reached on Teams. ' +
                  'Sign in with a work or school account.'}
            </div>
          </div>

          <p className="rp-hint">
            The call target is taken from your sign-in token, not from a field on this page.
            A second factor you can redirect is not a second factor.
          </p>
        </div>
      )}

      {choice === 'browser' && (
        <div style={{ marginBottom: 16 }}>
          <button className="rp-btn secondary" onClick={runCheck} disabled={checking} type="button">
            {checking ? 'Testing…' : 'Test this device'}
          </button>

          {device.checked && device.microphone && (
            <div className="rp-result ok" style={{ marginTop: 14 }}>
              <div className="rp-result-title">Device ready</div>
              <div className="rp-result-body">
                Microphone available. EntraGuard can hear the verification call, so coercion
                monitoring is active.
              </div>
            </div>
          )}

          {device.checked && !device.microphone && (
            <div className="rp-result warn" style={{ marginTop: 14 }}>
              <div className="rp-result-title">Verification will work, with one limitation</div>
              <div className="rp-result-body">
                {device.reason}
                <br /><br />
                The call will still ring and number matching will still work. But EntraGuard
                cannot hear the call, so it cannot detect whether someone is coaching you
                through it. Use your phone instead for the full protection.
              </div>
            </div>
          )}
        </div>
      )}

      {choice === 'phone' && (
        <div style={{ marginBottom: 16, textAlign: 'center' }}>
          {qr ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={qr} alt="QR code to open EntraGuard on your phone" width={190} height={190}
                 style={{ border: '1px solid var(--rp-border)', borderRadius: 4, background: '#fff' }} />
          ) : (
            <div style={{ padding: 24, color: 'var(--rp-text-3)', fontSize: 13 }}>Generating code…</div>
          )}

          <p className="rp-hint" style={{ textAlign: 'left', marginTop: 12 }}>
            Scan with your phone camera, then tap <strong>Connect this device</strong> and allow
            the microphone. Keep that page open — it is your handset.
          </p>

          <div className="mono" style={{
            fontSize: 11, color: 'var(--rp-text-3)', wordBreak: 'break-all',
            background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 3, padding: 8,
          }}>
            {phoneUrl}
          </div>

          <div
            className={`rp-result ${phoneReady ? 'ok' : 'warn'}`}
            style={{ marginTop: 14, textAlign: 'left' }}
          >
            <div className="rp-result-title">
              {phoneReady ? 'Phone connected' : 'Waiting for your phone'}
            </div>
            <div className="rp-result-body">
              {phoneReady
                ? 'Your handset is registered and listening. It will ring for verification.'
                : 'Scan the code, tap Connect this device, and allow the microphone. This updates automatically once your phone reports in.'}
            </div>
          </div>
        </div>
      )}

      {getAccessToken && (
        <VoiceEnrollment objectId={teamsObjectId} getAccessToken={getAccessToken} />
      )}

      <KnowledgeSetup tenantId={tenantId} objectId={teamsObjectId} upn={upn} />

      <button
        className="rp-btn block"
        style={{ marginTop: 16 }}
        disabled={
          !choice || busy
          || (choice === 'browser' && !device.checked)
          || (choice === 'phone' && !phoneReady)
          || (choice === 'teams' && !teamsValid)
        }
        onClick={() => choice && onEnrolled(choice, choice === 'teams' ? teamsId.trim() : undefined)}
        type="button"
      >
        {busy ? 'Starting verification…' : 'Continue'}
      </button>

      {choice === 'browser' && !device.checked && (
        <p className="rp-hint">
          Test the device first. Starting a verification against an endpoint that cannot answer
          is how you end up staring at a ringing screen that never resolves.
        </p>
      )}

      {choice === 'phone' && !phoneReady && (
        <p className="rp-hint">
          Waiting for your phone to connect. Continue stays disabled until this page can see
          your handset registered and listening — not merely until an identity exists for it.
        </p>
      )}
    </div>
  );
}
