import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { visualizer } from 'rollup-plugin-visualizer'

// `ANALYZE=1 npm run build` emits dist/stats.html (treemap of the bundle).
const analyze = !!process.env.ANALYZE

export default defineConfig({
  plugins: [
    react(),
    ...(analyze ? [visualizer({ filename: 'dist/stats.html', gzipSize: true, brotliSize: true })] : []),
  ],
  server: {
    proxy: {
      '/api': 'http://localhost:5039'
    }
  },
  build: {
    rollupOptions: {
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
