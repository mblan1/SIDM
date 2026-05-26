import { build, context } from 'esbuild';
import { copyFileSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const watch = process.argv.includes('--watch');

rmSync('dist', { recursive: true, force: true });
mkdirSync('dist', { recursive: true });

const config = {
  entryPoints: [
    'src/background.ts',
    'src/options.ts',
    'src/popup.ts',
    'src/picker.ts',
    'src/content.ts',
  ],
  bundle: true,
  outdir: 'dist',
  format: 'esm',
  target: 'firefox109',
  platform: 'browser',
  sourcemap: true,
  logLevel: 'info',
};

if (watch) {
  const ctx = await context(config);
  await ctx.watch();
  console.log('esbuild watching for changes...');
} else {
  await build(config);
}

// Static assets
copyFileSync('manifest.json', 'dist/manifest.json');
copyFileSync('src/options.html', 'dist/options.html');
copyFileSync('src/options.css', 'dist/options.css');
copyFileSync('src/popup.html', 'dist/popup.html');
copyFileSync('src/popup.css', 'dist/popup.css');
copyFileSync('src/picker.html', 'dist/picker.html');
copyFileSync('src/picker.css', 'dist/picker.css');

// Icons — the manifest references icons/icon-*.png paths, so the folder
// has to ship inside dist verbatim. AMO rejects uploads with missing
// icon files.
mkdirSync('dist/icons', { recursive: true });
for (const f of readdirSync('icons')) {
  copyFileSync(join('icons', f), join('dist/icons', f));
}

console.log('Build complete. Load the dist/ folder via about:debugging → "Load Temporary Add-on" → pick manifest.json.');
