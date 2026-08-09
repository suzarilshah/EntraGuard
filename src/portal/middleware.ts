import { NextResponse, type NextRequest } from 'next/server';

/**
 * Splits one build into two products.
 *
 * Contoso Treasury is a customer's application that has integrated EntraGuard. Serving it
 * from a path inside the security console undercut the entire premise — a judge looking at
 * `/app` on the admin portal sees one system pretending to be two, and the relying-party
 * boundary is the whole point of a step-up factor.
 *
 * So the same image runs twice on two hostnames, and `APP_MODE` decides which product it
 * is. One build, one deployment pipeline, no duplicated component tree to drift.
 *
 * The separation is enforced, not cosmetic: in treasury mode the admin blades are not
 * reachable at all, so somebody who guesses `/identity` on the customer's hostname gets a
 * 404 rather than a live view of the security console. A boundary that exists only in the
 * navigation is not a boundary.
 */
const TREASURY_ALLOWED = [
  '/app',
  '/settings',
  '/phone',
  '/api/verify',
  '/api/acs',
  '/api/presence',
  '/api/rp',
  '/_next',
  '/favicon',
];

export function middleware(request: NextRequest) {
  if (process.env.APP_MODE !== 'treasury') {
    return NextResponse.next();
  }

  const { pathname } = request.nextUrl;

  // The customer's application lives at the root of its own hostname.
  if (pathname === '/') {
    return NextResponse.rewrite(new URL('/app', request.url));
  }

  if (TREASURY_ALLOWED.some((prefix) => pathname.startsWith(prefix))) {
    return NextResponse.next();
  }

  // Everything else is the security console, which does not exist on this hostname.
  return NextResponse.rewrite(new URL('/not-found-treasury', request.url));
}

export const config = {
  matcher: ['/((?!_next/static|_next/image).*)'],
};
