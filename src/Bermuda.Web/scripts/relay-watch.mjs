// Polls src/ for .ts/.tsx changes and re-runs relay-compiler.
// Used in place of relay-compiler --watch when watchman is unavailable.
import { execSync } from 'node:child_process';
import { watch } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const srcDir = join(root, 'src');

let debounce = null;

function compile() {
  try {
    execSync('pnpm exec relay-compiler', { cwd: root, stdio: 'inherit' });
  } catch {
    // relay-compiler prints its own errors; keep watching
  }
}

// Initial compile
compile();

watch(srcDir, { recursive: true }, (_, filename) => {
  if (!filename?.match(/\.(ts|tsx)$/) || filename.includes('__generated__')) return;
  clearTimeout(debounce);
  debounce = setTimeout(compile, 300);
});

console.log(`[relay-watch] Watching ${srcDir} for changes...`);
