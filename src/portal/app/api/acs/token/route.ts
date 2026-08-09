import { NextResponse } from 'next/server';

/**
 * Issues the browser an ACS identity and VoIP token so the soft-phone can be called.
 *
 * Proxied rather than called directly so the media service does not need to be a
 * CORS-allowed origin for the RP app, and so the token-minting path stays on the server
 * side of the relying party — where, in production, the user's session would be verified
 * before an identity is handed out.
 */
export async function POST(request: Request) {
  const base = process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  try {
    const body = await request.json();
    const response = await fetch(`${base}/api/acs/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal: AbortSignal.timeout(15000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: `Media service unreachable: ${error instanceof Error ? error.message : error}` },
      { status: 502 },
    );
  }
}
