'use client';

import { useEffect, useState } from 'react';
import { IconPlay, IconRefresh } from './Icons';
import { MessageBar } from './Surfaces';

interface Scenario {
  id: string;
  name: string;
  description: string;
  expected: string;
  lines: number;
}

/**
 * Runs a scripted conversation through the real detection pipeline.
 *
 * Everything downstream of audio is genuine — the same Analyst deployment, the same
 * policy gate, the same Actuator, the same Sentinel writes. Only ACS and Speech are
 * bypassed, because the transcript is supplied rather than recognised.
 *
 * This exists so the system is testable without placing a phone call. Real telephony is
 * the right final test, but it is a poor inner loop and a fragile thing to depend on in
 * front of an audience.
 */
export function SimulateCall() {
  const [scenarios, setScenarios] = useState<Scenario[]>([]);
  const [selected, setSelected] = useState('helpdesk-fraud');
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);

  useEffect(() => {
    fetch('/api/simulate')
      .then((response) => (response.ok ? response.json() : []))
      .then((data) => Array.isArray(data) && setScenarios(data))
      .catch(() => setScenarios([]));
  }, []);

  const current = scenarios.find((s) => s.id === selected);

  const run = async () => {
    setRunning(true);
    setResult(null);
    try {
      const response = await fetch('/api/simulate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          scenario: selected,
          subjectUpn: 'demo.user@contoso.com',
          paceMs: 1200,
        }),
      });
      const data = await response.json();
      setResult(
        response.ok
          ? { ok: true, message: `Replaying ${data.lines} lines as session ${data.sessionId}. Watch it below.` }
          : { ok: false, message: data.error ?? `Media service returned ${response.status}.` },
      );
    } catch (error) {
      setResult({ ok: false, message: error instanceof Error ? error.message : String(error) });
    } finally {
      // The POST only starts the replay; the run itself continues server-side for as long
      // as the script takes, and arrives over SignalR.
      setTimeout(() => setRunning(false), 1500);
    }
  };

  if (scenarios.length === 0) {
    return (
      <MessageBar intent="warning" title="Simulation unavailable.">
        The media service did not return any scenarios. Check that it is running and that
        MEDIA_SERVICE_URL is configured.
      </MessageBar>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <select
          className="az-select"
          value={selected}
          onChange={(event) => setSelected(event.target.value)}
          aria-label="Scenario"
          style={{ minWidth: 230 }}
        >
          {scenarios.map((scenario) => (
            <option key={scenario.id} value={scenario.id}>
              {scenario.name} ({scenario.lines} lines)
            </option>
          ))}
        </select>

        <button className="az-cmd is-on" onClick={run} disabled={running} type="button">
          {running ? <IconRefresh size={15} className="az-spin" /> : <IconPlay size={15} />}
          {running ? 'Starting…' : 'Run simulation'}
        </button>
      </div>

      {current && (
        <div style={{ fontSize: 12.5, color: 'var(--az-text-2)', lineHeight: 1.5 }}>
          {current.description}
          <div style={{ marginTop: 5, color: 'var(--az-text-3)' }}>
            <b style={{ color: 'var(--az-text-2)' }}>Expected: </b>{current.expected}
          </div>
        </div>
      )}

      {result && (
        <MessageBar intent={result.ok ? 'success' : 'error'} title={result.ok ? 'Started.' : 'Failed.'}>
          {result.message}
        </MessageBar>
      )}

      {(
        <MessageBar intent="info" title="This is a replay, not an intercepted call.">
          The Analyst, policy gate, Actuator and Sentinel writes are all real. Only ACS and
          Speech are bypassed. Sessions started here are labelled <b>Simulated</b> everywhere
          they appear.
        </MessageBar>
      )}
    </div>
  );
}
