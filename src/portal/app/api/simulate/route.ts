import { forwardRp } from '@/lib/rpProxy';
export async function GET(request: Request) { return forwardRp(request, '/api/simulate/scenarios'); }
export async function POST(request: Request) { return forwardRp(request, '/api/simulate'); }
