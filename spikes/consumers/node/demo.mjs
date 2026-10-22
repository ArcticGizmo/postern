// node demo.mjs [--provider github] [--pipe NAME]
// Registers "node-spike", calls /user and /graphql through Postern, and checks an out-of-scope call is refused.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { call, posternError, register } from './postern-client.mjs';

const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
};
const provider = arg('--provider', 'github');
const pipe = arg('--pipe', process.env.POSTERN_PIPE || 'postern');
const tokenFile = path.join(os.homedir(), 'AppData', 'Local', 'Postern.Client', `node-spike.${pipe}.plain-token`);
let failed = 0;
const check = (ok, what, detail = '') => {
  console.log(`[${ok ? 'PASS' : 'FAIL'}] ${what} ${detail}`);
  if (!ok) failed++;
};

let token = fs.existsSync(tokenFile) ? fs.readFileSync(tokenFile, 'utf8').trim() : undefined;
if (token) {
  const probe = await call({ path: '/_postern/status', token, pipe });
  if (!JSON.parse(probe.body).app || JSON.parse(probe.body).app.status !== 'approved') token = undefined;
}
if (!token) {
  console.log("Registering 'node-spike' with Postern; approve it in the Postern window...");
  token = await register('node-spike', [`GET ${provider}/user`, `POST ${provider}/graphql`], { pipe });
  fs.mkdirSync(path.dirname(tokenFile), { recursive: true });
  fs.writeFileSync(tokenFile, token); // plain text: a spike shortcut, see postern-client.mjs
}

const user = await call({ path: `/${provider}/user`, token, pipe });
check(user.status === 200 && !posternError(user), `GET ${provider}/user`, `-> ${user.status}, remaining ${user.headers['postern-ratelimit-remaining']}`);

const gql = await call({ method: 'POST', path: `/${provider}/graphql`, body: { query: '{ viewer { login } }' }, token, pipe });
check(gql.status === 200 && JSON.parse(gql.body).data, `POST ${provider}/graphql`, `-> ${gql.status}`);

const denied = await call({ method: 'DELETE', path: `/${provider}/user`, token, pipe });
check(posternError(denied) === 'scope-denied', 'out-of-scope DELETE refused by Postern', `-> ${denied.status} ${posternError(denied)}`);

const misdirected = await call({ path: `/${provider}/user`, token, pipe, headers: { Host: 'api.github.com' } });
check(posternError(misdirected) === 'misdirected-host', 'request addressed to another host refused', `-> ${misdirected.status}`);

process.exit(failed ? 1 : 0);
