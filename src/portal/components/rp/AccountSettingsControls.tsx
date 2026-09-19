'use client';
import { useEffect, useState } from 'react';

interface Preferences { version: number; preferredChannel: string; verificationNotifications: boolean; approvalNotifications: boolean; updatedAt?: string }
interface Policy { version: number; minimumAssurance: string; maximumAgeMinutes: number; requireAnalyst: boolean; allowedChannels: string[] | null; channels: string[] }
interface Device { kind: string; name?: string; revoked: boolean; reachable: boolean; currentSession: boolean; lastSeen?: string }
interface Notice { id: string; title: string; detail: string; readAt?: string; createdAt: string }

async function api(path: string, method = 'GET', body?: unknown) {
  const response = await fetch(`/api/account/${path}`, { method, cache: 'no-store', headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
  const data = response.status === 204 ? {} : await response.json();
  if (!response.ok) throw new Error(data.error ?? `Request failed (${response.status}).`);
  return data;
}

export function AccountSettingsControls({ section, active }: { section: 'preferences' | 'policy'; active: boolean }) {
  const [preferences, setPreferences] = useState<Preferences | null>(null);
  const [policy, setPolicy] = useState<Policy | null>(null);
  const [canEdit, setCanEdit] = useState(false);
  const [devices, setDevices] = useState<Device[]>([]);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const [readiness, setReadiness] = useState<{ ceiling: string; candidateCount: number; sources: string[]; gaps: string[]; canMeetMinimum: boolean; note: string } | null>(null);
  useEffect(() => {
    if (!active) return;
    let cancelled = false; setBusy(true); setError('');
    void (async () => {
      try {
        if (section === 'preferences') {
          const [prefs, registered, inbox] = await Promise.all([api('preferences'), api('devices'), api('notifications')]);
          if (!cancelled) { setPreferences(prefs); setDevices(registered); setNotices(inbox.items); setCursor(inbox.cursor); }
        } else {
          const result = await api('policy'); if (!cancelled) { setPolicy(result.policy); setCanEdit(result.canEdit); }
        }
      } catch (e) { if (!cancelled) setError(e instanceof Error ? e.message : 'Settings could not be loaded.'); }
      finally { if (!cancelled) setBusy(false); }
    })();
    return () => { cancelled = true; };
  }, [active, section, refresh]);

  const run = async (action: () => Promise<void>) => {
    setBusy(true); setError(''); setMessage('');
    try { await action(); } catch (e) { setError(e instanceof Error ? e.message : 'The request failed.'); }
    finally { setBusy(false); }
  };

  return <div>
    {error && <div className="rp-result warn" role="alert"><p>{error}</p><button className="rp-btn secondary" onClick={() => setRefresh(v => v + 1)}>Reload settings</button></div>}
    <p role="status" className="rp-hint">{busy ? 'Working…' : message}</p>
    {section === 'preferences' && <div className="tw-settings-grid">
      <section className="tw-panel tw-backup-panel"><h2>Channel & notifications.</h2><p className="rp-sub">Preferences are saved to your account. Notifications are delivered to this in-app inbox; no email or SMS is sent.</p>
        {preferences && <form onSubmit={event => { event.preventDefault(); void run(async () => { setPreferences(await api('preferences', 'PUT', preferences)); setMessage('Preferences saved.'); }); }}>
          <label className="rp-field">Preferred verification channel<select className="rp-input" value={preferences.preferredChannel} disabled={busy} onChange={event => setPreferences({ ...preferences, preferredChannel: event.target.value })}><option value="teams">Microsoft Teams</option><option value="browser">This browser</option><option value="phone">My phone</option></select></label>
          <label className="rp-field"><span><input type="checkbox" checked={preferences.verificationNotifications} disabled={busy} onChange={event => setPreferences({ ...preferences, verificationNotifications: event.target.checked })} /> Verification outcome notifications</span></label>
          <label className="rp-field"><span><input type="checkbox" checked={preferences.approvalNotifications} disabled={busy} onChange={event => setPreferences({ ...preferences, approvalNotifications: event.target.checked })} /> Demo payment approval notifications</span></label>
          <button className="rp-btn" disabled={busy} type="submit">Save preferences</button>
          <p className="rp-hint">Version {preferences.version}{preferences.updatedAt && ` · Saved ${new Date(preferences.updatedAt).toLocaleString()}`}</p>
        </form>}
        <h2>Registered devices</h2><p className="rp-hint">One active registration per browser/phone channel. Revoking a registration also revokes its web session and ACS tokens. A new Microsoft sign-in can register again.</p>
        {devices.length === 0 && !busy && <p className="rp-sub">No registered ACS devices. Teams is managed by your organization.</p>}
        {devices.map(device => <div className="rp-result" key={device.kind}><strong>{device.name || device.kind}</strong><p className="rp-hint">{device.revoked ? 'Revoked' : device.reachable ? 'Recently reported reachable' : 'Not currently reporting presence'}{device.currentSession ? ' · This web session' : ''}</p><button type="button" className="rp-btn secondary" disabled={busy || device.revoked} onClick={() => void run(async () => { const result = await api(`devices/${device.kind}`, 'DELETE'); setMessage('Device registration and tokens revoked.'); if (result.currentSession) window.location.assign('/app'); else setRefresh(v => v + 1); })}>Revoke {device.kind}</button></div>)}
      </section>
      <section className="tw-panel tw-backup-panel"><h2>Your notification inbox.</h2><p className="rp-sub">Durable, account-scoped event notifications. Reading them does not change a verification or approval.</p>
        {notices.length === 0 && !busy && <p className="rp-hint">No notifications yet.</p>}
        {notices.map(item => <article key={item.id} className="rp-result"><strong>{item.title}</strong><p className="rp-hint">{item.detail}</p><small>{new Date(item.createdAt).toLocaleString()}</small><br />{item.readAt ? <span className="rp-hint">Read</span> : <button className="rp-btn secondary" disabled={busy} onClick={() => void run(async () => { await api(`notifications/${encodeURIComponent(item.id)}/read`, 'POST'); setNotices(current => current.map(n => n.id === item.id ? { ...n, readAt: new Date().toISOString() } : n)); })}>Mark as read</button>}</article>)}
        {cursor && <button className="rp-btn secondary" disabled={busy} onClick={() => void run(async () => { const page = await api(`notifications?cursor=${encodeURIComponent(cursor)}`); setNotices(current => [...current, ...page.items]); setCursor(page.cursor); })}>Load older notifications</button>}
      </section>
    </div>}
    {section === 'policy' && <div className="tw-settings-grid">
      <section className="tw-panel tw-backup-panel"><h2>Verification policy.</h2><p className="rp-sub">Enforced on the server. Changes invalidate grants based on older policy versions. {canEdit ? 'Your account can edit this tenant policy.' : 'Only tenant administrators can edit these settings.'}</p>
        {policy && <form onSubmit={event => { event.preventDefault(); void run(async () => { const saved = await api('policy', 'PUT', policy); setPolicy({ ...policy, version: saved.version }); setMessage('Tenant policy saved. New verification is required for protected operations.'); }); }}><fieldset disabled={busy || !canEdit} style={{ border: 0, padding: 0 }}>
          <label className="rp-field">Minimum assurance<select className="rp-input" value={policy.minimumAssurance} onChange={event => setPolicy({ ...policy, minimumAssurance: event.target.value })}>{['Low', 'Substantial', 'High'].map(level => <option key={level}>{level}</option>)}</select></label>
          <label className="rp-field">Maximum verification age (minutes)<input className="rp-input" type="number" min={1} max={60} required value={policy.maximumAgeMinutes} onChange={event => setPolicy({ ...policy, maximumAgeMinutes: Number(event.target.value) })} /></label>
          <label className="rp-field"><span><input type="checkbox" checked={policy.requireAnalyst} onChange={event => setPolicy({ ...policy, requireAnalyst: event.target.checked })} /> Require an Analyst assessment</span></label>
          <p className="rp-label">Allowed channels</p>{['teams', 'browser', 'phone'].map(channel => <label className="rp-field" key={channel}><span><input type="checkbox" checked={(policy.allowedChannels ?? policy.channels).includes(channel)} onChange={event => { const current = policy.allowedChannels ?? policy.channels; setPolicy({ ...policy, allowedChannels: event.target.checked ? [...current, channel] : current.filter(c => c !== channel) }); }} /> {channel}</span></label>)}
          {canEdit && <button className="rp-btn" type="submit" disabled={(policy.allowedChannels ?? policy.channels).length === 0}>Save tenant policy</button>}
        </fieldset><p className="rp-hint">Policy version {policy.version}</p></form>}
      </section>
      <section className="tw-panel tw-backup-panel"><h2>What could a call establish?</h2><p className="rp-sub">Check your currently available question sources and enrollment before placing a call. No call is placed by this check.</p><button className="rp-btn" disabled={busy} onClick={() => void run(async () => { setReadiness(await api('readiness')); })}>Check verification readiness</button>{readiness && <div className="rp-result"><strong>Potential assurance: {readiness.ceiling}</strong><p className="rp-hint">{readiness.candidateCount} candidates · {readiness.sources.join(', ') || 'No knowledge sources'}</p><p>{readiness.canMeetMinimum ? 'Available sources can potentially meet the tenant minimum.' : 'Available sources cannot currently meet the tenant minimum.'}</p><ul>{readiness.gaps.map(gap => <li key={gap}>{gap}</li>)}</ul><p className="rp-hint">{readiness.note}</p></div>}</section>
    </div>}
  </div>;
}
