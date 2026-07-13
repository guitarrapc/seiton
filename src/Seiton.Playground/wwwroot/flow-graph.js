// Flow tab renderer: draws a flow-json workflow (jobs + needs DAG + step lists +
// parallel boundaries) as an SVG graph with d3.zoom pan/zoom and click-to-detail.
// The flow-json contract is produced by PlaygroundFlowRunner (same as
// `seiton check --format flow-json`); this module only renders, never parses YAML.

const NODE_WIDTH = 260;
const HEADER_H = 30;
const META_H = 18;
const ROW_H = 22;
const ROW_TEXT_PAD = 10;
const PARALLEL_PAD_X = 6;
const GAP_X = 80;
const GAP_Y = 28;
const BOX_BOTTOM_PAD = 8;

/**
 * Renders one workflow into `container`. Returns false when there is nothing to draw
 * (no jobs, or the d3 global is unavailable).
 * @param {HTMLElement} container
 * @param {object|null} workflow  one entry of flow-json `workflows`
 * @param {{ onSelect?: (info: {type: string, data: object}) => void }} [callbacks]
 * @returns {boolean}
 */
export function renderFlowGraph(container, workflow, { onSelect } = {}) {
  container.replaceChildren();
  const d3 = globalThis.d3;
  if (!d3 || !workflow || !Array.isArray(workflow.jobs) || workflow.jobs.length === 0) {
    return false;
  }

  const jobs = workflow.jobs;
  const layout = computeLayout(jobs);

  const svg = d3
    .select(container)
    .append('svg')
    .attr('class', 'flow-svg')
    .attr('role', 'img')
    .attr('aria-label', 'Workflow execution flow graph');

  const viewport = svg.append('g').attr('class', 'flow-viewport');
  const edgeLayer = viewport.append('g');
  const nodeLayer = viewport.append('g');

  drawEdges(d3, edgeLayer, jobs, layout);
  for (const job of jobs) {
    drawJobNode(d3, nodeLayer, job, layout.get(job.id), onSelect);
  }

  const zoom = d3
    .zoom()
    .scaleExtent([0.2, 3])
    .on('zoom', (ev) => viewport.attr('transform', ev.transform));
  svg.call(zoom);
  fitToView(d3, svg, zoom, layout, container);
  return true;
}

/** Longest-path leveling over `needs` edges; document order within a level. */
function computeLayout(jobs) {
  const byId = new Map(jobs.map((j) => [j.id, j]));
  const levels = new Map();

  function levelOf(job, stack) {
    if (levels.has(job.id)) return levels.get(job.id);
    if (stack.has(job.id)) return 0; // cycle guard: broken workflows must not hang the UI
    stack.add(job.id);
    let level = 0;
    for (const dep of job.needs ?? []) {
      const depJob = byId.get(dep);
      if (depJob) level = Math.max(level, levelOf(depJob, stack) + 1);
    }
    stack.delete(job.id);
    levels.set(job.id, level);
    return level;
  }

  for (const job of jobs) levelOf(job, new Set());

  const columns = new Map();
  const layout = new Map();
  for (const job of jobs) {
    const level = levels.get(job.id);
    const x = level * (NODE_WIDTH + GAP_X);
    const y = columns.get(level) ?? 0;
    const rows = buildStepRows(job);
    const height = nodeHeight(job, rows);
    layout.set(job.id, { x, y, width: NODE_WIDTH, height, rows });
    columns.set(level, y + height + GAP_Y);
  }
  return layout;
}

/** Flattens steps into rows; parallel children are nested one depth level with a boundary. */
function buildStepRows(job) {
  const rows = [];
  const boundaries = [];
  appendRows(job.steps ?? [], 0, rows, boundaries);
  rows.boundaries = boundaries;
  return rows;
}

function appendRows(steps, depth, rows, boundaries) {
  for (const step of steps) {
    if (step.kind === 'parallel') {
      const start = rows.length;
      rows.push({ step, depth, parallelHeader: true });
      appendRows(step.steps ?? [], depth + 1, rows, boundaries);
      boundaries.push({ start, end: rows.length - 1, depth });
    } else {
      rows.push({ step, depth });
    }
  }
}

function jobMetaText(job) {
  if (job.kind === 'reusable') return `uses: ${job.uses ?? ''}`;
  const parts = [];
  if (job.name) parts.push(job.name);
  if (job.strategy?.hasMatrix) {
    parts.push(job.strategy.matrixIsExpression ? 'matrix: ${{ … }}' : `matrix: ${(job.strategy.matrixKeys ?? []).join(' × ')}`);
  }
  if (job.if) parts.push('if ⛊');
  return parts.join(' · ');
}

function nodeHeight(job, rows) {
  const metaH = jobMetaText(job) ? META_H : 0;
  return HEADER_H + metaH + rows.length * ROW_H + BOX_BOTTOM_PAD;
}

function drawEdges(d3, layer, jobs, layout) {
  const link = d3
    .linkHorizontal()
    .x((p) => p[0])
    .y((p) => p[1]);
  for (const job of jobs) {
    const target = layout.get(job.id);
    for (const dep of job.needs ?? []) {
      const source = layout.get(dep);
      if (!source) continue;
      layer
        .append('path')
        .attr('class', 'flow-edge')
        .attr('d', link({
          source: [source.x + source.width, source.y + source.height / 2],
          target: [target.x, target.y + target.height / 2],
        }));
    }
  }
}

function drawJobNode(d3, layer, job, pos, onSelect) {
  const g = layer
    .append('g')
    .attr('class', job.kind === 'reusable' ? 'flow-job flow-job--reusable' : 'flow-job')
    .attr('transform', `translate(${pos.x},${pos.y})`);

  g.append('rect')
    .attr('class', 'flow-job__box')
    .attr('width', pos.width)
    .attr('height', pos.height)
    .attr('rx', 8);

  const metaText = jobMetaText(job);
  const headerH = HEADER_H + (metaText ? META_H : 0);
  g.append('rect')
    .attr('class', 'flow-job__header')
    .attr('width', pos.width)
    .attr('height', headerH)
    .attr('rx', 8)
    .on('click', () => onSelect?.({ type: 'job', data: job }));

  g.append('text')
    .attr('class', 'flow-job__title')
    .attr('x', ROW_TEXT_PAD)
    .attr('y', 20)
    .text(truncate(job.kind === 'reusable' ? `⧉ ${job.id}` : job.id, 34));

  if (metaText) {
    g.append('text')
      .attr('class', 'flow-job__meta')
      .attr('x', ROW_TEXT_PAD)
      .attr('y', HEADER_H + 12)
      .text(truncate(metaText, 40));
  }

  // Parallel boundaries drawn behind their rows so simultaneous steps read as one block.
  for (const b of pos.rows.boundaries) {
    g.append('rect')
      .attr('class', 'flow-parallel-boundary')
      .attr('x', PARALLEL_PAD_X + b.depth * 10)
      .attr('y', headerH + b.start * ROW_H + 2)
      .attr('width', pos.width - 2 * PARALLEL_PAD_X - b.depth * 10)
      .attr('height', (b.end - b.start + 1) * ROW_H - 4)
      .attr('rx', 6);
  }

  pos.rows.forEach((row, i) => {
    const y = headerH + i * ROW_H;
    const rowG = g
      .append('g')
      .attr('class', 'flow-step')
      .on('click', () => onSelect?.({ type: 'step', data: row.step }));
    rowG
      .append('rect')
      .attr('class', 'flow-step__hit')
      .attr('x', 2)
      .attr('y', y)
      .attr('width', pos.width - 4)
      .attr('height', ROW_H);
    const text = rowG
      .append('text')
      .attr('class', 'flow-step__text')
      .attr('x', ROW_TEXT_PAD + row.depth * 14)
      .attr('y', y + 15);
    text
      .append('tspan')
      .attr('class', `flow-step__kind flow-step__kind--${row.step.kind}`)
      .text(kindLabel(row.step));
    text
      .append('tspan')
      .attr('dx', 6)
      .text(truncate(stepLabel(row.step), 30 - row.depth * 2));
  });
}

function kindLabel(step) {
  switch (step.kind) {
    case 'run': return 'run';
    case 'uses': return 'uses';
    case 'parallel': return '⇉ parallel';
    case 'wait': return 'wait';
    case 'wait-all': return 'wait-all';
    case 'cancel': return 'cancel';
    default: return step.kind ?? '?';
  }
}

function stepLabel(step) {
  if (step.kind === 'parallel') return `${(step.steps ?? []).length} steps`;
  if (step.kind === 'wait') return (step.targets ?? []).join(', ');
  if (step.kind === 'cancel') return step.target ?? '';
  const base = step.name ?? step.id ?? step.uses ?? firstLine(step.run) ?? '';
  return step.if ? `${base} ⛊` : base;
}

function firstLine(text) {
  if (!text) return null;
  const ix = text.indexOf('\n');
  return ix < 0 ? text : text.slice(0, ix);
}

function truncate(text, max) {
  if (!text) return '';
  return text.length <= max ? text : `${text.slice(0, Math.max(1, max - 1))}…`;
}

/** Scales and centers the viewport so the whole graph is visible initially. */
function fitToView(d3, svg, zoom, layout, container) {
  let maxX = 0;
  let maxY = 0;
  for (const pos of layout.values()) {
    maxX = Math.max(maxX, pos.x + pos.width);
    maxY = Math.max(maxY, pos.y + pos.height);
  }
  const cw = container.clientWidth || 600;
  const ch = container.clientHeight || 480;
  const pad = 20;
  const k = Math.min(1, (cw - pad * 2) / Math.max(1, maxX), (ch - pad * 2) / Math.max(1, maxY));
  const tx = (cw - maxX * k) / 2;
  const ty = pad;
  svg.call(zoom.transform, d3.zoomIdentity.translate(Math.max(pad, tx), ty).scale(k));
}
