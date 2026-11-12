import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { deflateSync } from 'node:zlib';

const statusText = { pending: 'Offen', accepted: 'Zugestimmt', declined: 'Abgelehnt', withdrawn: 'Widerrufen', expired: 'Abgelaufen' };

function readFont(file) {
  const font = readFileSync(file);
  const tables = {};
  for (let index = 0; index < font.readUInt16BE(4); index++) {
    const offset = 12 + index * 16;
    tables[font.toString('ascii', offset, offset + 4)] = font.readUInt32BE(offset + 8);
  }
  const units = font.readUInt16BE(tables.head + 18);
  const scale = value => Math.round(value * 1000 / units);
  const cmap = tables.cmap;
  const maps = [];
  for (let index = 0; index < font.readUInt16BE(cmap + 2); index++) {
    const offset = cmap + font.readUInt32BE(cmap + 8 + index * 8);
    const type = font.readUInt16BE(offset);
    if (type === 4 || type === 12) maps.push({ offset, type });
  }
  const map = maps.find(entry => entry.type === 12) ?? maps[0];
  if (!map) throw new Error('PDF-Schrift hat keine Unicode-Zeichentabelle.');
  const { offset } = map;
  function glyph(code) {
    if (map.type === 12) {
      let low = 0;
      let high = font.readUInt32BE(offset + 12) - 1;
      while (low <= high) {
        const index = Math.floor((low + high) / 2);
        const position = offset + 16 + index * 12;
        const start = font.readUInt32BE(position);
        if (code < start) high = index - 1;
        else if (code > font.readUInt32BE(position + 4)) low = index + 1;
        else return font.readUInt32BE(position + 8) + code - start;
      }
      return 0;
    }
    if (code > 0xffff) return 0;
    const count = font.readUInt16BE(offset + 6) / 2;
    const end = offset + 14;
    const start = end + 2 * count + 2;
    const delta = start + 2 * count;
    const range = delta + 2 * count;
    for (let index = 0; index < count; index++) {
      const step = 2 * index;
      if (code > font.readUInt16BE(end + step)) continue;
      const first = font.readUInt16BE(start + step);
      if (code < first) return 0;
      const shift = font.readInt16BE(delta + step);
      const size = font.readUInt16BE(range + step);
      if (!size) return (code + shift) & 0xffff;
      const id = font.readUInt16BE(range + step + size + 2 * (code - first));
      return id ? (id + shift) & 0xffff : 0;
    }
    return 0;
  }
  const metrics = font.readUInt16BE(tables.hhea + 34);
  const width = id => scale(font.readUInt16BE(tables.hmtx + Math.min(id, metrics - 1) * 4));
  const bbox = [36, 38, 40, 42].map(value => scale(font.readInt16BE(tables.head + value)));
  const ascent = scale(font.readInt16BE(tables.hhea + 4));
  const descent = scale(font.readInt16BE(tables.hhea + 6));
  return { font, glyph, width, bbox, ascent, descent };
}

const hex = id => id.toString(16).padStart(4, '0');

export function proofPdf({ evidence, seal }, fontFile = join(process.env.WINDIR ?? 'C:\\Windows', 'Fonts', 'segoeui.ttf')) {
  const { font, glyph, width, bbox, ascent, descent } = readFont(fontFile);
  const characters = new Map();
  const pages = [];
  let page = [];
  let y = 789;
  function character(value) {
    if (!characters.has(value)) {
      const id = glyph(value.codePointAt(0));
      characters.set(value, { cid: characters.size + 1, glyph: id, width: width(id) });
    }
    return characters.get(value);
  }
  function line(text, size, color = '0.12 0.22 0.19') {
    const codes = [...text].map(value => hex(character(value).cid)).join('');
    page.push(`BT /F0 ${size} Tf ${color} rg 49 ${y} Td <${codes}> Tj ET`);
    y -= size + 6;
  }
  function nextPage() {
    if (page.length) pages.push(page);
    page = [];
    y = 789;
    line('Dokju  /  Einwilligungsnachweis', 22, '0.07 0.35 0.29');
    line(`DEMO  ·  Seite ${pages.length + 1}`, 11);
    y -= 12;
  }
  function block(text, size = 11) {
    let current = '';
    let length = 0;
    const limit = 490_000 / size;
    const write = value => {
      if (y < 57) nextPage();
      line(value, size);
    };
    for (const word of String(text).split(/\s+/u)) {
      const wordWidth = [...word].reduce((sum, value) => sum + character(value).width, 0);
      const space = current ? character(' ').width : 0;
      if (current && length + space + wordWidth > limit) {
        write(current);
        current = '';
        length = 0;
      }
      if (current) {
        current += ' ';
        length += space;
      }
      for (const value of word) {
        const advance = character(value).width;
        if (length + advance > limit) {
          write(current);
          current = '';
          length = 0;
        }
        current += value;
        length += advance;
      }
    }
    if (current) write(current);
  }
  function section(title) {
    if (y < 113) nextPage();
    y -= 10;
    line(title, 14, '0.07 0.35 0.29');
  }
  const berlin = new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Europe/Berlin' });
  const date = value => `${berlin.format(new Date(value))} (Berlin)`;
  nextPage();
  block(evidence.title, 18);
  block(`Aktenzeichen: ${evidence.reference}`);
  block(`Vorgang: ${evidence.caseId}`);
  block(`Erstellt: ${date(evidence.created)} · Export: ${date(evidence.issuedAt)}`);
  block(`Verantwortliche Stelle: ${evidence.profile.controller}`);
  block(`KI-Anbieter: ${evidence.profile.provider}`);
  block(`Datenschutzkontakt: ${evidence.profile.contact}`);
  block(`Löschdatum in Dokju: ${date(evidence.proofUntil)}`);
  block(evidence.allAccepted ? 'Stand beim Export: Alle Teilnehmenden haben zugestimmt.' : 'Stand beim Export: Es liegt keine Zustimmung aller Teilnehmenden vor.');
  section('Entscheidungsverlauf');
  for (const person of evidence.participants) {
    block(`Person ${person.number} · ${statusText[person.status]} · Kennung ${person.id}`);
    for (const event of person.events)
      block(`    ${date(event.at)} · ${statusText[event.kind]} · Sprache ${event.language}`);
  }
  section('Einwilligungsinformationen');
  for (const [language, notice] of Object.entries(evidence.notices)) {
    if (y < 235) nextPage();
    block(`${language.toUpperCase()} · Version ${notice.version} · ${notice.heading}`, 12);
    block(notice.purpose);
    block(notice.retention);
    block(notice.rights);
    block(notice.choice);
    y -= 6;
  }
  section('Integrität');
  block('Der ZIP-Export verknüpft dieses PDF mit dem JSON-Nachweis. Beide Dateien können in derselben Dokju-Installation gemeinsam geprüft werden.');
  block(`Prüfwert: ${seal}`);
  pages.push(page);

  const objects = [null, '<< /Type /Catalog /Pages 2 0 R >>', null];
  const add = value => objects.push(value) - 1;
  const stream = (data, attributes = '') => {
    const compressed = deflateSync(data);
    return Buffer.concat([
      Buffer.from(`<< /Length ${compressed.length} /Filter /FlateDecode ${attributes} >>\nstream\n`),
      compressed, Buffer.from('\nendstream'),
    ]);
  };
  const fontData = add(stream(font, `/Length1 ${font.length}`));
  const descriptor = add(`<< /Type /FontDescriptor /FontName /SegoeUI /Flags 32 /FontBBox [${bbox.join(' ')}] /ItalicAngle 0 /Ascent ${ascent} /Descent ${descent} /CapHeight 700 /StemV 80 /FontFile2 ${fontData} 0 R >>`);
  const map = Buffer.alloc((characters.size + 1) * 2);
  const unicode = [];
  for (const [value, entry] of characters) {
    map.writeUInt16BE(entry.glyph, entry.cid * 2);
    const utf16 = Buffer.from(value, 'utf16le').swap16().toString('hex');
    unicode.push(`<${hex(entry.cid)}> <${utf16}>`);
  }
  const cmap = ['/CIDInit /ProcSet findresource begin', '12 dict begin', 'begincmap',
    '/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def',
    '/CMapName /DokjuUnicode def', '/CMapType 2 def', '1 begincodespacerange', '<0000> <ffff>', 'endcodespacerange'];
  for (let start = 0; start < unicode.length; start += 100) {
    const batch = unicode.slice(start, start + 100);
    cmap.push(`${batch.length} beginbfchar`, ...batch, 'endbfchar');
  }
  cmap.push('endcmap', 'CMapName currentdict /CMap defineresource pop', 'end', 'end');
  const unicodeId = add(stream(Buffer.from(cmap.join('\n'))));
  const mapId = add(stream(map));
  const widths = [...characters.values()].map(entry => entry.width);
  const descendant = add(`<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SegoeUI /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor ${descriptor} 0 R /CIDToGIDMap ${mapId} 0 R /W [1 [${widths.join(' ')}]] >>`);
  const fontId = add(`<< /Type /Font /Subtype /Type0 /BaseFont /SegoeUI /Encoding /Identity-H /DescendantFonts [${descendant} 0 R] /ToUnicode ${unicodeId} 0 R >>`);
  const kids = pages.map(commands => {
    const contents = add(stream(Buffer.from(commands.join('\n'))));
    return add(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F0 ${fontId} 0 R >> >> /Contents ${contents} 0 R >>`);
  });
  objects[2] = `<< /Type /Pages /Kids [${kids.map(id => `${id} 0 R`).join(' ')}] /Count ${kids.length} >>`;
  const chunks = [Buffer.from('%PDF-1.7\n%\xe2\xe3\xcf\xd3\n', 'latin1')];
  const offsets = [0];
  let position = chunks[0].length;
  for (let id = 1; id < objects.length; id++) {
    offsets.push(position);
    const chunk = Buffer.concat([Buffer.from(`${id} 0 obj\n`), Buffer.from(objects[id]), Buffer.from('\nendobj\n')]);
    chunks.push(chunk);
    position += chunk.length;
  }
  const entries = offsets.slice(1).map(value => `${String(value).padStart(10, '0')} 00000 n \n`);
  chunks.push(Buffer.from(`xref\n0 ${objects.length}\n0000000000 65535 f \n${entries.join('')}trailer\n<< /Size ${objects.length} /Root 1 0 R >>\nstartxref\n${position}\n%%EOF\n`));
  return Buffer.concat(chunks);
}
