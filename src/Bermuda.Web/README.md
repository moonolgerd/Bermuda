# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react/README.md) uses [Babel](https://babeljs.io/) for Fast Refresh
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react-swc) uses [SWC](https://swc.rs/) for Fast Refresh

## Linting

Linting is [Biome](https://biomejs.dev/) (`pnpm lint`, config in `biome.json`), not ESLint — Biome ships its own parser, so it isn't blocked by `typescript-eslint`'s lag behind new TypeScript releases. Only the linter is enabled; Biome's formatter and import-sorter are off (`"enabled": false` in `biome.json`) since this project has no separate formatting tool to replace and existing file formatting was left as-is when Biome was adopted.
