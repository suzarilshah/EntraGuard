import { forwardRp } from '@/lib/rpProxy';
import { NextResponse } from 'next/server';
export async function POST(request: Request) { return forwardRp(request, '/api/verify/knowledge'); }
export async function GET(request: Request) {
  const query = new URL(request.url).searchParams;
  const tenant = query.get('tenantId'); const subject = query.get('objectId');
  if (!tenant || !subject) return NextResponse.json({ error: 'Identity is required.' }, { status: 400 });
  return forwardRp(request, `/api/verify/knowledge/${encodeURIComponent(tenant)}/${encodeURIComponent(subject)}`);
}
