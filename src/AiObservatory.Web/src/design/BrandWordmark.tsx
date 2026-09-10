import { useId } from 'react'

// The FixPortal wordmark — "FixPortal" with the portal mark as the 'o'. The
// text is currentColor so it themes with the surrounding surface; the portal-o
// keeps its orange/blue gradients. Mirror of
// fixportal-assets/branding/wordmark/fixportal-wordmark.svg.
// Use at >= ~32px tall (the inner rings muddy below that); never the dark PNG.
export function BrandWordmark({ height = 40, className }: { height?: number; className?: string }) {
  const width = Math.round((height * 360) / 110)
  // SVG def ids are document-global and url(#id) resolves to the FIRST match, so
  // two wordmarks on one page (e.g. sidebar + footer) would share the first's
  // gradients/filter. Scope the ids per instance. useId() can contain ':'
  // (invalid in a CSS-style selector, though not in a funciri) — strip it so the
  // ids stay clean everywhere.
  const uid = useId().replace(/:/g, '')
  const orange = `${uid}-orange`
  const blue = `${uid}-blue`
  const blur = `${uid}-blur`
  return (
    <svg
      className={className}
      role="img"
      aria-label="FixPortal"
      width={width}
      height={height}
      viewBox="0 0 360 110"
    >
      <defs>
        <radialGradient id={orange} cx="50%" cy="50%" r="55%">
          <stop offset="0%" stopColor="#FFF4D6" />
          <stop offset="38%" stopColor="#FFB347" />
          <stop offset="78%" stopColor="#FF6B1A" />
          <stop offset="100%" stopColor="#A83400" />
        </radialGradient>
        <radialGradient id={blue} cx="50%" cy="50%" r="55%">
          <stop offset="0%" stopColor="#7CC3F0" />
          <stop offset="60%" stopColor="#1A8FE8" />
          <stop offset="100%" stopColor="#003D7A" />
        </radialGradient>
        <filter id={blur} x="-30%" y="-30%" width="160%" height="160%">
          <feGaussianBlur stdDeviation="3" />
        </filter>
      </defs>
      <text x="170" y="82" textAnchor="end" fontFamily="'Segoe UI','Helvetica Neue',Arial,sans-serif"
        fontSize="72" fontWeight="700" letterSpacing="-3" fill="currentColor">FixP</text>
      <ellipse cx="196" cy="58" rx="18" ry="29" fill={`url(#${blue})`} filter={`url(#${blur})`} opacity="0.95" />
      <ellipse cx="190" cy="56" rx="18" ry="29" fill={`url(#${orange})`} />
      <ellipse cx="190" cy="56" rx="11.5" ry="22" fill="none" stroke="#FFE8B8" strokeWidth="0.9" opacity="0.8" />
      <ellipse cx="190" cy="56" rx="6" ry="14" fill="none" stroke="#FFE8B8" strokeWidth="0.5" opacity="0.55" />
      <text x="212" y="82" textAnchor="start" fontFamily="'Segoe UI','Helvetica Neue',Arial,sans-serif"
        fontSize="72" fontWeight="700" letterSpacing="-3" fill="currentColor">rtal</text>
    </svg>
  )
}
