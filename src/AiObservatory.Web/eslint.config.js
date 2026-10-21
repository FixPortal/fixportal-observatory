import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import sonarjs from 'eslint-plugin-sonarjs'
import { defineConfig, globalIgnores } from 'eslint/config'
// Projects that consume a shared design-system package also spread its rules:
//   import { configs as designConfigs } from '@<org>/design/eslint'
//   ...designConfigs(),

// House bans, shared with scaffold-typescript. Each carries its WHY; a silenced rule
// with no rationale rots.
const noEnum = {
  selector: 'TSEnumDeclaration',
  message: 'No enums (erasable syntax only). Use a const object plus a union type.',
}
const noDefaultExport = {
  selector: 'ExportDefaultDeclaration',
  message: 'Named exports only: default exports rename silently and defeat find-references.',
}
const noBareNewDate = {
  selector: "NewExpression[callee.name='Date'][arguments.length=0]",
  message: 'Do not read the clock directly. Take a Clock (src/**/clock.ts) as a parameter.',
}

export default defineConfig([
  globalIgnores(['dist', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
      // Full SonarJS recommended set (cognitive complexity + bug/code-smell rules).
      // Severities are downgraded to 'warn' below -- advisory, never build-breaking.
      sonarjs.configs.recommended,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
      // Type-aware linting. Every .ts/.tsx matched here must belong to a tsconfig (the
      // template's tsconfig.app/node/test.json cover src and the two config files); a
      // file in no project fails with "was not found by the project service". Add new
      // top-level folders (scripts/, e2e/) to a tsconfig include.
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      // Every SonarJS rule runs as a warning, so findings surface in the editor
      // and CI logs without failing `eslint .`. cognitive-complexity keeps its
      // default threshold of 15.
      // The rules map must exist: if a plugin release drops it, fail loudly at config load, not skip the downgrade.
      ...Object.fromEntries(
        Object.keys(sonarjs.configs.recommended.rules).map((name) => [name, 'warn']),
      ),
      // Silence SonarJS rules that are stylistic policy or false-positive noise,
      // not code-quality signals -- they otherwise bury the findings that matter.
      'sonarjs/file-header': 'off', // wants a licence header on every file
      'sonarjs/arrow-function-convention': 'off', // pure formatting; Prettier owns it
      'sonarjs/declarations-in-global-scope': 'off', // misfires on ESM/.d.ts module declarations
      'sonarjs/cyclomatic-complexity': 'off', // gate on cognitive-complexity instead
      // False-positives on type-only refs and DOM lib globals; tsc + typescript-eslint
      // catch genuine reference errors (this rule cannot see types).
      'sonarjs/no-reference-error': 'off',
      'sonarjs/no-duplicate-string': 'off', // local UI copy and test fixtures are clearer inline
      'sonarjs/shorthand-property-grouping': 'off', // property ordering is formatting, not correctness
      'sonarjs/no-nested-conditional': 'off', // concise JSX state rendering is idiomatic here
      'sonarjs/max-union-size': 'off', // literal unions are the intended TypeScript model
      'sonarjs/elseif-without-else': 'off', // guard-style keyboard handlers are already exhaustive
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      // Numbers stringify safely in a template literal; the rule exists to catch objects, null
      // and undefined, which stay banned.
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
      // noPropertyAccessFromIndexSignature requires bracket access on index-signature records,
      // which the default dot-notation rule would then undo.
      '@typescript-eslint/dot-notation': ['error', { allowIndexSignaturePropertyAccess: true }],
      // Not in strictTypeChecked; a missing union case must fail the gate, not slip past
      // at runtime. Type-aware, so it lives ONLY in this typed block: a block without
      // projectService crashes with "rule requires type information".
      '@typescript-eslint/switch-exhaustiveness-check': 'error',
      'no-restricted-properties': [
        'error',
        {
          object: 'Date',
          property: 'now',
          message: 'Do not read the clock directly. Take a Clock (src/**/clock.ts) as a parameter.',
        },
      ],
      'no-restricted-syntax': ['error', noEnum, noDefaultExport, noBareNewDate],
    },
  },
  {
    // The single boundary file that may call a bare new Date(). The template ships no
    // clock file: create src/<area>/clock.ts when the first time-dependent code lands.
    // The boundary clock file reads the system clock with new Date(); Date.now() stays
    // banned even there (no-restricted-properties is not overridden), matching scaffold-typescript.
    files: ['src/**/clock.{ts,tsx}'],
    rules: { 'no-restricted-syntax': ['error', noEnum, noDefaultExport] },
  },
  {
    // Build/codegen scripts run under Node (ESM), not the browser. Give them Node
    // globals so `process` resolves, and relax the variable-name rule that flags
    // the conventional ESM `__filename`/`__dirname` shims.
    files: ['scripts/**/*.ts'],
    languageOptions: { globals: globals.node },
    rules: { 'sonarjs/variable-name': 'off' },
  },
  {
    // Test files have different conventions from app code. Repeated literals are
    // readable fixtures; passing `undefined` exercises missing-prop paths;
    // `import * as X` is idiomatic for vi.spyOn module mocks; render-helper
    // components trip name rules that only make sense for shipped code; hardcoded
    // IPs are test data.
    files: ['**/*.{test,spec}.{ts,tsx}'],
    rules: {
      // A sleep followed by an assert encodes a timing assumption a contended runner
      // will not honour. Use vi.useFakeTimers() or await a real completion signal.
      // This catches bare setTimeout(...) only; window./globalThis.setTimeout is review-only.
      'no-restricted-globals': [
        'error',
        {
          name: 'setTimeout',
          message: 'Use vi.useFakeTimers() or await a real completion signal.',
        },
      ],
      // Test code imports devDependencies (vitest, testing-library) by design.
      'sonarjs/no-duplicate-string': 'off',
      'sonarjs/no-undefined-assignment': 'off',
      'sonarjs/no-wildcard-import': 'off',
      'sonarjs/function-name': 'off',
      // No-op stubs (vi.fn(() => {}), empty handlers) are the normal way to satisfy a callback in a test.
      '@typescript-eslint/no-empty-function': 'off',
      // await act(async () => { ... }) is the Testing Library idiom for flushing pending promises;
      // the callback needs no await of its own.
      '@typescript-eslint/require-await': 'off',
      // A null behind a ! in a test throws at the dereference and fails the test, which is the
      // intended outcome; asserting first only restates it.
      '@typescript-eslint/no-non-null-assertion': 'off',
      // Tests delete keys from fixture objects to build invalid inputs.
      '@typescript-eslint/no-dynamic-delete': 'off',
      // Mock implementations and handler stubs in tests return promises where a void return is
      // declared (vi.fn typed as returning void); there is no production caller to mis-handle them.
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: false }],
      // Fires on expect(vi.fn-backed method) and expect(obj.method).toHaveBeenCalled(), which
      // never invoke the method unbound.
      '@typescript-eslint/unbound-method': 'off',
      // Vitest types expect.any(), expect.objectContaining() and mock.calls[n] as any, and untyped
      // vi.fn() mocks return any, so the no-unsafe-* family fires on idiomatic assertions.
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-unsafe-member-access': 'off',
      '@typescript-eslint/no-unsafe-return': 'off',
      '@typescript-eslint/no-unsafe-call': 'off',
      '@typescript-eslint/no-unsafe-argument': 'off',
      'sonarjs/no-hardcoded-ip': 'off',
      'sonarjs/no-implicit-dependencies': 'off',
    },
  },
  {
    // Only the default-export ban is relaxed here: tool config files (vite.config.ts,
    // vitest.config.ts, vite.<x>.config.ts) must default-export. The enum and bare
    // new Date() bans still apply. Declared last so it wins over the blocks above.
    files: ['**/*.config.ts'],
    rules: { 'no-restricted-syntax': ['error', noEnum, noBareNewDate] },
  },
  // Add narrowly-scoped project overrides below (e.g. generated code: max-lines
  // off; demo/mock generators: pseudo-random off). Keep each block commented with
  // WHY it is off -- a silenced rule with no rationale rots into cargo-cult config.
])
