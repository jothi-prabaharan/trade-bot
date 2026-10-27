// Renders the TradeBot icon set into public/ (run: npm run icons). The artwork below is the only source.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'public');
const GREEN = '#3fb950';
const BG = '#0d1117';

// Bot head with a rising chart for a face, on a 512 grid centred at (256, 248)
const art = (stroke = GREEN, face = '#161b22') => `
  <line x1="256" y1="168" x2="256" y2="112" stroke="${stroke}" stroke-width="24" stroke-linecap="round"/>
  <circle cx="256" cy="96" r="30" fill="${stroke}"/>
  <rect x="96" y="168" width="320" height="248" rx="64" fill="${face}" stroke="${stroke}" stroke-width="28"/>
  <polyline points="156,344 224,276 276,316 356,232" fill="none" stroke="${stroke}" stroke-width="34" stroke-linecap="round" stroke-linejoin="round"/>`;

const svg = (body) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">${body}</svg>`;
const scaled = (s, body) => `<g transform="translate(256 248) scale(${s}) translate(-256 -248)">${body}</g>`;

const rounded = svg(`<rect width="512" height="512" rx="112" fill="${BG}"/>${art()}`);
// Maskable: full bleed, artwork inside the central 80 % safe circle. iOS rounds the apple-touch icon itself.
const fullBleed = (s) => svg(`<rect width="512" height="512" fill="${BG}"/>${scaled(s, art())}`);
// Android status-bar badge: white silhouette on transparent
const badge = svg(scaled(1.15, art('#ffffff', 'none')));

const pngs = [
  ['icons/icon-192.png', rounded, 192],
  ['icons/icon-512.png', rounded, 512],
  ['icons/maskable-512.png', fullBleed(0.78), 512],
  ['icons/apple-touch-icon.png', fullBleed(0.86), 180],
  ['icons/badge-96.png', badge, 96],
];
const icoSizes = [16, 32, 48];

const browser = await chromium.launch();
const page = await browser.newPage();
async function render(source, size) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<style>*{margin:0}</style><img width="${size}" height="${size}" src="data:image/svg+xml;base64,${Buffer.from(source).toString('base64')}">`);
  return page.screenshot({ omitBackground: true });
}

mkdirSync(join(out, 'icons'), { recursive: true });
writeFileSync(join(out, 'favicon.svg'), rounded + '\n');
for (const [file, source, size] of pngs) writeFileSync(join(out, file), await render(source, size));
// One page, so render strictly one size at a time
const icoEntries = [];
for (const s of icoSizes) icoEntries.push([s, await render(rounded, s)]);
writeFileSync(join(out, 'favicon.ico'), ico(icoEntries));
await browser.close();
console.log(`Wrote favicon.svg, favicon.ico and ${pngs.length} PNGs to ${out}`);

/** ICO container with PNG entries (supported by every current browser and Windows). */
function ico(entries) {
  const header = Buffer.alloc(6 + 16 * entries.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(entries.length, 4);
  let offset = header.length;
  entries.forEach(([size, png], i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, e);
    header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(png.length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...entries.map(([, png]) => png)]);
}
