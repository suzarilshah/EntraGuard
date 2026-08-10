import { forwardAuthenticated } from '../forward';

/** Start an enrollment call. The media service takes the user from the token, not the body. */
export async function POST(request: Request) {
  return forwardAuthenticated(request, '/api/voice-profile/enrollment/start', 'POST');
}

export const dynamic = 'force-dynamic';
