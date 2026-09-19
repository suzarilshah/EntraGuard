import { readFile } from 'node:fs/promises';
import path from 'node:path';

/**
 * The EntraGuard Handbook, served as a single self-contained page.
 *
 * Read from disk at request time rather than imported, because it is a complete HTML
 * document and not a React tree: Next would have to parse 200KB of markup it will never
 * change a byte of. Reading it keeps the document authoritative and the build fast.
 *
 * Hostnames are substituted here rather than baked in. The handbook is public, the
 * repository is public, and neither should carry the estate's Container Apps FQDNs — so the
 * file holds placeholders and the running service fills in whatever it is actually deployed
 * behind. That also means the page tells the truth after a custom domain is attached,
 * instead of pointing readers at the URLs it was written against.
 */
export const dynamic = 'force-dynamic';

const CACHE_SECONDS = 300;

function hosts() {
  const media = process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';
  return {
    __MEDIA_URL__: media,
    __PORTAL_URL__: process.env.PORTAL_PUBLIC_URL?.replace(/\/$/, '') ?? '',
    __TREASURY_URL__: process.env.TREASURY_PUBLIC_URL?.replace(/\/$/, '') ?? '',
    // Internal ingress by design: reachable only from inside the Container Apps
    // environment, so there is no public URL to print and saying so is the honest value.
    __VOICEPRINT_URL__: process.env.VOICEPRINT_URL?.replace(/\/$/, '')
      ?? 'internal ingress — not reachable from the internet',
  };
}

export async function GET() {
  let html: string;

  try {
    html = await readFile(path.join(process.cwd(), 'content', 'handbook.html'), 'utf8');
  } catch {
    return new Response('The handbook is not available in this build.', {
      status: 404,
      headers: { 'Content-Type': 'text/plain; charset=utf-8' },
    });
  }

  for (const [token, value] of Object.entries(hosts())) {
    html = html.split(token).join(value);
  }

  return new Response(html, {
    headers: {
      'Content-Type': 'text/html; charset=utf-8',
      'Cache-Control': `public, max-age=${CACHE_SECONDS}`,
      // The handbook is documentation, not an application surface. It never frames
      // anything and nothing should frame it.
      'X-Frame-Options': 'DENY',
      'X-Content-Type-Options': 'nosniff',
    },
  });
}
