import { createHash } from 'node:crypto'

/** Identity keys describe source contents, independently of checkout root and host separators. */
export function sourceBuildHash(
  root: string,
  files: { path: string; bytes: Uint8Array }[],
  lock: Uint8Array,
): string {
  const normalizedRoot = root.replaceAll('\\', '/').replace(/\/$/, '') + '/'
  const keyed = files
    .map((file) => {
      const path = file.path.replaceAll('\\', '/')
      if (!path.startsWith(normalizedRoot)) throw new Error('source_outside_root')
      return { key: path.slice(normalizedRoot.length), bytes: file.bytes }
    })
    .filter((file) => file.key.split('/')[0] !== 'test')
    .sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0))
  const hash = createHash('sha256')
  for (const file of keyed) hash.update(file.key).update(file.bytes)
  return hash.update(lock).digest('hex')
}
