import { build, context } from 'esbuild';
import { copyFileSync, mkdirSync, rmSync } from 'node:fs';

const watch = process.argv.includes('--watch');

rmSync('dist', { recursive: true, force: true });
mkdirSync('dist', { recursive: true });

const config = {
  entryPoints: ['src/background.ts', 'src/options.ts'],
  bundle: true,
  outdir: 'dist',
  format: 'esm',
  target: 'es2022',
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

console.log('Build complete. Load the dist/ folder as an unpacked extension in Chrome.');
