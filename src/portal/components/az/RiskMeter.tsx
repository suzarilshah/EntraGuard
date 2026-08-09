/**
 * Risk as a segmented level meter, in Fluent severity colours.
 *
 * The thresholds are the policy gate's, not invented for the UI: 40 notify, 60 warn,
 * 80 contain, 90 terminate. What the operator sees and what the system acts on are the
 * same scale, so a reader can predict the system's behaviour from the display.
 */
const SEGMENTS = 25;

export type Band = 'success' | 'warning' | 'severe' | 'error';

export function bandFor(score: number): Band {
  if (score >= 90) return 'error';
  if (score >= 80) return 'severe';
  if (score >= 60) return 'warning';
  return 'success';
}

export const BAND_LABEL: Record<Band, string> = {
  success: 'Nominal',
  warning: 'Elevated',
  severe: 'High',
  error: 'Critical',
};

export function RiskMeter({
  score,
  confidence,
  stage,
}: {
  score: number;
  confidence?: number;
  stage?: string;
}) {
  const clamped = Math.max(0, Math.min(100, score));
  const lit = Math.round((clamped / 100) * SEGMENTS);
  const band = bandFor(clamped);

  return (
    <div className="az-meter">
      <div
        className="az-meter-scale"
        role="meter"
        aria-valuenow={Math.round(clamped)}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={`Risk score ${Math.round(clamped)} of 100 — ${BAND_LABEL[band]}`}
      >
        {Array.from({ length: SEGMENTS }, (_, index) => {
          // Each segment lights in the colour of the band it occupies, not the band the
          // current score sits in — so the meter shows the climb through the scale the way
          // a real level meter does, rather than flooding one flat colour.
          const segmentBand = bandFor(((index + 1) / SEGMENTS) * 100);
          return (
            <span key={index} className={`az-meter-seg${index < lit ? ` on-${segmentBand}` : ''}`} />
          );
        })}
      </div>

      <div className="az-meter-read">
        <span className={`az-meter-value t-${band}`}>{Math.round(clamped)}</span>
        <span style={{ display: 'flex', gap: 14, alignItems: 'baseline', flexWrap: 'wrap' }}>
          {stage && <span className="mono t-muted">{stage}</span>}
          {confidence !== undefined && (
            <span className="mono t-muted">confidence {Math.round(confidence * 100)}%</span>
          )}
          <span className={`az-badge ${band}`}>{BAND_LABEL[band]}</span>
        </span>
      </div>
    </div>
  );
}
