import { NextResponse } from 'next/server';

/** Poll one verification attempt for its verdict. */
export async function GET(
  request: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const base = process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  const { id } = await params;

  try {
    // Forwarded, not minted here. The media service redacts the match code for callers
    // without it, so a proxy that dropped this header would silently stop the RP being able
    // to show the number — which is why it is threaded through explicitly rather than by a
    // blanket header copy that a future refactor could quietly break.
    const token = request.headers.get('X-Verification-Token');

    const response = await fetch(`${base}/api/verify/${encodeURIComponent(id)}`, {
      cache: 'no-store',
      headers: token ? { 'X-Verification-Token': token } : undefined,
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
