import { forwardAuthenticated } from './forward';

/** Status and deletion of the signed-in user's own voice profile. */
export async function GET(request: Request) {
  return forwardAuthenticated(request, '/api/voice-profile/me', 'GET');
}

export async function DELETE(request: Request) {
  return forwardAuthenticated(request, '/api/voice-profile/me', 'DELETE');
}

export const dynamic = 'force-dynamic';
