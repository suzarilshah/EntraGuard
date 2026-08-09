import { NextResponse } from 'next/server';

/**
 * Proxies the step-up verification API to the media service.
 *
 * Kept behind the portal's own origin rather than called from the browser directly:
 * starting a verification is an authentication-flow operation, and an endpoint that can
 * place a call to any ACS identity should not be reachable from arbitrary pages on the
 * internet. In a production deployment this route is also where the RP app's session
 * cookie would be validated before the call is placed.
 */
const mediaService = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

async function forward(path: string, init?: RequestInit) {
  const base = mediaService();
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  try {
    const response = await fetch(`${base}${path}`, {
      ...init,
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

/** Recent attempts, for the admin blade. */
export async function GET() {
  return forward('/api/verify');
}

/** Start a verification: places the outbound call and returns the match code. */
export async function POST(request: Request) {
  const body = await request.json();
  return forward('/api/verify/start', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}
