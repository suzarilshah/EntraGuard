import { NextResponse } from 'next/server';

/**
 * Runtime configuration for the relying-party demo app.
 *
 * Read at request time rather than through NEXT_PUBLIC_*, because Next inlines those at
 * build time: a value set on the container app would silently not reach the browser, and
 * the Teams field would appear blank with no indication why. Anything here must be safe to
 * hand to an unauthenticated page — an Entra object ID is an identifier, not a secret.
 */
export const dynamic = 'force-dynamic';

export function GET() {
  return NextResponse.json({
    teamsObjectId: process.env.TEAMS_OBJECT_ID ?? '',
    teamsUpn: process.env.TEAMS_UPN ?? '',
    // Public by design — a client ID is an identifier, and MSAL needs it in the browser.
    entraClientId: process.env.ENTRA_RP_CLIENT_ID ?? '',
  });
}
