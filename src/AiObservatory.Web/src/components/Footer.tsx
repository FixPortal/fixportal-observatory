import { BrandWordmark } from '../design/BrandWordmark'

const PROJECT_URL = 'https://github.com/FixPortal/fixportal-observatory'

// Attribution is configuration, not code. A self-hoster's deployment should not carry this
// project's maintainer in its footer, so both halves are opt-in: set VITE_ATTRIBUTION_NAME and
// VITE_ATTRIBUTION_URL to credit whoever runs the instance, or leave them unset and the footer
// shows only the project link. The FixPortal deployment sets them.
const ATTRIBUTION_NAME = import.meta.env.VITE_ATTRIBUTION_NAME as string | undefined
const ATTRIBUTION_URL = import.meta.env.VITE_ATTRIBUTION_URL as string | undefined

export default function Footer() {
  const attributed = Boolean(ATTRIBUTION_NAME && ATTRIBUTION_URL)

  return (
    <footer className="site-footer">
      <div className="site-footer__band" aria-hidden="true">
        <BrandWordmark height={30} className="site-footer__wordmark" />
        <span className="site-footer__tagline">AI · USAGE · OBSERVATORY</span>
      </div>
      <div className="site-footer__attrib">
        {attributed && (
          <>
            Built by{' '}
            <a href={ATTRIBUTION_URL} target="_blank" rel="noopener noreferrer">{ATTRIBUTION_NAME}</a>
            {' · '}
          </>
        )}
        <a href={PROJECT_URL} target="_blank" rel="noopener noreferrer">Observatory on GitHub</a>
      </div>
    </footer>
  )
}
