import type { Metadata } from 'next';
import './globals.css';

// Deliberately bare. Two very different surfaces live under this root:
//
//   (admin) — the Azure portal blade an operator uses
//   /app    — "Contoso Treasury", the relying-party app an end user signs into
//
// They must not share chrome. Wrapping the RP app in Azure portal furniture would blur
// the demo's central point, which is that EntraGuard is a factor a third-party
// application consumes, not a place users go.

export const metadata: Metadata = {
  title: 'EntraGuard',
  description:
    'Real-time voice verification and anti-scam defence for Microsoft Entra ID authentication.',
  icons: { icon: '/favicon.svg' },
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
