import { forwardRp } from '@/lib/rpProxy';
export async function POST(request: Request) { return forwardRp(request, '/api/acs/token'); }
