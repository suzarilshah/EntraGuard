'use client';

import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Must match VoiceEnrollmentEndpoint.ConsentVersion on the server.
 *
 * Only a fallback: the version normally comes from the status call, which is authoritative.
 * It exists so a slow first load cannot send an empty consent version and be refused for
 * not consenting, which is a confusing way to fail at the moment somebody just consented.
 */
const CONSENT_VERSION = '2026-08-10.v1';

interface Profile {
  enrolled: boolean;
  consentVersion?: string;
  consentAt?: string;
  enrolledAt?: string;
  quality?: number;
  currentConsentVersion: string;
}

interface Enrollment {
  enrollmentId: string;
  state: string;
  phrasesTotal: number;
  phrasesCaptured: number;
  currentPhrase?: string | null;
  isComplete: boolean;
  enrolled: boolean;
  failure?: string | null;
  quality?: number;
}

/**
 * Voice profile setup and removal.
 *
 * Written to be honest about what this is, because a voiceprint is not a password. The user
 * is told before consenting that it cannot be changed after a breach, that the recordings
 * are discarded, that verification still works without it, and that they can delete it at
 * any time. Consent obtained without those four facts is not informed.
 */
export function VoiceEnrollment({
  objectId,
  getAccessToken,
}: {
  objectId?: string;
  getAccessToken: (forceMfa?: boolean) => Promise<string | null>;
}) {
  const [profile, setProfile] = useState<Profile | null>(null);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [consented, setConsented] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const load = useCallback(async () => {
    try {
      const token = await getAccessToken();
      if (!token) return;

      const response = await fetch('/api/voice-profile', {
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
      });
      if (response.ok) setProfile(await response.json());
    } catch {
      // Leave the panel in its unknown state rather than claiming "not enrolled", which
      // would invite a duplicate enrolment.
    }
  }, [getAccessToken]);

  useEffect(() => { void load(); }, [load]);

  // Watch an enrollment call while it runs, so the phrase on screen matches the one being
  // asked for on the phone.
  useEffect(() => {
    if (!enrollment || enrollment.isComplete) return;

    pollRef.current = setInterval(async () => {
      try {
        const token = await getAccessToken();
        if (!token) return;

        const response = await fetch(`/api/voice-profile/enrollment/${enrollment.enrollmentId}`, {
          headers: { Authorization: `Bearer ${token}` },
          cache: 'no-store',
        });
        if (!response.ok) return;

        const next: Enrollment = await response.json();
        setEnrollment(next);

        if (next.isComplete) {
          if (pollRef.current) clearInterval(pollRef.current);
          void load();
        }
      } catch {
        // Next tick retries.
      }
    }, 1500);

    return () => { if (pollRef.current) clearInterval(pollRef.current); };
  }, [enrollment, getAccessToken, load]);

  const enrol = async (reenroll: boolean) => {
    setBusy(true);
    setError(null);

    try {
      // Multi-factor is required to register a biometric, so the token is obtained with an
      // explicit MFA claims challenge rather than reusing whatever the session already had.
      const token = await getAccessToken(true);
      if (!token) throw new Error('Could not obtain an access token.');

      const response = await fetch('/api/voice-profile/enrollment', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({
          // Fall back to the constant when the status call has not landed yet, so a slow
          // network does not send an empty consent version and get refused for it.
          consentVersion: profile?.currentConsentVersion ?? CONSENT_VERSION,
          teamsUserId: objectId,
          reenroll,
        }),
      });

      // Never assume a body. An empty one is what produced "Unexpected end of JSON input",
      // which told the user nothing about the actual failure.
      const raw = await response.text();
      const data = raw.trim() ? JSON.parse(raw) : {};

      if (!response.ok) {
        throw new Error(
          data.error === 'mfa_required'
            ? 'Multi-factor authentication is required to register a voice profile.'
            : data.detail ?? data.error ?? `Enrolment could not start (${response.status}).`,
        );
      }

      setEnrollment(data);
    } catch (enrolError) {
      setError(enrolError instanceof Error ? enrolError.message : String(enrolError));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setBusy(true);
    setError(null);

    try {
      const token = await getAccessToken();
      if (!token) throw new Error('Could not obtain an access token.');

      const response = await fetch('/api/voice-profile', {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${token}` },
      });

      if (!response.ok) throw new Error('The voice profile could not be deleted.');

      setProfile(null);
      setEnrollment(null);
      await load();
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : String(deleteError));
    } finally {
      setBusy(false);
    }
  };

  const active = enrollment && !enrollment.isComplete;

  return (
    <section style={{ background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 4, padding: 18, marginBottom: 18 }}>
      <h2 className="rp-h1" style={{ fontSize: 16, marginBottom: 10 }}>Voice profile (optional)</h2>

      {active ? (
        <>
          <div className="rp-result warn">
            <div className="rp-result-title">
              Enrolment call in progress — phrase {Math.min(enrollment!.phrasesCaptured + 1, enrollment!.phrasesTotal)} of {enrollment!.phrasesTotal}
            </div>
            <div className="rp-result-body">
              Answer the Teams call and repeat each phrase clearly.
            </div>
          </div>

          {enrollment!.currentPhrase && (
            <div style={{
              marginTop: 14, padding: '16px 18px', borderRadius: 4,
              background: 'var(--rp-bg, #f6f6f6)', border: '1px solid var(--rp-border)',
              fontSize: 17, lineHeight: 1.5,
            }}>
              “{enrollment!.currentPhrase}”
            </div>
          )}

          <p className="rp-hint">
            Somewhere quiet, and alone. If a second voice is picked up, the recordings will
            not agree with each other and enrolment is refused rather than binding a blend of
            two people to your account.
          </p>
        </>
      ) : profile?.enrolled ? (
        <>
          <div className="rp-result ok">
            <div className="rp-result-title">Voice profile active</div>
            <div className="rp-result-body">
              Enrolled {profile.enrolledAt ? new Date(profile.enrolledAt).toLocaleDateString() : ''}.
              Consent version {profile.consentVersion}
              {profile.consentAt && <> on {new Date(profile.consentAt).toLocaleDateString()}</>}.
              {typeof profile.quality === 'number' && (
                <> Recording agreement {profile.quality.toFixed(2)}.</>
              )}
            </div>
          </div>

          <div style={{ display: 'flex', gap: 8, marginTop: 14 }}>
            <button className="rp-btn secondary" type="button" disabled={busy}
                    onClick={() => void enrol(true)}>
              Re-record
            </button>
            <button className="rp-btn secondary" type="button" disabled={busy}
                    onClick={() => void remove()}>
              Delete my voice profile
            </button>
          </div>

          <p className="rp-hint">
            Deletion is immediate and removes the stored template entirely. Verification
            continues to work without it — the number match and identity questions are
            unaffected.
          </p>
        </>
      ) : (
        <>
          <p className="rp-sub">
            EntraGuard can compare your voice on a verification call against a profile you
            record once. It is never the thing that grants or refuses access — a poor match
            asks you for a stronger factor rather than turning you away.
          </p>

          <div className="rp-result warn" style={{ marginTop: 12 }}>
            <div className="rp-result-title">Before you agree</div>
            <div className="rp-result-body">
              <ul style={{ margin: '6px 0 0 18px', padding: 0, lineHeight: 1.6 }}>
                <li>A voiceprint is not a password. You cannot change your voice if it leaks.</li>
                <li>
                  The recordings are not kept. Three phrases are converted into a
                  mathematical template and the audio is discarded — the template cannot be
                  played back as speech.
                </li>
                <li>The template is encrypted and stored against your directory account.</li>
                <li>You can delete it at any time, and verification still works without it.</li>
                <li>
                  Voice matching over a phone line is imperfect, which is exactly why it can
                  ask for another factor but never deny you on its own.
                </li>
              </ul>
            </div>
          </div>

          <label style={{ display: 'flex', gap: 10, alignItems: 'flex-start', margin: '14px 0' }}>
            <input type="checkbox" checked={consented}
                   onChange={(e) => setConsented(e.target.checked)} style={{ marginTop: 3 }} />
            <span style={{ fontSize: 13, lineHeight: 1.5 }}>
              I consent to EntraGuard creating and storing a voice profile from my speech,
              on the terms above.
            </span>
          </label>

          <button className="rp-btn" type="button" disabled={!consented || busy || !objectId}
                  onClick={() => void enrol(false)}>
            {busy ? 'Starting…' : 'Record my voice profile'}
          </button>

          <p className="rp-hint">
            You will be asked to sign in again with your second factor. Registering a
            biometric from a password-only session would let anyone with a stolen password
            bind their own voice to your account.
          </p>
        </>
      )}

      {enrollment?.isComplete && !enrollment.enrolled && (
        <div className="rp-result err" style={{ marginTop: 14 }}>
          <div className="rp-result-title">Enrolment did not complete</div>
          <div className="rp-result-body">{enrollment.failure}</div>
        </div>
      )}

      {error && (
        <div className="rp-result err" style={{ marginTop: 14 }}>
          <div className="rp-result-body">{error}</div>
        </div>
      )}
    </section>
  );
}
