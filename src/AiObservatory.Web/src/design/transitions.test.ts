import css from '../index.css?raw'
import { describe, expect, it } from 'vitest'

describe('control transitions', () => {
  it('names the properties that controls animate', () => {
    expect(css).not.toMatch(/transition:\s*all\b/)
  })
})
