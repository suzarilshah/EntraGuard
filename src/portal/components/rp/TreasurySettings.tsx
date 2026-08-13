'use client';

import { useEffect, useState } from 'react';
import { useEntraSignIn } from './useEntraSignIn';
import { VoiceEnrollment } from './VoiceEnrollment';
import { KnowledgeSetup } from './KnowledgeSetup';

interface VerificationRow {
  verificationId: string;
  upn: string;
  result: string;
  reason: string;
  startedAt: string;
  attempts: number;
  knowledgeBacking?: string | null;
  endpointKind?: string;
}

/**
 * What Contoso Treasury lets a user see and change about their own verification.
 *
 * This is the page that makes EntraGuard legible to the person it protects. Everywhere
 * else in this system the user is told a verdict; here they can see how they are reached,
 * what has been stored about them, and every time they were called and what came of it.
 *
 * Showing the history is the part that matters. A voice factor that can refuse you is a
 * factor you are entitled to audit — and an attacker who triggers verifications against
 * someone else's account leaves a trail the victim can actually find.
 */
export function TreasurySettings() {
  const auth = useEntraSignIn();
  const [history, setHistory] = useState<VerificationRow[]>([]);
  const [challenge, setChallenge] = useState<{ registered: boolean; question?: string; backing?: string } | null>(null);
  const [loading, setLoading] = useState(true);

  const upn = auth.identity?.upn;

  useEffect(() => {
    if (!upn) return;

    const load = async () => {
      try {
        const [attempts, stored] = await Promise.all([
          fetch('/api/verify', { cache: 'no-store' }).then((r) => (r.ok ? r.json() : [])),
          auth.identity
            ? fetch(
                `/api/verify/knowledge?tenantId=${encodeURIComponent(auth.identity.tenantId)}`
                + `&objectId=${encodeURIComponent(auth.identity.objectId)}`,
                { cache: 'no-store' },
              ).then((r) => (r.ok ? r.json() : null))
            : Promise.resolve(null),
        ]);

        // Only this user's own attempts. The full list belongs to the security console,
        // not to a customer application.
        setHistory((attempts ?? []).filter((a: VerificationRow) => a.upn === upn));
        setChallenge(stored);
      } catch {
        // Leave the page in its loading-failed state rather than showing an empty history
        // that reads as "you have never been verified".
      } finally {
        setLoading(false);
      }
    };

    void load();
  }, [upn, auth.identity]);

  if (auth.state !== 'signed-in') {
    return (
      <div className="rp">
        <header className="rp-header">
          <span className="rp-logo">CT</span>
          <span>
            <span className="rp-brandname">Contoso Treasury</span>
            <span className="rp-brandsub">Settings</span>
          </span>
        </header>
        <main className="rp-center">
          <div className="rp-card">
            <h1 className="rp-h1">Sign in to view settings</h1>
            <p className="rp-sub">These settings are specific to your work account.</p>
            <a className="rp-btn block" href="/" style={{ textAlign: 'center', display: 'block' }}>
              Go to sign in
            </a>
          </div>
        </main>
      </div>
    );
  }

  const outcome = (result: string) =>
    result === 'Passed'
      ? 'ok'
      : result === 'BlockedCoercion' || result === 'BlockedVoiceMismatch'
        ? 'warn'
        : 'err';

  return (
    <div className="rp">
      <header className="rp-header">
        <span className="rp-logo">CT</span>
        <span>
          <span className="rp-brandname">Contoso Treasury</span>
          <span className="rp-brandsub">Settings</span>
        </span>
        <span className="rp-header-right">
          <span>{auth.identity?.displayName}</span>
          <a className="rp-btn secondary" href="/">Back</a>
        </span>
      </header>

      <main className="rp-main">
        <div className="rp-appbar">
          <h1 className="rp-h1" style={{ marginBottom: 2 }}>Verification settings</h1>
        </div>

        <section style={{ background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 4, padding: 18, marginBottom: 18 }}>
          <h2 className="rp-h1" style={{ fontSize: 16, marginBottom: 10 }}>Your identity</h2>
          <div className="rp-field">
            <span className="rp-label">Account</span>
            <div className="mono" style={{ fontSize: 13 }}>{auth.identity?.upn}</div>
          </div>
          <div className="rp-field">
            <span className="rp-label">Directory object ID</span>
            <div className="mono" style={{ fontSize: 11, color: 'var(--rp-text-3)' }}>{auth.identity?.objectId}</div>
          </div>
          <p className="rp-hint">
            EntraGuard calls the Teams account this token identifies. It is taken from your
            sign-in, never from a field you can edit — a second factor you can redirect is
            not a second factor.
          </p>
        </section>

        <VoiceEnrollment
          objectId={auth.identity?.objectId}
          getAccessToken={auth.getAccessToken}
          getTokens={auth.getTokens}
        />

        <section style={{ background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 4, padding: 18, marginBottom: 18 }}>
          <h2 className="rp-h1" style={{ fontSize: 16, marginBottom: 10 }}>How you are challenged</h2>

          <div className="rp-result ok" style={{ marginBottom: 12 }}>
            <div className="rp-result-title">Number match — always</div>
            <div className="rp-result-body">
              A two-digit code appears on your screen and you enter it on the call. This is the
              strong factor: the code is visible only to whoever is looking at your screen.
            </div>
          </div>

          <div className="rp-result ok" style={{ marginBottom: 12 }}>
            <div className="rp-result-title">Live identity questions — when available</div>
            <div className="rp-result-body">
              Up to three questions about your own recent sign-in activity — where from, into
              what, on what device. Nothing is stored, so there is nothing to leak, and the
              answers stop being true within a day. Requires your tenant to have consented to
              EntraGuard reading its sign-in logs.
            </div>
          </div>

          <div className="rp-result ok" style={{ marginBottom: 12 }}>
            <div className="rp-result-title">Voice comparison — when enrolled</div>
            <div className="rp-result-body">
              Your speech on the call is compared to the profile you recorded. A weak match
              asks for a stronger factor; it never refuses you by itself, because voice
              matching over a phone line is not accurate enough to carry that weight alone.
            </div>
          </div>

          <div className={`rp-result ${challenge?.registered ? 'ok' : 'warn'}`}>
            <div className="rp-result-title">
              Backup security question — {challenge?.registered ? 'registered' : 'not set'}
            </div>
            <div className="rp-result-body">
              {challenge?.registered
                ? `“${challenge.question}” — used only when live sign-in activity is unavailable.`
                : 'Used only when live sign-in activity is unavailable, for example on a very new '
                  + 'account. You can add one below.'}
              <br /><br />
              Stored questions are the weakest option here by some distance — NIST 800-63
              rejects them as an authenticator, because the answers are researchable and
              permanent. It is a fallback, not the main check.
            </div>
          </div>

          {/*
            Moved here out of first-run setup. A factor this page itself calls the weakest
            option available, and which a published standard rejects outright, should be
            reachable for the accounts that genuinely need it rather than put in front of
            everybody before they have signed in once.
          */}
          <KnowledgeSetup
            tenantId={auth.identity?.tenantId}
            objectId={auth.identity?.objectId}
            upn={upn ?? ''}
          />
        </section>

        <section style={{ background: '#fff', border: '1px solid var(--rp-border)', borderRadius: 4, overflow: 'hidden' }}>
          <div style={{ padding: '14px 18px 0' }}>
            <h2 className="rp-h1" style={{ fontSize: 16, marginBottom: 4 }}>Your verification history</h2>
            <p className="rp-sub" style={{ marginBottom: 12 }}>
              Every time EntraGuard called you, and what it decided. If you see a verification
              you did not start, someone tried to sign in as you.
            </p>
          </div>

          {loading ? (
            <p className="rp-hint" style={{ padding: '0 18px 18px' }}>Loading…</p>
          ) : history.length === 0 ? (
            <p className="rp-hint" style={{ padding: '0 18px 18px' }}>
              No verifications yet for this account.
            </p>
          ) : (
            <table className="rp-table">
              <thead>
                <tr><th>When</th><th>Endpoint</th><th>Outcome</th><th>Detail</th></tr>
              </thead>
              <tbody>
                {history.slice(0, 20).map((row) => (
                  <tr key={row.verificationId}>
                    <td className="mono" style={{ fontSize: 11 }}>
                      {new Date(row.startedAt).toLocaleString()}
                    </td>
                    <td>{row.endpointKind ?? '—'}</td>
                    <td><span className={`rp-badge ${outcome(row.result)}`}>{row.result}</span></td>
                    <td style={{ fontSize: 12, color: 'var(--rp-text-2)' }}>{row.reason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <p className="rp-hint">
          Contoso Treasury does not decide whether you passed. It asks EntraGuard and honours
          the answer — which is why this page can show you the verdict but never change it.
        </p>
      </main>
    </div>
  );
}
