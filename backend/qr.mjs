// QR Model 2, version 5-L. One Reed–Solomon block (108 data + 26 parity bytes).
// Byte mode handles the ASCII invitation URLs with a four-module quiet zone.
const size = 37;
const dataWords = 108;
const parityWords = 26;

function multiply(left, right) {
  let result = 0;
  for (let bit = 7; bit >= 0; bit--) {
    result = (result << 1) ^ ((result >> 7) * 0x11d);
    result ^= ((right >> bit) & 1) * left;
  }
  return result;
}

function parity(data) {
  const divisor = new Uint8Array(parityWords);
  divisor[parityWords - 1] = 1;
  let root = 1;
  for (let degree = 0; degree < parityWords; degree++) {
    for (let i = 0; i < parityWords; i++)
      divisor[i] = multiply(divisor[i], root) ^ (divisor[i + 1] ?? 0);
    root = multiply(root, 2);
  }
  const result = new Uint8Array(parityWords);
  for (const byte of data) {
    const factor = byte ^ result[0];
    result.copyWithin(0, 1);
    result[parityWords - 1] = 0;
    for (let i = 0; i < parityWords; i++) result[i] ^= multiply(divisor[i], factor);
  }
  return result;
}

export function qr(text) {
  const input = Buffer.from(text, 'utf8');
  if (input.length > 106) throw new Error('Der QR-Link ist zu lang.');
  const bits = [];
  const append = (value, length) => {
    for (let bit = length - 1; bit >= 0; bit--) bits.push((value >> bit) & 1);
  };
  append(4, 4);
  append(input.length, 8);
  for (const byte of input) append(byte, 8);
  append(0, 4);
  while (bits.length % 8) bits.push(0);
  const data = [];
  for (let index = 0; index < bits.length; index += 8)
    data.push(bits.slice(index, index + 8).reduce((value, bit) => value * 2 + bit, 0));
  for (let pad = 0; data.length < dataWords; pad++) data.push(pad % 2 ? 0x11 : 0xec);
  const words = [...data, ...parity(data)];
  const grid = Array.from({ length: size }, () => Array(size).fill(null));
  const set = (x, y, value) => {
    if (x >= 0 && x < size && y >= 0 && y < size) grid[y][x] = Number(value);
  };
  for (let i = 0; i < size; i++) {
    set(i, 6, i % 2 === 0);
    set(6, i, i % 2 === 0);
  }
  for (const [x, y] of [[3, 3], [size - 4, 3], [3, size - 4]]) {
    for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
      const distance = Math.max(Math.abs(dx), Math.abs(dy));
      set(x + dx, y + dy, distance !== 2 && distance !== 4);
    }
  }
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++)
    set(30 + dx, 30 + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);

  // The fixed format word encodes correction level L and mask 0.
  const format = bit => (0x77c4 >> bit) & 1;
  for (let i = 0; i < 6; i++) set(8, i, format(i));
  set(8, 7, format(6));
  set(8, 8, format(7));
  set(7, 8, format(8));
  for (let i = 9; i < 15; i++) set(14 - i, 8, format(i));
  for (let i = 0; i < 8; i++) set(size - 1 - i, 8, format(i));
  for (let i = 8; i < 15; i++) set(8, size - 15 + i, format(i));
  set(8, size - 8, 1);

  let index = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    const up = ((right + 1) & 2) === 0;
    for (let vertical = 0; vertical < size; vertical++) {
      const y = up ? size - 1 - vertical : vertical;
      for (let x = right; x >= right - 1; x--) {
        if (grid[y][x] !== null) continue;
        const bit = ((words[index >> 3] ?? 0) >> (7 - (index & 7))) & 1;
        set(x, y, bit ^ ((x + y) % 2 === 0 ? 1 : 0));
        index++;
      }
    }
  }
  if (index !== words.length * 8 + 7) throw new Error('Ungültige QR-Matrix.');
  return grid.map(row => row.join(''));
}
