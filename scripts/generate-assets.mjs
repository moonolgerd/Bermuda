// Renders the SVG logo to all required PNG sizes for MSIX assets and web favicon.
import sharp from 'sharp';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dir = dirname(fileURLToPath(import.meta.url));
const root = join(__dir, '..');
const svgPath = join(root, 'src/Bermuda/Assets/logo.svg');
const svgBuf = readFileSync(svgPath);

const msixAssets = [
  // [filename, width, height]
  ['Square44x44Logo.png',   44,  44],
  ['Square150x150Logo.png', 150, 150],
  ['StoreLogo.png',         50,  50],
  ['Wide310x150Logo.png',   310, 150],
  ['SplashScreen.png',      620, 300],
];

for (const [name, w, h] of msixAssets) {
  const outPath = join(root, 'src/Bermuda/Assets', name);
  await sharp(svgBuf)
    .resize(w, h, { fit: 'contain', background: { r: 14, g: 165, b: 233, alpha: 1 } })
    .png()
    .toFile(outPath);
  console.log(`  ✔ ${name} (${w}×${h})`);
}

// Web favicon (32×32 ICO-compatible PNG placed in public/)
await sharp(svgBuf)
  .resize(32, 32)
  .png()
  .toFile(join(root, 'src/Bermuda.Web/public/favicon.png'));
console.log('  ✔ favicon.png (32×32)');

// App icon SVG copy for web assets
import { copyFileSync } from 'node:fs';
copyFileSync(svgPath, join(root, 'src/Bermuda.Web/src/assets/bermuda.svg'));
console.log('  ✔ bermuda.svg (web asset)');

console.log('\nAll assets generated.');
