// A minimal Postern client for Node, using only node:http. Node speaks to Windows named pipes natively through
// `socketPath`, so no native module is needed for the transport.
//
// Gaps compared with Postern.Client (.NET), on purpose for a spike:
//   - no server verification (GetNamedPipeServerProcessId needs a native call), so it trusts whatever owns the pipe;
//   - the token is kept in a plain file, not DPAPI.
// Both are fixable with a small native addon or by shelling out to postern-cli; see README.

import http from 'node:http';

const defaultPipe = process.env.POSTERN_PIPE || 'postern';

/** One HTTP request over the pipe. Resolves { status, headers, body } (body as a string). */
export function call({ method = 'GET', path, headers = {}, body, token, pipe = defaultPipe }) {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body);
    const req = http.request(
      {
        socketPath: `\\\\.\\pipe\\${pipe}`,
        method,
        path,
        headers: {
          Host: 'postern', // Postern refuses anything not addressed to "postern" (421)
          ...(token ? { 'Postern-App-Token': token } : {}),
          ...(payload !== undefined ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
          ...headers,
        },
      },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') }));
      },
    );
    req.on('error', reject);
    if (payload !== undefined) req.write(payload);
    req.end();
  });
}

/** Postern's own error code on a response, or undefined if Postern let it through. */
export const posternError = (res) => res.headers['postern-error'];

/** Register and wait for the user's decision. Resolves the token, or throws with Postern's reason. */
export async function register(name, scopes, { pipe = defaultPipe, timeoutMs = 150_000 } = {}) {
  const begin = await call({ method: 'POST', path: '/_postern/register', body: { name, scopes }, pipe });
  if (posternError(begin)) throw new Error(`${posternError(begin)}: ${begin.body}`);
  const { id, claim } = JSON.parse(begin.body);

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const wait = Math.max(1, Math.min(30, Math.round((deadline - Date.now()) / 1000)));
    const res = await call({ method: 'POST', path: `/_postern/register/${id}/claim?wait=${wait}`, body: { claim }, pipe });
    if (posternError(res)) throw new Error(`${posternError(res)}: ${JSON.parse(res.body).detail}`);
    if (res.status === 200) return JSON.parse(res.body).token;
  }
  throw new Error('timed out waiting for approval');
}
