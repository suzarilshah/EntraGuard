/** Shared route inventory: adding a blade also adds it to the operator auth guard. */
export const ADMIN_NAV = [
  { href: '/', label: 'Overview', icon: 'overview', group: 'General', description: 'Verification outcomes and operational summary' },
  { href: '/verification', label: 'Verifications', icon: 'check', group: 'Activity', description: 'Challenge results, assurance and evidence' },
  { href: '/live', label: 'Live calls', icon: 'phone', group: 'Activity', description: 'Live transcript and policy decisions' },
  { href: '/identity', label: 'Microsoft Entra ID', icon: 'identity', group: 'Identity & security', description: 'Directory users, sign-ins and Identity Protection' },
  { href: '/incidents', label: 'Sentinel incidents', icon: 'shield', group: 'Identity & security', description: 'Workspace incidents and investigation links' },
  { href: '/voice-insights', label: 'Voice insights', icon: 'activity', group: 'Monitoring', description: 'Voice comparisons, follow-ups and profile lifecycle' },
  { href: '/health', label: 'Azure resources', icon: 'resources', group: 'Monitoring', description: 'Container Apps, revisions and resource-group inventory' },
] as const;

export function isAdminPath(pathname: string) {
  const path = pathname.replace(/\/$/, '') || '/';
  return ADMIN_NAV.some(item => path === item.href || (item.href !== '/' && path.startsWith(`${item.href}/`)));
}
