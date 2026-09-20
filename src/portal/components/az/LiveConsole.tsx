'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { CallTimeline, formatClock, type TimelineAction, type TimelinePoint, type TimelineUtterance } from './CallTimeline';
import { RiskMeter } from './RiskMeter';
import { Card, Empty, MessageBar, Metric } from './Surfaces';
import { SimulateCall } from './SimulateCall';
import { IconPhone, IconShield, IconSiem, IconCheck } from './Icons';
import { useEntraSignIn } from '../rp/useEntraSignIn';

interface Line { sessionId: string; speaker: string; text: string; offsetMs: number; isFinal: boolean; simulated?: boolean }
interface Assessment {
  sessionId: string; riskScore: number; confidence: number; stage: string;
  vectors: string[]; evidence: { quote: string; speaker: string }[];
  rationale: string; analysisLatencyMs: number; at: string; simulated?: boolean;
}
interface Decision {
  sessionId: string; effectiveRisk: number; summary: string; actions: string[];
  blocked: { action: string; reason: string }[]; intervening: boolean; at: string;
}
interface Remediation {
  sessionId: string; action: string; outcome: string; reason: string;
  ladderRung: number; at: string;
}

const OUTCOME_TONE: Record<string, string> = {
  Succeeded: 'success',
  Unavailable: 'warning',
  BlockedByPolicy: 'warning',
  Failed: 'error',
};

const spaced = (value: string) => value.replace(/([A-Z])/g, ' $1').trim();

/**
 * The live monitoring position.
 *
 * Everything here arrives over SignalR while a call is in progress. Nothing is polled and
 * nothing is synthesised — an empty panel means no call is up, which is information.
 */
export function LiveConsole({ hubUrl }: { hubUrl: string }) {
  const auth = useEntraSignIn();
  const [connection, setConnection] = useState<'connecting' | 'live' | 'lost'>('connecting');
  const [lines, setLines] = useState<Line[]>([]);
  const [interim, setInterim] = useState<Line | null>(null);
  const [assessments, setAssessments] = useState<Assessment[]>([]);
  const [decisions, setDecisions] = useState<Decision[]>([]);
  const [remediations, setRemediations] = useState<Remediation[]>([]);
  const [startedAt, setStartedAt] = useState<number | null>(null);
  const [simulated, setSimulated] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!hubUrl || auth.state !== 'signed-in') return;

    const hub = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: async () => (await auth.getAccessToken()) ?? '', withCredentials: false })
      // A dropped hub connection mid-interception must recover on its own; an analyst
      // reloading the page during a live call is the last thing anyone needs.
      .withAutomaticReconnect([0, 1000, 3000, 6000, 10_000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    const reset = () => { setLines([]); setAssessments([]); setDecisions([]); setRemediations([]); setInterim(null); };

    hub.on('Transcript', (line: Line) => {
      setStartedAt((current) => current ?? Date.now() - line.offsetMs);
      if (line.simulated) setSimulated(true);
      if (line.isFinal) { setInterim(null); setLines((c) => [...c, line]); }
      else setInterim(line);
    });
    hub.on('Assessment', (a: Assessment) => setAssessments((c) => [...c, a]));
    hub.on('Decision', (d: Decision) => setDecisions((c) => [d, ...c].slice(0, 40)));
    hub.on('Remediation', (r: Remediation) => setRemediations((c) => [r, ...c].slice(0, 40)));
    hub.on('Session', (event: { state: string; simulated?: boolean }) => {
      if (event.state === 'answered' || event.state === 'connected') {
        reset();
        setStartedAt(Date.now());
        setSimulated(Boolean(event.simulated));
      }
    });

    hub.onreconnecting(() => setConnection('connecting'));
    hub.onreconnected(() => setConnection('live'));
    hub.onclose(() => setConnection('lost'));
    hub.start().then(() => setConnection('live')).catch(() => setConnection('lost'));

    setConnection('connecting');
    return () => void hub.stop();
  }, [hubUrl, auth.state, auth.getAccessToken]);

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    end.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }, [lines.length, interim]);

  const latest = assessments.at(-1);
  const elapsed = startedAt ? now - startedAt : 0;
  const hasCall = lines.length > 0 || assessments.length > 0;

  const riskPoints = useMemo<TimelinePoint[]>(() =>
    startedAt ? assessments.map((a) => ({ offsetMs: new Date(a.at).getTime() - startedAt, risk: a.riskScore })) : [],
  [assessments, startedAt]);

  const timelineUtterances = useMemo<TimelineUtterance[]>(() =>
    lines.map((l) => ({ speaker: l.speaker, offsetMs: l.offsetMs, text: l.text })), [lines]);

  const timelineActions = useMemo<TimelineAction[]>(() =>
    startedAt
      ? remediations
          .filter((r) => r.outcome === 'Succeeded' && r.action !== 'LogTelemetry')
          .map((r) => ({ offsetMs: new Date(r.at).getTime() - startedAt, label: spaced(r.action) }))
      : [],
  [remediations, startedAt]);

  const connectionBadge =
    connection === 'live' ? { cls: 'success', text: 'Stream live' }
    : connection === 'connecting' ? { cls: 'warning', text: 'Connecting' }
    : { cls: 'error', text: 'Stream lost' };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <details className="admin-disclosure"><summary>Simulation tools</summary><div><p className="admin-meta-note">A simulated transcript exercises the real model and configured integrations. It can write telemetry and incidents; it is not a side-effect-free preview.</p><SimulateCall /></div></details>

      <Card
        title={hasCall ? 'Call in progress' : 'Monitoring position'}
        icon={<IconShield size={15} />}
        actions={
          <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            {simulated && <span className="az-badge info">Simulated</span>}
            {hasCall && <span className="az-badge num">{formatClock(elapsed)}</span>}
            <span className={`az-badge ${connectionBadge.cls}`}>
              <span className={`az-dot${connection === 'live' ? ' az-pulse' : ''}`} />
              {connectionBadge.text}
            </span>
          </span>
        }
        footer={hasCall ? `${assessments.length} assessment${assessments.length === 1 ? '' : 's'} · ${lines.length} phrases` : undefined}
      >
        {hasCall ? (
          <>
            <RiskMeter score={latest?.riskScore ?? 0} confidence={latest?.confidence} stage={latest?.stage} />
            <div style={{ marginTop: 14, borderTop: '1px solid var(--az-border-soft)' }}>
              <CallTimeline
                utterances={timelineUtterances}
                riskPoints={riskPoints}
                actions={timelineActions}
                durationMs={elapsed}
              />
            </div>
          </>
        ) : (
          <Empty
            title="No call in progress"
            detail="Run a simulation above, or place a call to the monitored ACS identity — interception starts the moment it rings."
          />
        )}
      </Card>

      <div className="az-grid c2">
        <Card
          title="Transcript"
          icon={<IconPhone size={15} />}
          actions={<span className="az-badge">{lines.length} phrases</span>}
        >
          {lines.length === 0 && !interim ? (
            <Empty title="Silence" detail="Recognised speech appears here as it is spoken." />
          ) : (
            <div className="az-transcript">
              {lines.map((line, index) => (
                <div className="az-line" key={index}>
                  <span className={`az-line-who ${who(line.speaker)}`}>{who(line.speaker).toUpperCase()}</span>
                  <span className="az-line-text">{line.text}</span>
                </div>
              ))}
              {interim && (
                <div className="az-line">
                  <span className={`az-line-who ${who(interim.speaker)}`}>{who(interim.speaker).toUpperCase()}</span>
                  <span className="az-line-text interim">{interim.text}…</span>
                </div>
              )}
              <div ref={end} />
            </div>
          )}
        </Card>

        <Card
          title="Analyst verdict"
          icon={<IconShield size={15} />}
          actions={latest && <span className="az-badge num">{latest.analysisLatencyMs} ms</span>}
        >
          {!latest ? (
            <Empty
              title="No verdict yet"
              detail="The Analyst scores the conversation every few seconds once there is enough to read."
            />
          ) : (
            <>
              {latest.vectors.length > 0 && (
                <div className="az-badge-row" style={{ marginBottom: 12 }}>
                  {latest.vectors.map((vector) => (
                    <span key={vector} className="az-badge severe">{spaced(vector)}</span>
                  ))}
                </div>
              )}
              <p style={{ margin: '0 0 14px', fontSize: 13, color: 'var(--az-text-2)', lineHeight: 1.55 }}>
                {latest.rationale}
              </p>
              {latest.evidence.length > 0 && (
                <>
                  <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--az-text-3)', marginBottom: 8, textTransform: 'uppercase' }}>
                    Evidence
                  </div>
                  {latest.evidence.map((span, index) => (
                    <blockquote className="az-quote" key={index}>
                      &ldquo;{span.quote}&rdquo;
                      <cite>{span.speaker}</cite>
                    </blockquote>
                  ))}
                </>
              )}
            </>
          )}
        </Card>
      </div>

      <div className="az-grid c2">
        <Card
          title="Policy gate"
          icon={<IconCheck size={15} />}
          footer="Withheld actions are recorded too — that is how you answer “why didn’t it act?”"
        >
          {decisions.length === 0 ? (
            <Empty title="No decisions yet" detail="Every verdict passes the gate, including those that authorise nothing." />
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 13 }}>
              {decisions.slice(0, 6).map((decision, index) => (
                <div key={index}>
                  <div style={{ fontSize: 12.5, marginBottom: 5 }}>{decision.summary}</div>
                  {decision.blocked.map((blocked, blockedIndex) => (
                    <div key={blockedIndex} style={{ fontSize: 11.5, color: 'var(--az-text-3)', lineHeight: 1.5, marginTop: 3 }}>
                      <span className="az-badge warning" style={{ marginRight: 6 }}>withheld</span>
                      {spaced(blocked.action)} — {blocked.reason}
                    </div>
                  ))}
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card title="Actions taken" icon={<IconSiem size={15} />}>
          {remediations.length === 0 ? (
            <Empty title="No remediation" detail="Actions appear here with their real outcome, including failures." />
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
              {remediations.map((remediation, index) => (
                <div key={index} style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
                  <span className={`az-badge ${OUTCOME_TONE[remediation.outcome] ?? ''}`}>
                    {remediation.outcome}
                  </span>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontSize: 12.5 }}>
                      {spaced(remediation.action)}
                      <span className="t-muted" style={{ fontSize: 11, marginLeft: 6 }}>
                        rung {remediation.ladderRung}
                      </span>
                    </div>
                    <div style={{ fontSize: 11.5, color: 'var(--az-text-3)', lineHeight: 1.5 }}>
                      {remediation.reason}
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </Card>
      </div>

      {simulated && (
        <MessageBar intent="info" title="Simulated session.">
          This run replayed a scripted transcript. The Analyst, policy gate, Actuator and
          Sentinel writes were real; ACS and Speech were bypassed.
        </MessageBar>
      )}
    </div>
  );
}

function who(speaker: string): string {
  const normalised = speaker.toLowerCase();
  if (normalised === 'caller') return 'caller';
  if (normalised === 'protecteduser') return 'user';
  return 'unknown';
}
