import Link from 'next/link';
import { Breadcrumb } from '@/components/az/Chrome';
import { Card, PageHead } from '@/components/az/Surfaces';
import { IconError } from '@/components/az/Icons';

// The root layout reads the tenant from Microsoft Graph on every render, so nothing under
// it can be statically prerendered — including this page. Declaring that explicitly keeps
// the build clean instead of emitting a dynamic-server-usage warning.
export const dynamic = 'force-dynamic';

export default function NotFound() {
  return (
    <>
      <Breadcrumb trail={['Not found']} />
      <PageHead title="Not found" icon={<IconError size={17} />} />
      <div className="az-content">
        <Card title="404" icon={<IconError size={15} />}>
          <p style={{ margin: '0 0 14px', color: 'var(--az-text-2)' }}>
            That page does not exist in this portal.
          </p>
          <Link href="/" className="az-cmd is-on" style={{ display: 'inline-flex' }}>
            Back to Overview
          </Link>
        </Card>
      </div>
    </>
  );
}
