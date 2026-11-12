import { randomBytes } from 'node:crypto';
import { RuleError, equal } from './store.mjs';
import { languages, ui } from './text.mjs';

const css = `
:root{font:16px/1.6 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:#1a2739;background:#f3f5f8;color-scheme:light}
*{box-sizing:border-box}body{margin:0}main{max-width:680px;margin:auto;padding:26px 16px 44px}
header{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;margin:0 4px 20px}
.brand{font-size:27px;font-weight:750;letter-spacing:-.045em}.brand b{display:inline-grid;place-items:center;background:#bee9dc;color:#11574e;border-radius:9px;width:34px;height:34px;margin-right:9px;font-size:20px;vertical-align:2px}
nav{display:flex;gap:5px;flex-wrap:wrap}nav a{color:#31475d;border:1px solid #c6d2dd;text-decoration:none;border-radius:8px;padding:5px 9px;font-size:13px}
nav a[aria-current]{background:#def2ea;color:#07594d;font-weight:700;border-color:#80b7ad}article{background:white;border:1px solid #dbe2eb;border-radius:20px;padding:clamp(19px,5vw,38px);box-shadow:0 12px 36px #1523370c}
.demo{color:#6e4811;background:#fff5dc;border:1px solid #f3dfaf;border-radius:7px;padding:6px 10px;display:inline-block;font-size:13px;font-weight:650}
h1{margin:14px 0 10px;line-height:1.12;font-size:clamp(27px,7vw,39px);letter-spacing:-.035em}h2{font-size:18px;margin:23px 0 3px}p{margin:7px 0 13px}.context{color:#53667a;overflow-wrap:anywhere}
dl{margin:18px 0;display:grid;grid-template-columns:minmax(105px,1fr) 1.35fr;gap:6px 12px;background:#edf4f6;padding:14px;border-radius:10px;font-size:14px}
dt{font-weight:650;overflow-wrap:anywhere}dd{margin:0;overflow-wrap:anywhere}form{margin:20px 0 8px}.buttons{display:flex;flex-wrap:wrap;gap:10px}
@media(max-width:430px){dl{grid-template-columns:minmax(0,1fr);gap:2px}dt{color:#43596d;font-size:13px}dd{margin-bottom:8px}dd:last-child{margin-bottom:0}}
button,.button{font:inherit;font-weight:680;min-height:45px;border-radius:9px;padding:9px 14px;border:1px solid #08665d;background:#08665d;color:white;cursor:pointer;text-decoration:none;display:inline-block}
button.secondary{background:white;color:#23374c;border-color:#8597aa}button:hover,.button:hover{filter:brightness(.93)}
small{color:#53667a;display:block;margin-top:16px}a:focus-visible,button:focus-visible,input:focus-visible{outline:3px solid #a85c13;outline-offset:3px}
input[type=text]{width:100%;font:inherit;font-size:14px;padding:10px;border:1px solid #8597aa;border-radius:8px;margin:5px 0 14px}
.result{border-left:4px solid #08665d;padding-left:14px}
`;

export function escape(value) {
  return String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
}

function page(language, title, content, links = true) {
  const copy = ui[language];
  const navigation = languages.map(code =>
    `<a href="?lang=${code}" lang="${code === 'en' ? 'en' : 'de'}"${code === language ? ' aria-current="page"' : ''}>${{ de: 'Deutsch', einfach: 'Einfaches Deutsch', en: 'English' }[code]}</a>`
  ).join('');
  return `<!doctype html><html lang="${language === 'en' ? 'en' : 'de'}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)} | Dokju</title><style>${css}</style></head>
    <body><main><header><span class="brand"><b aria-hidden="true">✓</b>Dokju</span>${links ? `<nav aria-label="${language === 'en' ? 'Language' : 'Sprache'}">${navigation}</nav>` : ''}</header>
    <article><span class="demo">${copy.demo}</span>${content}</article></main></body></html>`;
}

function formatTime(value, language) {
  return new Intl.DateTimeFormat(language === 'en' ? 'en-GB' : 'de-DE', {
    dateStyle: 'medium', timeStyle: 'short', timeZone: 'Europe/Berlin',
  }).format(new Date(value)) + ' (Berlin)';
}

function choices(copy, language, claim, buttons) {
  return `<form method="post"><input type="hidden" name="lang" value="${language}"><input type="hidden" name="claim" value="${claim}"><div class="buttons">${buttons.map(([action, label, secondary]) =>
    `<button type="submit" name="action" value="${action}"${secondary ? ' class="secondary"' : ''}>${label}</button>`
  ).join('')}</div></form>`;
}

function textContent(item, invite, copy) {
  return `<p class="context">${escape(item.title)} · ${copy.participant} ${invite.number}</p>`;
}

export function sendHtml(response, code, html) {
  response.writeHead(code, {
    'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store',
    // Keep invite tokens out of Referer while preserving Origin on same-site HTML form posts.
    'Referrer-Policy': 'strict-origin', 'X-Content-Type-Options': 'nosniff', 'X-Frame-Options': 'DENY',
    'Content-Security-Policy': "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
  });
  response.end(html);
}

const tokenPattern = /^[A-Za-z0-9_-]{32}$/;

export async function portal(request, response, store, origin, readBody) {
  const url = new URL(request.url, origin);
  const language = languages.includes(url.searchParams.get('lang')) ? url.searchParams.get('lang') : 'de';
  const copy = ui[language];
  const invalid = () => sendHtml(response, 404, page(language, copy.invalid, `<h1>${copy.invalid}</h1>`));
  const route = /^\/(i|w)\/([A-Za-z0-9_-]{32})$/.exec(url.pathname);
  if (!route) return invalid();
  const [, kind, token] = route;
  const withdrawal = kind === 'w';
  const match = (withdrawal ? store.withdrawals : store.invites).get(token);
  if (!match) return invalid();
  const { item, invite } = match;
  const status = store.status(item, invite);
  let claim = /(?:^|;\s*)dokju_claim=([A-Za-z0-9_-]{32})(?:;|$)/.exec(request.headers.cookie ?? '')?.[1];
  if (request.method === 'POST') {
    if (request.headers.origin && request.headers.origin !== origin) throw new RuleError(copy.session, 403);
    if (request.headers['sec-fetch-site'] === 'cross-site') throw new RuleError(copy.session, 403);
    if (request.headers['content-type']?.split(';')[0] !== 'application/x-www-form-urlencoded')
      throw new RuleError(copy.session, 415);
    const form = new URLSearchParams(await readBody(request, 2048));
    const selected = form.get('lang');
    if (!tokenPattern.test(claim ?? '') || !equal(claim, form.get('claim')) || !languages.includes(selected))
      throw new RuleError(copy.session, 403);
    const action = form.get('action');
    if (!(withdrawal ? action === 'withdrawn' : ['accepted', 'declined'].includes(action))) return invalid();
    // Body I/O can yield while another browser answers. Re-read the live invitation.
    store.prune();
    const current = (withdrawal ? store.withdrawals : store.invites).get(token);
    if (!current) return invalid();
    const currentStatus = store.status(current.item, current.invite);
    if (withdrawal || currentStatus === 'pending') store.record(current, action, selected, withdrawal ? null : claim);
    else if (!store.isClaimant(current.invite, claim)) return invalid();
    else if (current.invite.events[0]?.kind !== action) {
      return sendHtml(response, 409, page(language, copy.conflict,
        `<h1>${copy.conflict}</h1><p><a class="button" href="?lang=${selected}">${copy.viewDecision}</a></p>`));
    }
    response.writeHead(303, { Location: `${url.pathname}?lang=${selected}`, 'Cache-Control': 'no-store', 'Referrer-Policy': 'strict-origin' });
    return response.end();
  }
  if (request.method !== 'GET') return invalid();
  if (status === 'expired' || (!withdrawal && status !== 'pending' && !store.isClaimant(invite, claim))) return invalid();
  if (!claim) {
    claim = randomBytes(24).toString('base64url');
    const secure = origin.startsWith('https:') ? '; Secure' : '';
    response.setHeader('Set-Cookie', `dokju_claim=${claim}; Path=${url.pathname}; SameSite=Strict; HttpOnly${secure}`);
  }
  const context = textContent(item, invite, copy);
  const last = invite.events.at(-1);
  const time = last ? `<small>${copy.time}: ${formatTime(last.at, language)}</small>` : '';
  if (status === 'withdrawn' || status === 'declined') {
    const explanation = status === 'declined' ? copy.declinedInfo : copy.withdrawnInfo;
    return sendHtml(response, 200, page(language, copy[status], `<h1>${copy[status]}</h1>${context}<p class="result">${explanation}</p>${time}`));
  }
  if (withdrawal) {
    return sendHtml(response, 200, page(language, copy.withdrawTitle,
      `<h1>${copy.withdrawTitle}</h1>${context}<p>${copy.withdrawText}</p>${choices(copy, language, claim, [['withdrawn', copy.withdraw]])}`));
  }
  if (status === 'accepted') {
    const link = `${origin}/w/${store.token(invite, 'withdraw')}?lang=${language}`;
    return sendHtml(response, 200, page(language, copy.accepted,
      `<h1>${copy.accepted}</h1>${context}<p class="result">${copy.save}</p><label><span>${copy.open}</span><input type="text" readonly value="${escape(link)}"></label><a class="button" href="${escape(link)}">${copy.open}</a>${time}`));
  }
  const notice = item.notices[language];
  const info = [[copy.controller, notice.controller], [copy.provider, notice.provider], [copy.contact, notice.contact], [copy.proofDate, formatTime(item.proofUntil, language)]];
  return sendHtml(response, 200, page(language, notice.heading,
    `<h1>${notice.heading}</h1>${context}<p>${notice.purpose}</p>
    <dl>${info.map(([label, value]) => `<dt>${label}</dt><dd>${escape(value)}</dd>`).join('')}</dl>
    <h2>${copy.storage}</h2><p>${notice.retention}</p><h2>${copy.rights}</h2><p>${notice.rights}</p>
    <h2>${copy.decision}</h2><p>${notice.choice}</p>
    ${choices(copy, language, claim, [['declined', copy.no, true], ['accepted', copy.yes]])}
    <small>${copy.until}: ${formatTime(invite.until ?? item.inviteUntil, language)}</small>`));
}
