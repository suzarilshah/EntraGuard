'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { analystFetch, GOOGLE_CONSOLE_URL } from './useGoogleSignIn';
import { startMicrophone } from './micClips';

/** What Treasury's existing result and dashboard screens read. */
export interface GoogleVerificationResult {
  verificationId: string;
  callState: string;
  matchCode: string | null;
  result: string;
  reason: string;
  grantsAccess: boolean;
  isComplete: boolean;
  attempts: number;
  peakRiskDuringCall: number;
  endpointKind: string;
  assuranceBasis?: string[];
  completedAt?: string;
}

interface Session { id: string; number: string; question: string }
interface Assessment { riskScore: number; confidence: number; complianceStage: string; vectors: string[] }
interface Verdict { outcome: string; reason: string; judgeReason?: string; peakRisk: number | null }

const RESULT: Record<string, string> = { passed: 'Passed', 'blocked-coaching': 'BlockedCoercion', failed: 'Failed' };

/**
 * Step-up for a Google-signed-in user, on Treasury's own verification card.
 *
 * The Microsoft path rings Teams or a phone through Azure Communication Services. A Google
 * account has no Entra directory behind it, so here the "call" is this browser: the
 * microphone streams ~4-second clips to the Gemini analyst on Cloud Run, which transcribes
 * and scores them, and the same deterministic rule refuses on coaching (risk ≥ 60 with
 * confidence ≥ 0.75) before it looks at the number. The question comes from this sign-in.
 */
export function GoogleVerification({ getIdToken, onComplete, onCancel }: {
  getIdToken: () => Promise<string>;
  onComplete: (result: GoogleVerificationResult) => void;
  onCancel: () => void;
}) {
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [entered, setEntered] = useState('');
  const [listening, setListening] = useState(false);
  const [level, setLevel] = useState(0);
  const [clips, setClips] = useState(0);
  const [heard, setHeard] = useState<string[]>([]);
  const [latest, setLatest] = useState<Assessment | null>(null);
  const [running, setRunning] = useState<'mic' | 'demo' | 'deciding' | null>(null);
  const mic = useRef<{ stop: () => Promise<void> } | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    analystFetch<Session>(getIdToken, '/api/sessions', { app: 'treasury' })
      .then(setSession)
      .catch((e: Error) => setError(e.message));
    return () => { void mic.current?.stop(); };
  }, [getIdToken]);

  const finish = useCallback(async (enteredNumber: string) => {
    if (!session) return;
    setRunning('deciding');
    try {
      if (mic.current) { await mic.current.stop(); mic.current = null; setListening(false); }
      const { verdict } = await analystFetch<{ verdict: Verdict }>(getIdToken, `/api/sessions/${session.id}/finish`, { enteredNumber: enteredNumber || null });
      onComplete({
        verificationId: session.id,
        callState: 'Completed',
        matchCode: session.number,
        result: RESULT[verdict.outcome] ?? 'Failed',
        reason: verdict.reason,
        grantsAccess: verdict.outcome === 'passed',
        isComplete: true,
        attempts: 0,
        peakRiskDuringCall: verdict.peakRisk ?? 0,
        endpointKind: 'browser · Gemini on Google Cloud',
        assuranceBasis: verdict.judgeReason ? [`Answer check: ${verdict.judgeReason}`] : undefined,
        completedAt: new Date().toISOString(),
      });
    } catch (e) {
      setError((e as Error).message);
      setRunning(null);
    }
  }, [getIdToken, onComplete, session]);

  const toggleMic = async () => {
    if (!session) return;
    setError(null);
    if (mic.current) {
      await mic.current.stop();
      mic.current = null;
      setListening(false);
      setRunning(null);
      return;
    }
    try {
      setRunning('mic');
      mic.current = await startMicrophone({
        onLevel: setLevel,
        onClip: async (wav) => {
          try {
            const r = await analystFetch<{ transcript: string; assessment: Assessment }>(getIdToken, `/api/sessions/${session.id}/audio`, { wav });
            setClips((n) => n + 1);
            setLatest(r.assessment);
            if (r.transcript) setHeard((h) => [...h, r.transcript].slice(-3));
          } catch (e) { setError((e as Error).message); }
        },
      });
      setListening(true);
    } catch (e) {
      setRunning(null);
      setError(`Microphone unavailable: ${(e as Error).message}. Use a demo call instead.`);
    }
  };

  // For judges without a microphone: the analyst plays a scripted call into this session.
  const runDemo = async (scenario: 'genuine' | 'coached') => {
    if (!session) return;
    setError(null);
    setRunning('demo');
    let number = entered;
    try {
      for (;;) {
        const step = await analystFetch<{ done: boolean; line?: { speaker: string; text: string }; assessment?: Assessment; enteredNumber?: string | null }>(
          getIdToken, `/api/sessions/${session.id}/script`, { scenario });
        if (step.enteredNumber && !number) { number = step.enteredNumber; setEntered(step.enteredNumber); }
        if (step.line && step.line.speaker !== 'system') setHeard((h) => [...h, step.line!.text].slice(-3));
        if (step.assessment) { setLatest(step.assessment); setClips((n) => n + 1); }
        if (step.done) break;
        await new Promise((r) => setTimeout(r, 500));
      }
      await finish(number);
    } catch (e) {
      setError((e as Error).message);
      setRunning(null);
    }
  };

  const press = (key: string) => setEntered((v) => (v.length >= 2 ? v : v + key));

  if (!session) {
    return (
      <div className="rp-card">
        <h1 className="rp-h1">Preparing your verification…</h1>
        {error && <div className="rp-result err" role="alert"><div className="rp-result-title">Could not start verification</div><div className="rp-result-body">{error}</div></div>}
        <button className="rp-btn secondary" type="button" onClick={onCancel} style={{ marginTop: 14 }}>Cancel</button>
      </div>
    );
  }

  const deciding = running === 'deciding';
  const steps = [
    { label: 'Calling this browser', done: true },
    { label: listening || running === 'demo' ? 'Listening — Gemini is assessing the call' : 'Answer to start the call', done: clips > 0 },
    { label: 'Number entered', done: entered.length === 2 },
    { label: 'Checking', done: false },
  ];
  const current = steps.findIndex((s) => !s.done);

  return (
    <div className="rp-card">
      <div className="rp-steps"><span className="rp-step done" /><span className="rp-step done" /><span className="rp-step active" /></div>
      <h1 className="rp-h1">Enter this number on the call</h1>
      <p className="rp-sub">EntraGuard is calling this browser. Answer, say the question’s answer out loud, then enter the number.</p>

      <div className="rp-code-label">Your number</div>
      <div className="rp-code">{session.number}</div>

      <div className="rp-result" style={{ marginTop: 16 }}>
        <div className="rp-result-title">EntraGuard asks</div>
        <div className="rp-result-body">{session.question}</div>
      </div>

      <ol className="rp-progress" style={{ marginTop: 18 }}>
        {steps.map((step, index) => (
          <li key={step.label} className={index < current ? 'done' : index === current ? 'active' : ''}>
            <span className="rp-progress-dot" aria-hidden="true" /><span>{step.label}</span>
          </li>
        ))}
      </ol>

      <div className={`rp-mic ${listening || running === 'demo' ? 'live' : 'off'}`} style={{ marginTop: 14 }}>
        <span className="rp-mic-dot" aria-hidden="true" style={listening ? { transform: `scale(${1 + level})` } : undefined} />
        <span>
          {clips > 0
            ? <>EntraGuard is listening — {clips} {clips === 1 ? 'assessment' : 'assessments'} by Gemini on Google Cloud{latest && <>, current risk {Math.round(latest.riskScore)}</>}</>
            : listening ? <>Microphone open — speak, and Gemini will assess every few seconds…</> : <>Not listening yet.</>}
        </span>
      </div>
      {heard.length > 0 && <p className="rp-hint" style={{ marginTop: 8 }}>Heard: “{heard.at(-1)}”</p>}

      <button className={`rp-btn block${listening ? ' secondary' : ''}`} type="button" style={{ marginTop: 14 }}
        onClick={() => void toggleMic()} disabled={deciding || running === 'demo'}>
        {listening ? 'Hang up microphone' : 'Answer the call (use microphone)'}
      </button>

      <div className="rp-entered" aria-live="polite">{entered || ' '}</div>
      <div className="rp-keypad">
        {['1','2','3','4','5','6','7','8','9','*','0','#'].map((key) => (
          <button key={key} className="rp-key" type="button" onClick={() => press(key)}
            disabled={deciding || running === 'demo' || entered.length >= 2 || key === '*' || key === '#'}>{key}</button>
        ))}
      </div>

      <div style={{ display: 'flex', gap: 8, marginTop: 18 }}>
        <button className="rp-btn" type="button" onClick={() => void finish(entered)} disabled={deciding || running === 'demo' || entered.length !== 2}>
          {deciding ? 'Checking…' : 'Finish verification'}
        </button>
        <button className="rp-btn secondary" type="button" onClick={() => setEntered('')} disabled={entered.length === 0 || deciding}>Clear</button>
        <button className="rp-btn secondary" type="button" onClick={onCancel} disabled={deciding}>Cancel</button>
      </div>

      <div className="rp-result warn" style={{ marginTop: 18 }}>
        <div className="rp-result-title">No microphone? Play a demo call</div>
        <div className="rp-result-body">
          Both reach the same number and the same correct answer — only the coaching differs.
          <div style={{ display: 'flex', gap: 8, marginTop: 10, flexWrap: 'wrap' }}>
            <button className="rp-btn secondary" type="button" onClick={() => void runDemo('genuine')} disabled={running !== null}>Genuine user, alone</button>
            <button className="rp-btn secondary" type="button" onClick={() => void runDemo('coached')} disabled={running !== null}>Coached in the room</button>
          </div>
        </div>
      </div>

      {error && <div className="rp-result err" role="alert" style={{ marginTop: 14 }}><div className="rp-result-title">Something went wrong</div><div className="rp-result-body">{error}</div></div>}

      <p className="rp-hint">
        Watch the live risk in the <a href={GOOGLE_CONSOLE_URL} target="_blank" rel="noreferrer">EntraGuard console on Google Cloud</a>.
        Never enter this number because someone asked you to.
      </p>
    </div>
  );
}
