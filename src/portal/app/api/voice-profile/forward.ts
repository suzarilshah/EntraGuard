import { NextResponse } from 'next/server';

/**
 * Proxy to the media service, carrying the caller's bearer token.
 *
 * Every other proxy route in this app forwards blindly, and the media service identifies the
 * user from a field in the JSON body. That is survivable for a knowledge question and not
 * survivable for a biometric: an object ID the browser can type is an object ID an attacker
 * can type, and enrolling their voice against your account is not a mistake you can undo by
 * changing a password.
 *
 * So these routes pass the Authorization header through unchanged and let the media service
 * validate it. This route deliberately does NOT inspect or trust the token itself — the
 * portal is not the security boundary, and a check here would only be a second place to get
 * it wrong.
 */
export async function forwardAuthenticated(
  request: Request,
  path: string,
  method: 'GET' | 'POST' | 'DELETE',
): Promise<Response> {
  const base = process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';
  if (!base) {
    return NextResponse.json({ error: 'MEDIA_SERVICE_URL is not configured.' }, { status: 503 });
  }

  const authorization = request.headers.get('authorization');
  if (!authorization) {
    // Refused here rather than forwarded, so an unauthenticated call never reaches the
    // service at all and the failure is unambiguous in the browser.
    return NextResponse.json(
      { error: 'Sign in before managing a voice profile.' },
      { status: 401 },
    );
  }

  const headers: Record<string, string> = { Authorization: authorization };
  let body: string | undefined;

  if (method === 'POST') {
    body = JSON.stringify(await request.json());
    headers['Content-Type'] = 'application/json';
  }

  try {
    const response = await fetch(`${base}${path}`, {
      method,
      headers,
      body,
      cache: 'no-store',
      signal: AbortSignal.timeout(20000),
    });

    const text = await response.text();
    return new NextResponse(text, {
      status: response.status,
      headers: { 'Content-Type': 'application/json' },
    });
  } catch (error) {
    return NextResponse.json(
      { error: `Media service unreachable: ${error instanceof Error ? error.message : error}` },
      { status: 502 },
    );
  }
}
