import { createCipheriv, createDecipheriv, createHash, createHmac, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { notices } from './text.mjs';

export const states = ['pending', 'accepted', 'declined', 'withdrawn', 'expired'];
export const demo = {
  controller: 'Jugend- und Sozialamt Musterstadt',
  provider: 'SpeechMind (Demo, ohne Verbindung)',
  contact: 'datenschutz@example.invalid',
  audioDays: 2,
  proofDays: 90,
};

export function equal(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string') return false;
  const left = Buffer.from(a);
  const right = Buffer.from(b);
  return left.length === right.length && timingSafeEqual(left, right);
}

export class RuleError extends Error {
  constructor(message, status = 400) {
    super(message);
    this.status = status;
  }
}

function label(value, max, name) {
  if (typeof value !== 'string' || !value.trim() || [...value.trim()].length > max || /[\x00-\x1f\x7f]/.test(value))
    throw new RuleError(`${name} ist erforderlich (maximal ${max} Zeichen, eine Zeile).`);
  return value.trim();
}

function days(value, max, name) {
  if (!Number.isInteger(value) || value < 1 || value > max)
    throw new RuleError(`${name} muss zwischen 1 und ${max} Tagen liegen.`);
  return value;
}

const day = 86_400_000;
const auditVersion = 'dokju-evidence-v1';
const documentVersion = 'dokju-evidence-v2';
const marker = Buffer.from('DOKJU01');
const invitationPeriod = 30 * 60_000;

function canonical(value) {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (value && typeof value === 'object')
    return '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}';
  return JSON.stringify(value);
}

export class Store {
  constructor(directory, key, clock = () => Date.now()) {
    this.key = key;
    this.clock = clock;
    mkdirSync(directory, { recursive: true });
    this.file = join(directory, 'store.dokju');
    this.data = existsSync(this.file) ? this.load() : { version: 1, cases: [] };
    this.index();
  }

  mac(value) {
    return createHmac('sha256', this.key).update(value).digest('base64url');
  }

  token(invite, type) {
    const version = type === 'invite' ? (invite.generation ?? 0) : 0;
    return this.mac(`capability:${type}:${invite.id}:${version}`).slice(0, 32);
  }

  load() {
    const file = readFileSync(this.file);
    if (file.length < 35 || !file.subarray(0, 7).equals(marker)) throw new Error('Ungültiger Datenspeicher.');
    const decipher = createDecipheriv('aes-256-gcm', this.key, file.subarray(7, 19));
    decipher.setAAD(marker);
    decipher.setAuthTag(file.subarray(19, 35));
    const data = JSON.parse(Buffer.concat([decipher.update(file.subarray(35)), decipher.final()]));
    if (data.version !== 1 || !Array.isArray(data.cases)) throw new Error('Unbekanntes Speicherformat.');
    return data;
  }

  commit(change) {
    const next = structuredClone(this.data);
    const result = change(next);
    const nonce = randomBytes(12);
    const cipher = createCipheriv('aes-256-gcm', this.key, nonce);
    cipher.setAAD(marker);
    const encrypted = Buffer.concat([cipher.update(JSON.stringify(next), 'utf8'), cipher.final()]);
    const temp = `${this.file}.${randomUUID()}.tmp`;
    try {
      const fd = openSync(temp, 'wx', 0o600);
      try {
        writeFileSync(fd, Buffer.concat([marker, nonce, cipher.getAuthTag(), encrypted]));
        fsyncSync(fd);
      } finally {
        closeSync(fd);
      }
      renameSync(temp, this.file);
    } finally {
      rmSync(temp, { force: true });
    }
    this.data = next;
    this.index();
    return result;
  }

  index() {
    this.invites = new Map();
    this.withdrawals = new Map();
    for (const item of this.data.cases) for (const invite of item.invites) {
      this.invites.set(this.token(invite, 'invite'), { item, invite });
      if (invite.events.some(event => event.kind === 'accepted'))
        this.withdrawals.set(this.token(invite, 'withdraw'), { item, invite });
    }
  }

  create(values) {
    const now = this.clock();
    const info = {
      controller: label(values.controller, 100, 'Verantwortliche Stelle'),
      provider: label(values.provider, 100, 'KI-Anbieter'),
      contact: label(values.contact, 110, 'Datenschutzkontakt'),
      audioDays: days(values.audioDays, 365, 'Aufbewahrung der Aufnahme'),
      proofDays: days(values.proofDays, 3650, 'Aufbewahrung des Nachweises'),
    };
    const count = values.count;
    if (!Number.isInteger(count) || count < 1 || count > 12)
      throw new RuleError('Bitte 1 bis 12 Teilnehmende angeben.');
    const item = {
      id: randomUUID(),
      title: label(values.title, 90, 'Gesprächsbezeichnung'),
      reference: label(values.reference, 64, 'Aktenzeichen'),
      created: new Date(now).toISOString(),
      inviteUntil: new Date(now + invitationPeriod).toISOString(),
      proofUntil: new Date(now + info.proofDays * day).toISOString(),
      info,
      notices: notices(info),
      invites: Array.from({ length: count }, (_, index) => ({ id: randomUUID(), number: index + 1, events: [] })),
    };
    this.commit(next => next.cases.push(item));
    return this.view(item);
  }

  status(item, invite, now = this.clock()) {
    return invite.events.at(-1)?.kind ?? (now >= Date.parse(invite.until ?? item.inviteUntil) ? 'expired' : 'pending');
  }

  view(item) {
    const now = this.clock();
    const invites = item.invites.map(invite => {
      const status = this.status(item, invite, now);
      return { id: invite.id, number: invite.number, status, changed: invite.events.at(-1)?.at ?? null,
        until: invite.until ?? item.inviteUntil, token: status === 'pending' ? this.token(invite, 'invite') : null };
    });
    return {
      id: item.id, title: item.title, reference: item.reference, created: item.created,
      inviteUntil: item.inviteUntil, proofUntil: item.proofUntil, info: item.info, invites,
      ready: invites.every(invite => invite.status === 'accepted'),
    };
  }

  prune() {
    if (this.data.cases.some(item => this.clock() >= Date.parse(item.proofUntil)))
      this.commit(next => { next.cases = next.cases.filter(item => this.clock() < Date.parse(item.proofUntil)); });
  }

  lookup(id) {
    const item = this.data.cases.find(entry => entry.id === id);
    if (!item) throw new RuleError('Vorgang nicht gefunden.', 404);
    return item;
  }

  renew(id, person) {
    const item = this.lookup(id);
    const invite = item.invites.find(entry => entry.number === person);
    if (!invite || !['pending', 'expired'].includes(this.status(item, invite)))
      throw new RuleError('Für diese Person liegt bereits eine Entscheidung vor.', 409);
    const now = this.clock();
    const expiry = Math.min(now + invitationPeriod, Date.parse(item.proofUntil));
    this.commit(next => {
      const target = next.cases.find(entry => entry.id === id).invites.find(entry => entry.number === person);
      target.generation = (target.generation ?? 0) + 1;
      target.until = new Date(expiry).toISOString();
    });
    return this.view(this.lookup(id));
  }

  record(match, kind, language, claim) {
    const { item, invite } = match;
    if (kind === 'withdrawn' && this.status(item, invite) === 'withdrawn') return;
    const expected = kind === 'withdrawn' ? 'accepted' : 'pending';
    if (this.status(item, invite) !== expected) throw new RuleError('Diese Aktion ist nicht mehr verfügbar.', 409);
    const event = {
      kind, at: new Date(this.clock()).toISOString(), language,
      previous: invite.events.at(-1)?.seal ?? null,
    };
    event.seal = this.mac(JSON.stringify([auditVersion, item.id, item.reference, item.notices, invite.id, event]));
    this.commit(next => {
      const target = next.cases.find(entry => entry.id === item.id).invites.find(entry => entry.id === invite.id);
      target.events.push(event);
      if (claim) target.claim = this.mac(`browser:${claim}`);
    });
  }

  isClaimant(invite, claim) {
    return !!claim && equal(invite.claim, this.mac(`browser:${claim}`));
  }

  evidence(id) {
    const item = this.lookup(id);
    const now = this.clock();
    const participants = item.invites.map(invite => ({
      id: invite.id, number: invite.number, status: this.status(item, invite, now),
      inviteUntil: invite.until ?? item.inviteUntil, renewals: invite.generation ?? 0, events: invite.events,
    }));
    const evidence = {
      schema: documentVersion, issuedAt: new Date(now).toISOString(),
      caseId: item.id, reference: item.reference, title: item.title,
      created: item.created, inviteUntil: item.inviteUntil, proofUntil: item.proofUntil,
      profile: item.info, notices: item.notices, allAccepted: participants.every(person => person.status === 'accepted'),
      participants,
    };
    return { evidence, seal: this.mac(canonical(evidence)) };
  }

  attachDocument(proof, pdf) {
    const sha256 = createHash('sha256').update(pdf).digest('hex');
    return { ...proof, document: { sha256, seal: this.mac(`pdf:${proof.seal}:${sha256}`) } };
  }

  verify(proof, pdfHash) {
    const schema = proof?.evidence?.schema;
    if (![auditVersion, documentVersion].includes(schema) ||
      !equal(proof.seal, this.mac(canonical(proof.evidence)))) return { valid: false };
    if (schema === auditVersion) return { valid: true };
    const document = proof.document;
    if (!/^[0-9a-f]{64}$/.test(document?.sha256 ?? '') ||
      !equal(document.seal, this.mac(`pdf:${proof.seal}:${document.sha256}`))) return { valid: false };
    return pdfHash == null ? { valid: true } : { valid: true, document: equal(document.sha256, pdfHash) };
  }
}
