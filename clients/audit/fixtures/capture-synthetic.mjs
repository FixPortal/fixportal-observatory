// Cross-runtime test entry point. This reads only the two committed synthetic fixtures.
import { mkdtemp, mkdir, copyFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { captureEvidence, writeCapture } from '../../observatory-audit.mjs';

const directory = await mkdtemp(join(tmpdir(), 'audit-fixture-native-'));
try {
  const sources = [];
  for (const tool of ['claude', 'codex']) {
    const root = join(directory, tool); await mkdir(root);
    await copyFile(new URL(`${tool}-synthetic.jsonl`, import.meta.url), join(root, 'synthetic.jsonl'));
    sources.push({ sourceId: `${tool}-local`, tool, roots: [root] });
  }
  const machineId = '11111111-1111-1111-1111-111111111111';
  const scope = { from: '2026-10-19', to: '2026-10-21', sources: sources.map(source => ({ machineId, sourceId: source.sourceId })), requestedChecks: ['sourceAccounting', 'dailyAttribution'], excludedChecks: [], allowPartial: false };
  const capture = await captureEvidence({ captureId: '22222222-2222-2222-2222-222222222222', requestId: '33333333-3333-3333-3333-333333333333', machineId, scope }, { identityDirectory: join(directory, 'identity'), sources, limits: { maxChunkBytes: 2200 } });
  await writeCapture(capture, process.argv[2]);
} finally { await rm(directory, { recursive: true, force: true }); }
