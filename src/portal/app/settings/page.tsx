// The relying-party stylesheet, same as the app page. Without it this route inherits the
// admin console's theme — the two products share one Next build, so a page that forgets
// this renders as the wrong company.
import '../app/rp.css';
import { TreasurySettings } from '../../components/rp/TreasurySettings';

export const metadata = { title: 'Settings · Contoso Treasury' };

export default function SettingsPage() {
  return <TreasurySettings />;
}
