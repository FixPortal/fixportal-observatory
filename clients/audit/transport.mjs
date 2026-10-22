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
    if (typeof path !== 'string' || !/^\/api\/audits\/capture-requests\/(claim|[0-9a-f-]{36}\/(seal|chunks\/[0-9]{1,4}))$/.test(path)) throw new Error('invalid_audit_path');
    const method = options.method ?? 'POST';
    if (!['POST', 'PUT'].includes(method)) throw new Error('invalid_audit_method');
    const headers = { 'X-Observatory-Audit-Key': configuration.key };
    for (const [name, value] of Object.entries(options.headers ?? {})) {
      if (!['Content-Type', 'X-Content-SHA256', 'X-Observatory-Audit-Lease', 'Idempotency-Key'].includes(name) || typeof value !== 'string') throw new Error('invalid_audit_header');
      headers[name] = value;
    }
    for (let attempt = 0; attempt < 3; attempt++) {
      let response;
      try {
        const timeout = AbortSignal.timeout(10000);
        const signal = options.signal ? AbortSignal.any([timeout, options.signal]) : timeout;
        response = await fetch(origin.origin + path, { method, headers, body: options.body, redirect: 'manual', signal });
      } catch {
        if (options.signal?.aborted || attempt === 2) throw new Error('audit_transport_failed');
        await (dependencies.sleep ?? sleep)(250 * 2 ** attempt, undefined, { signal: options.signal }); continue;
      }
      if (response.status >= 300 && response.status < 400) { await response.body?.cancel(); throw new Error('audit_redirect_blocked'); }
      if (!response.ok) {
        await response.body?.cancel();
        if ((response.status === 429 || response.status >= 500) && attempt < 2) { await (dependencies.sleep ?? sleep)(250 * 2 ** attempt, undefined, { signal: options.signal }); continue; }
        throw new Error(`audit_http_${response.status}`);
      }
      const parts = []; let length = 0;
      try {
        for await (const part of response.body ?? []) {
          length += part.length;
          if (length > 1024 * 1024) throw new Error('audit_response_limit');
          parts.push(part);
        }
      } catch (error) { throw new Error(error.message === 'audit_response_limit' ? 'audit_response_limit' : 'audit_transport_failed'); }
      return { status: response.status, body: Buffer.concat(parts).toString('utf8') };
    }
    throw new Error('audit_transport_failed');
  } };
}
