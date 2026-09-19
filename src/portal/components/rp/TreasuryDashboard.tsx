'use client';

import { useEffect, useRef, useState } from 'react';
import { TreasuryIcon } from './TreasuryExperience';
import { WorkspaceFooter, WorkspaceIcon, WorkspaceSidebar } from './TreasuryWorkspace';

export interface TreasuryVerificationEvidence {
  verificationId: string;
  result: string;
  reason: string;
  peakRiskDuringCall?: number;
  voiceOutcome?: string;
  voiceScore?: number | null;
  assuranceLevel?: string;
  assuranceBasis?: string[];
  assuranceGaps?: string[];
  endpointKind?: string;
  completedAt?: string;
}

type PaymentStatus = 'Awaiting approval' | 'Scheduled' | 'Released' | 'Approved';
interface Payment { reference: string; beneficiary: string; amount: number; status: PaymentStatus; category: string; date: string; country: string }
const money = (amount: number) => new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', maximumFractionDigits: 0 }).format(amount);
const date = (value: string) => new Date(`${value}T12:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' });
const statusClass = (status: PaymentStatus) => status === 'Released' ? 'released' : status === 'Scheduled' ? 'scheduled' : 'pending';

export function TreasuryDashboard({ name, upn, verification, onVerifyAgain, onVerifyPayment }: {
  name: string; upn: string; verification: TreasuryVerificationEvidence | null; onVerifyAgain: () => void; onVerifyPayment: (id: string) => void;
}) {
  const [PAYMENTS, setPayments] = useState<Payment[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [canApprove, setCanApprove] = useState(false);
  const [reload, setReload] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setLoadError(null);
    void fetch('/api/account/payments', { cache: 'no-store', signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error(response.status === 403 ? 'Your verified session expired. Verify again to access the ledger.' : 'Payment records could not be loaded.');
      const data = await response.json();
      if (!controller.signal.aborted) { setPayments(data.items); setCanApprove(Boolean(data.canApprove)); if (!data.enabled) setLoadError('The demo ledger is not enabled on this deployment.'); }
    }).catch(error => { if (!controller.signal.aborted) setLoadError(error.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [reload]);
  const [view, setView] = useState<'overview' | 'payments' | 'security'>('overview');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('All statuses');
  const [sort, setSort] = useState('reference');
  const [selected, setSelected] = useState<Payment | null>(null);
  const [notice, setNotice] = useState('');
  const dialog = useRef<HTMLDialogElement>(null);
  const total = PAYMENTS.reduce((sum, payment) => sum + payment.amount, 0);
  const groups = (['Awaiting approval', 'Scheduled', 'Released', 'Approved'] as const).map(label => ({ label, count: PAYMENTS.filter(p => p.status === label).length, amount: PAYMENTS.filter(p => p.status === label).reduce((sum, p) => sum + p.amount, 0) }));
  const filtered = PAYMENTS.filter(p => (status === 'All statuses' || p.status === status) && `${p.reference} ${p.beneficiary} ${p.category}`.toLowerCase().includes(search.trim().toLowerCase()))
    .sort((a, b) => sort === 'amount' ? b.amount - a.amount : sort === 'beneficiary' ? a.beneficiary.localeCompare(b.beneficiary) : b.reference.localeCompare(a.reference));

  const reviewPending = () => { setStatus('Awaiting approval'); setSearch(''); setView('payments'); };
  const exportPayments = () => {
    const rows = [['Reference', 'Beneficiary', 'Amount GBP', 'Status', 'Scheduled date', 'Data source'], ...filtered.map(p => [p.reference, p.beneficiary, String(p.amount), p.status, p.date, 'Illustrative demo data'])];
    const csv = rows.map(row => row.map(value => `"${value.replaceAll('"', '""')}"`).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8;' }));
    const link = document.createElement('a'); link.href = url; link.download = 'contoso-demo-payment-runs.csv'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    setNotice(`Exported ${filtered.length} demo payment runs.`);
  };
  const openPayment = (payment: Payment) => { setSelected(payment); dialog.current?.showModal(); };

  return (
    <div className="tw-layout">
      <WorkspaceSidebar account={name || upn}>
        <button type="button" aria-current={view === 'overview' ? 'page' : undefined} onClick={() => setView('overview')}><WorkspaceIcon kind="overview" />Overview</button>
        <button type="button" aria-current={view === 'payments' ? 'page' : undefined} onClick={() => setView('payments')}><WorkspaceIcon kind="payments" />Payment runs<span className="tw-nav-count">{PAYMENTS.length}</span></button>
        <button type="button" aria-current={view === 'security' ? 'page' : undefined} onClick={() => setView('security')}><TreasuryIcon kind="shield" size={18} />Session security</button>
        <a href="/settings"><WorkspaceIcon kind="settings" />Settings</a>
      </WorkspaceSidebar>
      <main className="tw-main">
        <div className="tw-breadcrumb">Workspace <span>/</span> {view === 'payments' ? 'Payment runs' : view === 'security' ? 'Session security' : 'Overview'}<span className="tw-demo-tag">DEMO DATA</span></div>
        <div className="tw-page-heading"><div><p className="tw-eyebrow">{view === 'overview' ? `WELCOME BACK, ${name.split(' ')[0] || 'COLLEAGUE'}` : 'CONTOSO TREASURY'}</p><h1>{view === 'overview' ? 'A clear view. A confident move.' : view === 'payments' ? 'Your payment runs.' : 'The trust behind this session.'}</h1><p>{view === 'security' ? 'Evidence returned by EntraGuard for your latest verification.' : 'Everything that matters to your treasury, thoughtfully brought together.'}</p></div><a className="tw-account-link" href="/settings"><span className="tw-avatar">{(name || upn).slice(0, 1).toUpperCase()}</span><span>{name || upn}<small>Manage your account ↗</small></span></a></div>

        {loading && <p role="status">Loading your authorized payment ledger…</p>}
        {loadError && <div className="rp-result warn" role="alert"><p>{loadError}</p><button type="button" className="rp-btn secondary" onClick={() => setReload(v => v + 1)}>Retry ledger</button><button type="button" className="rp-btn secondary" onClick={onVerifyAgain}>Verify again</button></div>}
        {view === 'overview' && !loading && !loadError && total > 0 && <>
          <div className="tw-overview-grid">
            <section className="tw-capital-card"><div className="tw-card-eyebrow">PAYMENTS AWAITING APPROVAL <span>GBP</span></div><div className="tw-capital-amount">{money(groups[0].amount)}<span>.00</span></div><p>{groups[0].count} payment runs ready for your review.</p><button type="button" onClick={reviewPending}>Explore payment runs <TreasuryIcon kind="arrow" size={17} /></button><div className="tw-capital-decoration" aria-hidden="true"><span /><span /><span /><span /></div><div className="tw-capital-caption">ILLUSTRATIVE PORTFOLIO · SEPTEMBER 2026</div></section>
            <section className="tw-session-card"><div className="tw-section-heading"><span className="tw-shield-tile"><TreasuryIcon kind="shield" size={22} /></span><span className="tw-pill released">Verification complete</span></div><h2>Welcome to your protected workspace.</h2><p>EntraGuard returned a successful verification. Explore the evidence behind this session.</p><button className="tw-text-button" type="button" onClick={() => setView('security')}>View verification details <TreasuryIcon kind="arrow" size={15} /></button></section>
          </div>
          <div className="tw-metrics">
            {groups.map(group => <section className="tw-metric" key={group.label}><div><span className={`tw-status-dot ${statusClass(group.label)}`} />{group.label}<span>{group.count} runs</span></div><strong>{money(group.amount)}</strong><small>{(group.amount / total * 100).toFixed(1)}% of the sample portfolio</small></section>)}
          </div>
          <section className="tw-distribution"><div><strong>Portfolio composition</strong><span>{money(total)} across {PAYMENTS.length} sample runs</span></div><div className="tw-distribution-bar" role="img" aria-label={groups.map(g => `${g.label}: ${money(g.amount)}`).join('; ')}>{groups.map(g => <span className={statusClass(g.label)} key={g.label} style={{ width: `${g.amount / total * 100}%` }} />)}</div></section>
        </>}

        {view !== 'security' ? <section className="tw-panel">
          <div className="tw-panel-heading"><div><h2>Payment runs</h2><p>Illustrative records. Review and export without moving funds.</p></div><button className="rp-btn secondary" type="button" onClick={exportPayments} disabled={filtered.length === 0}><WorkspaceIcon kind="download" />Export CSV</button></div>
          <div className="tw-table-tools"><label className="tw-search"><WorkspaceIcon kind="search" /><span className="sr-only">Search payment runs</span><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Search beneficiary or reference…" type="search" /></label><label className="tw-select"><span className="sr-only">Filter payment status</span><select value={status} onChange={event => setStatus(event.target.value)}>{['All statuses', 'Awaiting approval', 'Scheduled', 'Released'].map(option => <option key={option}>{option}</option>)}</select></label><label className="tw-select"><span className="sr-only">Sort payment runs</span><select value={sort} onChange={event => setSort(event.target.value)}><option value="reference">Latest reference</option><option value="amount">Highest amount</option><option value="beneficiary">Beneficiary A–Z</option></select></label></div>
          <div className="tw-table-scroll" role="region" aria-label="Payment runs" tabIndex={0}><table className="tw-table"><thead><tr><th>Beneficiary / reference</th><th>Payment type</th><th>Scheduled</th><th className="tw-money">Amount</th><th>Status</th><th><span className="sr-only">Details</span></th></tr></thead><tbody>{filtered.map(payment => <tr key={payment.reference}><td><div className="tw-beneficiary"><span className={`tw-beneficiary-icon ${statusClass(payment.status)}`}>{payment.beneficiary.split(' ').map(w => w[0]).slice(0, 2).join('')}</span><span><strong>{payment.beneficiary}</strong><small>{payment.reference}</small></span></div></td><td>{payment.category}</td><td>{date(payment.date)}</td><td className="tw-money">{money(payment.amount)}</td><td><span className={`tw-pill ${statusClass(payment.status)}`}>{payment.status}</span></td><td><button className="tw-icon-button" type="button" aria-label={`View ${payment.reference}`} onClick={() => openPayment(payment)}><TreasuryIcon kind="arrow" size={17} /></button></td></tr>)}</tbody></table></div>
          {filtered.length === 0 && <div className="tw-empty"><WorkspaceIcon kind="search" /><h3>No matching payment runs</h3><p>Try another beneficiary, reference, or status.</p><button className="rp-btn secondary" type="button" onClick={() => { setSearch(''); setStatus('All statuses'); }}>Clear filters</button></div>}
          <div className="tw-table-footer"><span aria-live="polite">Showing {filtered.length} of {PAYMENTS.length} payment runs</span><span>All amounts in GBP</span></div>
        </section> : <section className="tw-security-grid">
          <div className="tw-panel tw-evidence"><div className="tw-panel-heading"><div><span className="tw-eyebrow">VERIFICATION RECEIPT</span><h2>Your sign-in, explained.</h2></div><TreasuryIcon kind="shield" size={27} /></div><dl className="tw-facts"><div><dt>Account</dt><dd>{upn}</dd></div><div><dt>Result</dt><dd>{verification?.result ?? 'Not available'}</dd></div><div><dt>Assurance level</dt><dd>{verification?.assuranceLevel ?? 'Not reported'}</dd></div><div><dt>Call endpoint</dt><dd>{verification?.endpointKind ?? 'Not reported'}</dd></div><div><dt>Voice comparison</dt><dd>{verification?.voiceOutcome ?? 'Not assessed'}</dd></div><div><dt>Peak detected risk</dt><dd>{typeof verification?.peakRiskDuringCall === 'number' ? `${verification.peakRiskDuringCall}/100` : 'Not reported'}</dd></div><div><dt>Verification reference</dt><dd className="tw-reference">{verification?.verificationId ?? 'Not available'}</dd></div></dl><div className="tw-evidence-reason"><strong>EntraGuard’s explanation</strong><p>{verification?.reason || 'No explanation was returned with this session.'}</p></div>{Boolean(verification?.assuranceGaps?.length) && <div className="tw-evidence-reason"><strong>What could strengthen verification</strong><ul>{verification!.assuranceGaps!.map(gap => <li key={gap}>{gap}</li>)}</ul></div>}</div>
          <div className="tw-panel tw-security-actions"><span className="tw-shield-tile"><TreasuryIcon kind="lock" size={24} /></span><h2>Protection, on your terms.</h2><p>Review your account, manage an optional voice profile, or inspect recent verification attempts in Settings.</p><a className="rp-btn block" href="/settings">Manage verification settings <TreasuryIcon kind="arrow" size={15} /></a><button className="rp-btn secondary block" type="button" onClick={onVerifyAgain}>Start a new verification</button><p className="rp-hint">A successful verification is a result, not a guarantee that every threat was detected. Assurance and risk describe different aspects of the call.</p></div>
        </section>}
        <div className="tw-notice" role="status">{notice}</div><WorkspaceFooter />
      </main>
      <dialog ref={dialog} className="tw-payment-dialog" aria-labelledby="tw-payment-title" onClick={event => { if (event.target === event.currentTarget) dialog.current?.close(); }}>
        {selected?.status === 'Awaiting approval' && <div className="tw-demo-note"><p>Approval changes the durable demo ledger only. It requires a new verification bound to these payment details.</p><button type="button" className="rp-btn block" disabled={!canApprove} onClick={() => { dialog.current?.close(); onVerifyPayment(selected.reference); }}>{canApprove ? 'Verify to approve demo payment' : 'Payment-approver role required'}</button></div>}
        {selected && <><div className="tw-dialog-heading"><span className="tw-eyebrow">PAYMENT DETAILS · DEMO</span><button className="tw-icon-button" type="button" aria-label="Close payment details" onClick={() => dialog.current?.close()}><WorkspaceIcon kind="close" /></button></div><span className={`tw-pill ${statusClass(selected.status)}`}>{selected.status}</span><h2 id="tw-payment-title">{selected.beneficiary}</h2><div className="tw-dialog-amount">{money(selected.amount)}</div><dl className="tw-facts"><div><dt>Reference</dt><dd>{selected.reference}</dd></div><div><dt>Payment type</dt><dd>{selected.category}</dd></div><div><dt>Scheduled date</dt><dd>{date(selected.date)} 2026</dd></div><div><dt>Destination</dt><dd>{selected.country}</dd></div><div><dt>Currency</dt><dd>GBP · British pound</dd></div></dl><p className="tw-demo-note">This is an illustrative payment record. Payment approval and execution are not connected to a banking backend.</p><button className="rp-btn block" type="button" onClick={() => dialog.current?.close()}>Back to workspace</button></>}
      </dialog>
    </div>
  );
}
