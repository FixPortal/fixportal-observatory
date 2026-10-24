import { setTimeout as sleep } from 'node:timers/promises';

export function createAuditTransport(configuration, dependencies = {}) {
  let origin;
  try {
    origin = new URL(configuration.origin);
    const loopback = ['127.0.0.1', '[::1]', 'localhost'].includes(origin.hostname);
    if (origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash
      || !(origin.protocol === 'https:' || origin.protocol === 'http:' && loopback && configuration.allowLoopbackHttp === true)
      || typeof configuration.key !== 'string' || !/^[0-9a-f-]{36}\.[0-9a-f]{64}$/i.test(configuration.key)) throw new Error();
  } catch { throw new Error('invalid_audit_transport'); }
  return { async request(path, options = {}) {
    if (typeof path !== 'string' || !/^\/api\/audits\/capture-requests\/(claim|selected\/(claim|renew)|[0-9a-f-]{36}\/(seal|chunks\/[0-9]{1,4}))$/.test(path)) throw new Error('invalid_audit_path');
    const selected = path.includes('/selected/');
    if (selected && (!options.budget || options.method && options.method !== 'POST')) throw new Error('invalid_audit_selected_transport');
    const method = options.method ?? 'POST';
    if (!['POST', 'PUT'].includes(method)) throw new Error('invalid_audit_method');
    const headers = { 'X-Observatory-Audit-Key': configuration.key };
    for (const [name, value] of Object.entries(options.headers ?? {})) {
      if (!['Content-Type', 'X-Content-SHA256', 'X-Observatory-Audit-Lease', 'Idempotency-Key'].includes(name) || typeof value !== 'string') throw new Error('invalid_audit_header');
      headers[name] = value;
    }
    // At most three attempts, capped below the default five-minute capture lease.
    // Each upload gets latency allowance plus 128 KiB/s, bounded at ninety seconds.
    const bodyBytes = options.body == null ? 0 : Buffer.byteLength(options.body);
    if (selected && bodyBytes > 1024) throw new Error('invalid_audit_request');
    const budget = options.budget;
    const attemptBudget = path.endsWith('/claim') ? 10000
      : Math.min(90000, 10000 + Math.ceil(bodyBytes / (128 * 1024)) * 1000);
    const deadline = Math.min(options.deadline ?? Infinity, Date.now() + 270750);
    if (!Number.isFinite(deadline)) throw new Error('audit_transport_failed');
    // Shared selected-mode lifetime is monotonic. Generic mode keeps its existing wall deadline.
    const remaining = () => budget ? Math.min(budget.remainingMs(), options.remainingMs?.() ?? Infinity) : deadline - Date.now() - 1000;
    const retry = async attempt => {
      const delay = 250 * 2 ** attempt;
      budget?.check();
      if (options.signal?.aborted || remaining() <= delay) throw new Error('audit_transport_failed');
      await (dependencies.sleep ?? sleep)(delay, undefined, { signal: budget?.signal ?? options.signal });
    };
    const maxAttempts = selected ? 1 : 3;
    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      budget?.check();
      if (options.signal?.aborted || remaining() <= 0) throw new Error('audit_transport_failed');
      budget?.take('httpAttempts', 1, false, options.sealing === true);
      budget?.take('uploadBytes', bodyBytes, false, options.sealing === true);
      let response;
      try {
        const timeout = AbortSignal.timeout(Math.max(1, Math.floor(Math.min(attemptBudget, remaining()))));
        const signal = AbortSignal.any([timeout, ...(options.signal ? [options.signal] : []), ...(budget ? [budget.signal] : [])]);
        response = await (dependencies.fetch ?? fetch)(origin.origin + path, { method, headers, body: options.body, redirect: 'manual', signal });
      } catch {
        if (options.signal?.aborted || attempt === maxAttempts - 1) throw new Error('audit_transport_failed');
        await retry(attempt); continue;
      }
      if (response.status >= 300 && response.status < 400) { await response.body?.cancel(); throw new Error('audit_redirect_blocked'); }
      if (!response.ok) {
        await response.body?.cancel();
        if ((response.status === 429 || response.status >= 500) && attempt < maxAttempts - 1) { await retry(attempt); continue; }
        throw new Error(`audit_http_${response.status}`);
      }
      const parts = []; let length = 0;
      try {
        for await (const part of response.body ?? []) {
          budget?.check();
          if (remaining() <= 0) throw new Error('audit_transport_failed');
          const responseLimit = Math.min(1024 * 1024, budget?.limit('maxMetadataBytes') ?? 1024 * 1024);
          if (part.length > responseLimit - length) { await response.body?.cancel().catch(() => {}); throw new Error('audit_response_limit'); }
          length += part.length;
          parts.push(part);
        }
      } catch (error) { throw new Error(error.message === 'audit_response_limit' ? 'audit_response_limit' : 'audit_transport_failed'); }
      budget?.check();
      if (remaining() <= 0) throw new Error('audit_transport_failed');
      return { status: response.status, body: Buffer.concat(parts).toString('utf8') };
    }
    throw new Error('audit_transport_failed');
  } };
}
