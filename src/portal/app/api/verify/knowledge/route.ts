import { NextResponse } from 'next/server';

/**
 * Knowledge-question registration and lookup, proxied to the media service.
 *
 * Behind the portal's own origin like the rest of the verification API. The answer travels
 * through here in plaintext exactly once, on its way to being hashed server-side, and is
 * never written to a log or a response.
 */
const mediaService = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export async function POST(request: Request) {
  const base = mediaService();
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  const body = await request.json();

  try {
    const response = await fetch(`${base}/api/verify/knowledge`, {
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

/** Is a question already registered for this identity? Never returns anything verifiable. */
export async function GET(request: Request) {
  const base = mediaService();
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  const { searchParams } = new URL(request.url);
  const tenantId = searchParams.get('tenantId');
  const objectId = searchParams.get('objectId');

  if (!tenantId || !objectId) {
    return NextResponse.json({ error: 'tenantId and objectId are required.' }, { status: 400 });
  }

  try {
    const response = await fetch(
      `${base}/api/verify/knowledge/${encodeURIComponent(tenantId)}/${encodeURIComponent(objectId)}`,
      { cache: 'no-store', signal: AbortSignal.timeout(10000) },
    );
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: `Media service unreachable: ${error instanceof Error ? error.message : error}` },
      { status: 502 },
    );
  }
}
