import { cookies } from 'next/headers';
import { NextResponse } from 'next/server';

export const RP_COOKIE = 'entraguard-rp';
export const mediaBase = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export function sameOrigin(request: Request): boolean {
  try { return new URL(request.headers.get('origin') ?? '').origin === new URL(process.env.APP_PUBLIC_ORIGIN ?? request.url).origin; }
  catch { return false; }
}

export async function sessionHeaders(): Promise<Record<string, string>> {
  const value = (await cookies()).get(RP_COOKIE)?.value;
  return value ? { 'X-Rp-Session': value } : {};
}

export async function forwardRp(request: Request, path: string, method = request.method): Promise<Response> {
  if (!['GET', 'HEAD'].includes(method) && !sameOrigin(request))
    return NextResponse.json({ error: 'A same-origin request is required.' }, { status: 403 });
  const base = mediaBase();
  if (!base) return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  const headers = await sessionHeaders();
  if (!headers['X-Rp-Session']) return NextResponse.json({ error: 'Sign in to continue.' }, { status: 401 });
  const viewer = request.headers.get('X-Verification-Token');
  if (viewer) headers['X-Verification-Token'] = viewer;
  const version = request.headers.get('If-Match');
  if (version) headers['If-Match'] = version;
  const idempotency = request.headers.get('Idempotency-Key');
  if (idempotency) headers['Idempotency-Key'] = idempotency;
  if (path === '/api/account/stepup' && request.headers.get('X-Id-Token')) headers['X-Id-Token'] = request.headers.get('X-Id-Token')!;
  let body: string | undefined;
  if (!['GET', 'HEAD', 'DELETE'].includes(method)) { body = await request.text(); headers['Content-Type'] = 'application/json'; }
  try {
    const response = await fetch(`${base}${path}`, { method, headers, body, cache: 'no-store', signal: AbortSignal.timeout(20000) });
    if (response.status === 204) return new NextResponse(null, { status: 204, headers: { 'Cache-Control': 'no-store' } });
    const text = await response.text();
    return new NextResponse(text || JSON.stringify({ error: response.status === 401 ? 'Your session has expired. Sign in again.' : `Request returned ${response.status}.` }),
      { status: response.status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...(response.headers.get('etag') ? { ETag: response.headers.get('etag')! } : {}) } });
  } catch { return NextResponse.json({ error: 'The verification service is unavailable. Please retry.' }, { status: 502 }); }
}
