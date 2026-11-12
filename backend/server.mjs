import { createServer } from 'node:http';
import { isIPv4 } from 'node:net';
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';
import { Store, RuleError, demo } from './store.mjs';
import { qr } from './qr.mjs';
import { portal, escape, sendHtml } from './portal.mjs';
import { proofPdf } from './pdf.mjs';

function privateAddress(value) {
  if (value === '127.0.0.1') return true;
  if (!isIPv4(value)) return false;
  const [a, b] = value.split('.').map(Number);
  return a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31);
}

async function body(request, limit) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > limit) throw new RuleError('Die Anfrage ist zu groß.', 413);
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

function start(server, port, host) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    const ready = () => {
      server.off('error', reject);
      resolve(server.address().port);
    };
    if (host) server.listen(port, host, ready);
    else server.listen(port, ready);
  });
}

function createHandler(route, failure) {
  return createServer({ maxHeaderSize: 8192, requestTimeout: 6000, headersTimeout: 4000, keepAliveTimeout: 1200 },
    (request, response) => {
      route(request, response).catch(error => {
        if (response.destroyed || response.headersSent) return response.destroy();
        const expected = error instanceof RuleError;
        if (!expected) process.stderr.write(`Dokju: ${error.message}\n`);
        failure(response, expected ? error.status : 500, expected ? error.message : 'Dokju konnte die Anfrage nicht verarbeiten.');
      });
    });
}

export async function run(testSocket) {
  const key = Buffer.from(process.env.DOKJU_KEY ?? '', 'base64');
  const directory = process.env.DOKJU_DATA;
  const bind = process.env.DOKJU_BIND ?? '127.0.0.1';
  const mobilePort = Number(process.env.DOKJU_PORT ?? '8787');
  if (key.length !== 32 || !directory)
    throw new Error('Die lokale Startkonfiguration fehlt.');
  if (!privateAddress(bind) || !Number.isInteger(mobilePort) || mobilePort < 0 || mobilePort > 65535)
    throw new Error('Die Demo benötigt eine lokale oder private IPv4-Adresse und einen gültigen Port.');
  delete process.env.DOKJU_KEY;
  const store = new Store(directory, key);
  store.prune();
  let origin;
  const serveCitizen = async (request, response) => {
    if (request.headers.host !== new URL(origin).host || !request.url.startsWith('/') || request.url.startsWith('//'))
      throw new RuleError('Ungültige lokale Adresse.', 421);
    store.prune();
    await portal(request, response, store, origin, body);
  };
  const citizenFailure = (response, status, message) => sendHtml(response, status,
    `<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dokju</title><main><h1>Dokju</h1><p>${escape(message)}</p></main></html>`);
  const citizen = createHandler(serveCitizen, citizenFailure);
  const actualPort = await start(citizen, mobilePort, bind);
  origin = `http://${bind}:${actualPort}`;
  const mirror = testSocket ? createHandler(serveCitizen, citizenFailure) : null;
  if (mirror) await start(mirror, testSocket);

  function command({ method, path, data }) {
    const send = (status, value) => ({ status, value });
    if (typeof path !== 'string' || !path.startsWith('/')) throw new RuleError('Ungültige Anfrage.');
    store.prune();
    const url = new URL(path, 'http://dokju.local');
    if (method === 'POST' && (!data || typeof data !== 'object' || Array.isArray(data)))
      throw new RuleError('Ungültige Eingabe.');
    if (method === 'GET' && url.pathname === '/cases')
      return send(200, { origin, defaults: demo, cases: store.data.cases.map(item => store.view(item)).reverse() });
    if (method === 'POST' && url.pathname === '/cases')
      return send(201, store.create(data));
    if (method === 'POST' && url.pathname === '/verify')
      return send(200, store.verify(data.proof ?? data, data.pdfHash));
    const renew = /^\/cases\/([0-9a-f-]{36})\/renew$/.exec(url.pathname);
    if (method === 'POST' && renew) return send(200, store.renew(renew[1], data.person));
    const match = /^\/cases\/([0-9a-f-]{36})\/(status|export|invite)$/.exec(url.pathname);
    if (method !== 'GET' || !match) return send(404, { error: 'Unbekannte Aktion.' });
    const [, id, action] = match;
    if (action === 'status') return send(200, store.view(store.lookup(id)));
    if (action === 'invite') {
      const item = store.view(store.lookup(id));
      const person = item.invites.find(invite => String(invite.number) === url.searchParams.get('person'));
      if (!person?.token) return send(409, { error: 'Für diese Person ist kein offener QR-Code verfügbar.' });
      const link = `${origin}/i/${person.token}`;
      return send(200, { link, qr: qr(link) });
    }
    const proof = store.evidence(id);
    const pdf = proofPdf(proof);
    return send(200, { proof: store.attachDocument(proof, pdf), pdf: pdf.toString('base64') });
  }
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  input.on('line', line => {
    let result;
    try {
      if (line.length > 240_000) throw new RuleError('Die Eingabe ist zu groß.', 413);
      result = command(JSON.parse(line));
    } catch (error) {
      const expected = error instanceof RuleError || error instanceof SyntaxError;
      if (!expected) process.stderr.write(`Dokju: ${error.message}\n`);
      result = {
        status: expected ? (error.status ?? 400) : 500,
        value: { error: expected ? error.message : 'Dokju konnte die Anfrage nicht verarbeiten.' },
      };
    }
    process.stdout.write(JSON.stringify(result) + '\n');
  });
  process.stdout.write(JSON.stringify({ origin }) + '\n');
  const timer = setInterval(() => {
    try { store.prune(); } catch (error) { process.stderr.write(`Dokju: ${error.message}\n`); }
  }, 60_000);
  function stop() {
    clearInterval(timer);
    citizen.close();
    citizen.closeAllConnections();
    if (mirror) {
      mirror.close();
      mirror.closeAllConnections();
    }
    input.close();
    process.stdin.pause();
  }
  input.once('close', stop);
  process.once('SIGTERM', stop);
}

export function startupError(error) {
  process.stdout.write(JSON.stringify({ error: error.message }) + '\n');
  process.exit(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  run().catch(startupError);
