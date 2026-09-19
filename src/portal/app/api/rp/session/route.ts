import { cookies } from 'next/headers';
import { NextResponse } from 'next/server';
import { forwardRp, mediaBase, RP_COOKIE, sameOrigin, sessionHeaders } from '@/lib/rpProxy';

export async function POST(request: Request) {
  if (!sameOrigin(request)) return NextResponse.json({ error: 'Same-origin sign-in required.' }, { status: 403 });
  const authorization = request.headers.get('authorization');
  if (!authorization?.startsWith('Bearer ')) return NextResponse.json({ error: 'Microsoft access token required.' }, { status: 401 });
  if (!mediaBase()) return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  try {
    const response = await fetch(`${mediaBase()}/api/rp/session`, { method: 'POST', headers: { Authorization: authorization }, cache: 'no-store', signal: AbortSignal.timeout(15000) });
    if (!response.ok) return NextResponse.json({ error: 'EntraGuard could not validate this sign-in. Check API consent and try again.' }, { status: response.status });
    const issued = await response.json();
    if (typeof issued.token !== 'string' || !Number.isFinite(Date.parse(issued.expiresAt))) throw new Error('Invalid session response');
    // Revoke the previous cookie after a replacement has been issued; never reuse a session across accounts.
    const old = await sessionHeaders();
    if (old['X-Rp-Session']) {
      const revoked = await fetch(`${mediaBase()}/api/rp/session`, { method: 'DELETE', headers: old, cache: 'no-store', signal: AbortSignal.timeout(10000) });
      if (!revoked.ok && revoked.status !== 401) throw new Error('Old session could not be revoked');
    }
    (await cookies()).set(RP_COOKIE, issued.token, { httpOnly: true, secure: process.env.NODE_ENV === 'production', sameSite: 'lax', path: '/', expires: new Date(issued.expiresAt) });
    return NextResponse.json({ signedIn: true, expiresAt: issued.expiresAt }, { headers: { 'Cache-Control': 'no-store' } });
  } catch { return NextResponse.json({ error: 'The session service is unavailable. Please retry.' }, { status: 502 }); }
}
export async function GET(request: Request) { return forwardRp(request, '/api/rp/session'); }
export async function DELETE(request: Request) {
  const response = await forwardRp(request, '/api/rp/session');
  if (response.ok || response.status === 401) (await cookies()).delete(RP_COOKIE);
  return response;
}
