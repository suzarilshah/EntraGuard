'use client';
import { useId, useMemo, useRef, useState } from 'react';
import { IconSort } from './Icons';
import { AdminGlyph } from './AdminGlyph';
import { Empty } from './Surfaces';
import { fullCell, safeControlPlaneLink, tableCsv } from '@/lib/adminTable';

export type CellFormat = 'text' | 'risk' | 'compositeRisk' | 'outcome' | 'severity' | 'riskLevel' | 'signInResult' | 'verificationResult' | 'datetime' | 'duration' | 'boolean' | 'link';
export interface Column { key: string; label?: string; format?: CellFormat; align?: 'left' | 'right'; width?: 'narrow' | 'medium' | 'wide'; }
const WIDTH: Record<string, React.CSSProperties> = { narrow: { width: '1%', whiteSpace: 'nowrap' }, medium: { minWidth: 200 }, wide: { minWidth: 280 } };

export function DataTable({ columns, rows, emptyTitle, emptyDetail, filterable = true, title = 'Results', details = true, exportable = true }: {
  columns: Column[]; rows: unknown[][]; emptyTitle: string; emptyDetail: string; filterable?: boolean; title?: string; details?: boolean; exportable?: boolean;
}) {
  const [sort, setSort] = useState<{ index: number; dir: 1 | -1 } | null>(null);
  const [filter, setFilter] = useState('');
  const [selected, setSelected] = useState<unknown[] | null>(null);
  const [message, setMessage] = useState('');
  const dialog = useRef<HTMLDialogElement>(null);
  const id = useId();
  const processed = useMemo(() => {
    let out = rows;
    if (filter.trim()) { const needle = filter.trim().toLowerCase(); out = out.filter(row => row.some(cell => fullCell(cell).toLowerCase().includes(needle))); }
    if (sort) out = [...out].sort((a, b) => {
      const x = a[sort.index], y = b[sort.index];
      return (typeof x === 'number' && typeof y === 'number' ? x - y : fullCell(x).localeCompare(fullCell(y))) * sort.dir;
    });
    return out;
  }, [rows, filter, sort]);
  const exportCsv = () => {
    const blob = new Blob(['\uFEFF', tableCsv(columns.map(c => c.label ?? c.key), processed)], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob); const anchor = document.createElement('a'); anchor.href = url;
    anchor.download = `entraguard-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.csv`; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000); setMessage(`Exported ${processed.length} loaded rows.`);
  };
  if (!rows.length) return <Empty title={emptyTitle} detail={emptyDetail} />;
  return <>
    {(filterable || exportable) && <div className="az-table-toolbar">
      {filterable && <input className="az-filter" type="search" placeholder="Filter loaded rows…" value={filter} onChange={e => setFilter(e.target.value)} aria-label={`Filter ${title}`} />}
      {exportable && <button className="az-cmd" type="button" onClick={exportCsv} disabled={!processed.length}>Export CSV</button>}
      <span className="az-count" aria-live="polite">{processed.length} of {rows.length} loaded rows</span>
      {message && <span className="sr-only" role="status">{message}</span>}
    </div>}
    <div className="az-table-wrap" role="region" aria-label={title} tabIndex={0}>
      <table className="az-table"><thead><tr>{columns.map((column, index) => <th key={column.key} style={{ textAlign: column.align ?? 'left', ...(column.width ? WIDTH[column.width] : {}) }} aria-sort={sort?.index === index ? sort.dir === 1 ? 'ascending' : 'descending' : 'none'}>
        <button className="az-sort-button" type="button" onClick={() => setSort(current => current?.index === index ? { index, dir: current.dir === 1 ? -1 : 1 } : { index, dir: 1 })}>{column.label ?? column.key}<IconSort size={10} className={sort?.index === index ? undefined : 'az-dim'} /></button>
      </th>)}{details && <th><span className="sr-only">Row details</span></th>}</tr></thead>
      <tbody>{processed.map((row, rowIndex) => <tr key={rowIndex}>{columns.map((column, index) => <td key={column.key} style={{ textAlign: column.align ?? 'left', ...(column.width === 'narrow' ? WIDTH.narrow : {}) }} title={fullCell(row[index])}>{renderCell(row[index], column.format)}</td>)}{details && <td><button type="button" className="admin-table-action" aria-label={`View row ${rowIndex + 1} details in ${title}`} onClick={() => { setSelected(row); requestAnimationFrame(() => dialog.current?.showModal()); }}>View details</button></td>}</tr>)}</tbody></table>
    </div>
    {!processed.length && <Empty title="No matching rows" detail="Try another filter. Filtering only searches the loaded page." />}
    <dialog ref={dialog} className="admin-detail-dialog" aria-labelledby={`${id}-title`}><div className="admin-detail-head"><h2 id={`${id}-title`}>{title} details</h2><button type="button" className="az-icon-btn" aria-label="Close row details" onClick={() => dialog.current?.close()}><AdminGlyph name="close" /></button></div>
      {selected && <dl>{columns.map((column, i) => <div key={column.key}><dt>{column.label ?? column.key}</dt><dd>{column.format === 'link' ? renderCell(selected[i], 'link') : fullCell(selected[i])}</dd></div>)}</dl>}
    </dialog>
  </>;
}

const VERIFICATION_TONE: Record<string, string> = { Passed: 'success', BlockedCoercion: 'error', BlockedVoiceMismatch: 'warning', StepUpRequired: 'warning', Pending: 'info', Failed: 'warning', Timeout: 'warning', CallFailed: 'warning' };
const OUTCOME_TONE: Record<string, string> = { Succeeded: 'success', Running: 'success', Ready: 'success', Failed: 'error', Unavailable: 'warning', BlockedByPolicy: 'warning', Processing: 'info', Stopped: 'warning' };
function renderCell(value: unknown, format: CellFormat = 'text'): React.ReactNode {
  if (value === null || value === undefined || value === '') return '—';
  if (format === 'link' && safeControlPlaneLink(value)) return <a href={value.url} target="_blank" rel="noreferrer">{value.label} ↗</a>;
  if (format === 'datetime') { const date = new Date(String(value)); return Number.isNaN(date.getTime()) ? formatCell(value) : `${date.toISOString().slice(0,19).replace('T',' ')} UTC`; }
  if (format === 'duration') return Number.isFinite(Number(value)) ? `${(Number(value) / 1000).toFixed(1)} s` : '—';
  if (format === 'boolean') return value === true ? 'Yes' : value === false ? 'No' : 'Unknown';
  if (format === 'verificationResult') return <span className={`az-badge ${VERIFICATION_TONE[String(value)] ?? ''}`}>{String(value) === 'BlockedCoercion' ? 'Blocked · coaching' : String(value) === 'StepUpRequired' ? 'Additional MFA required' : String(value) === 'BlockedVoiceMismatch' ? 'Legacy voice refusal' : formatCell(value)}</span>;
  if (format === 'outcome') return <span className={`az-badge ${OUTCOME_TONE[String(value)] ?? ''}`}>{formatCell(value)}</span>;
  if (format === 'riskLevel') { const risk = String(value).toLowerCase(); return <span className={`az-badge ${risk === 'high' ? 'error' : risk === 'medium' ? 'warning' : risk === 'low' ? 'info' : ''}`}>{risk === 'hidden' ? 'Not available' : formatCell(value)}</span>; }
  if (format === 'severity') return <span className={`az-badge ${value === 'High' ? 'error' : value === 'Medium' ? 'warning' : 'info'}`}>{formatCell(value)}</span>;
  if (format === 'signInResult') return <span className={`az-badge ${Number(value) === 0 ? 'success' : 'warning'}`}>{Number(value) === 0 ? 'Success' : `Failed (${value})`}</span>;
  if (format === 'risk' || format === 'compositeRisk') {
    const score = Number(value); const cuts = format === 'risk' ? [90,80,60] : [75,50,25];
    return <span className={`az-badge ${score >= cuts[0] ? 'error' : score >= cuts[1] ? 'severe' : score >= cuts[2] ? 'warning' : ''}`}>{formatCell(value)}</span>;
  }
  return formatCell(value);
}
export function formatCell(value: unknown): string {
  const text = typeof value === 'number' && !Number.isInteger(value) ? value.toFixed(2) : fullCell(value);
  return text.length > 120 ? `${text.slice(0, 120)}…` : text;
}
