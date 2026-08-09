/**
 * Turn Azure SDK errors into something a human can act on.
 *
 * `DefaultAzureCredential` failures in particular arrive as a multi-paragraph transcript
 * of every credential type it tried. Dumping that into a panel buries the one sentence
 * that matters — usually "nobody is signed in" — under several hundred characters of
 * chain diagnostics. The full text still goes to the server log; the UI gets the summary.
 */
export function summariseAzureError(message: string): string {
  const text = message.trim();

  if (/ChainedTokenCredential|CredentialUnavailableError|DefaultAzureCredential/i.test(text)) {
    return 'No Azure credential is available. Run `az login` for local development, or confirm the container app has its user-assigned managed identity attached.';
  }

  if (/AADSTS9002313|Invalid request/i.test(text)) {
    return 'The cached Azure token is stale. Run `az login --scope https://management.core.windows.net//.default`.';
  }

  if (/AADSTS700016|application.*not found/i.test(text)) {
    return 'The configured application was not found in this tenant. Re-run scripts/02-entra-apps.sh.';
  }

  if (/ENOTFOUND|ECONNREFUSED|fetch failed/i.test(text)) {
    return 'Could not reach the Azure endpoint. Check network access and that the resource is deployed.';
  }

  // Anything unrecognised: first sentence only, hard-capped. A panel is not a log viewer.
  const firstSentence = text.split(/(?<=\.)\s/)[0] ?? text;
  return firstSentence.length > 180 ? `${firstSentence.slice(0, 180)}…` : firstSentence;
}
