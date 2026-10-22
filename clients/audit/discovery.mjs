import { lstat, realpath, readdir, open } from 'node:fs/promises';
import { constants } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, relative, isAbsolute, join } from 'node:path';

export function withinRoot(root, path) {
  const child = relative(root, path);
  return child === '' || !child.startsWith('..' + (process.platform === 'win32' ? '\\' : '/')) && child !== '..' && !isAbsolute(child);
}

export async function safePath(root, path) {
  if (!withinRoot(root, path)) throw new Error('unsafe_source_path');
  const physical = await realpath(path);
  if (!withinRoot(root, physical) || resolve(path).toLowerCase() !== physical.toLowerCase() && process.platform === 'win32' || process.platform !== 'win32' && resolve(path) !== physical) throw new Error('unsafe_source_path');
  let current = resolve(path);
  while (withinRoot(root, current)) {
    if ((await lstat(current)).isSymbolicLink()) throw new Error('unsafe_source_path');
    if (current === root) break;
    current = resolve(current, '..');
  }
  return physical;
}

export async function discoverSources(sources, maxFiles = 10000) {
  const files = [], codes = [];
  let skippedFiles = 0;
  const seen = new Set();
  for (const source of sources) {
    for (const configuredRoot of source.roots) {
      const root = resolve(configuredRoot);
      try {
        await safePath(root, root);
        const directories = [{ path: root, depth: 0 }];
        while (directories.length) {
          const directory = directories.pop();
          await safePath(root, directory.path);
          const entries = await readdir(directory.path, { withFileTypes: true });
          entries.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
          for (const entry of entries) {
            const path = join(directory.path, entry.name);
            if (entry.isSymbolicLink()) { codes.push('unsafe_source_path'); skippedFiles++; continue; }
            if (entry.isDirectory()) {
              if (directory.depth >= 64) { codes.push('source_directory_limit'); skippedFiles++; }
              else directories.push({ path, depth: directory.depth + 1 });
            } else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
              const key = source.sourceId + '\0' + (process.platform === 'win32' ? path.toLowerCase() : path);
              if (seen.has(key)) continue;
              seen.add(key);
              if (files.length >= maxFiles) { codes.push('source_file_count_limit'); skippedFiles++; continue; }
              files.push({ path, root, sourceId: source.sourceId, tool: source.tool });
            }
          }
        }
      } catch (error) { codes.push(error.message === 'unsafe_source_path' ? error.message : 'source_discovery_failed'); skippedFiles++; }
    }
  }
  files.sort((a, b) => a.sourceId < b.sourceId ? -1 : a.sourceId > b.sourceId ? 1 : a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { files, codes, skippedFiles };
}

const fingerprint = value => [value.dev, value.ino, value.size, value.mtimeNs, value.ctimeNs].join(':');

// No records escape a changing attempt. Two retries follow the initial attempt.
export async function readStable(file, maxBytes, dependencies = {}) {
  for (let attempt = 0; attempt < 3; attempt++) {
    let handle;
    try {
      await safePath(file.root, file.path);
      handle = await open(file.path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
      const before = await handle.stat({ bigint: true });
      if (!before.isFile()) throw new Error('unsafe_source_path');
      if (before.size > BigInt(maxBytes)) throw new Error('source_file_limit');
      await safePath(file.root, file.path);
      let text, rawBytes;
      if (dependencies.readFile) { text = await dependencies.readFile(file.path); rawBytes = Buffer.from(text); }
      else {
        // The buffer is limited by the pre-read size plus one, even if the file grows.
        const buffer = Buffer.alloc(Number(before.size) + 1);
        let length = 0;
        while (length < buffer.length) {
          const { bytesRead } = await handle.read(buffer, length, buffer.length - length, null);
          if (!bytesRead) break;
          length += bytesRead;
        }
        // Fatal UTF-8 avoids silently replacing source bytes with plausible values.
        rawBytes = buffer.subarray(0, length);
        try { text = new TextDecoder('utf-8', { fatal: true }).decode(rawBytes); }
        catch { throw new Error('invalid_source_encoding'); }
      }
      if (typeof text !== 'string' || Buffer.byteLength(text) > maxBytes) throw new Error('source_file_limit');
      const after = await handle.stat({ bigint: true });
      await safePath(file.root, file.path);
      const named = await lstat(file.path, { bigint: true });
      if (fingerprint(before) === fingerprint(after) && fingerprint(after) === fingerprint(named)) return { text, byteLength: rawBytes.length, rawHash: createHash('sha256').update(rawBytes).digest('hex') };
    } catch (error) {
      const code = ['unsafe_source_path', 'source_file_limit', 'invalid_source_encoding'].includes(error.message) ? error.message : 'source_read_failed';
      return { code };
    } finally { await handle?.close(); }
  }
  return { code: 'source_changed' };
}
