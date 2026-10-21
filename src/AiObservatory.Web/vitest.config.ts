import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    // Bound concurrent jsdom/TypeScript initialization on high-core developer hosts.
    maxWorkers: 4,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    // The chart components import recharts through React.lazy, so whichever test first
    // mounts a non-empty chart pays Vite's full cold transform of that module graph inside
    // its own act() wait. Measured on a cleared node_modules/.vite: 14.1s and 6.3s on two
    // runs, against the unconfigured 5s default -- and a different test paid it each time,
    // so it presents as a random single-test flake rather than as a slow file. This is a
    // generous diagnostic ceiling, not a performance assertion: warm runs finish in ~28s
    // total, so nothing here is timing-sensitive once the cache exists.
    testTimeout: 30_000,
    // NOTE: `globals: true` is deliberately NOT set. Tests import { describe, it,
    // expect } from 'vitest' explicitly. This matters for ArchUnitTS -- see
    // architecture.archunit.ts for why the no-globals choice drives the wrapper.
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
      include: ['src/**/*.{ts,tsx}'],
      // Thresholds set to floor(full-source actual). Measured 2026-09-01.
      thresholds: {
        statements: 71,
        branches: 62,
        functions: 63,
        lines: 73,
      },
    },
  },
})
