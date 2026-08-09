import { NextResponse } from 'next/server';

const media = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export async function GET(_: Request, { params }: { params: Promise<{ upn: string }> }) {
  const base = media();
  if (!base) return NextResponse.json({ any: false }, { status: 503 });
  const { upn } = await params;
  try {
    const response = await fetch(`${base}/api/presence/${encodeURIComponent(upn)}`, {
      cache: 'no-store',
      signal: AbortSignal.timeout(8000),
    });
    return NextResponse.json(await response.json(), { status: response.status });
  } catch {
    // A failed check must read as "not reachable", never as "probably fine" — the whole
    // point of this gate is to stop a call going out to a device that cannot answer.
    return NextResponse.json({ any: false }, { status: 200 });
  }
}
