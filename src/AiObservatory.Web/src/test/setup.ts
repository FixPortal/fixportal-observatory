// Extends Vitest's `expect` with the jest-dom matchers (toBeInTheDocument, etc.).
import '@testing-library/jest-dom/vitest'

// This project runs Vitest WITHOUT `globals: true` (a deliberate choice -- tests
// import { describe, it, expect } from 'vitest', and it keeps ArchUnitTS's root
// import from throwing; see architecture.archunit.ts). RTL's auto-cleanup needs
// globals, so register it explicitly here. A double-cleanup is a no-op, so tests
// with their own afterEach(cleanup) are unaffected.
import { afterEach } from 'vitest'
import { cleanup } from '@testing-library/react'
afterEach(() => { cleanup() })

// Add project-specific test-environment shims below. Common ones:
//   - vi.stubEnv('VITE_SOME_FLAG', 'false')  // pin dev-only flags off under test

// jsdom doesn't implement the <dialog> imperative API — showModal/close throw.
// Modal components (SubscriptionModal, SpendEntryModal) call showModal() from a
// ref callback, so every test that renders one needs this shim.
// ponytail: polyfilling the two missing built-in methods directly on the existing
// prototype is the standard shim pattern; sonarjs wants a whole subclass declaration,
// which is overkill for patching a browser API jsdom doesn't implement.
if (!HTMLDialogElement.prototype.showModal) {
  // eslint-disable-next-line sonarjs/class-prototype
  HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) { this.open = true }
}
if (!HTMLDialogElement.prototype.close) {
  // eslint-disable-next-line sonarjs/class-prototype
  HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
    this.open = false
    this.dispatchEvent(new Event('close'))
  }
}
