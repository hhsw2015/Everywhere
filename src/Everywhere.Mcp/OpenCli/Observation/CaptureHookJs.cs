namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC docs/specs/everywhere-self-expanding.md Phase 2.5 — the JS
/// probe installed via <c>browser_add_init_script</c> at capture_start.
/// Hooks <c>fetch</c> and <c>XMLHttpRequest.setRequestHeader/send</c>,
/// writes (url, method, payload_sha256, headers) triples into
/// <c>window.__ew_capture__.signatures</c>. C# pulls the buffer at
/// capture_stop via <c>browser_cdp_evaluate</c>.
/// </summary>
public static class CaptureHookJs
{
    /// <summary>Well-known header names that likely carry a signature.</summary>
    public static readonly string[] SignatureHeaderNames =
    [
        "authorization", "x-signature", "x-sign", "x-sig", "x-auth-token",
        "x-csrf-token", "x-api-key", "x-request-signature", "x-hmac-signature",
        "x-timestamp", "x-nonce", "x-request-id",
    ];

    /// <summary>Renders the hook script — inlines the sink names so the page has no external contract.</summary>
    public static string Render()
    {
        var names = string.Join(',', SignatureHeaderNames.Select(n => "'" + n + "'"));
        return $$"""
(() => {
  if (window.__ew_capture__) return;
  const SIG = new Set([{{names}}]);
  const buf = window.__ew_capture__ = {
    signatures: [],
    mutations: [],
    dropped: 0,
    started_at: Date.now(),
  };
  const MAX_ENTRIES = 500;
  const MAX_SAMPLE = 512;

  async function sha256(text) {
    try {
      const enc = new TextEncoder().encode(text);
      const buf2 = await crypto.subtle.digest('SHA-256', enc);
      return Array.from(new Uint8Array(buf2)).map(b => b.toString(16).padStart(2, '0')).join('');
    } catch { return ''; }
  }
  function shape(v) {
    if (v == null) return 'null';
    if (typeof v === 'string') return 'string';
    if (typeof v === 'number') return 'number';
    if (typeof v === 'boolean') return 'boolean';
    if (v instanceof FormData) return 'FormData';
    if (v instanceof URLSearchParams) return 'URLSearchParams';
    if (v instanceof ArrayBuffer || ArrayBuffer.isView(v)) return 'binary';
    if (Array.isArray(v)) return 'array';
    if (typeof v === 'object') return 'object';
    return typeof v;
  }
  function pickSigHeaders(headers) {
    const out = {};
    if (!headers) return out;
    try {
      if (headers instanceof Headers) {
        headers.forEach((val, key) => { if (SIG.has(key.toLowerCase())) out[key] = String(val); });
      } else if (Array.isArray(headers)) {
        for (const [k, v] of headers) if (SIG.has(String(k).toLowerCase())) out[k] = String(v);
      } else if (typeof headers === 'object') {
        for (const k of Object.keys(headers)) if (SIG.has(k.toLowerCase())) out[k] = String(headers[k]);
      }
    } catch {}
    return out;
  }
  function push(entry) {
    if (buf.signatures.length >= MAX_ENTRIES) { buf.dropped++; return; }
    buf.signatures.push(entry);
  }

  // fetch hook
  const _fetch = window.fetch;
  window.fetch = async function(input, init) {
    try {
      const method = (init && init.method) || (input && input.method) || 'GET';
      const url = typeof input === 'string' ? input : (input && input.url) || '';
      let payloadStr = '';
      if (init && init.body != null) {
        if (typeof init.body === 'string') payloadStr = init.body;
        else if (init.body instanceof URLSearchParams) payloadStr = init.body.toString();
        else if (init.body instanceof FormData) payloadStr = '[FormData]';
        else if (init.body instanceof ArrayBuffer || ArrayBuffer.isView(init.body)) payloadStr = '[binary]';
        else { try { payloadStr = JSON.stringify(init.body); } catch { payloadStr = String(init.body); } }
      }
      const hash = payloadStr ? await sha256(payloadStr) : '';
      const headers = pickSigHeaders(init && init.headers);
      if (Object.keys(headers).length > 0 || method !== 'GET') {
        push({
          ts: Date.now(), url, method,
          payload_sha256: hash,
          payload_shape: shape(init && init.body),
          payload_sample: payloadStr.slice(0, MAX_SAMPLE),
          signature_headers: headers,
        });
      }
    } catch {}
    return _fetch.apply(this, arguments);
  };

  // XHR hook
  const XProto = XMLHttpRequest.prototype;
  const _open = XProto.open, _setHdr = XProto.setRequestHeader, _send = XProto.send;
  XProto.open = function(method, url) {
    this.__ew_method = method; this.__ew_url = url; this.__ew_headers = {};
    return _open.apply(this, arguments);
  };
  XProto.setRequestHeader = function(name, value) {
    if (SIG.has(String(name).toLowerCase())) (this.__ew_headers = this.__ew_headers || {})[name] = String(value);
    return _setHdr.apply(this, arguments);
  };
  XProto.send = function(body) {
    (async () => {
      try {
        let payloadStr = '';
        if (body != null) {
          if (typeof body === 'string') payloadStr = body;
          else if (body instanceof URLSearchParams) payloadStr = body.toString();
          else if (body instanceof FormData) payloadStr = '[FormData]';
          else if (body instanceof ArrayBuffer || ArrayBuffer.isView(body)) payloadStr = '[binary]';
          else { try { payloadStr = JSON.stringify(body); } catch { payloadStr = String(body); } }
        }
        const headers = this.__ew_headers || {};
        if (Object.keys(headers).length > 0 || (this.__ew_method || 'GET') !== 'GET') {
          push({
            ts: Date.now(), url: this.__ew_url || '', method: this.__ew_method || 'GET',
            payload_sha256: payloadStr ? await sha256(payloadStr) : '',
            payload_shape: shape(body),
            payload_sample: payloadStr.slice(0, MAX_SAMPLE),
            signature_headers: headers,
          });
        }
      } catch {}
    })();
    return _send.apply(this, arguments);
  };
})();
""";
    }

    /// <summary>The evaluate expression used to pull + clear the buffer.</summary>
    public const string DrainExpression = @"(function(){
  const b = window.__ew_capture__;
  if (!b) return null;
  const out = { signatures: b.signatures.slice(), dropped: b.dropped };
  b.signatures.length = 0;
  b.dropped = 0;
  return JSON.stringify(out);
})()";
}
