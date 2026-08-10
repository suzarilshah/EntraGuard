import { forwardAuthenticated } from '../../forward';

export async function GET(
  request: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const { id } = await params;
  return forwardAuthenticated(
    request, `/api/voice-profile/enrollment/${encodeURIComponent(id)}`, 'GET');
}

export const dynamic = 'force-dynamic';
