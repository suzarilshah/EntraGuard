import { NextResponse } from 'next/server';

/** Poll one verification attempt for its verdict. */
export async function GET(
  _request: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const base = process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  const { id } = await params;

  try {
    const response = await fetch(`${base}/api/verify/${encodeURIComponent(id)}`, {
      cache: 'no-store',
      signal: AbortSignal.timeout(10000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch (error) {
    return NextResponse.json(
      { error: `Media service unreachable: ${error instanceof Error ? error.message : error}` },
      { status: 502 },
    );
  }
}
