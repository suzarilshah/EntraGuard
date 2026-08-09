import { NextResponse } from 'next/server';

/** Digits reported by the device that answered the verification call. */
const media = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const base = media();
  if (!base) return NextResponse.json({ error: 'MEDIA_SERVICE_URL not configured.' }, { status: 503 });
  const { id } = await params;
  try {
    const response = await fetch(`${base}/api/verify/${encodeURIComponent(id)}/digits`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(await request.json()),
      signal: AbortSignal.timeout(10000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: error instanceof Error ? error.message : String(error) },
      { status: 502 },
    );
  }
}
