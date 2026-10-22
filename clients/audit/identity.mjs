import { createHmac, randomBytes, randomUUID } from 'node:crypto';
import { mkdir, lstat, readFile, writeFile, chmod } from 'node:fs/promises';
import { join } from 'node:path';
import { userInfo } from 'node:os';
import { execFileSync } from 'node:child_process';

export async function loadIdentity(directory) {
  try {
    await mkdir(directory, { recursive: true, mode: 0o700 });
    if ((await lstat(directory)).isSymbolicLink()) throw new Error('invalid_identity');
    if (process.platform === 'win32') {
      // Reset also removes pre-existing explicit grants, which /inheritance:r alone leaves behind.
      execFileSync('icacls.exe', [directory, '/reset'], { stdio: 'pipe', windowsHide: true });
      execFileSync('icacls.exe', [directory, '/inheritance:r', '/grant:r', `${userInfo().username}:(OI)(CI)F`], { stdio: 'pipe', windowsHide: true });
    }
    else await chmod(directory, 0o700);
    const path = join(directory, 'identity.json');
    try {
      const info = await lstat(path);
      if (!info.isFile() || info.isSymbolicLink() || info.size > 1024) throw new Error('invalid_identity');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      try { await writeFile(path, JSON.stringify({ generation: randomUUID(), key: randomBytes(32).toString('hex') }), { flag: 'wx', mode: 0o600 }); }
      catch (writeError) { if (writeError.code !== 'EEXIST') throw writeError; }
    }
    if (process.platform === 'win32') execFileSync('icacls.exe', [path, '/reset'], { stdio: 'pipe', windowsHide: true });
    else await chmod(path, 0o600);
    const value = JSON.parse(await readFile(path, 'utf8'));
    if (!/^[0-9a-f]{64}$/.test(value.key) || !/^[0-9a-f-]{36}$/.test(value.generation)) throw new Error('invalid_identity');
    return { generation: value.generation, key: Buffer.from(value.key, 'hex') };
  } catch { throw new Error('identity_unavailable'); }
}

export function identityContext(identity, context) {
  const id = (domain, value) => {
    if (typeof value !== 'string' || !value.length || value.length > 4096 || !['file', 'session', 'message', 'agent'].includes(domain)) throw new Error('invalid_source_identifier');
    return createHmac('sha256', identity.key).update(JSON.stringify(['observatory-audit-v1', context.machineId, context.sourceId, identity.generation, domain, value])).digest('hex');
  };
  return { machineId: context.machineId, sourceId: context.sourceId, generation: identity.generation, fileId: context.fileId, id };
}
