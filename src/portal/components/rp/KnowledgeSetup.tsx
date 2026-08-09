'use client';

import { useEffect, useState } from 'react';

const SUGGESTED = [
  'What was the name of your first pet?',
  'What street did you live on as a child?',
  'What was the make of your first car?',
  'What is your oldest cousin’s first name?',
];

/**
 * Registration for the spoken knowledge question.
 *
 * Optional on purpose. The number match is the strong factor — it is on a screen a coach in
 * the room cannot see. A spoken answer is the opposite: it is the one part of this flow
 * somebody standing over you can hear and feed you. It earns its place only because the
 * call is being listened to for exactly that, so a prompted answer becomes evidence rather
 * than a bypass.
 */
export function KnowledgeSetup({
  tenantId,
  objectId,
  upn,
}: {
  tenantId?: string;
  objectId?: string;
  upn: string;
}) {
  const [registered, setRegistered] = useState<string | null>(null);
  const [backing, setBacking] = useState<string | null>(null);
  const [question, setQuestion] = useState(SUGGESTED[0]);
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!tenantId || !objectId) return;

    fetch(`/api/verify/knowledge?tenantId=${encodeURIComponent(tenantId)}&objectId=${encodeURIComponent(objectId)}`,
      { cache: 'no-store' })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d) return;
        setRegistered(d.registered ? d.question : null);
        setBacking(d.backing ?? null);
      })
      .catch(() => {
        // Absence of a question is the safe default: the call falls back to the code alone.
      });
  }, [tenantId, objectId]);

  const save = async () => {
    setBusy(true);
    setError(null);

    try {
      const response = await fetch('/api/verify/knowledge', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tenantId, objectId, question, answer }),
      });

      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? `Could not save (${response.status}).`);

      setRegistered(question);
      setBacking(data.backing ?? null);
      // Dropped from memory the moment it is registered. There is nothing to gain by
      // keeping it in a React state field for the rest of the session.
      setAnswer('');
      setOpen(false);
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : String(saveError));
    } finally {
      setBusy(false);
    }
  };

  if (!tenantId || !objectId) return null;

  const where =
    backing === 'directory'
      ? 'stored in your Entra ID directory as a custom security attribute'
      : backing === 'table'
        ? 'stored by EntraGuard, because your tenant has not granted directory attribute access'
        : null;

  return (
    <div style={{ marginTop: 18, borderTop: '1px solid var(--rp-border)', paddingTop: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 10 }}>
        <span className="rp-label" style={{ margin: 0 }}>Security question (optional)</span>
        <button
          className="rp-btn secondary"
          type="button"
          style={{ padding: '3px 10px', fontSize: 12 }}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? 'Cancel' : registered ? 'Change' : 'Add one'}
        </button>
      </div>

      {registered && !open && (
        <div className="rp-result ok" style={{ marginTop: 10 }}>
          <div className="rp-result-title">Question registered</div>
          <div className="rp-result-body">
            “{registered}” — asked aloud after the number match succeeds.
            {where && <><br />Your answer is hashed and {where}. Nothing can read it back,
              including EntraGuard.</>}
          </div>
        </div>
      )}

      {!registered && !open && (
        <p className="rp-hint" style={{ marginTop: 8 }}>
          Without one, the number match alone decides. That is the stronger factor — the code
          is on a screen nobody beside you can see. A spoken answer adds a check that someone
          in the room could hear, which is why EntraGuard listens for that while you answer.
        </p>
      )}

      {open && (
        <div style={{ marginTop: 12 }}>
          <div className="rp-field">
            <label className="rp-label" htmlFor="kq">Question</label>
            <select
              id="kq"
              className="rp-input"
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
            >
              {SUGGESTED.map((q) => <option key={q} value={q}>{q}</option>)}
            </select>
          </div>

          <div className="rp-field">
            <label className="rp-label" htmlFor="ka">Answer</label>
            <input
              id="ka"
              className="rp-input"
              type="password"
              value={answer}
              onChange={(e) => setAnswer(e.target.value)}
              autoComplete="off"
              placeholder="You will say this out loud on the call"
            />
          </div>

          {error && (
            <div className="rp-result err" style={{ marginBottom: 12 }}>
              <div className="rp-result-body">{error}</div>
            </div>
          )}

          <button
            className="rp-btn block"
            type="button"
            onClick={() => void save()}
            disabled={busy || answer.trim().length === 0}
          >
            {busy ? 'Saving…' : 'Register question'}
          </button>

          <p className="rp-hint">
            Pick something you will say the same way every time. Capitalisation, punctuation
            and hesitation are ignored — {upn} only has to say the word itself.
          </p>
        </div>
      )}
    </div>
  );
}
