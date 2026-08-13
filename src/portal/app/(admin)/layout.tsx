import type { Metadata } from 'next';
import { TopBar, SideNav } from '@/components/az/Chrome';
import { getTenant } from '@/lib/azure/graph';

export const metadata: Metadata = {
  title: 'EntraGuard',
};

export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  // Read once here so the account chip is populated on every blade without each page
  // repeating the Graph call.
  const tenant = await getTenant();

  return (
    <>
      <TopBar
        tenant={tenant.data?.displayName ?? 'Directory'}
        upn={tenant.data?.verifiedDomains?.find((domain) => domain.isDefault)?.name ?? 'entraguard'}
      />
      <div className="az-shell">
        <SideNav />
        <div className="az-main">{children}</div>
      </div>
    </>
  );
}
