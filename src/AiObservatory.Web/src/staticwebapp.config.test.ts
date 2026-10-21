import { describe, expect, it } from 'vitest'
import raw from '../public/staticwebapp.config.json?raw'

interface StaticWebAppConfig {
  navigationFallback?: { rewrite?: string; exclude?: string[] }
}

// The Azure Static Web App has no server of ours to fall back to index.html, so without this
// file a reload or shared link to /docs/<chapter> answers with Azure's 404 page.
describe('staticwebapp.config.json', () => {
  const config = JSON.parse(raw) as StaticWebAppConfig

  it('serves the app shell for client-side routes such as /docs/api', () => {
    expect(config.navigationFallback?.rewrite).toBe('/index.html')
  })

  it('leaves real assets alone so a missing file is still a 404, not the app shell', () => {
    const exclude = config.navigationFallback?.exclude ?? []
    expect(exclude).toContain('/assets/*')
    expect(exclude.some((pattern) => pattern.includes('js'))).toBe(true)
  })
})
