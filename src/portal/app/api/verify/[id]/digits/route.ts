import { forwardRp } from '@/lib/rpProxy';
export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  return forwardRp(request, `/api/verify/${encodeURIComponent((await params).id)}/digits`);
}
