import { NextResponse } from 'next/server';

/**
 * Proxies simulation requests to the media service.
 *
 * The browser cannot call the media service directly for this: it would need the service
 * exposed as a CORS-allowed origin, and a start-a-call endpoint is exactly the kind of
 * thing that should stay behind the portal's own boundary rather than be reachable from
 * any page on the internet.
 */
const mediaService = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export async function GET() {
  const base = mediaService();
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  try {
    const response = await fetch(`${base}/api/simulate/scenarios`, {
      cache: 'no-store',
      signal: AbortSignal.timeout(8000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: `Media service unreachable: ${error instanceof Error ? error.message : error}` },
      { status: 502 },
    );
  }
}

export async function POST(request: Request) {
  const base = mediaService();
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  try {
    const body = await request.json();
    const response = await fetch(`${base}/api/simulate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      // Returns as soon as the replay is accepted; the run continues server-side and
      // streams to the browser over SignalR.
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
