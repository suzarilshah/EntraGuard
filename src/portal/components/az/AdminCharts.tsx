import { Empty } from './Surfaces';
import type { QueryRows } from '@/lib/azure/logs';

const SERIES = [
  { key: 'Passed', label: 'Checks passed', color: 'var(--az-blue)' },
  { key: 'Coercion', label: 'Coaching blocked', color: 'var(--az-error)' },
  { key: 'StepUp', label: 'Additional MFA', color: 'var(--az-warning)' },
  { key: 'Other', label: 'Other outcomes', color: 'var(--az-text-3)' },
];
export function VerificationChart({ data }: { data: QueryRows }) {
  if (!data.rows.length) return <Empty title="No recorded verifications in this window" detail="Choose another time range or complete a verification in Treasury." />;
  const bins = data.rows.map(row => ({ time: String(row[data.columns.indexOf('TimeGenerated')]), values: SERIES.map(series => Number(row[data.columns.indexOf(series.key)] ?? 0)) }));
  const maximum = Math.max(1, ...bins.map(bin => bin.values.reduce((a, b) => a + b, 0)));
  const labelEvery = Math.max(1, Math.ceil(bins.length / 6));
  return <><div className="admin-chart" role="img" aria-label={`Verification outcomes by UTC time; highest bin ${maximum} attempts.`}>
    {bins.map((bin, i) => <div className="admin-chart-bar" key={`${bin.time}-${i}`} title={`${bin.time}: ${SERIES.map((s,j) => `${bin.values[j]} ${s.label.toLowerCase()}`).join(', ')}`}>
      {SERIES.map((series,j) => <span key={series.key} style={{ height: `${bin.values[j] / maximum * 100}%`, background: series.color }} />)}
      {i % labelEvery === 0 && <small>{bin.time.slice(5, 10)} {bin.time.slice(11, 16)}</small>}
    </div>)}
  </div><div className="admin-chart-legend">{SERIES.map(s => <span key={s.key}><i style={{ background:s.color }} />{s.label}</span>)}<span>UTC · latest outcome per verification</span></div></>;
}

export function OutcomeDistribution({ values }: { values: { label: string; value: number; color: string }[] }) {
  const total = values.reduce((sum, item) => sum + item.value, 0);
  if (!total) return <Empty title="No outcomes in this window" detail="Counts appear after telemetry is ingested." />;
  return <div className="admin-outcome-list">{values.map(item => <div key={item.label}><div className="admin-outcome-line"><span>{item.label}</span><strong>{item.value.toLocaleString('en-US')}</strong></div><div className="admin-outcome-track"><span style={{ width: `${item.value / total * 100}%`, background:item.color }} /></div></div>)}</div>;
}
