import { Suspense } from 'react';
import { PhoneEndpoint } from '@/components/rp/PhoneEndpoint';
import '../app/rp.css';

export const metadata = {
  title: 'EntraGuard — your device',
  description: 'Receive EntraGuard verification calls on this device.',
};

// The account arrives in the query string, so this page is per-user and cannot be
// prerendered. Next also requires useSearchParams to sit inside a Suspense boundary,
// otherwise the build fails during static export.
export const dynamic = 'force-dynamic';

/**
 * The user's handset.
 *
 * Opened on a real phone, this registers the same ACS identity the desktop session uses,
 * so an EntraGuard verification call genuinely rings on the phone in the user's hand. No
 * PSTN number is involved — number purchase returns 403 on this subscription — so the call
 * arrives over data rather than the phone network. From the user's point of view the
 * difference is invisible: their phone rings, a voice asks for the number on their screen,
 * they key it in.
 */
export default function PhonePage() {
  return (
    <Suspense fallback={<div className="rp" style={{ minHeight: '100vh' }} />}>
      <PhoneEndpoint />
    </Suspense>
  );
}
