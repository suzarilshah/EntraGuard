'use client';

import { useCallback, useEffect, useState } from 'react';
import { useEntraSignIn } from './useEntraSignIn';
import { VoiceEnrollment } from './VoiceEnrollment';
import { KnowledgeSetup } from './KnowledgeSetup';
import { TreasuryBrand, TreasuryIcon } from './TreasuryExperience';
import { WorkspaceFooter, WorkspaceIcon, WorkspaceSidebar } from './TreasuryWorkspace';
import { AccountSettingsControls } from './AccountSettingsControls';

interface VerificationRow {
  verificationId: string; upn: string; result: string; reason: string; startedAt: string;
  attempts: number; knowledgeBacking?: string | null; endpointKind?: string; assuranceLevel?: string;
}

type Section = 'identity' | 'voice' | 'methods' | 'activity' | 'preferences' | 'policy';
const SECTIONS: { id: Section; label: string; description: string }[] = [
  { id: 'identity', label: 'Profile & identity', description: 'Your work account is the starting point for every verification.' },
  { id: 'voice', label: 'Voice recognition', description: 'An optional, personal layer of verification. Always your choice.' },
  { id: 'methods', label: 'Verification methods', description: 'Understand what you are asked, and manage your backup question.' },
  { id: 'activity', label: 'Recent activity', description: 'See recent verification attempts returned by EntraGuard for your account.' },
  { id: 'preferences', label: 'Preferences & devices', description: 'Saved channel preferences, device registrations and your in-app inbox.' },
  { id: 'policy', label: 'Policy & readiness', description: 'Server-enforced assurance policy and available verification sources.' },
];

export function TreasurySettings() {
  const auth = useEntraSignIn();
  const [section, setSection] = useState<Section>('identity');
  const [history, setHistory] = useState<VerificationRow[]>([]);
  const [loading, setLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [outcomeFilter, setOutcomeFilter] = useState('all');
  const [refresh, setRefresh] = useState(0);
  const [cursor, setCursor] = useState<string | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const upn = auth.identity?.upn;

  const loadHistory = useCallback(async (signal: AbortSignal) => {
    if (!upn) return;
    setLoading(true); setHistoryError(null);
    try {
      const response = await fetch(`/api/account/history?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`, { cache: 'no-store', signal });
      if (!response.ok) throw new Error(`Recent activity is unavailable (${response.status}).`);
      const page = await response.json();
      const attempts: unknown = page.items;
      if (!Array.isArray(attempts)) throw new Error('The service returned an unexpected activity response.');
      const own = attempts.filter((a): a is VerificationRow => a && typeof a.upn === 'string' && a.upn.toLowerCase() === upn.toLowerCase());
      if (!signal.aborted) { setHistory(own); setNextCursor(page.cursor ?? null); }
    } catch (error) {
      if (!signal.aborted) setHistoryError(error instanceof Error ? error.message : 'Recent activity could not be loaded.');
    } finally { if (!signal.aborted) setLoading(false); }
  }, [upn, cursor]);

  useEffect(() => { const controller = new AbortController(); void loadHistory(controller.signal); return () => controller.abort(); }, [loadHistory, refresh]);
  const active = SECTIONS.find(s => s.id === section)!;
  const visible = history.filter(row => outcomeFilter === 'all' || (outcomeFilter === 'passed' ? row.result === 'Passed' : row.result !== 'Passed'));

  return (
    <div className="rp treasury treasury-settings">
      <a className="treasury-skip" href="#treasury-settings-content">Skip to main content</a>
      <header className="rp-header"><TreasuryBrand /><span className="rp-header-right">{auth.identity?.displayName && <span>{auth.identity.displayName}</span>}<a className="rp-btn secondary" href="/app">Back to Treasury <TreasuryIcon kind="arrow" size={14} /></a></span></header>
      {auth.state !== 'signed-in' ? <main className="tw-settings-signin" id="treasury-settings-content" tabIndex={-1}><div className="rp-card"><span className="tw-shield-tile"><TreasuryIcon kind="lock" size={25} /></span><h1 className="rp-h1">{auth.state === 'loading' ? 'Opening your settings…' : 'Your settings. Your account.'}</h1><p className="rp-sub">Sign in with your work account to manage your verification preferences.</p>{auth.error && <p className="rp-result err" role="alert">{auth.error}</p>}<a className="rp-btn block" href="/app">Go to sign in <TreasuryIcon kind="arrow" size={16} /></a></div></main> : <div className="tw-layout">
        <WorkspaceSidebar account={auth.identity?.displayName || upn || 'Work account'} label="SETTINGS">
          {SECTIONS.map(item => <button key={item.id} type="button" aria-current={section === item.id ? 'page' : undefined} onClick={() => setSection(item.id)}>{item.id === 'identity' ? <TreasuryIcon kind="user" size={18} /> : item.id === 'voice' ? <TreasuryIcon kind="voice" size={18} /> : <WorkspaceIcon kind={item.id === 'methods' ? 'settings' : 'activity'} />}{item.label}</button>)}
          <a href="/app"><WorkspaceIcon kind="overview" />Back to workspace</a>
        </WorkspaceSidebar>
        <main className="tw-main" id="treasury-settings-content" tabIndex={-1}>
          <div className="tw-breadcrumb">Settings <span>/</span> {active.label}<span className="tw-demo-tag">YOUR ACCOUNT</span></div>
          <div className="tw-page-heading"><div><p className="tw-eyebrow">PERSONAL SECURITY CENTER</p><h1>{active.label}.</h1><p>{active.description}</p></div><span className="tw-pill released"><TreasuryIcon kind="user" size={13} />Work account signed in</span></div>

          <section hidden={section !== 'identity'} aria-label="Profile and identity">
            <div className="tw-settings-grid"><div className="tw-panel tw-profile-panel"><div className="tw-profile-cover"><span className="tw-profile-avatar">{(auth.identity?.displayName || upn || 'C').slice(0, 1).toUpperCase()}</span><TreasuryIcon kind="shield" size={40} /></div><div className="tw-profile-body"><h2>{auth.identity?.displayName}</h2><p>{upn}</p><span className="tw-pill scheduled">Microsoft Entra ID</span><dl className="tw-facts"><div><dt>Work account</dt><dd>{upn}</dd></div><div><dt>Directory object ID</dt><dd className="tw-reference">{auth.identity?.objectId}</dd></div><div><dt>Home tenant</dt><dd className="tw-reference">{auth.identity?.tenantId}</dd></div></dl><p className="rp-hint">These details come from your Microsoft sign-in. Your organization manages this identity; the Teams call target is not editable here.</p></div></div>
              <div className="tw-panel tw-security-actions"><span className="tw-shield-tile"><TreasuryIcon kind="voice" size={25} /></span><h2>Make verification feel familiar.</h2><p>Enrol an optional voice profile, learn how identity questions work, and check your recent sign-in verification activity.</p><button className="rp-btn block" type="button" onClick={() => setSection('voice')}>Manage voice recognition <TreasuryIcon kind="arrow" size={16} /></button><button className="rp-btn secondary block" type="button" onClick={() => setSection('activity')}>Review recent activity</button><div className="tw-demo-note">Settings describe your verification options. Tenant-wide policy and enforcement are managed by your administrator.</div></div></div>
          </section>

          {/* Keep panels mounted: changing sections must not discard an enrollment in progress. */}
          <section hidden={section !== 'voice'} aria-label="Voice recognition" className="tw-settings-grid">
            <div className="tw-panel tw-settings-component"><VoiceEnrollment objectId={auth.identity?.objectId} getAccessToken={auth.getAccessToken} getTokens={auth.getTokens} /></div>
            <aside className="tw-panel tw-security-actions"><span className="tw-shield-tile"><TreasuryIcon kind="shield" size={25} /></span><h2>Your voice stays your choice.</h2><ul className="tw-checklist"><li>Three spoken enrollment phrases.</li><li>A stored speaker template, not a retained audio recording.</li><li>Fresh Microsoft authentication for enrollment.</li><li>Delete or re-record through the controls on this page.</li></ul><p className="rp-hint">The default deployment observes voice scores. How a comparison affects verification depends on the deployment’s enforcement configuration.</p></aside>
          </section>

          <section hidden={section !== 'methods'} aria-label="Verification methods" className="tw-settings-grid">
            <div className="tw-panel tw-method-panel"><div className="tw-panel-heading"><div><span className="tw-eyebrow">LAYERS OF VERIFICATION</span><h2>More context. Better decisions.</h2></div></div><ol className="tw-method-list"><li><span>01</span><div><h3>Number match</h3><p>Enter the two digits shown in your browser using the call keypad.</p></div></li><li><span>02</span><div><h3>Identity questions, when available</h3><p>The service selects up to four questions from available sign-in, directory and recent activity sources. A registered question may also be included. Availability depends on permissions and data.</p></div></li><li><span>03</span><div><h3>Voice comparison, when enrolled</h3><p>Your speech can be compared with your profile. A voice match is not proof that a call is free of coercion.</p></div></li><li><span>04</span><div><h3>Coercion monitoring</h3><p>The Analyst checks the conversation for coaching. Detected coercion can refuse verification even when the digits are correct.</p></div></li></ol></div>
            <div className="tw-panel tw-backup-panel"><span className="tw-eyebrow">OPTIONAL FALLBACK</span><h2>Backup security question.</h2><p className="rp-sub">Used when live sources are unavailable, or alongside available questions. A stored personal fact is weaker than a strong authentication factor.</p><KnowledgeSetup tenantId={auth.identity?.tenantId} objectId={auth.identity?.objectId} upn={upn || ''} /></div>
          </section>

          <section hidden={section !== 'activity'} aria-label="Recent verification activity" className="tw-panel">
            <div className="tw-panel-heading"><div><h2>Your verification history</h2><p>Durable receipts scoped to your signed-in tenant and account.</p></div><button className="rp-btn secondary" type="button" disabled={loading} onClick={() => { setCursor(null); setRefresh(value => value + 1); }}><WorkspaceIcon kind="refresh" />{loading ? 'Refreshing…' : 'Refresh activity'}</button></div>
            <div className="tw-table-tools"><label className="tw-select"><span>Outcome </span><select value={outcomeFilter} onChange={event => setOutcomeFilter(event.target.value)}><option value="all">All outcomes</option><option value="passed">Passed</option><option value="other">Other outcomes</option></select></label><span className="tw-activity-scope">Current work account only</span></div>
            {historyError ? <div className="tw-empty" role="alert"><h3>Activity could not be loaded</h3><p>{historyError}</p><button className="rp-btn secondary" type="button" onClick={() => setRefresh(value => value + 1)}>Try again</button></div> : loading ? <div className="tw-empty" role="status">Loading recent activity…</div> : visible.length === 0 ? <div className="tw-empty"><WorkspaceIcon kind="activity" /><h3>No recent records to show</h3><p>{outcomeFilter === 'all' ? 'The service returned no retained attempts for this account. Older attempts may already have expired.' : 'No retained attempts match this outcome filter.'}</p></div> : <div className="tw-table-scroll" role="region" aria-label="Verification history" tabIndex={0}><table className="tw-table"><thead><tr><th>Time / reference</th><th>Endpoint</th><th>Result</th><th>Details</th></tr></thead><tbody>{visible.slice(0, 20).map(row => <tr key={row.verificationId}><td><strong>{new Date(row.startedAt).toLocaleString()}</strong><small className="tw-row-reference">{row.verificationId}</small></td><td>{row.endpointKind || 'Not reported'}</td><td><span className={`tw-pill ${row.result === 'Passed' ? 'released' : 'pending'}`}>{row.result}</span></td><td><details className="tw-history-detail"><summary>View explanation</summary><p>{row.reason || 'No explanation returned.'}</p>{row.assuranceLevel && <p>Assurance: {row.assuranceLevel}</p>}</details></td></tr>)}</tbody></table></div>}
            <div className="tw-table-footer"><span>{historyError ? 'Service unavailable' : `${Math.min(visible.length, 20)} matching receipts on this page`}</span><span>Source: EntraGuard durable ledger</span>{cursor && <button className="rp-btn secondary" disabled={loading} onClick={() => setCursor(null)}>Newest receipts</button>}{nextCursor && <button className="rp-btn secondary" disabled={loading} onClick={() => setCursor(nextCursor)}>Older receipts</button>}</div>
          </section>
          <section hidden={section !== 'preferences'} aria-label="Preferences and devices"><AccountSettingsControls section="preferences" active={section === 'preferences'} /></section>
          <section hidden={section !== 'policy'} aria-label="Policy and readiness"><AccountSettingsControls section="policy" active={section === 'policy'} /></section>
          <div className="tw-settings-note"><TreasuryIcon kind="lock" size={16} /><span>If you do not recognize an attempt, contact your organization’s IT help desk through a trusted channel.</span></div>
          <WorkspaceFooter />
        </main>
      </div>}
    </div>
  );
}
