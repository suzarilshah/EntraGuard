/**
 * The call as a multitrack recording.
 *
 * A call genuinely is a time-series with two channels and events pinned at moments, which
 * is what a DAW session view exists to show. Reading it that way — risk as the automation
 * lane, each speaker as a track, interventions as markers — makes the shape of an attack
 * legible at a glance: the caller's blocks dominate, the user's replies shorten, the risk
 * lane climbs, and the intervention pin lands before it tops out.
 *
 * That last relationship is the entire product argument, and no arrangement of stat tiles
 * can show it.
 */
export interface TimelineUtterance { speaker: string; offsetMs: number; text: string }
export interface TimelinePoint { offsetMs: number; risk: number }
export interface TimelineAction { offsetMs: number; label: string }

/** Approximate spoken duration so utterance blocks have honest width. */
function estimateDurationMs(text: string): number {
  // ~2.7 words/second is unhurried conversational speech. The 400 ms floor keeps very
  // short utterances ("okay") visible as marks instead of collapsing to nothing.
  return Math.max(400, (text.trim().split(/\s+/).length / 2.7) * 1000);
}

export function CallTimeline({
  utterances,
  riskPoints,
  actions,
  durationMs,
}: {
  utterances: TimelineUtterance[];
  riskPoints: TimelinePoint[];
  actions: TimelineAction[];
  durationMs: number;
}) {
  // A minimum span stops a four-second call rendering as one full-width smear.
  const span = Math.max(durationMs, 30_000);
  const pct = (ms: number) => `${Math.min(100, Math.max(0, (ms / span) * 100))}%`;

  const caller = utterances.filter((u) => u.speaker.toLowerCase() === 'caller');
  const user = utterances.filter((u) => u.speaker.toLowerCase() === 'protecteduser');

  const path =
    riskPoints.length > 1
      ? riskPoints
          .map((point, index) => {
            const x = Math.min(100, (point.offsetMs / span) * 100);
            const y = 100 - Math.max(0, Math.min(100, point.risk));
            return `${index === 0 ? 'M' : 'L'} ${x.toFixed(2)} ${y.toFixed(2)}`;
          })
          .join(' ')
      : null;

  return (
    <div className="az-timeline">
      <div className="az-track">
        <span className="az-track-label">Risk</span>
        <div className="az-lane risk">
          {path ? (
            <svg
              viewBox="0 0 100 100"
              preserveAspectRatio="none"
              style={{ width: '100%', height: '100%', display: 'block' }}
              aria-label="Risk trajectory across the call"
            >
              {/* The containment threshold, so the trajectory reads against the line that
                  actually triggers identity remediation. */}
              <line
                x1="0" y1="20" x2="100" y2="20"
                stroke="var(--az-severe)" strokeWidth="0.4" strokeDasharray="1.5 1.5"
                opacity="0.6" vectorEffect="non-scaling-stroke"
              />
              <path d={`${path} L 100 100 L 0 100 Z`} fill="var(--az-blue)" opacity="0.16" />
              <path d={path} fill="none" stroke="var(--az-blue-light)" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
            </svg>
          ) : (
            <span className="mono t-muted" style={{ position: 'absolute', left: 10, top: 22 }}>
              awaiting first assessment
            </span>
          )}
          {actions.map((action, index) => (
            <span
              key={`${action.label}-${index}`}
              className="az-pin"
              data-label={action.label}
              style={{ left: pct(action.offsetMs) }}
            />
          ))}
        </div>
      </div>

      {([['Caller', caller, 'caller'], ['User', user, 'user']] as const).map(([label, lines, cls]) => (
        <div className="az-track" key={label}>
          <span className="az-track-label">{label}</span>
          <div className="az-lane">
            {lines.map((line, index) => (
              <span
                key={index}
                className={`az-utt ${cls}`}
                style={{ left: pct(line.offsetMs), width: pct(estimateDurationMs(line.text)) }}
                title={line.text}
              />
            ))}
          </div>
        </div>
      ))}

      <div className="az-ruler">
        <span className="az-track-label" />
        <div className="az-ruler-marks">
          {[0, 0.25, 0.5, 0.75, 1].map((fraction) => (
            <span key={fraction}>{formatClock(span * fraction)}</span>
          ))}
        </div>
      </div>
    </div>
  );
}

export function formatClock(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(total / 60)}:${(total % 60).toString().padStart(2, '0')}`;
}
