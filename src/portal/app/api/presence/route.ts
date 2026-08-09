import { NextResponse } from 'next/server';

/** Device heartbeats, proxied to the media service which owns the presence table. */
const media = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export async function POST(request: Request) {
  const base = media();
  if (!base) return NextResponse.json({ error: 'MEDIA_SERVICE_URL not configured.' }, { status: 503 });
  try {
    const response = await fetch(`${base}/api/presence`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(await request.json()),
      signal: AbortSignal.timeout(8000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: error instanceof Error ? error.message : String(error) },
      { status: 502 },
    );
  }
}
