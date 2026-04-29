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

const { getAssemblyExports, getConfig, runMain } = await dotnet
    .withApplicationArguments('playground')
    .create();

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
await runMain();

const loading = document.getElementById('loading');
const resultTable = document.getElementById('lint-result');
const resultBody = document.getElementById('lint-result-body');
const successMsg = document.getElementById('success-msg');
const errorMsg = document.getElementById('error-msg');
const fileSelect = document.getElementById('filetype-select');
const sampleSelect = document.getElementById('sample-select');
const permalinkBtn = document.getElementById('permalink-btn');
const applyFixesBtn = document.getElementById('apply-fixes-btn');
const urlInput = document.getElementById('url-input');
const fetchBtn = document.getElementById('fetch-btn');

const editor = CodeMirror(document.getElementById('editor'), {
    mode: 'yaml',
    theme: 'material-darker',
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

const DEBOUNCE_MS = 300;
let debounceId = null;
/** Coalesce refreshes while typing so measurements track height for layout (page scroll). */
let sizingRaf = null;

editor.on('change', (_cm, changeObj) => {
    if (sizingRaf === null) {
        sizingRaf = requestAnimationFrame(() => {
            sizingRaf = null;
            editor.refresh();
        });
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
    runLint();
});

permalinkBtn.addEventListener('click', () => {
    try {
        const src = new TextEncoder().encode(editor.getValue());
        const compressed = deflate(src, { level: 9 });
        const b64 = uint8ToBase64(compressed);
        const url = `${location.pathname}${location.search}#${b64}`;
        history.replaceState(null, '', url);
        permalinkBtn.textContent = 'URL updated';
        setTimeout(() => { permalinkBtn.textContent = 'Permalink'; }, 1400);
    } catch (e) {
        showError(e?.message ?? String(e));
    }
});

applyFixesBtn.addEventListener('click', () => {
    try {
        const yaml = exports.Seiton.Playground.LintInterop.ApplyAllFixes(
            editor.getValue(),
            getSelectedFilePath(),
        );
        editor.setValue(yaml);
        editor.refresh();
        applyFixesBtn.hidden = true;
        runLint();
    } catch (e) {
        showError(e?.message ?? String(e));
    }
});

fetchBtn.addEventListener('click', () => fetchAndLint());
urlInput.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter') {
        fetchAndLint();
    }
});

async function fetchAndLint() {
    const raw = urlInput?.value?.trim() ?? '';
    if (!raw) {
        showError('Paste a URL first.');
        return;
    }
    clearError();
    fetchBtn.disabled = true;
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
        runLint();
    } catch (e) {
        showError(e?.message ?? String(e));
    } finally {
        fetchBtn.disabled = false;
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

function runLint() {
    const source = editor.getValue();
    const filePath = getSelectedFilePath();

    try {
        const json = exports.Seiton.Playground.LintInterop.RunLint(source, filePath);
        const diagnostics = JSON.parse(json);
        renderResults(diagnostics);
        clearError();
    } catch (err) {
        showError(err?.message ?? String(err));
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
        marker.style.color = diag.severity === 'Error' ? '#ff5370' : '#ffcb6b';
        marker.textContent = '●';
        editor.setGutterMarker(lineIndex, 'error-marker', marker);
    }
}

function clearError() {
    errorMsg.replaceChildren();
    errorMsg.style.display = 'none';
}

function showError(message) {
    errorMsg.replaceChildren();
    appendTextLinkifyingUrls(errorMsg, message ?? '');
    errorMsg.style.display = 'block';
    successMsg.style.display = 'none';
    resultTable.hidden = true;
    applyFixesBtn.hidden = true;
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

loading.style.display = 'none';
runLint();
requestAnimationFrame(() => {
    editor.refresh();
});
