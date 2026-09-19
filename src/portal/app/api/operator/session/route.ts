import { forwardRp } from '@/lib/rpProxy';
export async function GET(request: Request) { return forwardRp(request, '/api/operator/session'); }
