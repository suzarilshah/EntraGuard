// Run from the repository root: node scripts/render-hackathon-architecture.cjs
// Requires the existing src/portal dependencies (sharp). No network access needed.
const fs = require('node:fs/promises');
const path = require('node:path');
const { createRequire } = require('node:module');

const root = path.resolve(__dirname, '..');
const portalRequire = createRequire(path.join(root, 'src/portal/package.json'));
const sharp = portalRequire('sharp');
const directory = path.join(root, 'docs/diagrams');

async function main() {
  let svg = await fs.readFile(path.join(directory, 'hackathon-architecture.source.svg'), 'utf8');
  const references = [...new Set([...svg.matchAll(/href="(azure-icons\/[\w-]+\.svg)"/g)].map(m => m[1]))];
  for (const reference of references) {
    const original = await fs.readFile(path.join(directory, reference));
    const data = `data:image/svg+xml;base64,${original.toString('base64')}`;
    svg = svg.replaceAll(`href="${reference}"`, `href="${data}"`);
  }
  if (/href="(?!data:)/.test(svg)) throw new Error('Unresolved external image reference');
  // A standalone SVG: original icon bytes embedded without changing paths/colors.
  await fs.writeFile(path.join(directory, 'hackathon-architecture.svg'), svg);
  for (const [suffix, density] of [['', 72], ['-4k', 144]]) {
    const filename = `hackathon-architecture${suffix}.png`;
    const result = await sharp(Buffer.from(svg), { density }).png().toFile(path.join(directory, filename));
    console.log(`${filename}: ${result.width} × ${result.height}`);
  }
  console.log(`Embedded ${references.length} original Microsoft SVG assets; no external image dependencies.`);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
