import { TreasuryApp } from '@/components/rp/TreasuryApp';
import './rp.css';

export const metadata = {
  title: 'Contoso Treasury',
  description: 'Payment operations — protected by EntraGuard voice verification.',
};

/**
 * The relying-party demo application.
 *
 * A fictional line-of-business app holding something worth stealing — payment runs — so
 * that step-up verification is obviously warranted rather than ceremonial. Everything
 * lives in one client component because the whole flow is a single stateful journey:
 * password, method choice, live call, verdict, access.
 */
export default function TreasuryPage() {
  return <TreasuryApp />;
}
