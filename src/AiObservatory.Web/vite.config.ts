import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { visualizer } from 'rollup-plugin-visualizer'
import { createHash } from 'node:crypto'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import { sourceBuildHash } from './tools/sourceIdentity'

// `ANALYZE=1 npm run build` emits dist/stats.html (treemap of the bundle).
const analyze = !!process.env['ANALYZE']
const sourceRoot = resolve(import.meta.dirname, 'src')
const sourceFiles = readdirSync(sourceRoot, { recursive: true, withFileTypes: true })
  .filter(
    (entry) =>
      entry.isFile() &&
      /\.(tsx?|css)$/.test(entry.name) &&
      !/\.(test|spec|archunit)\./.test(entry.name),
  )
  .map((entry) => resolve(entry.parentPath, entry.name))
  .sort()
const buildHash = sourceBuildHash(
  sourceRoot,
  sourceFiles.map((path) => ({ path, bytes: readFileSync(path) })),
  readFileSync(resolve(import.meta.dirname, 'package-lock.json')),
)
const auditIdentity = {
  __AUDIT_BUILD_ID__: JSON.stringify(`web-source-sha256:${buildHash}`),
  __AUDIT_SOURCE_ID__: JSON.stringify(
    `summarizeCosts-sha256:${createHash('sha256')
      .update(readFileSync(resolve(sourceRoot, 'lib/costSummary.ts')))
      .update(readFileSync(resolve(sourceRoot, 'lib/currency.ts')))
      .digest('hex')}`,
  ),
}

export default defineConfig({
  define: auditIdentity,
  plugins: [
    react(),
    ...(analyze
      ? [visualizer({ filename: 'dist/stats.html', gzipSize: true, brotliSize: true })]
      : []),
  ],
  server: {
    proxy: {
      '/api': 'http://localhost:5039',
    },
  },
  build: {
    rolldownOptions: {
      output: {
        // Split the react/query vendor out of the app chunk for cacheability.
        // recharts is lazily imported, so it lands in its own async chunk anyway.
        manualChunks(id: string) {
          if (/node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) return 'react-vendor'
          if (/node_modules[\\/]@tanstack[\\/]/.test(id)) return 'react-vendor'
          return undefined
        },
      },
    },
  },
})
