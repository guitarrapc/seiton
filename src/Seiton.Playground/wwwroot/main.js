/* global CodeMirror */

import { deflate, inflate } from 'https://cdn.jsdelivr.net/npm/pako@2.1.0/+esm';
import { dotnet } from './_framework/dotnet.js';

/** Built-in snippets (classification depends on Document selector). */
const SAMPLES = {
  default:
    `# Paste your workflow YAML to this code editor

on:
  push:
    branch: main
    tags:
      - 'v\\d+'
jobs:
  test:
    strategy:
      matrix:
        os: [macos-latest, linux-latest]
    runs-on: \${{ matrix.os }}
    steps:
      - run: echo "Checking commit '\${{ github.event.head_commit.message }}'"
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
      - uses: actions/cache@v4
        with:
          path: ~/.npm
          key: \${{ matrix.platform }}-node-\${{ hashFiles('**/package-lock.json') }}
        if: \${{ github.repository.permissions.admin == true }}
      - run: npm install && npm test
`,
  simple:
    `# Paste your workflow YAML to this code editor

on:
  push:
    branch: main

jobs:
  test:
    strategy:
      matrix:
        os: [macos-latest, linux-latest]
    runs-on: \${{ matrix.os }}
    steps:
      - uses: actions/checkout@v6
      - uses: actions/cache@v4
        with:
          path: ~/.npm
          key: \${{ matrix.platform }}-node-\${{ hashFiles('**/package-lock.json') }}
        if: \${{ github.repository.permissions.admin == true }}
      - run: npm install && npm test
`,
  minimal:
    `on:
  push:
    branches: [main]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "hello"
      - uses: actions/checkout@v4
`,
  fixPermissions:
    `on: push
permissions: write-all
jobs:
  build:
    permissions:
      contents: read
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
`,
  matrix:
    `on: push
jobs:
  test:
    strategy:
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: \${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - run: echo "\${{ runner.os }}"
`,
  actionComposite:
    `name: My composite
description: Demo action.yml
runs:
  using: composite
  steps:
    - run: echo hello world
      shell: bash
`,
};

const THEME_STORAGE_KEY = 'seiton-playground-color-mode';
const THEME_CYCLE_ORDER = ['system', 'light', 'dark'];

function playgroundColorSchemeDarkQuery() {
  return window.matchMedia('(prefers-color-scheme: dark)');
}

function getStoredColorMode() {
  try {
    const v = localStorage.getItem(THEME_STORAGE_KEY);
    if (v === 'light' || v === 'dark') return v;
  } catch (_) {
    /* ignore */
  }
  return 'system';
}

function setStoredColorMode(mode) {
  try {
    if (mode === 'system') localStorage.removeItem(THEME_STORAGE_KEY);
    else localStorage.setItem(THEME_STORAGE_KEY, mode);
  } catch (_) {
    /* ignore */
  }
}

function applyColorModeToDocument(mode) {
  const root = document.documentElement;
  if (mode === 'light') root.setAttribute('data-theme', 'light');
  else if (mode === 'dark') root.setAttribute('data-theme', 'dark');
  else root.removeAttribute('data-theme');
  const meta = document.getElementById('meta-color-scheme');
  if (meta) {
    if (mode === 'light') meta.setAttribute('content', 'light');
    else if (mode === 'dark') meta.setAttribute('content', 'dark');
    else meta.setAttribute('content', 'light dark');
  }
}

function effectiveUiIsDark() {
  const mode = getStoredColorMode();
  if (mode === 'light') return false;
  if (mode === 'dark') return true;
  return playgroundColorSchemeDarkQuery().matches;
}

function getCodeMirrorTheme() {
  return effectiveUiIsDark() ? 'material-darker' : 'default';
}

/** Accessible name for theme cycle control (visual is icon-only). */
function themeAccessibilityLabel(mode) {
  const suffix = 'Click to cycle: System, Light, Dark.';
  if (mode === 'light') {
    return `Color theme: Light. ${suffix}`;
  }
  if (mode === 'dark') {
    return `Color theme: Dark. ${suffix}`;
  }
  return `Color theme: System. ${suffix}`;
}

let exports = null;
let runtimeReady = false;
let urlControlsReady = false;

/** Base URL for GitHub release pages; path segment is the semver tag (displayed with leading v). */
const SEITON_RELEASE_TAG_BASE_URL = 'https://github.com/guitarrapc/seiton/releases/tag/';

const versionEl = document.getElementById('playground-version');

function syncVersionBadge() {
  if (!exports || !versionEl) {
    return;
  }
  try {
    const v = exports.Seiton.Playground.LintInterop.GetProductVersion();
    if (typeof v === 'string' && v.length > 0) {
      const label = v.startsWith('v') ? v : `v${v}`;
      versionEl.textContent = label;
      versionEl.href = SEITON_RELEASE_TAG_BASE_URL + encodeURIComponent(label);
      versionEl.setAttribute('aria-label', `Release ${label} — open on GitHub`);
      versionEl.hidden = false;
    }
  } catch {
    /* ignore — older bundles or trimmed exports */
  }
}

const loading = document.getElementById('loading');
const resultTable = document.getElementById('lint-result');
const resultBody = document.getElementById('lint-result-body');
const successMsg = document.getElementById('success-msg');
const toastStack = document.getElementById('toast-stack');
const fileSelect = document.getElementById('filetype-select');
const sampleSelect = document.getElementById('sample-select');
const permalinkBtn = document.getElementById('permalink-btn');
const permalinkShareTitle = 'Share — copy link to clipboard; YAML is stored in URL hash';
const permalinkDoneCopied = 'Link copied to clipboard';
const permalinkDoneNoClipboard = 'URL updated — copy from address bar if clipboard was blocked';
const applyFixesBtn = document.getElementById('apply-fixes-btn');
const urlInput = document.getElementById('url-input');
const fetchBtn = document.getElementById('fetch-btn');

/** True while <code>fetchAndLint</code> awaits network; blocks overlapping runs and input echo re-enabling the button. */
let fetchInFlight = false;

/** Set to false if the .NET WASM runtime has crashed; prevents further calls into dead runtime. */
let runtimeAlive = true;

/** @typedef {'error'|'success'|'info'} ToastVariant */

const FETCH_READY_TITLE = 'Fetch and lint YAML from this URL';
const FETCH_READY_LABEL = 'Fetch and lint YAML from this URL';
const FETCH_EMPTY_TITLE = 'Enter a YAML URL first';
const FETCH_EMPTY_LABEL = 'Fetch and lint YAML — enter a URL first';
const FETCH_INVALID_TITLE = 'Incomplete URL — use a full hostname (two or more labels), localhost, or an IP.';
const FETCH_INVALID_LABEL = 'Fetch YAML — URL looks incomplete or invalid.';
const FETCH_BUSY_TITLE = 'Fetching YAML…';
const FETCH_BUSY_LABEL = 'Fetching YAML — please wait';

/** @type {Record<ToastVariant, number>} */
const TOAST_DURATION_MS = { error: 8000, success: 3800, info: 4200 };

/**
 * Client-side gate: non-empty-but-broken pasted strings ("https://github.", "//x", paths only) stay non-actionable.
 * Does not guarantee fetch success; normalization + server round-trip decide that.
 * @param {string} trimmed
 * @returns {boolean}
 */
function looksLikePlausibleHttpFetchUrl(trimmed) {
  if (!trimmed) {
    return false;
  }
  let u;
  try {
    u = new URL(trimmed);
  } catch {
    return false;
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') {
    return false;
  }
  const host = u.hostname.toLowerCase();
  if (!host) {
    return false;
  }

  if (host === 'localhost') {
    return true;
  }
  if (host.includes(':')) {
    return true;
  }
  /** @type {boolean} */
  const looksIpv4 =
    /^(\d{1,3}\.){3}\d{1,3}$/.test(host) && host.split('.').every((p) => Number(p) >= 0 && Number(p) <= 255);

  if (looksIpv4) {
    return true;
  }

  /** Two labels minimum (domain + public suffix-ish segment). Blocks bare "github" / typos stopped mid-host. */
  const labels = host.split('.');
  if (labels.some((part) => part.length === 0)) {
    return false;
  }
  if (labels.length < 2) {
    return false;
  }
  const leaf = labels[labels.length - 1];
  if (leaf.length < 2 || !/^[a-z0-9-]{1,63}$/i.test(leaf)) {
    return false;
  }
  return labels.every((part) => part.length <= 63 && /^[a-z0-9-]{1,63}$/i.test(part));
}

/**
 * Toast host: holds dismiss callback for global Escape (capture phase).
 * @typedef {HTMLElement & { _seitonToastDismiss?: () => void }} SeitonToastHost
 */

/**
 * Toast at top of viewport; does not clear lint diagnostics.
 * @param {string} message
 * @param {ToastVariant} [variant]
 * @param {number} [durationMs]
 */
function showToast(message, variant = 'info', durationMs) {
  const stack = toastStack;
  if (!stack) return;
  const ms = durationMs ?? TOAST_DURATION_MS[variant] ?? TOAST_DURATION_MS.info;

  const wrap = document.createElement('div');
  wrap.className = `toast toast--${variant}`;
  wrap.setAttribute('role', variant === 'error' ? 'alert' : 'status');

  const bodyEl = document.createElement('div');
  bodyEl.className = 'toast__body';
  appendTextLinkifyingUrls(bodyEl, message ?? '');

  const dismissBtn = document.createElement('button');
  dismissBtn.type = 'button';
  dismissBtn.className = 'toast__dismiss';
  dismissBtn.setAttribute('aria-label', 'Dismiss notification');
  dismissBtn.textContent = '\u2715';

  wrap.append(bodyEl, dismissBtn);
  stack.appendChild(wrap);
  requestAnimationFrame(() => {
    wrap.classList.add('toast--show');
  });

  let hideTimer = window.setTimeout(() => removeToastElement(wrap), ms);
  const dismiss = () => {
    window.clearTimeout(hideTimer);
    hideTimer = 0;
    removeToastElement(wrap);
  };
  dismissBtn.addEventListener('click', dismiss);
  /** @type {SeitonToastHost} */
  const toastHost = /** @type {SeitonToastHost} */ (wrap);
  toastHost._seitonToastDismiss = dismiss;
}

/** @param {HTMLElement} el */
function removeToastElement(el) {
  if (!el?.parentElement || el.dataset.toastClosing) return;
  el.dataset.toastClosing = '1';
  el.classList.remove('toast--show');
  el.classList.add('toast--out');
  window.setTimeout(() => {
    try {
      el.remove();
    } catch {
      /* ignore */
    }
  }, 240);
}

function installToastGlobalEscapeDismiss() {
  if (!toastStack) {
    return;
  }
  document.addEventListener(
    'keydown',
    (ev) => {
      if (ev.key !== 'Escape') {
        return;
      }
      /** @type {SeitonToastHost | null} */
      const top = /** @type {SeitonToastHost | null} */ (toastStack.lastElementChild);
      if (!top || typeof top._seitonToastDismiss !== 'function') {
        return;
      }
      ev.preventDefault();
      ev.stopPropagation();
      top._seitonToastDismiss();
    },
    true,
  );
}

installToastGlobalEscapeDismiss();

function syncFetchButtonEnabled() {
  if (!fetchBtn || !urlInput) return;
  if (fetchInFlight) {
    fetchBtn.disabled = true;
    urlInput.disabled = true;
    fetchBtn.title = FETCH_BUSY_TITLE;
    fetchBtn.setAttribute('aria-label', FETCH_BUSY_LABEL);
    return;
  }
  urlInput.disabled = false;

  const raw = (urlInput.value ?? '').trim();
  if (!raw.length) {
    fetchBtn.disabled = true;
    fetchBtn.title = FETCH_EMPTY_TITLE;
    fetchBtn.setAttribute('aria-label', FETCH_EMPTY_LABEL);
    return;
  }
  const okShape = looksLikePlausibleHttpFetchUrl(raw);
  fetchBtn.disabled = !okShape;
  if (!okShape) {
    fetchBtn.title = FETCH_INVALID_TITLE;
    fetchBtn.setAttribute('aria-label', FETCH_INVALID_LABEL);
    return;
  }
  fetchBtn.title = FETCH_READY_TITLE;
  fetchBtn.setAttribute('aria-label', FETCH_READY_LABEL);
}

function markUrlControlsReady() {
  if (urlControlsReady) {
    return;
  }
  urlControlsReady = true;
  document.body?.setAttribute('data-url-controls-ready', 'true');
}

const editor = CodeMirror(document.getElementById('editor'), {
  mode: 'yaml',
  theme: getCodeMirrorTheme(),
  lineNumbers: true,
  lineWrapping: true,
  autofocus: true,
  styleActiveLine: true,
  /** Grow with document so the page scrolls instead of trapping scroll inside CodeMirror only. */
  viewportMargin: Infinity,
  gutters: ['CodeMirror-linenumbers', 'error-marker'],
  extraKeys: {
    Tab(cm) {
      cm.execCommand(cm.somethingSelected() ? 'indentMore' : 'insertSoftTab');
    },
  },
  value: getDefaultSource(),
});

function syncEditorTheme() {
  editor.setOption('theme', getCodeMirrorTheme());
  editor.refresh();
}

playgroundColorSchemeDarkQuery().addEventListener('change', () => {
  if (getStoredColorMode() === 'system') syncEditorTheme();
});

const themeCycleBtn = document.getElementById('theme-cycle-btn');

function updateThemeCycleButton() {
  if (!themeCycleBtn) {
    return;
  }
  const mode = getStoredColorMode();
  themeCycleBtn.dataset.themeMode = mode;
  themeCycleBtn.setAttribute('aria-label', themeAccessibilityLabel(mode));
}

function cycleColorMode() {
  const cur = getStoredColorMode();
  const i = Math.max(0, THEME_CYCLE_ORDER.indexOf(cur));
  const next = THEME_CYCLE_ORDER[(i + 1) % THEME_CYCLE_ORDER.length];
  setStoredColorMode(next);
  applyColorModeToDocument(next);
  syncEditorTheme();
  updateThemeCycleButton();
}

if (themeCycleBtn) {
  themeCycleBtn.addEventListener('click', cycleColorMode);
}
updateThemeCycleButton();

const DEBOUNCE_MS = 300;
const utf8Decoder = new TextDecoder();
let debounceId = null;
/** Coalesce refreshes while typing so measurements track height for layout (page scroll). */
let sizingRaf = null;

/**
 * Concurrency & staleness control for lint execution.
 * - lintInProgress: true while RunLint is executing (prevents re-entry)
 * - lintPendingRetry: set to true if a change occurred while lint was in progress; triggers re-lint on completion
 * - lastLintedSource / lastLintedFilePath: track previous lint inputs to skip redundant lint for identical content
 */
let lintInProgress = false;
let lintPendingRetry = false;
let lastLintedSource = '';
let lastLintedFilePath = '';

editor.on('change', (_cm, changeObj) => {
  if (sizingRaf === null) {
    sizingRaf = requestAnimationFrame(() => {
      sizingRaf = null;
      editor.refresh();
    });
  }

  // If lint is currently in progress, mark that a retry is needed after it finishes.
  // The debounce timer is still managed so that rapid typing coalesces properly.
  if (lintInProgress) {
    lintPendingRetry = true;
  }

  if (debounceId !== null) {
    clearTimeout(debounceId);
  }

  const run = () => {
    debounceId = null;
    runLint();
  };

  if (changeObj.origin === 'paste') {
    run();
  } else {
    debounceId = setTimeout(run, DEBOUNCE_MS);
  }
});

window.addEventListener('resize', () => {
  editor.refresh();
});

fileSelect.addEventListener('change', () => {
  // filePath changed — invalidate so lint runs even if source is the same.
  lastLintedSource = '';
  lastLintedFilePath = '';
  runLint();
});

sampleSelect.addEventListener('change', () => {
  const key = sampleSelect.value;
  if (!key || !SAMPLES[key]) {
    return;
  }
  const text = SAMPLES[key];
  if (key === 'actionComposite') {
    fileSelect.value = 'action.yml';
  } else {
    fileSelect.value = '.github/workflows/test.yml';
  }
  editor.setValue(text);
  editor.refresh();
  lastLintedSource = '';
  lastLintedFilePath = '';
  runLint();
});

/**
 * Synchronous clipboard fallback (helps while the originating click is still a “user gesture”).
 * @param {string} text
 * @returns {boolean}
 */
function tryClipboardCopyViaTextArea(text) {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.setAttribute('readonly', '');
  ta.style.position = 'fixed';
  ta.style.left = '-9999px';
  ta.style.top = '0';
  document.body.appendChild(ta);
  try {
    ta.focus();
    ta.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    if (ta.parentNode) {
      ta.parentNode.removeChild(ta);
    }
  }
}

function schedulePermalinkFeedback(copied) {
  const msg = copied ? permalinkDoneCopied : permalinkDoneNoClipboard;
  permalinkBtn.title = msg;
  permalinkBtn.setAttribute('aria-label', msg);
  window.setTimeout(() => {
    permalinkBtn.title = permalinkShareTitle;
    permalinkBtn.setAttribute('aria-label', permalinkShareTitle);
  }, 1800);
}

permalinkBtn.addEventListener('click', () => {
  try {
    const src = new TextEncoder().encode(editor.getValue());
    const compressed = deflate(src, { level: 9 });
    const b64 = uint8ToBase64(compressed);
    const url = `${location.pathname}${location.search}#${b64}`;
    history.replaceState(null, '', url);
    const fullUrl = location.href;
    if (tryClipboardCopyViaTextArea(fullUrl)) {
      schedulePermalinkFeedback(true);
      return;
    }
    const w = navigator.clipboard?.writeText;
    if (w) {
      w.call(navigator.clipboard, fullUrl)
        .then(() => {
          schedulePermalinkFeedback(true);
        })
        .catch(() => {
          schedulePermalinkFeedback(false);
        });
      return;
    }
    schedulePermalinkFeedback(false);
  } catch (e) {
    showToast(e?.message ?? String(e), 'error');
  }
});

applyFixesBtn.addEventListener('click', () => {
  if (!runtimeAlive || !runtimeReady || !exports) return;
  try {
    const original = editor.getValue();
    const yaml = exports.Seiton.Playground.LintInterop.ApplyAllFixes(
      original,
      getSelectedFilePath(),
    );
    if (yaml === original) {
      // Fix pass returned unchanged YAML — either an error occurred
      // (logged to console.error by C#) or no fixes were applicable.
      showToast('No changes were made. Either no auto-applicable fixes were available or fix application failed (see browser console).', 'info');
      return;
    }
    editor.setValue(yaml);
    editor.refresh();
    applyFixesBtn.hidden = true;
    // Invalidate so the lint after fix application actually runs.
    lastLintedSource = '';
    lastLintedFilePath = '';
    runLint();
  } catch (e) {
    if (isRuntimeDeadError(e)) {
      handleRuntimeDeath();
      return;
    }
    showToast(e?.message ?? String(e), 'error');
  }
});

fetchBtn.addEventListener('click', () => fetchAndLint());
if (urlInput) {
  urlInput.addEventListener('input', syncFetchButtonEnabled);
  /** Paste updates value asynchronously; next frame picks up pasted URL. */
  urlInput.addEventListener('paste', () => {
    requestAnimationFrame(() => syncFetchButtonEnabled());
  });
  urlInput.addEventListener('keydown', (ev) => {
    if (ev.key !== 'Enter') {
      return;
    }
    ev.preventDefault();
    if (fetchInFlight) {
      return;
    }
    const raw = (urlInput.value ?? '').trim();
    if (!raw.length) {
      urlInput.focus();
      showToast(FETCH_EMPTY_TITLE, 'info', 2600);
      return;
    }
    if (!looksLikePlausibleHttpFetchUrl(raw)) {
      urlInput.focus();
      showToast(FETCH_INVALID_TITLE, 'info', 3200);
      return;
    }
    fetchAndLint();
  });
}
syncFetchButtonEnabled();
markUrlControlsReady();

async function fetchAndLint() {
  if (fetchInFlight) {
    return;
  }
  const raw = urlInput?.value?.trim() ?? '';
  if (!raw) {
    return;
  }
  if (!looksLikePlausibleHttpFetchUrl(raw)) {
    showToast(FETCH_INVALID_TITLE, 'info');
    return;
  }
  fetchInFlight = true;
  syncFetchButtonEnabled();
  try {
    const fetchUrl = normalizeGitHubBlobToRaw(raw);
    const res = await fetch(fetchUrl, { mode: 'cors', redirect: 'follow', cache: 'no-store' });
    if (!res.ok) {
      throw new Error(`fetch failed: ${res.status} ${res.statusText}`);
    }
    const ct = (res.headers.get('content-type') ?? '').toLowerCase();
    if (ct.includes('text/html')) {
      throw new Error('Got HTML (not raw YAML). For github.com files, use the “Raw” link, or a raw.githubusercontent.com / gist.githubusercontent.com URL.');
    }
    const text = await res.text();
    editor.setValue(text);
    editor.refresh();
    lastLintedSource = '';
    lastLintedFilePath = '';
    runLint();
    // Skip the success toast when the runtime died inside runLint() — the crash
    // message is already visible and a "Loaded YAML" toast would be misleading.
    if (runtimeAlive) {
      showToast('Loaded YAML from URL.', 'success');
    }
  } catch (e) {
    showToast(e?.message ?? String(e), 'error');
  } finally {
    fetchInFlight = false;
    syncFetchButtonEnabled();
  }
}

/**
 * github.com/{owner}/{repo}/blob/{ref}/{path} → raw.githubusercontent.com
 */
function normalizeGitHubBlobToRaw(input) {
  let u;
  try {
    u = new URL(input);
  } catch {
    throw new Error('Invalid URL');
  }
  if (u.hostname === 'raw.githubusercontent.com' || u.hostname === 'gist.githubusercontent.com') {
    return u.href;
  }
  if (u.hostname === 'github.com') {
    const m = u.pathname.match(/^\/([^/]+)\/([^/]+)\/blob\/([^/]+)\/(.+)$/);
    if (m) {
      const [, owner, repo, ref, rest] = m;
      return `https://raw.githubusercontent.com/${owner}/${repo}/${ref}/${rest}`;
    }
  }
  return u.href;
}

function uint8ToBase64(buf) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < buf.length; i += chunk) {
    const sub = buf.subarray(i, i + chunk);
    binary += String.fromCharCode.apply(null, sub);
  }
  return btoa(binary);
}

/** Typical https? URLs in prose (excluding spaces and angle brackets / parens in path edge cases). */
const URL_SPLIT_RE = /https?:\/\/[^\s<>()]+/gi;

/**
 * Turns http(s) substrings into links under `parent`; row clicks are not propagated from links.
 * @param {HTMLElement} parent
 * @param {string} [text]
 */
function appendTextLinkifyingUrls(parent, text) {
  const s = String(text ?? '');
  const matches = [...s.matchAll(URL_SPLIT_RE)];
  if (matches.length === 0) {
    parent.appendChild(document.createTextNode(s));
    return;
  }
  let sliceFrom = 0;
  for (const m of matches) {
    const full = m[0];
    const start = /** @type {number} */ (m.index);
    if (start > sliceFrom) {
      parent.appendChild(document.createTextNode(s.slice(sliceFrom, start)));
    }
    sliceFrom = start + full.length;
    const hrefRaw = full.replace(/[).,;:!?]+$/g, '');
    const a = document.createElement('a');
    a.className = 'result-link';
    try {
      a.href = new URL(hrefRaw).href;
    } catch {
      parent.appendChild(document.createTextNode(full));
      continue;
    }
    a.textContent = full;
    a.addEventListener('click', (ev) => {
      ev.stopPropagation();
    });
    parent.appendChild(a);
  }
  if (sliceFrom < s.length) {
    parent.appendChild(document.createTextNode(s.slice(sliceFrom)));
  }
}

/**
 * Detects whether an error indicates the .NET WASM runtime has exited/crashed.
 * @param {unknown} err
 * @returns {boolean}
 */
function isRuntimeDeadError(err) {
  if (!err) return false;
  const msg = String(err?.message ?? err).toLowerCase();
  return msg.includes('.net runtime already exited')
    || msg.includes('runtime already exited')
    || msg.includes('runtime has already exited');
}

/**
 * Called once when the .NET WASM runtime is detected as dead.
 * Stops all lint calls and shows a persistent error message.
 */
function handleRuntimeDeath() {
  runtimeAlive = false;
  if (debounceId !== null) {
    clearTimeout(debounceId);
    debounceId = null;
  }
  showToast(
    'The WebAssembly runtime has crashed. Please reload the page to continue.',
    'error',
    60000,
  );
  // Show an inline message in the result area
  resultBody.replaceChildren();
  resultTable.hidden = false;
  successMsg.style.display = 'none';
  const row = document.createElement('tr');
  const cell = document.createElement('td');
  cell.setAttribute('colspan', '2');
  cell.textContent = 'Runtime crashed — please reload the page.';
  cell.style.color = 'var(--danger, #ff5370)';
  row.appendChild(cell);
  resultBody.appendChild(row);
}

function runLint() {
  if (!runtimeAlive || !runtimeReady || !exports) {
    return;
  }

  // Re-entry guard: if lint is already in progress (shouldn't happen with sync calls,
  // but defensive against future async changes or unexpected event ordering).
  if (lintInProgress) {
    lintPendingRetry = true;
    return;
  }

  const source = editor.getValue();
  const filePath = getSelectedFilePath();

  // Staleness check: skip if content + filePath are identical to last successful lint.
  if (source === lastLintedSource && filePath === lastLintedFilePath) {
    return;
  }

  lintInProgress = true;
  lintPendingRetry = false;

  try {
    const utf8Bytes = exports.Seiton.Playground.LintInterop.RunLint(source, filePath);
    const json = utf8Decoder.decode(utf8Bytes);
    const diagnostics = JSON.parse(json);
    // Do not treat an internal-error fallback as a successful lint: if we cached
    // the staleness key here a transient C# exception would permanently block retries
    // on the same content/path until the user edits the file.
    const isInternalError = diagnostics.length === 1 && diagnostics[0].ruleId === 'internal-error';
    if (!isInternalError) {
      lastLintedSource = source;
      lastLintedFilePath = filePath;
    }
    renderResults(diagnostics);
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    showToast(err?.message ?? String(err), 'error');
  } finally {
    lintInProgress = false;
  }

  // If content changed while we were executing, schedule a re-lint after debounce.
  // Skip if runtime died during the try/catch — handleRuntimeDeath() already stopped scheduling.
  if (lintPendingRetry && runtimeAlive) {
    lintPendingRetry = false;
    if (debounceId !== null) {
      clearTimeout(debounceId);
    }
    debounceId = setTimeout(() => {
      debounceId = null;
      runLint();
    }, DEBOUNCE_MS);
  }
}

function renderResults(diagnostics) {
  resultBody.replaceChildren();
  editor.clearGutter('error-marker');

  let anyFixable = false;
  for (const diag of diagnostics) {
    if (diag.fixable) {
      anyFixable = true;
      break;
    }
  }

  applyFixesBtn.hidden = !anyFixable;

  if (diagnostics.length === 0) {
    resultTable.hidden = true;
    successMsg.style.display = 'block';
    applyFixesBtn.hidden = true;
    return;
  }

  successMsg.style.display = 'none';
  resultTable.hidden = false;

  for (const diag of diagnostics) {
    const row = document.createElement('tr');
    row.addEventListener('click', () => {
      const line = Math.max(0, (diag.line ?? 1) - 1);
      const ch = Math.max(0, (diag.column ?? 1) - 1);
      editor.setCursor({ line, ch });
      editor.focus();
    });

    const posCell = document.createElement('td');
    const posTag = document.createElement('span');
    posTag.className = 'pos-chip';
    posTag.textContent = `line:${diag.line}, col:${diag.column}`;
    posCell.appendChild(posTag);
    row.appendChild(posCell);

    const descCell = document.createElement('td');
    appendTextLinkifyingUrls(descCell, diag.message ?? '');
    if (diag.fixable) {
      const fx = document.createElement('span');
      fx.className = 'fix-chip';
      fx.title = diag.fixDescription ?? 'Auto-fix available';
      fx.textContent = 'Fix';
      descCell.appendChild(fx);
    }
    if (diag.ruleId) {
      const kindTag = document.createElement('span');
      kindTag.className = 'rule-chip';
      kindTag.textContent = diag.ruleId;
      descCell.appendChild(kindTag);
    }
    row.appendChild(descCell);

    resultBody.appendChild(row);

    const lineIndex = Math.max(0, (diag.line ?? 1) - 1);
    const marker = document.createElement('div');
    marker.className =
      diag.severity === 'Error' ? 'gutter-marker gutter-marker--error' : 'gutter-marker gutter-marker--warning';
    marker.textContent = '●';
    editor.setGutterMarker(lineIndex, 'error-marker', marker);
  }
}

function getSelectedFilePath() {
  return fileSelect ? fileSelect.value : '.github/workflows/test.yml';
}

function getDefaultSource() {
  if (window.location.hash && window.location.hash.length > 1) {
    try {
      const b64 = window.location.hash.slice(1);
      const binary = atob(b64);
      const compressed = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) {
        compressed[i] = binary.charCodeAt(i);
      }
      const decompressed = inflate(compressed);
      return new TextDecoder().decode(decompressed);
    } catch {
      /* ignore */
    }
  }

  return SAMPLES.default;
}

void initializeRuntime();

async function initializeRuntime() {
  try {
    const runtime = await dotnet
      .withApplicationArguments('playground')
      .create();
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    await runtime.runMain();
    runtimeReady = true;
    syncVersionBadge();
    loading.style.display = 'none';
    runLint();
    requestAnimationFrame(() => {
      editor.refresh();
    });
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    runtimeAlive = false;
    runtimeReady = false;
    loading.style.display = 'none';
    showToast(err?.message ?? String(err), 'error', 60000);
  }
}
