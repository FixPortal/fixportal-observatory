import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import sonarjs from 'eslint-plugin-sonarjs'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
      sonarjs.configs.recommended,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    rules: {
      ...Object.fromEntries(
        Object.keys(sonarjs.configs.recommended.rules ?? {}).map(name => [name, 'warn']),
      ),
      'sonarjs/file-header': 'off',                  // wants a licence header on every file
      'sonarjs/arrow-function-convention': 'off',    // pure formatting (single-param parens)
      'sonarjs/declarations-in-global-scope': 'off', // misfires on ESM/.d.ts module declarations
      'sonarjs/cyclomatic-complexity': 'off',        // we gate on cognitive-complexity instead
      'sonarjs/no-reference-error': 'off',           // false-positives on type-only refs and DOM
                                                     // lib globals. tsc + typescript-eslint catch
                                                     // genuine reference errors; this rule cannot
                                                     // see types.
      'sonarjs/no-duplicate-string': 'off',           // local UI copy and test fixtures are clearer inline
      'sonarjs/shorthand-property-grouping': 'off',   // property ordering is formatting, not correctness
      'sonarjs/no-nested-conditional': 'off',         // concise JSX state rendering is idiomatic here
      'sonarjs/max-union-size': 'off',                // literal unions are the intended TypeScript model
      'sonarjs/elseif-without-else': 'off',           // guard-style keyboard handlers are already exhaustive
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
    },
  },
])
