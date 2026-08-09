import type { Provenance } from '@/lib/azure/credential';
import { IconWarning, IconError, IconInfo, IconCheck, IconShield } from './Icons';

/** Azure MessageBar. Severity picks the icon and the tint, exactly as Fluent does. */
export function MessageBar({
  intent = 'info',
  title,
  children,
}: {
  intent?: 'info' | 'warning' | 'error' | 'success';
  title?: string;
  children: React.ReactNode;
}) {
  const Icon = intent === 'warning' ? IconWarning
    : intent === 'error' ? IconError
    : intent === 'success' ? IconCheck
    : IconInfo;

  return (
    <div className={`az-msgbar ${intent}`} role={intent === 'error' ? 'alert' : 'status'}>
      <Icon size={15} />
      <span>{title && <b>{title} </b>}{children}</span>
    </div>
  );
}

const PROVENANCE: Record<Provenance, string> = {
  graph: 'Microsoft Graph',
  kql: 'Log Analytics (KQL)',
  live: 'Media service',
  arm: 'Azure Resource Manager',
};

/**
 * States which API produced a panel's contents.
 *
 * On every data surface without exception. A security console that mixes live reads with
 * placeholders and looks identical either way teaches its reader to trust things they
 * should not.
 */
export function Source({ provenance, degraded }: { provenance: Provenance; degraded?: string }) {
  return (
    <span
      className={`az-badge ${degraded ? 'warning' : ''}`}
      title={degraded ?? `Live read from ${PROVENANCE[provenance]}`}
    >
      <span className="az-dot" />
      {PROVENANCE[provenance]}{degraded ? ' · limited' : ''}
    </span>
  );
}

export function Card({
  title,
  icon,
  source,
  degraded,
  actions,
  flush,
  footer,
  children,
}: {
  title: string;
  icon?: React.ReactNode;
  source?: Provenance;
  degraded?: string;
  actions?: React.ReactNode;
  flush?: boolean;
  footer?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section className="az-card">
      <header className="az-card-head">
        <h2 className="az-card-title">{icon}{title}</h2>
        <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {actions}
          {source && <Source provenance={source} degraded={degraded} />}
        </span>
      </header>
      {/* The reason a panel is empty is rendered where the data would be — "no risky
          users" and "we are not permitted to see risky users" look identical on a chart
          and mean opposite things. */}
      {degraded && <MessageBar intent="warning" title="Limited.">{degraded}</MessageBar>}
      <div className={`az-card-body${flush ? ' flush' : ''}`}>{children}</div>
      {footer && <div className="az-card-foot">{footer}</div>}
    </section>
  );
}

export function Empty({ title, detail }: { title: string; detail: string }) {
  return (
    <div className="az-empty">
      <IconShield size={26} />
      <strong>{title}</strong>
      {detail}
    </div>
  );
}

export function Metric({
  value,
  label,
  tone,
}: {
  value: React.ReactNode;
  label: string;
  tone?: 'success' | 'warning' | 'severe' | 'error' | 'muted';
}) {
  return (
    <div className="az-metric">
      <span className={`az-metric-value${tone ? ` t-${tone}` : ''}`}>{value}</span>
      <span className="az-metric-label">{label}</span>
    </div>
  );
}

export function PageHead({
  title,
  subtitle,
  icon,
}: {
  title: string;
  subtitle?: string;
  icon?: React.ReactNode;
}) {
  return (
    <div className="az-page-head">
      <h1 className="az-page-title">
        <span className="az-res-icon">{icon ?? <IconShield size={17} />}</span>
        EntraGuard | {title}
      </h1>
      {subtitle && <p className="az-page-sub">{subtitle}</p>}
    </div>
  );
}
