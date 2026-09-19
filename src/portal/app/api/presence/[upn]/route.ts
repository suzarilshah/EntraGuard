import { forwardRp } from '@/lib/rpProxy';
export async function GET(request: Request, { params }: { params: Promise<{ upn: string }> }) {
  return forwardRp(request, `/api/presence/${encodeURIComponent((await params).upn)}`);
}
