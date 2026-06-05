import { deflate, inflate } from 'https://cdn.jsdelivr.net/npm/pako@2.1.0/+esm';

/** @typedef {{ yaml: string, config: string, filePath: string }} ShareState */

export const SHARE_PAYLOAD_VERSION = 2;
export const DEFAULT_SHARE_FILE_PATH = '.github/workflows/test.yml';
export const MAX_SHARE_HASH_LENGTH = 16384;
export const MAX_SHARE_URL_LENGTH = 8192;

/**
 * @param {ShareState} state
 * @returns {string} hash segment (no leading #)
 */
export function encodeShareState(state) {
  const yaml = state.yaml ?? '';
  const config = state.config ?? '';
  let filePath = (state.filePath ?? '').trim();
  if (!filePath) {
    filePath = DEFAULT_SHARE_FILE_PATH;
  }

  /** @type {Record<string, string|number>} */
  const payload = { v: SHARE_PAYLOAD_VERSION, y: yaml };
  if (config.length > 0) {
    payload.c = config;
  }
  if (filePath !== DEFAULT_SHARE_FILE_PATH) {
    payload.p = filePath;
  }

  const jsonUtf8 = new TextEncoder().encode(JSON.stringify(payload));
  const compressed = deflate(jsonUtf8, { level: 9 });
  return uint8ToBase64Url(compressed);
}

/**
 * @param {string} yaml
 * @param {string} [filePath]
 * @returns {string}
 */
export function encodeYamlOnlyShare(yaml, filePath) {
  return encodeShareState({
    yaml,
    config: '',
    filePath: filePath ?? DEFAULT_SHARE_FILE_PATH,
  });
}

/**
 * @param {string} hashSegment
 * @returns {{ ok: true, state: ShareState } | { ok: false, error: string }}
 */
export function decodeShareHash(hashSegment) {
  if (!hashSegment || !hashSegment.length) {
    return { ok: false, error: 'empty hash' };
  }

  let compressed;
  try {
    compressed = decodeHashToBytes(hashSegment);
  } catch (e) {
    return { ok: false, error: e?.message ?? String(e) };
  }

  let decompressed;
  try {
    decompressed = inflate(compressed);
  } catch (e) {
    return { ok: false, error: e?.message ?? String(e) };
  }

  const text = new TextDecoder().decode(decompressed);
  const v2 = tryParseV2Json(text);
  if (v2) {
    return { ok: true, state: v2 };
  }

  return {
    ok: true,
    state: {
      yaml: text,
      config: '',
      filePath: DEFAULT_SHARE_FILE_PATH,
    },
  };
}

/**
 * @param {string} yaml
 * @param {string} config
 * @param {string} filePath
 * @returns {string}
 */
export function formatClipboardBundle(yaml, config, filePath) {
  const path = (filePath ?? '').trim() || DEFAULT_SHARE_FILE_PATH;
  const parts = [
    '# seiton playground — paste workflow into the editor and config into the config panel',
    `--- workflow: ${path} ---`,
    yaml ?? '',
  ];
  let text = parts.join('\n');
  if (!text.endsWith('\n')) {
    text += '\n';
  }
  const cfg = config ?? '';
  if (cfg.length > 0) {
    text += '--- config ---\n';
    text += cfg.endsWith('\n') ? cfg : `${cfg}\n`;
  }
  return text;
}

/**
 * @param {string} hashSegment
 * @param {string} fullUrl
 * @returns {boolean}
 */
export function isShareWithinLimits(hashSegment, fullUrl) {
  return hashSegment.length <= MAX_SHARE_HASH_LENGTH && fullUrl.length <= MAX_SHARE_URL_LENGTH;
}

/**
 * @param {string} text
 * @returns {ShareState | null}
 */
function tryParseV2Json(text) {
  if (!text.length || text[0] !== '{') {
    return null;
  }
  try {
    const obj = JSON.parse(text);
    if (obj?.v !== SHARE_PAYLOAD_VERSION || typeof obj.y !== 'string') {
      return null;
    }
    const config = typeof obj.c === 'string' ? obj.c : '';
    let filePath = typeof obj.p === 'string' && obj.p.trim() ? obj.p.trim() : DEFAULT_SHARE_FILE_PATH;
    return { yaml: obj.y, config, filePath };
  } catch {
    return null;
  }
}

/**
 * @param {Uint8Array} buf
 * @returns {string}
 */
function uint8ToBase64Url(buf) {
  return uint8ToBase64(buf).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

/**
 * @param {Uint8Array} buf
 * @returns {string}
 */
function uint8ToBase64(buf) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < buf.length; i += chunk) {
    const sub = buf.subarray(i, i + chunk);
    binary += String.fromCharCode.apply(null, sub);
  }
  return btoa(binary);
}

/**
 * @param {string} hashSegment
 * @returns {Uint8Array}
 */
function decodeHashToBytes(hashSegment) {
  if (hashSegment.includes('+') || hashSegment.includes('/')) {
    const binary = atob(hashSegment);
    const out = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      out[i] = binary.charCodeAt(i);
    }
    return out;
  }

  let b64 = hashSegment.replace(/-/g, '+').replace(/_/g, '/');
  const mod = b64.length % 4;
  if (mod > 0) {
    b64 += '='.repeat(4 - mod);
  }
  const binary = atob(b64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}
