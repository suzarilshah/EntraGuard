'use client';

import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { ADMIN_NAV } from '@/lib/adminNavigation';
import { useEntraSignIn } from '../rp/useEntraSignIn';
import { AdminGlyph } from './AdminGlyph';
import { IconSearch, IconChevron } from './Icons';

export function AdminShell({ children, tenant, upn, resourceGroup }: {
  children: React.ReactNode; tenant: string; upn: string; resourceGroup: string;
}) {
  const [theme, setTheme] = useState<'light' | 'dark'>('light');
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  useEffect(() => {
    try { if (localStorage.getItem('entraguard.admin.theme.v1') === 'dark') setTheme('dark'); } catch { /* Optional presentation preference. */ }
  }, []);
  const toggleTheme = () => setTheme(current => {
    const next = current === 'light' ? 'dark' : 'light';
    try { localStorage.setItem('entraguard.admin.theme.v1', next); } catch { /* Still applies to this visit. */ }
    return next;
  });
  return <div className="admin-portal" data-theme={theme} data-collapsed={collapsed} data-mobile-open={mobileOpen}>
    <a className="admin-skip" href="#admin-main">Skip to main content</a>
    <TopBar tenant={tenant} upn={upn} theme={theme} toggleTheme={toggleTheme} toggleNav={() => {
      if (window.matchMedia('(max-width: 900px)').matches) setMobileOpen(open => !open);
      else setCollapsed(value => !value);
    }} />
    {mobileOpen && <button className="admin-nav-scrim" aria-label="Close navigation" onClick={() => setMobileOpen(false)} />}
    <div className="az-shell"><SideNav resourceGroup={resourceGroup} onNavigate={() => setMobileOpen(false)} /><div className="az-main" id="admin-main" tabIndex={-1}>{children}</div></div>
  </div>;
}

export function TopBar({ tenant, upn, theme, toggleTheme, toggleNav }: {
  tenant: string; upn: string; theme: 'light' | 'dark'; toggleTheme: () => void; toggleNav: () => void;
}) {
  const router = useRouter();
  const auth = useEntraSignIn();
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(0);
  const [accountOpen, setAccountOpen] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const [message, setMessage] = useState('');
  const input = useRef<HTMLInputElement>(null);
  const searchBox = useRef<HTMLDivElement>(null);
  const account = useRef<HTMLDivElement>(null);
  const results = ADMIN_NAV.filter(item => `${item.label} ${item.description}`.toLowerCase().includes(search.trim().toLowerCase()));
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); input.current?.focus(); }
      if (event.key === 'Escape') { setSearch(''); setAccountOpen(false); }
    };
    const outside = (event: PointerEvent) => {
      if (!account.current?.contains(event.target as Node)) setAccountOpen(false);
      if (!searchBox.current?.contains(event.target as Node)) setSearch('');
    };
    document.addEventListener('keydown', key); document.addEventListener('pointerdown', outside);
    return () => { document.removeEventListener('keydown', key); document.removeEventListener('pointerdown', outside); };
  }, []);
  const navigate = (href: string) => { setSearch(''); router.push(href); input.current?.blur(); };
  const logout = async () => {
    setSigningOut(true); setMessage('');
    try {
      await auth.signOut();
      const response = await fetch('/api/rp/session', { cache: 'no-store' });
      if (response.status === 401) window.location.assign('/operator-signin');
      else setMessage('Sign-out could not be confirmed. Please retry.');
    } catch { setMessage('Sign-out could not be completed. Please retry.'); }
    finally { setSigningOut(false); }
  };
  return <header className="az-header">
    <button className="az-icon-btn admin-menu-button" type="button" aria-label="Toggle navigation" onClick={toggleNav}><AdminGlyph name="menu" /></button>
    <Link href="/" prefetch={false} className="az-header-brand"><span className="admin-brand-symbol"><AdminGlyph name="shield" size={22} /></span>EntraGuard <span className="admin-console-label">Security console</span></Link>
    <div ref={searchBox} className="admin-global-search"><label className="az-search"><IconSearch size={15} /><input ref={input} type="search" placeholder="Search EntraGuard pages" aria-label="Search EntraGuard pages" role="combobox" aria-expanded={Boolean(search.trim())} aria-controls="admin-search-results" aria-autocomplete="list" aria-activedescendant={search && results[active] ? `admin-search-${active}` : undefined} value={search} onChange={event => { setSearch(event.target.value); setActive(0); }} onKeyDown={event => {
      if (event.key === 'ArrowDown') { event.preventDefault(); setActive(i => Math.min(i + 1, results.length - 1)); }
      if (event.key === 'ArrowUp') { event.preventDefault(); setActive(i => Math.max(0, i - 1)); }
      if (event.key === 'Enter' && results[active]) { event.preventDefault(); navigate(results[active].href); }
    }} /><kbd>Ctrl K</kbd></label>
    {search.trim() && <div className="admin-search-results" id="admin-search-results" role="listbox" aria-label="Console pages">{results.length === 0 ? <p role="status">No matching pages.</p> : results.map((item, i) => <button id={`admin-search-${i}`} key={item.href} role="option" aria-selected={active === i} onMouseEnter={() => setActive(i)} onClick={() => navigate(item.href)}><AdminGlyph name={item.icon} /><span><strong>{item.label}</strong><small>{item.description}</small></span><span className="admin-search-group">{item.group}</span></button>)}</div>}
    </div>
    <div className="az-header-actions">
      <a className="az-icon-btn" href="https://docs.entraguard.my" target="_blank" rel="noreferrer" aria-label="Open EntraGuard documentation" title="Documentation"><AdminGlyph name="help" /></a>
      <button className="az-icon-btn" type="button" onClick={toggleTheme} aria-label={`Use ${theme === 'light' ? 'dark' : 'light'} theme`} title={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}><AdminGlyph name="theme" /></button>
      <div ref={account} className="admin-account-wrap"><button type="button" className="az-account" aria-expanded={accountOpen} aria-controls="admin-account-panel" onClick={() => setAccountOpen(value => !value)}><span className="az-account-lines"><b>{upn || 'Signed-in operator'}</b><span>{tenant}</span></span><span className="az-avatar">{(upn || 'EG').slice(0, 2).toUpperCase()}</span></button>
        {accountOpen && <div className="admin-account-panel" id="admin-account-panel"><strong>{upn || 'Signed-in operator'}</strong><span>Data directory: {tenant}</span><p>Directory data is read from the hosting tenant. This menu does not switch Graph tenants.</p><button className="az-cmd admin-primary" type="button" onClick={() => void logout()} disabled={signingOut}>{signingOut ? 'Signing out…' : 'Sign out'}</button>{message && <p role="alert">{message}</p>}</div>}
      </div>
    </div>
  </header>;
}

export function SideNav({ resourceGroup, onNavigate }: { resourceGroup: string; onNavigate: () => void }) {
  const pathname = usePathname();
  const [filter, setFilter] = useState('');
  const items = ADMIN_NAV.filter(item => item.label.toLowerCase().includes(filter.toLowerCase()));
  return <aside className="az-nav">
    <div className="admin-resource-header"><span className="admin-resource-logo"><AdminGlyph name="shield" size={29} /></span><div><strong>EntraGuard</strong><span>Voice identity protection</span></div></div>
    <div className="admin-nav-filter"><IconSearch size={13} /><input aria-label="Filter navigation" placeholder="Search menu" value={filter} onChange={event => setFilter(event.target.value)} /></div>
    <nav aria-label="EntraGuard sections">{items.map((item, index) => <div key={item.href}>{items[index - 1]?.group !== item.group && <div className="az-nav-group">{item.group}</div>}<Link prefetch={false} href={item.href} className="az-nav-item" aria-current={pathname === item.href ? 'page' : undefined} title={item.label} onClick={onNavigate}><AdminGlyph name={item.icon} size={17} /><span>{item.label}</span></Link></div>)}</nav>
    {!items.length && <p className="admin-nav-empty">No matching pages.</p>}
    <div className="admin-nav-footer"><span>RESOURCE GROUP</span><strong>{resourceGroup || 'Not configured'}</strong><a href="https://portal.azure.com" target="_blank" rel="noreferrer">Open Azure portal <AdminGlyph name="external" size={12} /></a></div>
  </aside>;
}

export function Breadcrumb({ trail }: { trail: string[] }) {
  return <nav className="az-breadcrumb" aria-label="Breadcrumb"><Link prefetch={false} href="/">Home</Link>{trail.map((crumb, index) => <span key={`${crumb}-${index}`} style={{ display: 'contents' }}><span className="sep" aria-hidden="true"><IconChevron size={10} /></span><span aria-current={index === trail.length - 1 ? 'page' : undefined}>{crumb}</span></span>)}</nav>;
}
