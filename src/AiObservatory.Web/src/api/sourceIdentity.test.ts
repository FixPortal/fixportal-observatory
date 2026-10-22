import { expect, test } from 'vitest'
import { sourceBuildHash } from '../../tools/sourceIdentity'

const bytes = new Uint8Array([1, 2, 3])
test('build identity uses equivalent root-relative keys on Windows and POSIX', () => {
  const posix = sourceBuildHash('/one/src', [{ path: '/one/src/nested/a.ts', bytes }], bytes)
  expect(
    sourceBuildHash('D:\\other\\src', [{ path: 'D:\\other\\src\\nested\\a.ts', bytes }], bytes),
  ).toBe(posix)
  expect(sourceBuildHash('/two/src', [{ path: '/two/src/nested/a.ts', bytes }], bytes)).toBe(posix)
})
test('only the exact first test directory is excluded and ordering is deterministic', () => {
  const base = [{ path: '/root/src/main.ts', bytes }]
  const hash = sourceBuildHash('/root/src', base, bytes)
  expect(
    sourceBuildHash('/root/src', [...base, { path: '/root/src/test/setup.ts', bytes }], bytes),
  ).toBe(hash)
  expect(
    sourceBuildHash('/root/src', [...base, { path: '/root/src/testable/code.ts', bytes }], bytes),
  ).not.toBe(hash)
  const extended = [...base, { path: '/root/src/nested/test/code.ts', bytes }]
  expect(sourceBuildHash('/root/src', extended, bytes)).not.toBe(hash)
  expect(sourceBuildHash('/root/src', extended.toReversed(), bytes)).toBe(
    sourceBuildHash('/root/src', extended, bytes),
  )
})
