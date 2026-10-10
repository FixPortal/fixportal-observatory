const PROJECT_URL = 'https://github.com/FixPortal/fixportal-observatory'

// Read inside the component, not at module scope, so a test (or a rebuild) can flip it.
export default function DemoBanner() {
  if (import.meta.env.VITE_DEMO_BANNER !== 'true') return null

  return (
    <div
      role="status"
      style={{ padding: '0.5rem 1rem', textAlign: 'center', background: '#1f2a44', color: '#f3f4f6', fontSize: '0.875rem' }}
    >
      Demo instance: every figure here is synthetic and the dashboard is read-only.{' '}
      <a href={PROJECT_URL} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit', textDecoration: 'underline' }}>
        Observatory on GitHub
      </a>
    </div>
  )
}
