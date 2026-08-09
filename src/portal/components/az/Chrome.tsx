'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  IconGrid, IconShield, IconPhone, IconHistory, IconPerson, IconSiem,
  IconResources, IconSearch, IconBell, IconSettings, IconHelp, IconChevron, IconCheck,
} from './Icons';

/**
 * The Azure portal's global command bar.
 *
 * Reproduced because it is how an Azure operator orients themselves — the waffle, the
 * global search, the account chip in the corner. A tool that lives inside this ecosystem
 * and looks nothing like it makes the reader work out where they are first.
 *
 * The search box is intentionally a real, working filter over the nav rather than
 * decoration: shipping a search field that does nothing would be worse than omitting it.
 */
export function TopBar({ tenant, upn }: { tenant: string; upn: string }) {
  const initials = (upn || 'EG')
    .split('@')[0]
    .split(/[.\-_]/)
    .slice(0, 2)
    .map((s) => s[0]?.toUpperCase() ?? '')
    .join('') || 'EG';

  return (
    <header className="az-header">
      <span className="az-waffle" aria-hidden="true"><IconGrid size={18} /></span>
      <span className="az-header-brand">Microsoft Azure</span>

      <label className="az-search">
        <IconSearch size={14} />
        <input
          type="search"
          placeholder="Search resources, services, and docs (G+/)"
          aria-label="Search"
          onChange={(event) => {
            // Filters the nav rail live. A search box that looks real and does nothing is
            // worse than no search box at all.
            const term = event.target.value.toLowerCase();
            document.querySelectorAll<HTMLElement>('[data-nav-item]').forEach((el) => {
              const label = el.dataset.navItem ?? '';
              el.style.display = !term || label.includes(term) ? '' : 'none';
            });
          }}
        />
      </label>

      <div className="az-header-actions">
        <button className="az-icon-btn" aria-label="Notifications" type="button"><IconBell size={17} /></button>
        <button className="az-icon-btn" aria-label="Settings" type="button"><IconSettings size={17} /></button>
        <button className="az-icon-btn" aria-label="Help" type="button"><IconHelp size={17} /></button>
        <div className="az-account" title={`${upn} — ${tenant}`}>
          <div className="az-account-lines">
            <b>{upn || 'EntraGuard'}</b>
            <span>{tenant || 'Directory'}</span>
          </div>
          <span className="az-avatar">{initials}</span>
        </div>
      </div>
    </header>
  );
}

const NAV = [
  { href: '/', label: 'Overview', Icon: IconShield, group: 'EntraGuard' },
  { href: '/live', label: 'Live calls', Icon: IconPhone, group: 'EntraGuard' },
  { href: '/sessions', label: 'Call history', Icon: IconHistory, group: 'EntraGuard' },
  { href: '/verification', label: 'Voice verification', Icon: IconCheck, group: 'EntraGuard' },
  { href: '/identity', label: 'Identity risk', Icon: IconPerson, group: 'Security' },
  { href: '/sentinel', label: 'Microsoft Sentinel', Icon: IconSiem, group: 'Security' },
  { href: '/health', label: 'Resource footprint', Icon: IconResources, group: 'Monitoring' },
];

export function SideNav() {
  const pathname = usePathname();
  let lastGroup = '';

  return (
    <nav className="az-nav" aria-label="EntraGuard sections">
      {NAV.map((item) => {
        const showGroup = item.group !== lastGroup;
        lastGroup = item.group;
        return (
          <div key={item.href}>
            {showGroup && <div className="az-nav-group">{item.group}</div>}
            <Link
              href={item.href}
              className="az-nav-item"
              data-nav-item={item.label.toLowerCase()}
              aria-current={pathname === item.href ? 'page' : undefined}
            >
              <item.Icon size={16} />
              {item.label}
            </Link>
          </div>
        );
      })}
    </nav>
  );
}

export function Breadcrumb({ trail }: { trail: string[] }) {
  return (
    <nav className="az-breadcrumb" aria-label="Breadcrumb">
      <Link href="/">Home</Link>
      {trail.map((crumb, index) => (
        <span key={crumb} style={{ display: 'contents' }}>
          <span className="sep" aria-hidden="true"><IconChevron size={11} /></span>
          {index === trail.length - 1 ? (
            <span aria-current="page">{crumb}</span>
          ) : (
            <Link href="/">{crumb}</Link>
          )}
        </span>
      ))}
    </nav>
  );
}
