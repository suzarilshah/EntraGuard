import { forwardRp } from '@/lib/rpProxy';
import { NextResponse } from 'next/server';
async function forward(request: Request, { params }: { params: Promise<{ path: string[] }> }) {
  const parts = (await params).path;
  if (!['history', 'preferences', 'devices', 'notifications', 'readiness', 'policy', 'payments', 'approvals', 'grant', 'stepup'].includes(parts[0]))
    return NextResponse.json({ error: 'Not found' }, { status: 404 });
  return forwardRp(request, `/api/account/${parts.map(encodeURIComponent).join('/')}${new URL(request.url).search}`);
}
export { forward as GET, forward as POST, forward as PUT, forward as DELETE };
