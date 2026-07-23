/* global CodeMirror */

import { dotnet } from './_framework/dotnet.js';
import {
  decodeShareHash,
  encodeShareState,
  encodeYamlOnlyShare,
  formatClipboardBundle,
  isShareWithinLimits,
} from './share-payload.js';
import { captureFlowViewState, renderFlowGraph, flowStructureSignature, updateFlowGraphDiagnostics } from './flow-graph.js';

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
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: actions/cache@v4
        with:
          path: ~/.npm
          key: ubuntu-node-\${{ hashFiles('**/package-lock.json') }}
      - run: npm install && npm test
`,
  minimal:
    `on:
  push:
    branches: [main]
jobs:
  test:
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    steps:
      - run: echo "hello"
        if: contains('push', github.event_name)
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
  parallelSteps:
    `on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - id: build-frontend
        run: npm run build
        background: true
      - id: build-backend
        run: npm run build
        background: true
      - wait: [build-frontend, build-backend]
      - parallel:
        - run: npm run build-app1
        - run: npm run build-app2
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
const permalinkShareTitle =
  'Share — copy link with workflow YAML and config in URL hash';
const permalinkYamlOnlyCopied =
  'Link copied (workflow YAML only — config omitted because URL was too long)';
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

/**
 * @returns {{ state?: import('./share-payload.js').ShareState, error?: string }}
 */
function readShareFromLocationHash() {
  const raw = window.location.hash;
  if (!raw || raw.length <= 1) {
    return {};
  }
  const decoded = decodeShareHash(raw.slice(1));
  if (!decoded.ok) {
    return { error: decoded.error };
  }
  return { state: decoded.state };
}

const shareFromUrl = readShareFromLocationHash();

function getInitialEditorYaml() {
  if (shareFromUrl.state) {
    return shareFromUrl.state.yaml;
  }
  return SAMPLES.default;
}

function getInitialConfigYaml() {
  return shareFromUrl.state?.config ?? '';
}

/**
 * @param {import('./share-payload.js').ShareState} state
 */
function applyShareFilePathToSelect(state) {
  if (!fileSelect || !state?.filePath) {
    return;
  }
  for (const opt of fileSelect.options) {
    if (opt.value === state.filePath) {
      fileSelect.value = state.filePath;
      return;
    }
  }
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
  value: getInitialEditorYaml(),
});

applyShareFilePathToSelect(shareFromUrl.state);

function syncEditorTheme() {
  editor.setOption('theme', getCodeMirrorTheme());
  editor.refresh();
  syncConfigEditorTheme();
}

playgroundColorSchemeDarkQuery().addEventListener('change', () => {
  if (getStoredColorMode() === 'system') {
    syncEditorTheme();
    refreshMermaidPreviewOnThemeChange();
  }
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
  refreshMermaidPreviewOnThemeChange();
}

if (themeCycleBtn) {
  themeCycleBtn.addEventListener('click', cycleColorMode);
}
updateThemeCycleButton();

/* ─── Config Editor ─── */
const CONFIG_DEBOUNCE_MS = 500;
const configPanel = document.getElementById('config-panel');
const configToggleBtn = document.getElementById('config-toggle-btn');
const configEditorWrap = document.getElementById('config-editor-wrap');
const configDiagnosticsEl = document.getElementById('config-diagnostics');
const configTemplateSelect = document.getElementById('config-template-select');

/** Built-in config templates for quick setup. */
const CONFIG_TEMPLATES = {
  timeoutAndLatest: `fix:
  defaults:
    job-timeout-minutes: 15

rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
`,
  fullFix: `# NOTE: enable-network uses the GitHub API (unauthenticated, 60 req/hr limit).
# SHA/digest pinning is resolved via api.github.com when "Apply fixes" is clicked.
fix:
  defaults:
    job-timeout-minutes: 15
  pinning:
    enable-network: true
    min-age-days: 14
  images:
    enable-network: true

rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
`,
  exclusions: `rules:
  checkout-persist-credentials:
    severity: warning
  job-permissions-required:
    enabled: false

exclusions:
  - file: ".github/workflows/test.yml"
    rules:
      - job-timeout-minutes-required
      - runner-no-latest
`,
};

const configEditor = CodeMirror(document.getElementById('config-editor'), {
  mode: 'yaml',
  theme: getCodeMirrorTheme(),
  lineNumbers: true,
  lineWrapping: true,
  value: getInitialConfigYaml(),
});

let configDebounceId = null;

configToggleBtn.addEventListener('click', () => {
  const collapsed = configPanel.classList.toggle('config-panel--collapsed');
  configToggleBtn.setAttribute('aria-expanded', String(!collapsed));
  configEditorWrap.hidden = collapsed;
  if (!collapsed) {
    configEditor.refresh();
  }
});

configTemplateSelect.addEventListener('change', () => {
  const key = configTemplateSelect.value;
  if (!key || !CONFIG_TEMPLATES[key]) {
    // "none" selected — clear config editor
    configEditor.setValue('');
    configEditor.refresh();
    return;
  }
  // Expand panel if collapsed
  if (configPanel.classList.contains('config-panel--collapsed')) {
    configPanel.classList.remove('config-panel--collapsed');
    configToggleBtn.setAttribute('aria-expanded', 'true');
    configEditorWrap.hidden = false;
  }
  configEditor.setValue(CONFIG_TEMPLATES[key]);
  configEditor.refresh();
});

configEditor.on('change', () => {
  if (configDebounceId !== null) {
    clearTimeout(configDebounceId);
  }
  configDebounceId = setTimeout(() => {
    configDebounceId = null;
    const yaml = configEditor.getValue();
    const diags = setConfig(yaml);
    renderConfigDiagnostics(diags);
  }, CONFIG_DEBOUNCE_MS);
});

// ─── Config editor resize handle ───
(function initConfigResize() {
  const handle = document.getElementById('config-resize-handle');
  const cmEl = document.querySelector('#config-editor .CodeMirror');
  if (!handle || !cmEl) return;

  let startY = 0;
  let startH = 0;

  function onPointerMove(e) {
    const newH = Math.max(80, startH + (e.clientY - startY));
    cmEl.style.height = newH + 'px';
    configEditor.refresh();
  }

  function cleanupResize() {
    handle.classList.remove('config-panel__resize-handle--active');
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerup', cleanupResize);
    document.removeEventListener('pointercancel', cleanupResize);
  }

  handle.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    startY = e.clientY;
    startH = cmEl.offsetHeight;
    handle.classList.add('config-panel__resize-handle--active');
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', cleanupResize);
    document.addEventListener('pointercancel', cleanupResize);
  });
})();

function renderConfigDiagnostics(diagnostics) {
  if (!diagnostics || diagnostics.length === 0) {
    configDiagnosticsEl.hidden = true;
    configDiagnosticsEl.textContent = '';
    return;
  }
  configDiagnosticsEl.hidden = false;
  configDiagnosticsEl.textContent = diagnostics
    .map(d => d.message || d.Message || '')
    .filter(Boolean)
    .join('\n');
}

function syncConfigEditorTheme() {
  configEditor.setOption('theme', getCodeMirrorTheme());
  configEditor.refresh();
}

const DEBOUNCE_MS = 500;
/** Fold diagnostic messages longer than this in the results table. */
const DIAG_MESSAGE_COLLAPSE_MIN_CHARS = 160;
/** Line count (inclusive) shown while a diagnostic message is folded. */
const DIAG_MESSAGE_COLLAPSED_LINES = 3;
const utf8Decoder = new TextDecoder();
let debounceId = null;
/** Coalesce refreshes while typing so measurements track height for layout (page scroll). */
let sizingRaf = null;

/**
 * Concurrency & staleness control for lint execution.
 * - lintInProgress: true while RunLint is executing (prevents re-entry)
 * - lintPendingRetry: set to true if a change occurred while lint was in progress; triggers re-lint on completion
 * - lastLintedSource / lastLintedFilePath / lastConfigVersion: track previous lint inputs to skip redundant lint for identical content
 */
let lintInProgress = false;
let lintPendingRetry = false;
let lastLintedSource = '';
let lastLintedFilePath = '';

/** Monotonically incremented on each successful SetConfig call; included in staleness check. */
let configVersion = 0;
let lastConfigVersion = 0;

// ─── Flow tab (Result / Flow / Mermaid switch + D3 graph) ───
const tabResultBtn = document.getElementById('tab-result-btn');
const tabFlowBtn = document.getElementById('tab-flow-btn');
const tabMermaidBtn = document.getElementById('tab-mermaid-btn');
const resultPanel = document.getElementById('result-panel');
const flowPanel = document.getElementById('flow-panel');
const mermaidPanel = document.getElementById('mermaid-panel');
const mermaidOutputEl = document.getElementById('mermaid-output');
const mermaidPreviewEl = document.getElementById('mermaid-preview');
const mermaidEmptyEl = document.getElementById('mermaid-empty');
const mermaidCopyBtn = document.getElementById('mermaid-copy-btn');
const mermaidPreviewBtn = document.getElementById('mermaid-preview-btn');
const mermaidZoomOutBtn = document.getElementById('mermaid-zoom-out-btn');
const mermaidZoomResetBtn = document.getElementById('mermaid-zoom-reset-btn');
const mermaidZoomInBtn = document.getElementById('mermaid-zoom-in-btn');
const flowGraphEl = document.getElementById('flow-graph');
const flowEmptyEl = document.getElementById('flow-empty');
const flowDetailEl = document.getElementById('flow-detail');
const flowWorkflowInfoEl = document.getElementById('flow-workflow-info');
const flowZoomOutBtn = document.getElementById('flow-zoom-out-btn');
const flowZoomResetBtn = document.getElementById('flow-zoom-reset-btn');
const flowZoomInBtn = document.getElementById('flow-zoom-in-btn');

let activeResultsTab = 'result';
let lastFlowSource = null;
let lastFlowFilePath = null;
let lastMermaidSource = null;
let lastMermaidFilePath = null;
let lastMermaidText = '';
let mermaidPreviewActive = false;
let mermaidInitialized = false;
let mermaidRenderSeq = 0;
let mermaidZoomController = null;
let mermaidPreviewRenderTimer = null;
const MERMAID_PREVIEW_DEBOUNCE_MS = 200;
/** Diagnostics from the most recent lint, shared with the flow graph markers. */
let lastDiagnostics = [];
let lastFlowDiagnostics = null;
let lastRenderedFlowSignature = '';
/** When true, the next flow render fits/resets instead of restoring pan/zoom (URL fetch, sample swap, …). */
let flowViewResetPending = false;
let flowZoomController = null;

function selectResultsTab(tab) {
  activeResultsTab = tab;
  const flowActive = tab === 'flow';
  const mermaidActive = tab === 'mermaid';
  tabResultBtn.classList.toggle('results-tab--active', tab === 'result');
  tabFlowBtn.classList.toggle('results-tab--active', flowActive);
  tabMermaidBtn.classList.toggle('results-tab--active', mermaidActive);
  tabResultBtn.setAttribute('aria-selected', String(tab === 'result'));
  tabFlowBtn.setAttribute('aria-selected', String(flowActive));
  tabMermaidBtn.setAttribute('aria-selected', String(mermaidActive));
  resultPanel.hidden = tab !== 'result';
  flowPanel.hidden = !flowActive;
  mermaidPanel.hidden = !mermaidActive;
  if (flowActive) {
    refreshFlow();
  } else if (mermaidActive) {
    refreshMermaid();
  } else {
    clearEditorFlowHighlight();
  }
}

// ─── Flow node → editor line highlight ───

/** 0-based start lines of every flow node in the current graph (for spill trimming). */
let flowNodeStartLines = new Set();
/** CodeMirror line handles currently carrying the flow highlight class. */
let flowHighlightHandles = [];

function collectFlowStartLines(workflow) {
  const set = new Set();
  const visitSteps = (steps) => {
    for (const s of steps ?? []) {
      if (s.line > 0) set.add(s.line - 1);
      visitSteps(s.steps);
    }
  };
  for (const job of workflow?.jobs ?? []) {
    if (job.line > 0) set.add(job.line - 1);
    visitSteps(job.steps);
  }
  return set;
}

/** Start lines of the node itself and its descendants (these may stay highlighted). */
function ownFlowStartLines(node) {
  const set = new Set();
  const visit = (n) => {
    if (n.line > 0) set.add(n.line - 1);
    for (const child of n.steps ?? []) visit(child);
  };
  visit(node);
  return set;
}

function clearEditorFlowHighlight() {
  for (const handle of flowHighlightHandles) {
    try {
      editor.removeLineClass(handle, 'background', 'flow-hl-line');
    } catch {
      // line was removed by an edit — nothing to clear
    }
  }
  flowHighlightHandles = [];
}

/**
 * Highlights the source lines of a clicked flow node in the editor and scrolls to them.
 * Parsed ranges can spill into the next sibling's first line, so trailing lines that
 * are the start of another (non-descendant) node are trimmed off the highlight.
 * @param {{ line?: number, endLine?: number, steps?: object[] }} node
 */
function highlightEditorLinesForFlowNode(node) {
  clearEditorFlowHighlight();
  const start = (node.line ?? 0) - 1;
  if (start < 0 || start >= editor.lineCount()) {
    return;
  }
  // A parallel step's own range covers only its header line, so extend the
  // highlight to the deepest descendant end line.
  let maxEndLine = node.endLine ?? node.line ?? 0;
  const visitEnds = (n) => {
    if ((n.endLine ?? 0) > maxEndLine) maxEndLine = n.endLine;
    for (const child of n.steps ?? []) visitEnds(child);
  };
  visitEnds(node);
  let end = Math.min(editor.lineCount() - 1, Math.max(start, maxEndLine - 1));
  const own = ownFlowStartLines(node);
  while (end > start && flowNodeStartLines.has(end) && !own.has(end)) {
    end--;
  }
  for (let i = start; i <= end; i++) {
    flowHighlightHandles.push(editor.addLineClass(i, 'background', 'flow-hl-line'));
  }
  // On the stacked mobile layout, scrolling the auto-height editor also moves the
  // whole page away from the tapped graph node. Keep the highlight, but leave the
  // page position unchanged; wide layouts retain the source-jump behavior.
  if (!globalThis.matchMedia?.('(max-width: 880px)').matches) {
    editor.scrollIntoView({ from: { line: start, ch: 0 }, to: { line: end, ch: 0 } }, 60);
  }
}

tabResultBtn.addEventListener('click', () => selectResultsTab('result'));
tabFlowBtn.addEventListener('click', () => selectResultsTab('flow'));
tabMermaidBtn.addEventListener('click', () => selectResultsTab('mermaid'));
flowZoomOutBtn.addEventListener('click', () => flowZoomController?.zoomOut());
flowZoomResetBtn.addEventListener('click', () => flowZoomController?.reset());
flowZoomInBtn.addEventListener('click', () => flowZoomController?.zoomIn());
mermaidZoomOutBtn.addEventListener('click', () => mermaidZoomController?.zoomOut());
mermaidZoomResetBtn.addEventListener('click', () => mermaidZoomController?.reset());
mermaidZoomInBtn.addEventListener('click', () => mermaidZoomController?.zoomIn());

function setFlowZoomController(controller) {
  if (flowZoomController !== controller) {
    flowZoomController?.dispose();
  }
  flowZoomController = controller;
  const disabled = controller === null;
  flowZoomOutBtn.disabled = disabled;
  flowZoomResetBtn.disabled = disabled;
  flowZoomInBtn.disabled = disabled;
}

function setMermaidZoomController(controller) {
  if (mermaidZoomController !== controller) {
    mermaidZoomController?.dispose();
  }
  mermaidZoomController = controller;
  const disabled = controller === null;
  mermaidZoomOutBtn.disabled = disabled;
  mermaidZoomResetBtn.disabled = disabled;
  mermaidZoomInBtn.disabled = disabled;
}

/**
 * Fetches flow-json from the WASM backend and re-renders the graph.
 * Skipped when source + filePath are unchanged since the last render.
 * @param {boolean} [force]
 */
function refreshFlow(force = false) {
  if (!runtimeAlive || !runtimeReady || !exports) {
    return;
  }
  const source = editor.getValue();
  const filePath = getSelectedFilePath();
  if (!force
    && source === lastFlowSource
    && filePath === lastFlowFilePath
    && lastFlowDiagnostics === lastDiagnostics) {
    return;
  }
  try {
    const utf8Bytes = exports.Seiton.Playground.LintInterop.GetFlowJson(source, filePath);
    const flowDoc = JSON.parse(utf8Decoder.decode(utf8Bytes));
    renderFlow(flowDoc, { preserveView: true });
    if (flowDoc?.error || !globalThis.d3) {
      return;
    }
    lastFlowSource = source;
    lastFlowFilePath = filePath;
    lastFlowDiagnostics = lastDiagnostics;
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    showToast(err?.message ?? String(err), 'error');
  }
}

/** True when Mermaid text has no job nodes or subgraphs (empty workflow or action.yml). */
function isMermaidEmpty(mermaidText) {
  if (!mermaidText) {
    return true;
  }
  if (mermaidText.startsWith('%% Seiton error:')) {
    return false;
  }
  return !/^\s*(?:subgraph\s+)?(?:w\d+)?j\d+\b/m.test(mermaidText);
}

function mermaidThemeName() {
  return effectiveUiIsDark() ? 'dark' : 'default';
}

function ensureMermaidInitialized() {
  const m = globalThis.mermaid;
  if (!m) {
    return null;
  }
  if (!mermaidInitialized) {
    m.initialize({
      startOnLoad: false,
      theme: mermaidThemeName(),
      securityLevel: 'strict',
      flowchart: { htmlLabels: false },
    });
    mermaidInitialized = true;
  }
  return m;
}

function syncMermaidTheme() {
  const m = globalThis.mermaid;
  if (!m || !mermaidInitialized) {
    return;
  }
  m.initialize({
    startOnLoad: false,
    theme: mermaidThemeName(),
    securityLevel: 'strict',
    flowchart: { htmlLabels: false },
  });
}

function setMermaidToolbarEnabled(enabled) {
  mermaidCopyBtn.disabled = !enabled;
  mermaidPreviewBtn.disabled = !enabled;
}

function mermaidFitScale(bounds, viewport) {
  const pad = 24;
  return Math.max(
    0.01,
    Math.min(
      1,
      Math.max(1, viewport.width - pad * 2) / bounds.width,
      Math.max(1, viewport.height - pad * 2) / bounds.height,
    ),
  );
}

function fitMermaidPreview(d3, svg, zoom, bounds, viewport) {
  const fitScale = mermaidFitScale(bounds, viewport);
  const tx = (viewport.width - bounds.width * fitScale) / 2 - bounds.x * fitScale;
  const ty = (viewport.height - bounds.height * fitScale) / 2 - bounds.y * fitScale;
  const transform = d3.zoomIdentity.translate(tx, ty).scale(fitScale);
  svg.call(zoom.transform, transform);
  return transform;
}

/**
 * Adds drag, wheel, and pinch navigation to a rendered Mermaid SVG.
 * The minimum scale is the fit-to-view scale, so zooming out never makes the
 * complete diagram smaller than its initial fitted view.
 */
function wireMermaidPreviewZoom(svgElement) {
  const d3 = globalThis.d3;
  const viewBox = svgElement?.viewBox?.baseVal;
  if (!d3 || !viewBox || viewBox.width <= 0 || viewBox.height <= 0) {
    return null;
  }

  const bounds = {
    x: viewBox.x,
    y: viewBox.y,
    width: viewBox.width,
    height: viewBox.height,
  };
  const graphRoots = Array.from(svgElement.children)
    .filter((child) => child.localName === 'g');
  if (graphRoots.length === 0) {
    return null;
  }

  const wrapper = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  wrapper.classList.add('mermaid-preview__viewport');
  svgElement.insertBefore(wrapper, graphRoots[0]);
  for (const root of graphRoots) {
    wrapper.appendChild(root);
  }

  const svg = d3.select(svgElement);
  const viewportLayer = d3.select(wrapper);
  let viewport = { width: 1, height: 1 };
  let fitScale = 1;

  const zoom = d3.zoom()
    .on('zoom', (event) => {
      viewportLayer.attr('transform', event.transform);
    });

  function measureViewport() {
    viewport = {
      width: Math.max(1, mermaidPreviewEl.clientWidth),
      height: Math.max(1, mermaidPreviewEl.clientHeight),
    };
    fitScale = mermaidFitScale(bounds, viewport);
    svg
      .attr('viewBox', `0 0 ${viewport.width} ${viewport.height}`)
      .attr('width', viewport.width)
      .attr('height', viewport.height);
    zoom
      .extent([[0, 0], [viewport.width, viewport.height]])
      .translateExtent([
        [bounds.x - 24, bounds.y - 24],
        [bounds.x + bounds.width + 24, bounds.y + bounds.height + 24],
      ])
      .scaleExtent([fitScale, Math.max(8, fitScale * 8)]);
  }

  function reset() {
    measureViewport();
    fitMermaidPreview(d3, svg, zoom, bounds, viewport);
  }

  svgElement.style.maxWidth = 'none';
  svgElement.style.width = '100%';
  svgElement.style.height = '100%';
  mermaidPreviewEl.classList.add('mermaid-preview--zoomable');
  measureViewport();
  svg.call(zoom);
  fitMermaidPreview(d3, svg, zoom, bounds, viewport);

  let resizeFrame = null;
  const resizeObserver = typeof ResizeObserver === 'function'
    ? new ResizeObserver(() => {
      if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
      resizeFrame = requestAnimationFrame(() => {
        resizeFrame = null;
        reset();
      });
    })
    : null;
  resizeObserver?.observe(mermaidPreviewEl);

  return {
    zoomOut: () => svg.transition().duration(160).call(zoom.scaleBy, 0.8),
    reset: () => reset(),
    zoomIn: () => svg.transition().duration(160).call(zoom.scaleBy, 1.25),
    dispose: () => {
      resizeObserver?.disconnect();
      if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
      svg.on('.zoom', null);
      mermaidPreviewEl.classList.remove('mermaid-preview--zoomable');
    },
  };
}

function setMermaidPreviewMode(preview) {
  mermaidPreviewActive = preview;
  mermaidPreviewBtn.textContent = preview ? 'Source' : 'Preview';
  mermaidPreviewBtn.title = preview ? 'Show source text' : 'Preview rendered diagram';
  mermaidPreviewBtn.setAttribute(
    'aria-label',
    preview ? 'Show Mermaid source' : 'Preview Mermaid diagram',
  );
  mermaidOutputEl.hidden = preview;
  mermaidPreviewEl.hidden = !preview;
  if (preview) {
    scheduleMermaidPreviewRender();
  } else {
    mermaidRenderSeq++;
    setMermaidZoomController(null);
    mermaidPreviewEl.replaceChildren();
  }
}

async function renderMermaidPreviewSvg() {
  const m = ensureMermaidInitialized();
  const renderToken = ++mermaidRenderSeq;
  setMermaidZoomController(null);
  mermaidPreviewEl.replaceChildren();
  if (!m || !lastMermaidText) {
    const msg = document.createElement('p');
    msg.className = 'notification';
    msg.textContent = m
      ? 'Mermaid preview unavailable.'
      : 'Mermaid preview unavailable: mermaid.js failed to load.';
    mermaidPreviewEl.appendChild(msg);
    return;
  }
  syncMermaidTheme();
  const id = `seiton-mermaid-${renderToken}`;
  try {
    const { svg, bindFunctions } = await m.render(id, lastMermaidText);
    if (!mermaidPreviewActive || renderToken !== mermaidRenderSeq) {
      return;
    }
    mermaidPreviewEl.innerHTML = svg;
    bindFunctions?.(mermaidPreviewEl);
    setMermaidZoomController(
      wireMermaidPreviewZoom(mermaidPreviewEl.querySelector('svg')),
    );
  } catch (err) {
    if (!mermaidPreviewActive || renderToken !== mermaidRenderSeq) {
      return;
    }
    const msg = document.createElement('p');
    msg.className = 'notification';
    msg.textContent = `Mermaid preview failed: ${err?.message ?? err}`;
    mermaidPreviewEl.appendChild(msg);
  }
}

function scheduleMermaidPreviewRender() {
  if (mermaidPreviewRenderTimer !== null) {
    clearTimeout(mermaidPreviewRenderTimer);
  }
  mermaidPreviewRenderTimer = setTimeout(() => {
    mermaidPreviewRenderTimer = null;
    if (mermaidPreviewActive && activeResultsTab === 'mermaid') {
      void renderMermaidPreviewSvg();
    }
  }, MERMAID_PREVIEW_DEBOUNCE_MS);
}

function refreshMermaidPreviewOnThemeChange() {
  if (mermaidPreviewActive && activeResultsTab === 'mermaid' && lastMermaidText) {
    scheduleMermaidPreviewRender();
  }
}

/**
 * Fetches flow-mermaid from the WASM backend and updates the Mermaid panel.
 * @param {boolean} [force]
 */
function refreshMermaid(force = false) {
  if (!runtimeAlive || !runtimeReady || !exports) {
    return;
  }
  const source = editor.getValue();
  const filePath = getSelectedFilePath();
  if (!force && source === lastMermaidSource && filePath === lastMermaidFilePath) {
    return;
  }
  try {
    const utf8Bytes = exports.Seiton.Playground.LintInterop.GetFlowMermaid(source, filePath);
    const mermaidText = utf8Decoder.decode(utf8Bytes);
    renderMermaid(mermaidText);
    if (!mermaidText.startsWith('%% Seiton error:')) {
      lastMermaidSource = source;
      lastMermaidFilePath = filePath;
    }
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return;
    }
    showToast(err?.message ?? String(err), 'error');
  }
}

/**
 * Renders Mermaid flowchart text in the output panel.
 * @param {string} mermaidText
 */
function renderMermaid(mermaidText) {
  lastMermaidText = mermaidText ?? '';
  if (mermaidText.startsWith('%% Seiton error:')) {
    setMermaidPreviewMode(false);
    mermaidOutputEl.textContent = mermaidText;
    mermaidOutputEl.hidden = false;
    mermaidPreviewEl.hidden = true;
    mermaidEmptyEl.hidden = true;
    setMermaidToolbarEnabled(false);
    return;
  }
  if (isMermaidEmpty(mermaidText)) {
    setMermaidPreviewMode(false);
    mermaidOutputEl.hidden = true;
    mermaidPreviewEl.hidden = true;
    mermaidEmptyEl.hidden = false;
    mermaidEmptyEl.textContent = 'No workflow structure to export.';
    setMermaidToolbarEnabled(false);
    return;
  }
  mermaidOutputEl.textContent = mermaidText;
  mermaidEmptyEl.hidden = true;
  setMermaidToolbarEnabled(true);
  if (mermaidPreviewActive) {
    mermaidOutputEl.hidden = true;
    mermaidPreviewEl.hidden = false;
    scheduleMermaidPreviewRender();
  } else {
    mermaidOutputEl.hidden = false;
    mermaidPreviewEl.hidden = true;
  }
}

mermaidPreviewBtn.addEventListener('click', () => {
  if (mermaidPreviewBtn.disabled) {
    return;
  }
  setMermaidPreviewMode(!mermaidPreviewActive);
});

mermaidCopyBtn.addEventListener('click', async () => {
  if (!lastMermaidText || mermaidCopyBtn.disabled) {
    return;
  }
  const fenced = `\`\`\`mermaid\n${lastMermaidText.trimEnd()}\n\`\`\``;
  const copied = await copyTextToClipboard(fenced);
  showToast(copied ? 'Mermaid copied to clipboard' : 'Copy failed — select the text manually', copied ? 'success' : 'error');
});

/**
 * Renders a flow-json document into the flow panel (graph, empty notice, detail reset).
 * @param {{ version: number, workflows: object[], error?: string }} flowDoc
 * @param {{ preserveView?: boolean, resetView?: boolean }} [options]
 */
function renderFlow(flowDoc, { preserveView = false, resetView = false } = {}) {
  if (resetView) {
    flowViewResetPending = true;
  }
  hideFlowDetail();
  const workflow = flowDoc?.workflows?.[0] ?? null;
  const signature = flowStructureSignature(workflow);
  const pendingViewReset = flowViewResetPending;
  const diagOnly = Boolean(
    signature
    && signature === lastRenderedFlowSignature
    && flowGraphEl.querySelector('.flow-svg')
    && !pendingViewReset,
  );
  const shouldPreserveView = preserveView
    && !pendingViewReset
    && flowGraphEl.querySelector('.flow-svg');
  const initialView = shouldPreserveView ? captureFlowViewState(flowGraphEl) : null;
  if (pendingViewReset) {
    flowViewResetPending = false;
  }
  flowNodeStartLines = collectFlowStartLines(workflow);
  if (diagOnly) {
    updateFlowGraphDiagnostics(flowGraphEl, workflow, lastDiagnostics);
    renderWorkflowInfo(workflow);
    flowEmptyEl.hidden = true;
    flowGraphEl.hidden = false;
    return;
  }

  setFlowZoomController(null);
  const rendered = renderFlowGraph(flowGraphEl, workflow, {
    onSelect: showFlowDetail,
    diagnostics: lastDiagnostics,
    onZoomReady: setFlowZoomController,
    initialView,
  });
  if (rendered) {
    lastRenderedFlowSignature = signature;
  } else {
    lastRenderedFlowSignature = '';
  }
  renderWorkflowInfo(rendered ? workflow : null);
  flowEmptyEl.hidden = rendered;
  flowGraphEl.hidden = !rendered;
  if (!rendered) {
    flowEmptyEl.textContent = flowDoc?.error
      ? `Flow rendering failed: ${flowDoc.error}`
      : globalThis.d3
        ? 'No workflow structure to visualize.'
        : 'Flow graph unavailable: D3.js failed to load.';
  }
}

/**
 * Renders the workflow context strip above the graph: one chip per trigger event.
 * The schedule chip and the concurrency chip open the detail panel on click,
 * consistent with clicking a job/step node.
 * @param {object|null} workflow
 */
function renderWorkflowInfo(workflow) {
  flowWorkflowInfoEl.replaceChildren();
  if (!workflow) {
    flowWorkflowInfoEl.hidden = true;
    return;
  }

  const addChip = (text, onClick) => {
    const chip = document.createElement(onClick ? 'button' : 'span');
    chip.className = onClick
      ? 'flow-workflow-info__chip flow-workflow-info__chip--clickable'
      : 'flow-workflow-info__chip';
    chip.textContent = text;
    if (onClick) {
      chip.type = 'button';
      chip.addEventListener('click', onClick);
    }
    flowWorkflowInfoEl.appendChild(chip);
  };

  const schedules = workflow.schedules ?? [];
  for (const eventName of workflow.on ?? []) {
    if (eventName === 'schedule' && schedules.length > 0) {
      addChip(`on: schedule (${schedules.length})`, () =>
        showFlowContextDetail(
          'on: schedule',
          schedules.map((s, i) => [
            `cron ${i + 1}`,
            `${s.cron}${s.timezone ? ` (${s.timezone})` : ' (UTC)'}`,
          ]),
        ));
    } else {
      addChip(`on: ${eventName}`, null);
    }
  }

  const concurrency = workflow.concurrency;
  if (concurrency) {
    addChip(`concurrency${concurrency.cancelInProgress ? ' ⛔' : ''}`, () =>
      showFlowContextDetail('concurrency', [
        ['group', concurrency.group],
        ['cancel-in-progress', concurrency.cancelInProgress ? 'true' : 'false'],
        ['queue', concurrency.queue],
      ]));
  }

  flowWorkflowInfoEl.hidden = flowWorkflowInfoEl.childElementCount === 0;
}

/**
 * Shows workflow-context details (schedule crons, concurrency) in the detail panel.
 * @param {string} titleText
 * @param {Array<[string, string | null | undefined]>} entries
 */
function showFlowContextDetail(titleText, entries) {
  clearEditorFlowHighlight();
  flowDetailEl.replaceChildren();
  const title = document.createElement('div');
  title.className = 'flow-detail__title';
  title.textContent = titleText;
  const dl = document.createElement('dl');
  dl.className = 'flow-detail__list';
  for (const [label, value] of entries) {
    if (value === null || value === undefined || value === '') {
      continue;
    }
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    dd.textContent = value;
    dl.append(dt, dd);
  }
  flowDetailEl.append(title, dl);
  flowDetailEl.hidden = false;
}

/** Shows job/step details for a clicked graph node and highlights its editor lines. */
function showFlowDetail(info) {
  highlightEditorLinesForFlowNode(info.data);
  flowDetailEl.replaceChildren();
  const title = document.createElement('div');
  title.className = 'flow-detail__title';
  const dl = document.createElement('dl');
  dl.className = 'flow-detail__list';
  const add = (label, value) => {
    if (value === null || value === undefined || value === '' || (Array.isArray(value) && value.length === 0)) {
      return;
    }
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    dd.textContent = Array.isArray(value) ? value.join(', ') : String(value);
    dl.append(dt, dd);
  };
  if (info.type === 'job') {
    const job = info.data;
    title.textContent = `job: ${job.id}`;
    add('name', job.name);
    add('kind', job.kind);
    add('if', job.if);
    add('needs', job.needs);
    add('runs-on', job.runsOn);
    add('timeout-minutes', job.timeoutMinutes);
    if (Array.isArray(job.permissions)) {
      add('permissions', job.permissions.length === 0 ? '{} (deny all)' : job.permissions);
    }
    add('environment', job.environment);
    add('uses', job.uses);
    if (job.strategy?.hasMatrix) {
      add('matrix', job.strategy.matrixIsExpression ? '${{ … }} (dynamic)' : job.strategy.matrixKeys);
      const combinations = job.strategy.combinations ?? [];
      if (combinations.length > 0) {
        add(
          `variants (${combinations.length})`,
          combinations
            .map((c) => Object.entries(c).map(([k, v]) => `${k}=${v}`).join(', '))
            .join('\n'),
        );
      }
    }
  } else {
    const step = info.data;
    title.textContent = `step: ${step.name ?? step.id ?? step.kind}`;
    add('kind', step.kind);
    add('id', step.id);
    add('if', step.if);
    if (step.background) {
      // backgroundOutcome is part of the flow-json contract (computed by Seiton.Core).
      add(
        'background',
        step.backgroundOutcome === 'awaited'
          ? 'true (a later wait / wait-all waits for this step)'
          : step.backgroundOutcome === 'cancelled'
            ? 'true (cancelled by a later cancel step)'
            : 'true (later steps do not wait for this step)',
      );
    }
    add('timeout-minutes', step.timeoutMinutes);
    if (step.continueOnError) {
      add('continue-on-error', 'true');
    }
    add('run', step.run);
    add('working-directory', step.workingDirectory);
    add('uses', step.uses);
    if (step.with) {
      add(
        'with',
        Object.entries(step.with)
          .map(([key, value]) => `${key}: ${value}`)
          .join('\n'),
      );
    }
    add('wait targets', step.targets);
    add('cancel target', step.target);
  }
  const nodeDiags = info.diagnostics ?? [];
  if (nodeDiags.length > 0) {
    add(
      `diagnostics (${nodeDiags.length})`,
      nodeDiags
        .map((d) => `[${(d.severity ?? 'Info')}] L${d.line}: ${d.message}`)
        .join('\n'),
    );
  }
  flowDetailEl.append(title, dl);
  flowDetailEl.hidden = false;
}

function hideFlowDetail() {
  clearEditorFlowHighlight();
  flowDetailEl.hidden = true;
  flowDetailEl.replaceChildren();
}

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
  flowViewResetPending = true;
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
  flowViewResetPending = true;
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

/**
 * @param {string} hashSegment
 * @returns {string}
 */
function buildShareUrlFromHash(hashSegment) {
  return `${location.pathname}${location.search}#${hashSegment}`;
}

/**
 * @param {boolean} copied
 * @param {'full'|'yaml-only'} [mode]
 */
function finishShareCopy(copied, mode = 'full') {
  if (mode === 'yaml-only') {
    const msg = copied
      ? permalinkYamlOnlyCopied
      : `${permalinkDoneNoClipboard} (workflow YAML only — config omitted because URL was too long)`;
    permalinkBtn.title = msg;
    permalinkBtn.setAttribute('aria-label', msg);
    showToast(msg, copied ? 'success' : 'info');
    window.setTimeout(() => {
      permalinkBtn.title = permalinkShareTitle;
      permalinkBtn.setAttribute('aria-label', permalinkShareTitle);
    }, 3200);
    return;
  }
  schedulePermalinkFeedback(copied);
}

/**
 * @param {string} text
 * @returns {Promise<boolean>}
 */
async function copyTextToClipboard(text) {
  if (tryClipboardCopyViaTextArea(text)) {
    return true;
  }
  const w = navigator.clipboard?.writeText;
  if (!w) {
    return false;
  }
  try {
    await w.call(navigator.clipboard, text);
    return true;
  } catch {
    return false;
  }
}

permalinkBtn.addEventListener('click', async () => {
  try {
    const shareState = {
      yaml: editor.getValue(),
      config: configEditor.getValue(),
      filePath: getSelectedFilePath(),
    };
    let hash = encodeShareState(shareState);
    let shareMode = 'full';
    let url = buildShareUrlFromHash(hash);
    let fullUrl = `${location.origin}${url}`;

    if (!isShareWithinLimits(hash, fullUrl)) {
      hash = encodeYamlOnlyShare(shareState.yaml, shareState.filePath);
      shareMode = 'yaml-only';
      url = buildShareUrlFromHash(hash);
      fullUrl = `${location.origin}${url}`;
    }

    if (!isShareWithinLimits(hash, fullUrl)) {
      const bundle = formatClipboardBundle(
        shareState.yaml,
        shareState.config,
        shareState.filePath,
      );
      const copied = await copyTextToClipboard(bundle);
      showToast(
        copied
          ? 'URL too long for sharing. Workflow and config copied to clipboard instead.'
          : 'URL too long for sharing. Copy workflow and config manually from the editors.',
        copied ? 'success' : 'info',
        8000,
      );
      return;
    }

    history.replaceState(null, '', url);
    fullUrl = location.href;
    const copied = await copyTextToClipboard(fullUrl);
    finishShareCopy(copied, shareMode);
  } catch (e) {
    showToast(e?.message ?? String(e), 'error');
  }
});

applyFixesBtn.addEventListener('click', async () => {
  if (!runtimeAlive || !runtimeReady || !exports) return;
  const original = editor.getValue();
  const filePath = getSelectedFilePath();

  // Disable button and show busy state
  applyFixesBtn.disabled = true;
  const originalText = applyFixesBtn.textContent;
  applyFixesBtn.textContent = 'Fixing\u2026';

  try {
    const jsonStr = await exports.Seiton.Playground.LintInterop.ApplyAllFixesWithNetworkAsync(
      original,
      filePath,
    );
    const result = JSON.parse(jsonStr);
    const yaml = result.yaml;

    if (yaml === original) {
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

    // Show toast with resolution stats if network fixes were attempted
    if (result.resolved > 0 || result.failed > 0) {
      const parts = [];
      if (result.resolved > 0) parts.push(`${result.resolved} pinned`);
      if (result.failed > 0) parts.push(`${result.failed} failed`);
      if (result.skipped > 0) parts.push(`${result.skipped} skipped`);
      showToast(`Fixes applied. Network: ${parts.join(', ')}.`, result.failed > 0 ? 'error' : 'success');
    }
  } catch (e) {
    if (isRuntimeDeadError(e)) {
      handleRuntimeDeath();
      return;
    }
    showToast(e?.message ?? String(e), 'error');
  } finally {
    applyFixesBtn.disabled = false;
    applyFixesBtn.textContent = originalText;
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
    flowViewResetPending = true;
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

/** Typical https? URLs in prose (excluding spaces and angle brackets / parens in path edge cases). */
const URL_SPLIT_RE = /https?:\/\/[^\s<>()]+/gi;

/**
 * Cheap pre-filter before layout measurement (see {@link maybeAttachDiagMessageToggle}).
 * @param {string} [text]
 * @returns {boolean}
 */
function shouldCollapseDiagMessage(text) {
  const s = String(text ?? '');
  if (s.length >= DIAG_MESSAGE_COLLAPSE_MIN_CHARS) {
    return true;
  }
  let lines = 1;
  for (let i = 0; i < s.length; i++) {
    if (s.charCodeAt(i) === 10) {
      lines += 1;
      if (lines > DIAG_MESSAGE_COLLAPSED_LINES) {
        return true;
      }
    }
  }
  return false;
}

/**
 * Counts rendered lines for wrapped inline content (text + links).
 * @param {HTMLElement} msgEl
 * @returns {number}
 */
function countRenderedDiagMessageLines(msgEl) {
  const range = document.createRange();
  range.selectNodeContents(msgEl);
  const rects = range.getClientRects();
  if (rects.length === 0) {
    return 0;
  }
  /** @type {number[]} */
  const tops = [];
  for (const rect of rects) {
    const top = Math.round(rect.top);
    if (!tops.some((t) => Math.abs(t - top) <= 1)) {
      tops.push(top);
    }
  }
  return tops.length;
}

/**
 * Adds Show more/less only when line-clamp actually hides content (avoids no-op toggles).
 * @param {HTMLElement} wrap
 * @param {HTMLElement} msgEl
 */
function maybeAttachDiagMessageToggle(wrap, msgEl) {
  if (!shouldCollapseDiagMessage(msgEl.textContent)) {
    return;
  }

  const attachIfNeeded = () => {
    const renderedLines = countRenderedDiagMessageLines(msgEl);
    if (renderedLines <= DIAG_MESSAGE_COLLAPSED_LINES) {
      return;
    }
    if (wrap.querySelector('.diag-message-toggle')) {
      return;
    }
    msgEl.classList.add('diag-message--collapsed');
    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'diag-message-toggle';
    toggle.textContent = 'Show more';
    toggle.setAttribute('aria-expanded', 'false');
    toggle.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const folded = msgEl.classList.toggle('diag-message--collapsed');
      toggle.setAttribute('aria-expanded', folded ? 'false' : 'true');
      toggle.textContent = folded ? 'Show more' : 'Show less';
    });
    const chips = wrap.querySelector('.diag-chips');
    if (chips) {
      wrap.insertBefore(toggle, chips);
    } else {
      wrap.appendChild(toggle);
    }
  };

  if (typeof globalThis.requestAnimationFrame === 'function') {
    globalThis.requestAnimationFrame(attachIfNeeded);
  } else {
    attachIfNeeded();
  }
}

/**
 * @param {HTMLElement} cell
 * @param {string} [message]
 * @param {{ fixable?: boolean, fixDescription?: string, ruleId?: string }} diag
 */
function appendDiagnosticDescriptionCell(cell, message, diag) {
  const wrap = document.createElement('div');
  wrap.className = 'diag-desc';

  const msgEl = document.createElement('div');
  msgEl.className = 'diag-message';
  appendTextLinkifyingUrls(msgEl, message ?? '');
  wrap.appendChild(msgEl);

  if (diag.fixable || diag.ruleId) {
    const chips = document.createElement('div');
    chips.className = 'diag-chips';
    if (diag.ruleId) {
      const kindTag = document.createElement('span');
      kindTag.className = 'rule-chip';
      kindTag.textContent = diag.ruleId;
      chips.appendChild(kindTag);
    }
    if (diag.fixable) {
      const fx = document.createElement('span');
      fx.className = 'fix-chip';
      fx.title = diag.fixDescription ?? 'Included in Apply all fixes';
      fx.textContent = 'Fixable';
      chips.appendChild(fx);
    }
    wrap.appendChild(chips);
  }

  cell.appendChild(wrap);
  maybeAttachDiagMessageToggle(wrap, msgEl);
}

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

  // Staleness check: skip if content + filePath + config are identical to last successful lint.
  if (source === lastLintedSource && filePath === lastLintedFilePath && configVersion === lastConfigVersion) {
    return;
  }

  lintInProgress = true;
  lintPendingRetry = false;

  try {
    const flowActive = activeResultsTab === 'flow';
    const mermaidActive = activeResultsTab === 'mermaid';
    const utf8Bytes = flowActive
      ? exports.Seiton.Playground.LintInterop.RunLintWithFlowJson(source, filePath)
      : mermaidActive
        ? exports.Seiton.Playground.LintInterop.RunLintWithMermaid(source, filePath)
        : exports.Seiton.Playground.LintInterop.RunLint(source, filePath);
    const response = JSON.parse(utf8Decoder.decode(utf8Bytes));
    const diagnostics = flowActive || mermaidActive ? (response.diagnostics ?? response) : response;
    const flowDoc = flowActive ? (response.flow ?? { version: 1, workflows: [] }) : null;
    const mermaidText = mermaidActive ? (response.mermaid ?? '') : null;
    // Do not treat an internal-error fallback as a successful lint: if we cached
    // the staleness key here a transient C# exception would permanently block retries
    // on the same content/path until the user edits the file.
    const isInternalError = diagnostics.length === 1 && diagnostics[0].ruleId === 'internal-error';
    if (!isInternalError) {
      lastLintedSource = source;
      lastLintedFilePath = filePath;
      lastConfigVersion = configVersion;
      lastDiagnostics = diagnostics;
    }
    renderResults(diagnostics);
    if (flowActive) {
      renderFlow(flowDoc, { preserveView: true });
      lastFlowSource = source;
      lastFlowFilePath = filePath;
      lastFlowDiagnostics = lastDiagnostics;
    } else if (mermaidActive) {
      renderMermaid(mermaidText);
      lastMermaidSource = source;
      lastMermaidFilePath = filePath;
    }
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

/**
 * Sends config YAML to the WASM SetConfig export. On success, increments configVersion
 * and triggers re-lint. Returns parsed diagnostics array (empty on success).
 * @param {string} configYaml
 * @returns {Array} config diagnostics (empty array on success)
 */
function setConfig(configYaml) {
  if (!runtimeAlive || !runtimeReady || !exports) {
    return [];
  }
  try {
    const utf8Bytes = exports.Seiton.Playground.LintInterop.SetConfig(configYaml);
    const json = utf8Decoder.decode(utf8Bytes);
    const diagnostics = JSON.parse(json);
    if (diagnostics.length === 0) {
      // Success: config updated, invalidate staleness and trigger re-lint
      configVersion++;
      runLint();
    }
    return diagnostics;
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
      return [];
    }
    showToast(err?.message ?? String(err), 'error');
    return [];
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
    row.dataset.severity = (diag.severity || 'error').toLowerCase();
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

    const sevCell = document.createElement('td');
    const sevTag = document.createElement('span');
    sevTag.className = `severity-chip severity-chip--${(diag.severity || 'error').toLowerCase()}`;
    sevTag.textContent = diag.severity || 'Error';
    sevCell.appendChild(sevTag);
    row.appendChild(sevCell);

    const descCell = document.createElement('td');
    appendDiagnosticDescriptionCell(descCell, diag.message ?? '', diag);
    row.appendChild(descCell);

    resultBody.appendChild(row);

    const lineIndex = Math.max(0, (diag.line ?? 1) - 1);
    const marker = document.createElement('div');
    marker.className =
      diag.severity === 'Error'
        ? 'gutter-marker gutter-marker--error'
        : diag.severity === 'Info'
          ? 'gutter-marker gutter-marker--info'
          : 'gutter-marker gutter-marker--warning';
    marker.textContent = '●';
    editor.setGutterMarker(lineIndex, 'error-marker', marker);
  }
}

function getSelectedFilePath() {
  return fileSelect ? fileSelect.value : '.github/workflows/test.yml';
}

/**
 * Runs lint without updating editor UI. Used by Playwright when <c>?seitonTestHooks=1</c>.
 * @param {string} source
 * @param {string} [filePath]
 * @returns {{ ok: boolean, error?: string, diagnostics?: unknown[], internalError?: boolean }}
 */
function runLintForTest(source, filePath) {
  if (!runtimeAlive || !runtimeReady || !exports) {
    return { ok: false, error: 'runtime not ready' };
  }
  try {
    const path = filePath ?? getSelectedFilePath();
    const utf8Bytes = exports.Seiton.Playground.LintInterop.RunLint(source, path);
    const json = utf8Decoder.decode(utf8Bytes);
    const diagnostics = JSON.parse(json);
    const internalError =
      diagnostics.length === 1 && diagnostics[0].ruleId === 'internal-error';
    return { ok: true, diagnostics, internalError };
  } catch (err) {
    if (isRuntimeDeadError(err)) {
      handleRuntimeDeath();
    }
    return { ok: false, error: String(err?.message ?? err) };
  }
}

/**
 * Exposes lint/config entry points for browser tests (<c>?seitonTestHooks=1</c> only).
 */
function installTestHooksIfRequested() {
  try {
    const params = new URLSearchParams(globalThis.location?.search ?? '');
    if (params.get('seitonTestHooks') !== '1') {
      return;
    }
    globalThis.__SEITON_PLAYGROUND_TEST__ = {
      runLint: (source, filePath) => runLintForTest(source, filePath),
      setConfig: (configYaml) => {
        const diags = setConfig(configYaml ?? '');
        return { diagnostics: diags };
      },
      renderDiagnostics: (diagnostics) => renderResults(diagnostics ?? []),
      shouldCollapseDiagMessage,
      getRuntimeAlive: () => runtimeAlive,
      getRuntimeFlags: () => exports?.Seiton?.Playground?.LintInterop?.GetRuntimeFlags?.() ?? '',
      getFlow: (source, filePath) => {
        try {
          const utf8Bytes = exports.Seiton.Playground.LintInterop.GetFlowJson(
            source ?? '',
            filePath ?? getSelectedFilePath(),
          );
          return { ok: true, flow: JSON.parse(utf8Decoder.decode(utf8Bytes)) };
        } catch (err) {
          return { ok: false, error: String(err?.message ?? err) };
        }
      },
      getMermaid: (source, filePath) => {
        try {
          const utf8Bytes = exports.Seiton.Playground.LintInterop.GetFlowMermaid(
            source ?? '',
            filePath ?? getSelectedFilePath(),
          );
          return { ok: true, mermaid: utf8Decoder.decode(utf8Bytes) };
        } catch (err) {
          return { ok: false, error: String(err?.message ?? err) };
        }
      },
      selectResultsTab: (tab) => {
        if (tab === 'flow' || tab === 'mermaid') {
          selectResultsTab(tab);
        } else {
          selectResultsTab('result');
        }
      },
      renderMermaid: (mermaidText) => renderMermaid(mermaidText ?? ''),
      setMermaidPreviewMode: (preview) => setMermaidPreviewMode(Boolean(preview)),
      isMermaidPreviewActive: () => mermaidPreviewActive,
      renderFlow: (flowDoc) => renderFlow(flowDoc ?? { version: 1, workflows: [] }),
      resetFlowView: () => {
        flowViewResetPending = true;
      },
      renderFlowWithDiagnostics: (flowDoc, diagnostics, options) => {
        lastDiagnostics = diagnostics ?? [];
        renderFlow(flowDoc ?? { version: 1, workflows: [] }, { preserveView: true, ...options });
      },
    };
  } catch {
    /* ignore */
  }
}

void initializeRuntime();

function applyShareConfigAfterRuntimeReady() {
  const cfg = shareFromUrl.state?.config ?? '';
  if (!cfg.length) {
    return;
  }
  if (configPanel.classList.contains('config-panel--collapsed')) {
    configPanel.classList.remove('config-panel--collapsed');
    configToggleBtn.setAttribute('aria-expanded', 'true');
    configEditorWrap.hidden = false;
  }
  // Config editor is pre-seeded from URL at construction time; only apply to WASM here.
  const diags = setConfig(cfg);
  renderConfigDiagnostics(diags);
}

async function initializeRuntime() {
  try {
    if (shareFromUrl.error) {
      showToast(
        `Could not restore from URL: ${shareFromUrl.error}. Showing default sample.`,
        'info',
        6000,
      );
    }

    const runtime = await dotnet
      .withApplicationArguments('playground')
      .create();
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    await runtime.runMain();
    runtimeReady = true;
    installTestHooksIfRequested();
    syncVersionBadge();
    loading.style.display = 'none';
    applyShareConfigAfterRuntimeReady();
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
