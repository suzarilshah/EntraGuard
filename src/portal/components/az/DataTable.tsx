'use client';

import { useMemo, useState } from 'react';
import { IconSort } from './Icons';
import { Empty } from './Surfaces';

/**
 * How a cell should be presented.
 *
 * A serializable descriptor rather than a render callback: this is a Client Component, and
 * React cannot pass functions across the server/client boundary. Passing `render` props
 * from a page compiles fine and then throws "Functions cannot be passed directly to Client
 * Components" at request time — and only once a query actually returns columns, so an
 * environment where the data source is degraded hides it completely.
 */
export type CellFormat =
  | 'text'
  /** 0-100 risk score, coloured by the policy gate's own thresholds. */
  | 'risk'
  /** Succeeded / Failed / Unavailable / BlockedByPolicy. */
  | 'outcome'
  /** High / Medium / Low incident severity. */
  | 'severity'
  /** Entra ID risk level: high / medium / low. */
  | 'riskLevel'
  /** Sign-in error code, where 0 means success. */
  | 'signInResult'
  /** Passed / Failed / BlockedCoercion / Timeout / CallFailed. */
  | 'verificationResult';

export interface Column {
  key: string;
  label?: string;
  format?: CellFormat;
  align?: 'left' | 'right';

  /**
   * Roughly how much horizontal room this column needs.
   *
   * Browsers size table columns from their content, which starves the one column that
   * matters most here: a reason is a sentence, and every other column is a word or a number,
   * so the sentence gets squeezed into a two-word ribbon while an identifier sits in
   * comfortable whitespace. "wide" claims space; "narrow" gives it up so the wide ones can
   * have it. Left unset, the browser decides, which is right for most tables.
   */
  width?: 'narrow' | 'wide';
}

/** Minimum widths that make a sentence readable without letting an ID sprawl. */
const WIDTH: Record<string, React.CSSProperties> = {
  narrow: { width: '1%', whiteSpace: 'nowrap' },
  wide: { minWidth: 320 },
};

/**
 * Azure DetailsList equivalent: sortable columns and a live filter.
 *
 * Sorting and filtering run client-side over the already-fetched page. That is the right
 * trade here — these result sets are capped at tens of rows by the KQL itself, so a round
 * trip per sort would add latency and Log Analytics cost for no benefit.
 */
export function DataTable({
  columns,
  rows,
  emptyTitle,
  emptyDetail,
  filterable = true,
}: {
  columns: Column[];
  rows: unknown[][];
  emptyTitle: string;
  emptyDetail: string;
  filterable?: boolean;
}) {
  const [sort, setSort] = useState<{ index: number; dir: 1 | -1 } | null>(null);
  const [filter, setFilter] = useState('');

  const processed = useMemo(() => {
    let out = rows;

    if (filter.trim()) {
      const needle = filter.toLowerCase();
      out = out.filter((row) => row.some((cell) => String(cell ?? '').toLowerCase().includes(needle)));
    }

    if (sort) {
      out = [...out].sort((a, b) => {
        const x = a[sort.index];
        const y = b[sort.index];
        // Numbers compare numerically, everything else lexically — sorting a risk score
        // as a string would put 9 above 80.
        if (typeof x === 'number' && typeof y === 'number') return (x - y) * sort.dir;
        return String(x ?? '').localeCompare(String(y ?? '')) * sort.dir;
      });
    }

    return out;
  }, [rows, filter, sort]);

  if (rows.length === 0) {
    return <Empty title={emptyTitle} detail={emptyDetail} />;
  }

  return (
    <>
      {filterable && (
        <div className="az-table-toolbar">
          <input
            className="az-filter"
            placeholder="Filter rows…"
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            aria-label="Filter table rows"
          />
          <span className="az-count">
            {processed.length}{processed.length !== rows.length && ` of ${rows.length}`} row
            {rows.length === 1 ? '' : 's'}
          </span>
        </div>
      )}

      <div className="az-table-wrap">
        <table className="az-table">
          <thead>
            <tr>
              {columns.map((column, index) => (
                <th
                  key={column.key}
                  className="sortable"
                  style={{
                    textAlign: column.align ?? 'left',
                    ...(column.width ? WIDTH[column.width] : {}),
                  }}
                  onClick={() =>
                    setSort((current) =>
                      current?.index === index
                        ? { index, dir: current.dir === 1 ? -1 : 1 }
                        : { index, dir: 1 },
                    )
                  }
                  aria-sort={
                    sort?.index === index ? (sort.dir === 1 ? 'ascending' : 'descending') : 'none'
                  }
                >
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                    {column.label ?? column.key}
                    <IconSort size={11} className={sort?.index === index ? undefined : 'az-dim'} />
                  </span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {processed.map((row, rowIndex) => (
              <tr key={rowIndex}>
                {columns.map((column, columnIndex) => (
                  <td
                    key={column.key}
                    style={{
                      textAlign: column.align ?? 'left',
                      ...(column.width === 'narrow' ? WIDTH.narrow : {}),
                    }}
                    className={typeof row[columnIndex] === 'number' ? 'num' : undefined}
                  >
                    {renderCell(row[columnIndex], column.format)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {processed.length === 0 && (
        <Empty title="No matching rows" detail={`Nothing matches "${filter}".`} />
      )}
    </>
  );
}

// BlockedCoercion is styled as an error rather than a warning on purpose: a correct
// credential refused under duress is a security event, not a degraded outcome.
const VERIFICATION_TONE: Record<string, string> = {
  Passed: 'success',
  BlockedCoercion: 'error',
  // Same reasoning: the credential was correct and EntraGuard refused it anyway. That is
  // the system working, and it belongs in the eye-line of whoever reads this table.
  BlockedVoiceMismatch: 'error',
  Failed: 'warning',
  Timeout: 'warning',
  CallFailed: 'warning',
};

const OUTCOME_TONE: Record<string, string> = {
  Succeeded: 'success',
  Failed: 'error',
  Unavailable: 'warning',
  BlockedByPolicy: 'warning',
};

/** Thresholds match PolicyGate: 40 notify, 60 warn, 80 contain, 90 terminate. */
function riskTone(score: number): string {
  if (score >= 90) return 'error';
  if (score >= 80) return 'severe';
  if (score >= 60) return 'warning';
  return 'success';
}

function renderCell(value: unknown, format: CellFormat = 'text'): React.ReactNode {
  if (format === 'text') return formatCell(value);
  if (value === null || value === undefined || value === '') return '—';

  switch (format) {
    case 'risk':
      return <span className={`az-badge ${riskTone(Number(value))}`}>{formatCell(value)}</span>;
    case 'outcome':
      return <span className={`az-badge ${OUTCOME_TONE[String(value)] ?? ''}`}>{formatCell(value)}</span>;
    case 'severity':
      return (
        <span className={`az-badge ${String(value) === 'High' ? 'error' : 'warning'}`}>
          {formatCell(value)}
        </span>
      );
    case 'riskLevel':
      return (
        <span className={`az-badge ${String(value).toLowerCase() === 'high' ? 'error' : 'warning'}`}>
          {formatCell(value)}
        </span>
      );
    case 'verificationResult':
      return (
        <span className={`az-badge ${VERIFICATION_TONE[String(value)] ?? ''}`}>
          {String(value) === 'BlockedCoercion'
            ? 'Blocked — coercion'
            : String(value) === 'BlockedVoiceMismatch'
              ? 'Blocked — voice'
              : formatCell(value)}
        </span>
      );
    case 'signInResult':
      return (
        <span className={`az-badge ${Number(value) === 0 ? 'success' : 'error'}`}>
          {Number(value) === 0 ? 'success' : formatCell(value)}
        </span>
      );
    default:
      return formatCell(value);
  }
}

export function formatCell(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : value.toFixed(2);
  if (value instanceof Date) return value.toISOString().replace('T', ' ').slice(0, 19);
  if (Array.isArray(value)) return value.filter(Boolean).join(', ') || '—';
  if (typeof value === 'object') return JSON.stringify(value);
  const text = String(value);
  return text.length > 120 ? `${text.slice(0, 120)}…` : text;
}
