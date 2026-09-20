import type { Metadata } from 'next';
import { AdminShell } from '@/components/az/Chrome';
import { getTenant } from '@/lib/azure/graph';
import { getOperatorProfile } from '@/lib/azure/mediaService';
import { resourceGroup } from '@/lib/adminData';
import './admin.css';

export const metadata: Metadata = {
  title: 'EntraGuard',
};

export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  // Read once here so the account chip is populated on every blade without each page
  // repeating the Graph call.
  const [tenant, operator] = await Promise.all([getTenant(), getOperatorProfile()]);

  return (
    <AdminShell tenant={tenant.data?.displayName ?? 'Directory unavailable'}
      upn={operator.data?.owner.upn ?? ''} resourceGroup={resourceGroup() ?? ''}>{children}</AdminShell>
  );
}
