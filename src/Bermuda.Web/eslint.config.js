import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import babelParser from '@babel/eslint-parser'

// NOTE: typescript-eslint does not yet support TypeScript 7's rewritten
// compiler internals (crashes on import - see @typescript-eslint/typescript-estree
// create-program/shared.js referencing removed `ts.Extension` APIs). Until it
// is updated, TS/TSX files are parsed with @babel/eslint-parser (syntax-only,
// no type-aware rules). Switch back to typescript-eslint once it publishes a
// release supporting TypeScript 7.
export default [
  { ignores: ['dist'] },
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
      parser: babelParser,
      parserOptions: {
        requireConfigFile: false,
        babelOptions: {
          presets: ['@babel/preset-typescript', '@babel/preset-react'],
        },
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...js.configs.recommended.rules,
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': [
        'warn',
        { allowConstantExport: true },
      ],
      'semi': ['error', 'never'],
      'no-unused-vars': 'off',
      'no-undef': 'off',
    },
  },
]
