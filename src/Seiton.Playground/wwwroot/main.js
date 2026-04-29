/* global CodeMirror, pako */

import { dotnet } from './_framework/dotnet.js';

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
const permalinkBtn = document.getElementById('permalink-btn');

const editor = CodeMirror(document.getElementById('editor'), {
    mode: 'yaml',
    theme: 'material-darker',
    lineNumbers: true,
    lineWrapping: true,
    autofocus: true,
    styleActiveLine: true,
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

editor.on('change', (_cm, changeObj) => {
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

fileSelect.addEventListener('change', () => runLint());

permalinkBtn.addEventListener('click', () => {
    try {
        const src = new TextEncoder().encode(editor.getValue());
        const compressed = pako.deflate(src, { level: 9 });
        const b64 = uint8ToBase64(compressed);
        const url = `${location.pathname}${location.search}#${b64}`;
        history.replaceState(null, '', url);
        permalinkBtn.textContent = 'Copied hash';
        setTimeout(() => { permalinkBtn.textContent = 'Permalink'; }, 1200);
    } catch (e) {
        showError(e?.message ?? String(e));
    }
});

function uint8ToBase64(buf) {
    let binary = '';
    const chunk = 0x8000;
    for (let i = 0; i < buf.length; i += chunk) {
        const sub = buf.subarray(i, i + chunk);
        binary += String.fromCharCode.apply(null, sub);
    }
    return btoa(binary);
}

function runLint() {
    const source = editor.getValue();
    const filePath = getSelectedFilePath();

    try {
        const json = exports.Seiton.Playground.LintInterop.RunLint(source, filePath);
        const diagnostics = JSON.parse(json);
        renderResults(diagnostics);
    } catch (err) {
        showError(err?.message ?? String(err));
    }
}

function renderResults(diagnostics) {
    resultBody.textContent = '';
    editor.clearGutter('error-marker');
    errorMsg.style.display = 'none';

    if (diagnostics.length === 0) {
        resultTable.hidden = true;
        successMsg.style.display = 'block';
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
        descCell.appendChild(document.createTextNode(diag.message ?? ''));
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
        marker.style.color = '#ff5370';
        marker.textContent = '●';
        editor.setGutterMarker(lineIndex, 'error-marker', marker);
    }
}

function showError(message) {
    errorMsg.textContent = message;
    errorMsg.style.display = 'block';
    successMsg.style.display = 'none';
    resultTable.hidden = true;
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
            const decompressed = pako.inflate(compressed);
            return new TextDecoder().decode(decompressed);
        } catch {
            /* ignore */
        }
    }

    return `# Paste your workflow YAML to check with seiton

on:
  push:
    branches: [main]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "hello"
      - uses: actions/checkout@v4
`;
}

loading.style.display = 'none';
runLint();
