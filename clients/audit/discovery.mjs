import { lstat, realpath, opendir, open } from 'node:fs/promises';
import { constants } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, relative, isAbsolute, join } from 'node:path';

export function withinRoot(root, path) {
  const child = relative(root, path);
  return child === '' || !child.startsWith('..' + (process.platform === 'win32' ? '\\' : '/')) && child !== '..' && !isAbsolute(child);
}

export async function safePath(root, path, budget) {
  budget?.check(true);
  if (!withinRoot(root, path)) throw new Error('unsafe_source_path');
  const physical = await realpath(path);
  if (!withinRoot(root, physical) || resolve(path).toLowerCase() !== physical.toLowerCase() && process.platform === 'win32' || process.platform !== 'win32' && resolve(path) !== physical) throw new Error('unsafe_source_path');
  let current = resolve(path);
  while (withinRoot(root, current)) {
    budget?.check(true);
    if ((await lstat(current)).isSymbolicLink()) throw new Error('unsafe_source_path');
    if (current === root) break;
    current = resolve(current, '..');
  }
  return physical;
}

export async function discoverSources(sources, maxFiles = 10000, dependencies = {}) {
  const budget = dependencies.budget;
  const files = [], codes = [];
  const diagnostic = code => { budget?.take('diagnostics', 1, true); codes.push(code); };
  let skippedFiles = 0;
  const seen = new Set();
  for (const source of sources) {
    for (const configuredRoot of source.roots) {
      const root = resolve(configuredRoot);
      try {
        await safePath(root, root, budget);
        budget?.take('queuedPaths', 1, true);
        const directories = [{ path: root, depth: 0 }];
        while (directories.length) {
          const directory = directories.pop();
          await safePath(root, directory.path, budget);
          const entries = await opendir(directory.path, { bufferSize: 1 });
          try { while (true) {
            budget?.take('directoryEntries', 1, true);
            const entry = await entries.read();
            if (!entry) break;
            if (budget && Buffer.byteLength(directory.path) + Buffer.byteLength(entry.name) + 1 > budget.limit('maxPathBytes')) {
              budget.stopNative(); return { files, codes: [...codes, 'capture_limit'], skippedFiles };
            }
            const path = join(directory.path, entry.name);
            if (entry.isSymbolicLink()) { diagnostic('unsafe_source_path'); skippedFiles++; continue; }
            if (entry.isDirectory()) {
              if (directory.depth >= (budget?.limit('maxDepth') ?? 64)) {
                if (budget) { budget.stopNative(); return { files, codes: [...codes, 'capture_limit'], skippedFiles }; }
                diagnostic('source_directory_limit'); skippedFiles++;
              } else { budget?.take('queuedPaths', 1, true); directories.push({ path, depth: directory.depth + 1 }); }
            } else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
              const key = source.sourceId + '\0' + (process.platform === 'win32' ? path.toLowerCase() : path);
              if (seen.has(key)) continue;
              try { budget?.take('files', 1, true); }
              catch (error) { skippedFiles++; throw error; }
              seen.add(key);
              if (files.length >= maxFiles) { diagnostic('source_file_count_limit'); skippedFiles++; continue; }
              files.push({ path, root, sourceId: source.sourceId, tool: source.tool });
            }
          } } finally { await entries.close(); }
        }
      } catch (error) {
        if (error.captureLimit) return { files, codes: [...codes, 'capture_limit'], skippedFiles };
        try { budget?.check(true); }
        catch (limit) { if (!limit.captureLimit) throw limit; return { files, codes: [...codes, 'capture_limit'], skippedFiles }; }
        try { diagnostic(error.message === 'unsafe_source_path' ? error.message : 'source_discovery_failed'); skippedFiles++; }
        catch (limit) { if (!limit.captureLimit) throw limit; return { files, codes: [...codes, 'capture_limit'], skippedFiles }; }
      }
    }
  }
  files.sort((a, b) => a.sourceId < b.sourceId ? -1 : a.sourceId > b.sourceId ? 1 : a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { files, codes, skippedFiles };
}

const fingerprint = value => [value.dev, value.ino, value.size, value.mtimeNs, value.ctimeNs].join(':');

// No records escape a changing attempt. Two retries follow the initial attempt.
export async function readStable(file, maxBytes, dependencies = {}) {
  const budget = dependencies.budget;
  for (let attempt = 0; attempt < 3; attempt++) {
    let handle;
    try {
      await safePath(file.root, file.path, budget);
      handle = await open(file.path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
      const before = await handle.stat({ bigint: true });
      if (!before.isFile()) throw new Error('unsafe_source_path');
      if (before.size > BigInt(Math.min(maxBytes, budget?.limit('maxFileBytes') ?? maxBytes))) {
        if (budget) throw Object.assign(new Error('capture_limit'), { captureLimit: true });
        throw new Error('source_file_limit');
      }
      await safePath(file.root, file.path, budget);
      let text, rawBytes;
      // Opaque whole-file helper is retained only for legacy unbudgeted tests.
      // Budgeted reads always use the counted handle loop below.
      if (dependencies.readFile && !budget) {
        text = await dependencies.readFile(file.path); rawBytes = Buffer.from(text);
        budget?.check(true);
      }
      else {
        // The buffer is limited by the pre-read size plus one, even if the file grows.
        budget?.check(true);
        // Admit the buffer before allocation; every actual request is separately debited.
        budget?.room('readBytes', Number(before.size) + 1, true);
        const buffer = Buffer.alloc(Number(before.size) + 1);
        let length = 0;
        while (length < buffer.length) {
          budget?.check(true);
          budget?.take('readBytes', buffer.length - length, true);
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
      await safePath(file.root, file.path, budget);
      const named = await lstat(file.path, { bigint: true });
      if (fingerprint(before) === fingerprint(after) && fingerprint(after) === fingerprint(named)) return { text, byteLength: rawBytes.length, rawHash: createHash('sha256').update(rawBytes).digest('hex') };
    } catch (error) {
      if (error.captureLimit) { budget?.stopNative(); return { code: 'capture_limit' }; }
      try { budget?.check(true); }
      catch (limit) { if (!limit.captureLimit) throw limit; budget?.stopNative(); return { code: 'capture_limit' }; }
      const code = ['unsafe_source_path', 'source_file_limit', 'invalid_source_encoding'].includes(error.message) ? error.message : 'source_read_failed';
      return { code };
    } finally { await handle?.close(); }
  }
  return { code: 'source_changed' };
}
