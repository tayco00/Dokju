import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { Store } from '../backend/store.mjs';

const server = fileURLToPath(new URL('./service-harness.mjs', import.meta.url));
const fixture = {
  title: 'Beratung mit Özer <Test>', reference: 'AZ-Ä-42', count: 2, controller: 'Verwaltung',
  provider: 'Protokolldienst', contact: 'datenschutz@example.invalid', audioDays: 2, proofDays: 90,
};

function temporary(t) {
  const directory = mkdtempSync(join(tmpdir(), 'dokju-test-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

async function launch(directory, key, bind = '127.0.0.1', port = '0', publicOrigin = '') {
  const socketPath = globalThis.process.platform === 'win32'
    ? '\\\\.\\pipe\\LOCAL\\dokju-test-' + randomUUID() : join(directory, 'test.sock');
  const process = spawn(globalThis.process.execPath, [server], {
    windowsHide: true,
    env: { ...globalThis.process.env, DOKJU_DATA: directory, DOKJU_KEY: key.toString('base64'),
      DOKJU_TEST_SOCKET: socketPath, DOKJU_PORT: port, DOKJU_BIND: bind, DOKJU_PUBLIC_ORIGIN: publicOrigin },
  });
  const output = createInterface({ input: process.stdout });
  const replies = output[Symbol.asyncIterator]();
  const startup = JSON.parse((await replies.next()).value);
  assert.ok(!startup.error, startup.error);
  async function request(route, body) {
    process.stdin.write(JSON.stringify({ method: body === undefined ? 'GET' : 'POST', path: route, data: body }) + '\n');
    return JSON.parse((await replies.next()).value);
  }
  function citizen(url, options = {}) {
    const target = new URL(url);
    return new Promise((resolve, reject) => {
      const data = options.body ? options.body.toString() : null;
      const headers = { Host: target.host, ...options.headers };
      if (data !== null) {
        headers['Content-Type'] = 'application/x-www-form-urlencoded';
        headers['Content-Length'] = Buffer.byteLength(data);
      }
      const call = httpRequest({ socketPath, method: options.method ?? 'GET', path: target.pathname + target.search, headers }, response => {
        const chunks = [];
        response.on('data', chunk => chunks.push(chunk));
        response.on('end', () => resolve(new Response(Buffer.concat(chunks), { status: response.statusCode, headers: response.headers })));
        response.on('error', reject);
      });
      call.on('error', reject);
      call.end(data ?? undefined);
    });
  }
  async function stop() {
    if (process.exitCode !== null) return;
    process.stdin.end();
    await once(process, 'exit');
  }
  return { ...startup, citizen, request, stop };
}

async function openForm(app, url) {
  const response = await app.citizen(url);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('referrer-policy'), 'strict-origin');
  const raw = await response.text();
  assert.doesNotMatch(raw, /Bitte nur Testdaten verwenden|Use test data only|Das ist ein Test/);
  const cookie = response.headers.get('set-cookie').split(';')[0];
  const claim = /name="claim" value="([A-Za-z0-9_-]+)"/.exec(raw)[1];
  const lang = /name="lang" value="([a-z]+)"/.exec(raw)[1];
  return { cookie, claim, lang, raw, url };
}

async function submit(app, form, action, headers = {}) {
  return app.citizen(form.url, {
    method: 'POST', redirect: 'manual',
    headers: { Cookie: form.cookie, ...headers },
    body: new URLSearchParams({ claim: form.claim, lang: form.lang, action }),
  });
}

test('Desktop and citizen flow survives a restart, preserves exact notices and revokes the recording gate', async t => {
  const directory = temporary(t);
  const key = randomBytes(32);
  let app = await launch(directory, key);
  t.after(async () => { await app.stop(); });
  assert.equal((await app.citizen(app.origin + '/cases')).status, 404);
  assert.equal((await app.citizen(app.origin + '/cases', { headers: { Host: 'attacker.test' } })).status, 421);
  assert.equal((await app.request('/cases', { ...fixture, count: 0 })).status, 400);
  const created = await app.request('/cases', fixture);
  assert.equal(created.status, 201);
  const base = '/cases/' + created.value.id;
  const first = (await app.request(base + '/invite?person=1')).value;
  const second = (await app.request(base + '/invite?person=2')).value;
  assert.notEqual(first.link, second.link);
  assert.equal(first.qr.length, 37);
  assert.ok(first.qr.every(row => /^[01]{37}$/.test(row)));
  const english = await openForm(app, first.link + '?lang=en');
  const simple = await openForm(app, second.link + '?lang=einfach');
  assert.match(english.raw, /I consent/);
  assert.match(english.raw, /Özer &lt;Test&gt;/);
  assert.match(simple.raw, /Ja, ich bin einverstanden/);
  assert.equal((await submit(app, english, 'accepted', { Origin: 'http://different.test' })).status, 403);
  assert.equal((await submit(app, english, 'accepted', { Origin: 'null' })).status, 403);
  assert.equal((await submit(app, { ...english, claim: 'a'.repeat(32) }, 'accepted')).status, 403);
  assert.equal((await submit(app, english, 'accepted', { Origin: app.origin })).status, 303);
  assert.equal((await submit(app, english, 'accepted')).status, 303);
  assert.equal((await submit(app, english, 'declined')).status, 409);
  assert.equal((await app.request(base + '/status')).value.ready, false);
  assert.equal((await app.citizen(first.link)).status, 404);
  const closedInvitation = await app.request(base + '/invite?person=1');
  assert.equal(closedInvitation.status, 409);
  assert.match(closedInvitation.value.error, /kein offener QR-Code/);
  assert.equal((await submit(app, simple, 'accepted')).status, 303);
  assert.equal((await app.request(base + '/status')).value.ready, true);
  const receipt = await (await app.citizen(english.url, { headers: { Cookie: english.cookie } })).text();
  const withdrawal = /href="([^"]+\/w\/[^"]+)"/.exec(receipt)[1];
  assert.notEqual(withdrawal, first.link);
  assert.equal((await app.request(base + '/renew', { person: 1 })).status, 409);

  const port = new URL(app.origin).port;
  await app.stop();
  app = await launch(directory, key, '127.0.0.1', port);
  assert.equal((await app.request(base + '/status')).value.ready, true);
  const withdraw = await openForm(app, withdrawal);
  assert.match(withdraw.raw, /Withdraw my consent/);
  assert.equal((await submit(app, withdraw, 'withdrawn')).status, 303);
  assert.equal((await submit(app, withdraw, 'withdrawn')).status, 303);
  const status = (await app.request(base + '/status')).value;
  assert.equal(status.ready, false);
  assert.deepEqual(status.invites.map(person => person.status), ['withdrawn', 'accepted']);
  const exported = (await app.request(base + '/export')).value;
  const { evidence } = exported.proof;
  assert.equal(evidence.reference, fixture.reference);
  assert.deepEqual(evidence.participants[0].events.map(event => event.kind), ['accepted', 'withdrawn']);
  assert.equal(evidence.participants[0].events[0].language, 'en');
  assert.equal(evidence.participants[1].events[0].language, 'einfach');
  assert.ok(evidence.notices.en.purpose);
  assert.ok(!JSON.stringify(exported.proof).includes(first.link.split('/').at(-1)));
  assert.ok(!JSON.stringify(exported.proof).includes(new URL(withdrawal).pathname.split('/').at(-1)));
  const pdf = Buffer.from(exported.pdf, 'base64');
  const pdfHash = createHash('sha256').update(pdf).digest('hex');
  assert.equal(pdf.subarray(0, 8).toString(), '%PDF-1.7');
  assert.equal((await app.request('/verify', exported.proof)).value.valid, true);
  assert.deepEqual((await app.request('/verify', { proof: exported.proof, pdfHash })).value, { valid: true, document: true });
  assert.deepEqual((await app.request('/verify', { proof: exported.proof, pdfHash: 'f'.repeat(64) })).value, { valid: true, document: false });
  const unbound = { ...exported.proof };
  delete unbound.document;
  assert.equal((await app.request('/verify', unbound)).value.valid, false);
  exported.proof.evidence.title = 'Veränderter Text';
  assert.equal((await app.request('/verify', exported.proof)).value.valid, false);
  assert.ok(!readFileSync(join(directory, 'store.dokju')).includes(Buffer.from(fixture.title)));
  await app.stop();
});

test('Mobile portal links use the configured HTTPS origin and secure cookies', async t => {
  const directory = temporary(t);
  const key = randomBytes(32);
  await assert.rejects(launch(directory, key, '192.168.0.20'), /bindet ausschließlich an 127\.0\.0\.1/);
  await assert.rejects(launch(directory, key, '127.0.0.1', '0', 'http://einwilligung.example'), /HTTPS-Adresse/);
  await assert.rejects(launch(directory, key, '127.0.0.1', '0', 'https://einwilligung.example/portal'), /ohne Pfad/);
  await assert.rejects(launch(directory, key, '127.0.0.1', '0', `https://${'a'.repeat(62)}.example`), /höchstens 71 Zeichen/);
  const app = await launch(directory, key, '127.0.0.1', '0', 'https://einwilligung.example/');
  t.after(() => app.stop());
  assert.equal(app.origin, 'https://einwilligung.example');
  assert.deepEqual((await app.request('/cases')).value, { origin: app.origin, cases: [] });
  const created = await app.request('/cases', { ...fixture, count: 1 });
  const base = '/cases/' + created.value.id;
  const { link } = (await app.request(base + '/invite?person=1')).value;
  assert.ok(link.startsWith(app.origin + '/i/'));
  assert.equal((await app.citizen(link, { headers: { Host: '127.0.0.1' } })).status, 421);
  assert.match((await app.citizen(link)).headers.get('set-cookie'), /; Secure\b/);
  const form = await openForm(app, link);
  assert.equal((await submit(app, form, 'accepted', { Origin: app.origin })).status, 303);
  const receipt = await (await app.citizen(form.url, { headers: { Cookie: form.cookie } })).text();
  assert.match(receipt, /href="https:\/\/einwilligung\.example\/w\//);
});

test('Parallel decisions only commit once and a refusal blocks recording', async t => {
  const app = await launch(temporary(t), randomBytes(32));
  t.after(() => app.stop());
  const created = await app.request('/cases', { ...fixture, count: 1 });
  const base = '/cases/' + created.value.id;
  const { link } = (await app.request(base + '/invite?person=1')).value;
  const a = await openForm(app, link);
  const b = await openForm(app, link);
  const results = await Promise.all([submit(app, a, 'declined'), submit(app, b, 'declined')]);
  assert.deepEqual(results.map(result => result.status).sort(), [303, 404]);
  const proof = (await app.request(base + '/export')).value.proof.evidence;
  assert.equal(proof.allAccepted, false);
  assert.equal(proof.participants[0].status, 'declined');
  assert.equal(proof.participants[0].events.length, 1);
  await app.stop();
});

test('Expired invitations can be replaced independently and old proofs are deleted on schedule', t => {
  const directory = temporary(t);
  const key = randomBytes(32);
  let time = Date.parse('2026-11-12T17:00:00Z');
  const store = new Store(directory, key, () => time);
  const item = store.create({ ...fixture, proofDays: 1 });
  const oldEvidence = { schema: 'dokju-evidence-v1' };
  assert.deepEqual(store.verify({ evidence: oldEvidence, seal: store.mac(JSON.stringify(oldEvidence)) }), { valid: true });
  const first = store.invites.get(item.invites[0].token);
  store.record(first, 'accepted', 'de', 'claim');
  time += 30 * 60_000;
  assert.equal(store.view(store.lookup(item.id)).invites[1].status, 'expired');
  const renewed = store.renew(item.id, 2);
  assert.equal(renewed.invites[0].status, 'accepted');
  assert.equal(renewed.invites[1].status, 'pending');
  assert.notEqual(renewed.invites[1].token, item.invites[1].token);
  assert.equal(store.invites.has(item.invites[1].token), false);
  time += 86_400_000;
  store.prune();
  assert.equal(store.data.cases.length, 0);
  assert.equal(store.invites.size, 0);
  assert.equal(new Store(directory, key).data.cases.length, 0);
  assert.throws(() => new Store(directory, randomBytes(32)));
  const file = readFileSync(join(directory, 'store.dokju'));
  file[file.length - 1] ^= 0xff;
  writeFileSync(join(directory, 'store.dokju'), file);
  assert.throws(() => new Store(directory, key));
});
