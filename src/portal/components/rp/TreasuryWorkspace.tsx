import type { ReactNode } from 'react';
import { TreasuryIcon } from './TreasuryExperience';

export function WorkspaceIcon({ kind }: { kind: 'overview' | 'payments' | 'activity' | 'settings' | 'search' | 'download' | 'close' | 'refresh' }) {
  const paths = {
    overview: <><rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" /><rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" /></>,
    payments: <><rect x="3" y="5" width="18" height="14" rx="3" /><path d="M3 10h18M7 15h3" /></>,
    activity: <><path d="M3 12h4l3-7 4 14 3-7h4" /></>,
    settings: <><path d="M4 7h16M4 17h16" /><circle cx="9" cy="7" r="3" fill="currentColor" stroke="none" /><circle cx="15" cy="17" r="3" fill="currentColor" stroke="none" /></>,
    search: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></>,
    download: <><path d="M12 3v12m-4-4 4 4 4-4M4 16v5h16v-5" /></>,
    close: <path d="m6 6 12 12M6 18 18 6" />,
    refresh: <><path d="M20 8a8 8 0 1 0 0 8M20 3v5h-5" /></>,
  };
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[kind]}</svg>;
}

export function WorkspaceSidebar({ children, account, label = 'WORKSPACE' }: { children: ReactNode; account: string; label?: string }) {
  return (
    <aside className="tw-sidebar">
      <div className="tw-organization"><span className="tw-org-mark">C</span><div><strong>Contoso Group</strong><span>Treasury workspace</span></div></div>
      <div className="tw-nav-label">{label}</div>
      <nav className="tw-nav" aria-label={label === 'SETTINGS' ? 'Settings sections' : 'Treasury workspace'}>{children}</nav>
      <div className="tw-sidebar-note"><TreasuryIcon kind="shield" size={25} /><strong>Designed around trust.</strong><p>Your work identity and voice verification, connected by EntraGuard.</p><a href="/settings">Manage your protection <TreasuryIcon kind="arrow" size={14} /></a></div>
      <div className="tw-sidebar-account"><span className="tw-avatar">{account.slice(0, 1).toUpperCase() || 'C'}</span><span>{account}<small>Work account</small></span></div>
    </aside>
  );
}

export function WorkspaceFooter() {
  return <footer className="tw-footer"><span>CONTOSO TREASURY <span> / </span> ENTRAGUARD</span><span>Demonstration workspace · No funds are moved</span></footer>;
}
