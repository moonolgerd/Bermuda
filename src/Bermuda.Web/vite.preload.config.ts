import { defineConfig } from 'vite'

// Builds src/fdc3/preload.ts into the self-contained script the WPF host
// injects into every page (see src/fdc3/preload.ts). Output is committed, and
// CI checks it's up to date with the sources.
export default defineConfig({
  publicDir: false, // don't copy public/ (the probe app etc.) next to the script
  build: {
    lib: {
      entry: 'src/fdc3/preload.ts',
      formats: ['iife'],
      name: 'BermudaFdc3Preload',
      fileName: () => 'fdc3-preload.js',
    },
    outDir: '../Bermuda/Preload',
    emptyOutDir: false,
    minify: false,
    target: 'es2020',
  },
})
