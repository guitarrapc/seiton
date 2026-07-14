// Flow tab renderer: draws a flow-json workflow as an SVG graph.
//
// Two levels of structure are rendered:
//   1. Job DAG: `needs` edges between job boxes (columns = longest-path level).
//   2. Intra-job step flow: inside each job box the steps form their own flow —
//      the main lane runs top-to-bottom, `background: true` steps fork into side
//      lanes, `wait`/`wait-all` join those lanes back, `cancel` cuts them, and
//      `parallel` boundaries hold simultaneous children.
//
// Wheel / pinch: continuous zoom at the pointer; LOD shifts at zoom-band edges with
// layout compensation so scroll feels like zooming, not a discrete detail toggle.
//   lod0 — compact job cards + `N steps` summary, intra-job flow hidden
//   lod1 — tighter job cards, step frames/edges/simplified labels
//   lod2 — full job cards and labels including markers
// Step frames and labels always appear together at lod1/lod2.
//
// The flow-json contract is produced by PlaygroundFlowRunner (same as
// `seiton check --format flow-json`); this module only renders, never parses YAML.

const NODE_W = 200;
const NODE_H = 26;
const BG_NODE_W = 170;
const BG_LANE_GAP = 18;
const STEP_GAP_Y = 16;
const JOB_PAD = 10;
const HEADER_H = 30;
const META_H = 18;
const LEGS_H = 20;
const MIN_JOB_W = 260;
const GAP_X = 90;
const GAP_Y = 36;
const PAR_HEADER_H = 18;
const PAR_CHILD_W = 150;
const PAR_CHILD_H = 24;
const PAR_PAD = 8;
const PAR_CHILDREN_PER_ROW = 3;
const MAX_LEG_CHIPS = 4;

const LOD_CLASSES = ['flow-svg--lod0', 'flow-svg--lod1', 'flow-svg--lod2'];

const DISPLAY_SCALE_MIN = 0.5;
const DISPLAY_SCALE_MAX = 1.75;
// Continuous zoom per wheel tick; LOD shifts when k crosses band edges (with hysteresis).
const WHEEL_ZOOM_SENS = 0.0014;
const LOD_COMPENSATE_EXP = 0.55;
/** Drop to lod-1 when k falls below [n/a, lod1 floor, lod2 floor]. */
const LOD_DROP_K = [null, 0.6, 0.74];
/** Rise to lod+1 when k rises above [lod0 ceil, lod1 ceil, n/a]. */
const LOD_RISE_K = [0.74, 0.92, null];

const MIN_JOB_W_LOD0 = 148;
const SUMMARY_H = 18;
const INNER_SCALE_LOD1 = 0.85;
const MIN_JOB_W_LOD1 = 200;

const LOD2_PROFILE = {
  nodeW: NODE_W,
  nodeH: NODE_H,
  bgNodeW: BG_NODE_W,
  minJobW: MIN_JOB_W,
  jobPad: JOB_PAD,
  stepGapY: STEP_GAP_Y,
};

/**
 * Renders one workflow into `container`. Returns false when there is nothing to draw
 * (no jobs, or the d3 global is unavailable).
 * @param {HTMLElement} container
 * @param {object|null} workflow  one entry of flow-json `workflows`
 * @param {{ onSelect?: (info: {type: string, data: object, diagnostics?: object[]}) => void,
 *           diagnostics?: object[],
 *           onZoomReady?: (controller: {
 *             zoomIn: () => void, zoomOut: () => void, reset: () => void, dispose: () => void
 *           }) => void }} [callbacks]
 * @returns {boolean}
 */
export function renderFlowGraph(container, workflow, { onSelect, diagnostics, onZoomReady } = {}) {
  container.replaceChildren();
  const d3 = globalThis.d3;
  if (!d3 || !workflow || !Array.isArray(workflow.jobs) || workflow.jobs.length === 0) {
    return false;
  }

  const jobs = workflow.jobs;
  const layouts = computeAllLayouts(jobs);
  const graphState = {
    currentLod: 2,
    layouts,
    jobs,
    jobNodes: new Map(),
    edgeSelections: [],
    groupFrameRects: [],
    groupedMembers: new Set(),
  };
  const diagMap = mapDiagnostics(jobs, diagnostics ?? []);

  const svg = d3
    .select(container)
    .append('svg')
    .attr('class', 'flow-svg flow-svg--lod2')
    .attr('role', 'img')
    .attr('aria-label', 'Workflow execution flow graph');

  const viewport = svg.append('g').attr('class', 'flow-viewport');
  const groupLayer = viewport.append('g');
  const edgeLayer = viewport.append('g');
  const nodeLayer = viewport.append('g');

  const initialLayout = layouts[2];
  drawNeedsGroups(groupLayer, edgeLayer, initialLayout.groups, initialLayout.layout, graphState);
  graphState.groupedMembers = new Set(initialLayout.groups.flatMap((g) => g.members));

  // Click selection: exactly one job/step highlighted at a time.
  let selectedGroup = null;
  const select = (group, info) => {
    if (selectedGroup) selectedGroup.classed('flow-node--selected', false);
    selectedGroup = group;
    group.classed('flow-node--selected', true);
    onSelect?.({ ...info, diagnostics: diagMap.get(info.data) ?? [] });
  };

  drawJobEdges(d3, edgeLayer, jobs, initialLayout.layout, graphState.groupedMembers, graphState);
  for (const job of jobs) {
    const id = jobKey(job.id);
    const node = drawJobNode(d3, nodeLayer, job, initialLayout.layout.get(id), select, diagMap);
    graphState.jobNodes.set(id, node);
  }

  wireNeedsHover(svg, jobs, graphState.jobNodes, graphState.edgeSelections);

  const zoom = d3
    .zoom()
    .scaleExtent([DISPLAY_SCALE_MIN, DISPLAY_SCALE_MAX])
    .filter((event) => {
      // Drag-pan only; wheel / pinch zoom are handled as LOD changes.
      if (event.type === 'wheel' || event.type === 'dblclick') return false;
      if (event.type === 'touchstart' && event.touches.length > 1) return false;
      return !event.button;
    })
    .on('zoom', (ev) => {
      viewport.attr('transform', ev.transform);
    });
  svg.call(zoom);
  const wheelCleanup = wireLodWheel(svg, zoom, graphState, d3);
  const fit = () => fitToView(d3, svg, zoom, graphState, container);
  fit();

  let resizeFrame = null;
  const resizeObserver = typeof ResizeObserver === 'function'
    ? new ResizeObserver(() => {
      if (!globalThis.matchMedia?.('(max-width: 880px)').matches) return;
      if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
      resizeFrame = requestAnimationFrame(() => {
        resizeFrame = null;
        fit();
      });
    })
    : null;
  resizeObserver?.observe(container);

  onZoomReady?.({
    zoomIn: () => svg.call(zoom.scaleBy, 1.25),
    zoomOut: () => svg.call(zoom.scaleBy, 0.8),
    reset: fit,
    dispose: () => {
      wheelCleanup?.();
      resizeObserver?.disconnect();
      if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
    },
  });
  return true;
}

// ─── Diagnostics mapping ───

/**
 * Maps lint diagnostics to graph nodes by source line: the innermost step whose
 * range contains the line wins; lines inside the job but outside any step mark
 * the job itself. Returns a Map keyed by the job/step DTO object.
 * @param {object[]} jobs
 * @param {object[]} diagnostics  playground lint diagnostics ({line, severity, message, ...})
 * @returns {Map<object, object[]>}
 */
function mapDiagnostics(jobs, diagnostics) {
  const map = new Map();
  if (diagnostics.length === 0) return map;

  const push = (node, diag) => {
    const list = map.get(node);
    if (list) list.push(diag);
    else map.set(node, [diag]);
  };

  const inRange = (node, line) => node.line > 0 && line >= node.line && line <= node.endLine;

  // Parsed ranges can spill into the next sibling's first line, so among all
  // containing nodes the one with the greatest start line wins. Nested parallel
  // children start later than their parent, so this also picks the deepest node.
  const matchStep = (steps, line) => {
    let best = null;
    const visit = (list) => {
      for (const step of list ?? []) {
        if (inRange(step, line) && (!best || step.line >= best.line)) best = step;
        visit(step.steps);
      }
    };
    visit(steps);
    return best;
  };

  for (const diag of diagnostics) {
    const line = diag.line ?? 0;
    if (line <= 0) continue;
    let job = null;
    for (const candidate of jobs) {
      if (inRange(candidate, line) && (!job || candidate.line >= job.line)) job = candidate;
    }
    if (!job) continue;
    push(job, diag); // job aggregates everything inside it (badge + detail)
    const step = matchStep(job.steps, line);
    if (step) push(step, diag);
  }

  return map;
}

/** The highest severity in a diagnostic list, as a css modifier. */
function maxSeverity(diags) {
  let level = 'info';
  for (const d of diags) {
    const s = (d.severity ?? '').toLowerCase();
    if (s === 'error') return 'error';
    if (s === 'warning') level = 'warning';
  }
  return level;
}

function countBySeverity(diags) {
  const counts = { error: 0, warning: 0, info: 0 };
  for (const d of diags) {
    const s = (d.severity ?? 'info').toLowerCase();
    counts[s === 'error' ? 'error' : s === 'warning' ? 'warning' : 'info']++;
  }
  return counts;
}

/** Adds a severity dot (with hover tooltip) at the top-right corner of a node rect. */
function drawMarker(group, diags, x, y) {
  if (!diags || diags.length === 0) return;
  const severity = maxSeverity(diags);
  const marker = group
    .append('g')
    .attr('class', `flow-marker flow-marker--${severity}`);
  marker.append('circle').attr('cx', x).attr('cy', y).attr('r', 5);
  if (diags.length > 1) {
    marker
      .append('text')
      .attr('class', 'flow-marker__count')
      .attr('x', x)
      .attr('y', y + 3)
      .attr('text-anchor', 'middle')
      .text(diags.length > 9 ? '9+' : String(diags.length));
  }
  marker
    .append('title')
    .text(diags.map((d) => `${d.severity}: ${d.message}`).join('\n'));
}

/** Layout-size ratio between LOD tiers, raised to LOD_COMPENSATE_EXP. */
function lodCompensatingScale(prevLod, nextLod, state) {
  const prev = layoutBounds(state.layouts[prevLod].layout, state.layouts[prevLod].groups);
  const next = layoutBounds(state.layouts[nextLod].layout, state.layouts[nextLod].groups);
  const prevSize = Math.max(prev.width, prev.height, 1);
  const nextSize = Math.max(next.width, next.height, 1);
  return (prevSize / nextSize) ** LOD_COMPENSATE_EXP;
}

function clampDisplayScale(k) {
  return Math.max(DISPLAY_SCALE_MIN, Math.min(DISPLAY_SCALE_MAX, k));
}

/** Zoom transform that keeps `pointer` (svg-local px) fixed on screen. */
function scaleTransformAt(d3, transform, newK, pointer) {
  const [px, py] = pointer;
  const tx = px - (px - transform.x) * (newK / transform.k);
  const ty = py - (py - transform.y) * (newK / transform.k);
  return d3.zoomIdentity.translate(tx, ty).scale(newK);
}

/** Applies layout + class for `lod` and returns the compensating scale factor from `prevLod`. */
function transitionLod(svg, state, d3, prevLod, lod) {
  const compensate = lodCompensatingScale(prevLod, lod, state);
  applyGraphLayout(state, lod, d3);
  svg.attr('class', `flow-svg ${LOD_CLASSES[lod]}`);
  return compensate;
}

/** Target LOD after zooming to `k`, using hysteresis thresholds. */
function lodForScale(lod, k) {
  let next = lod;
  for (; ;) {
    if (LOD_DROP_K[next] !== null && k < LOD_DROP_K[next] && next > 0) {
      next--;
      continue;
    }
    if (LOD_RISE_K[next] !== null && k > LOD_RISE_K[next] && next < 2) {
      next++;
      continue;
    }
    return next;
  }
}

/** Scroll / pinch: zoom continuously; shift LOD (with compensation) at band boundaries. */
function wireLodWheel(svg, zoom, state, d3) {
  const handler = (event) => {
    event.preventDefault();
    const pointer = d3.pointer(event, svg.node());
    const current = d3.zoomTransform(svg.node());
    const factor = Math.exp(-event.deltaY * WHEEL_ZOOM_SENS);
    let k = clampDisplayScale(current.k * factor);

    let lod = state.currentLod;
    const targetLod = lodForScale(lod, k);
    if (targetLod !== lod) {
      k = clampDisplayScale(k * transitionLod(svg, state, d3, lod, targetLod));
      lod = targetLod;
    }

    svg.call(zoom.transform, scaleTransformAt(d3, current, k, pointer));
  };
  const node = svg.node();
  node?.addEventListener('wheel', handler, { passive: false });
  return () => node?.removeEventListener('wheel', handler);
}

function computeAllLayouts(jobs) {
  return [0, 1, 2].map((lod) => computeLayout(jobs, lod));
}

/** Repositions job cards, edges, and group frames for the active LOD layout. */
function applyGraphLayout(state, lod, d3) {
  const { layout, groups } = state.layouts[lod];
  state.currentLod = lod;
  const innerScale = lod === 1 ? INNER_SCALE_LOD1 : 1;

  for (const [id, node] of state.jobNodes) {
    const pos = layout.get(id);
    node.group.attr('transform', `translate(${pos.x},${pos.y})`);
    node.box.attr('width', pos.width).attr('height', pos.height);
    node.header.attr('width', pos.width).attr('height', pos.headerH);
    updateJobChrome(node, pos);
    if (node.summary) {
      node.summary.attr('y', pos.headerH + 14);
    }
    const innerY = pos.headerH + JOB_PAD;
    node.inner.attr(
      'transform',
      innerScale < 1
        ? `translate(${JOB_PAD},${innerY}) scale(${innerScale})`
        : `translate(${JOB_PAD},${innerY})`,
    );
  }

  updateNeedsGroupFrames(state, groups, layout);
  updateEdgePaths(state, d3, layout, groups);
}

/** Keeps the job title and diagnostic badge inside the card after LOD resizes. */
function updateJobChrome(node, pos) {
  if (node.diagbadge) {
    node.diagbadge.attr('x', pos.width - JOB_PAD);
  }
  if (node.title && node.titleText) {
    node.title.text(truncate(node.titleText, titleMaxChars(pos.width, node.diagCounts)));
  }
}

function diagBadgeReservedPx(counts) {
  if (!counts) return 0;
  let segments = 0;
  if (counts.error > 0) segments++;
  if (counts.warning > 0) segments++;
  if (counts.info > 0) segments++;
  if (segments === 0) return 0;
  return segments * 22 + (segments - 1) * 6;
}

function titleMaxChars(width, diagCounts) {
  const reserved = diagBadgeReservedPx(diagCounts);
  return Math.max(4, Math.floor((width - JOB_PAD * 2 - reserved) / 6.2));
}

function layoutBounds(layout, groups) {
  let maxX = 0;
  let maxY = 0;
  for (const pos of layout.values()) {
    maxX = Math.max(maxX, pos.x + pos.width);
    maxY = Math.max(maxY, pos.y + pos.height);
  }
  for (const group of groups) {
    const frame = groupFrameBounds(group.members, layout);
    maxX = Math.max(maxX, frame.x + frame.width);
    maxY = Math.max(maxY, frame.y + frame.height);
  }
  return { x: 0, y: 0, width: maxX, height: maxY };
}

function groupFrameBounds(members, layout) {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = 0;
  let maxY = 0;
  for (const id of members) {
    const pos = layout.get(id);
    minX = Math.min(minX, pos.x);
    minY = Math.min(minY, pos.y);
    maxX = Math.max(maxX, pos.x + pos.width);
    maxY = Math.max(maxY, pos.y + pos.height);
  }
  return {
    x: minX - GROUP_PAD,
    y: minY - GROUP_PAD,
    width: maxX - minX + GROUP_PAD * 2,
    height: maxY - minY + GROUP_PAD * 2,
  };
}

function updateNeedsGroupFrames(state, groups, layout) {
  for (let i = 0; i < groups.length; i++) {
    const group = groups[i];
    const frame = groupFrameBounds(group.members, layout);
    group.frame = frame;
    const rect = state.groupFrameRects[i];
    if (rect) {
      rect.attr('x', frame.x).attr('y', frame.y).attr('width', frame.width).attr('height', frame.height);
    }
  }
}

function updateEdgePaths(state, d3, layout, groups) {
  const link = d3.linkHorizontal().x((p) => p[0]).y((p) => p[1]);
  for (const edge of state.edgeSelections) {
    if (edge.kind === 'group') {
      const source = layout.get(edge.from);
      const frame = edge.group.frame ?? groupFrameBounds(edge.group.members, layout);
      if (!source || !frame) continue;
      const sx = source.x + source.width;
      const sy = source.y + source.height / 2;
      const tx = frame.x;
      const ty = frame.y + frame.height / 2;
      const mx = (sx + tx) / 2;
      edge.path.attr('d', `M${sx},${sy}C${mx},${sy} ${mx},${ty} ${tx},${ty}`);
    } else {
      const source = layout.get(edge.from);
      const target = layout.get(edge.to);
      if (!source || !target) continue;
      edge.path.attr('d', link({
        source: [source.x + source.width, source.y + source.height / 2],
        target: [target.x, target.y + target.height / 2],
      }));
    }
  }
}

// ─── Job DAG layout ───

/** GitHub Actions job identifiers are case-insensitive. */
function jobKey(id) {
  return String(id ?? '').toLowerCase();
}

/** Stable key for a job's `needs` set — jobs with equal keys share their dependencies. */
function needsSignature(job) {
  return [...(job.needs ?? [])].map(jobKey).sort().join('\0');
}

/**
 * Longest-path leveling over `needs` edges. Within a column, jobs with the same
 * needs signature sort adjacently (document order otherwise) so they can be
 * grouped into one card at far zoom, like GitHub's own workflow graph.
 */
function computeLayout(jobs, lod) {
  const byId = new Map(jobs.map((j) => [jobKey(j.id), j]));
  const levels = new Map();

  function levelOf(job, stack) {
    const id = jobKey(job.id);
    if (levels.has(id)) return levels.get(id);
    if (stack.has(id)) return 0; // cycle guard: broken workflows must not hang the UI
    stack.add(id);
    let level = 0;
    for (const dep of job.needs ?? []) {
      const depJob = byId.get(jobKey(dep));
      if (depJob) level = Math.max(level, levelOf(depJob, stack) + 1);
    }
    stack.delete(id);
    levels.set(id, level);
    return level;
  }

  for (const job of jobs) levelOf(job, new Set());

  // Column x positions depend on the widest job in each preceding column.
  const columnJobs = new Map();
  for (const job of jobs) {
    const level = levels.get(jobKey(job.id));
    if (!columnJobs.has(level)) columnJobs.set(level, []);
    columnJobs.get(level).push(job);
  }

  // Same-needs jobs become vertical neighbors (stable within a signature).
  for (const column of columnJobs.values()) {
    const order = new Map(column.map((job, i) => [jobKey(job.id), i]));
    column.sort((a, b) => {
      const bySig = needsSignature(a).localeCompare(needsSignature(b));
      return bySig !== 0 ? bySig : order.get(jobKey(a.id)) - order.get(jobKey(b.id));
    });
  }

  const measured = new Map();
  for (const job of jobs) {
    measured.set(jobKey(job.id), measureJob(job, lod));
  }

  const columnX = new Map();
  let x = 0;
  const maxLevel = Math.max(...columnJobs.keys());
  for (let level = 0; level <= maxLevel; level++) {
    columnX.set(level, x);
    let widest = lod === 0 ? MIN_JOB_W_LOD0 : lod === 1 ? MIN_JOB_W_LOD1 : MIN_JOB_W;
    for (const job of columnJobs.get(level) ?? []) {
      widest = Math.max(widest, measured.get(jobKey(job.id)).width);
    }
    x += widest + GAP_X;
  }

  const layout = new Map();
  const groups = [];
  for (let level = 0; level <= maxLevel; level++) {
    let y = 0;
    let run = null;
    for (const job of columnJobs.get(level) ?? []) {
      const id = jobKey(job.id);
      const m = measured.get(id);
      layout.set(id, { x: columnX.get(level), y, width: m.width, height: m.height, graph: m.graph, headerH: m.headerH });

      const sig = needsSignature(job);
      if (run && run.sig === sig) {
        run.members.push(id);
      } else {
        if (run && run.members.length > 1) groups.push(run);
        run = { sig, needs: job.reducedNeeds ?? job.needs ?? [], members: [id] };
      }

      y += m.height + GAP_Y;
    }

    if (run && run.members.length > 1) groups.push(run);
  }

  return { layout, groups };
}

// ─── Same-needs group cards (far zoom) ───

const GROUP_PAD = 10;

/**
 * At lod0 each same-needs run renders as one card: a frame around the members,
 * member incoming edges hidden, and a single group edge per shared dependency —
 * job relationships matter more than step detail when zoomed out.
 */
function drawNeedsGroups(groupLayer, edgeLayer, groups, layout, state) {
  for (const group of groups) {
    const frame = groupFrameBounds(group.members, layout);
    group.frame = frame;

    const rect = groupLayer
      .append('rect')
      .attr('class', 'flow-needs-group')
      .attr('x', frame.x)
      .attr('y', frame.y)
      .attr('width', frame.width)
      .attr('height', frame.height)
      .attr('rx', 10);
    state.groupFrameRects.push(rect);

    // One edge per shared dependency, pointing at the card instead of each member.
    for (const dep of group.needs) {
      const source = layout.get(jobKey(dep));
      if (!source) continue;
      const sx = source.x + source.width;
      const sy = source.y + source.height / 2;
      const tx = frame.x;
      const ty = frame.y + frame.height / 2;
      const mx = (sx + tx) / 2;
      const path = edgeLayer
        .append('path')
        .attr('class', 'flow-edge flow-edge--group')
        .attr('d', `M${sx},${sy}C${mx},${sy} ${mx},${ty} ${tx},${ty}`);
      state.edgeSelections.push({ kind: 'group', from: jobKey(dep), group, path });
    }
  }
}

function drawJobEdges(d3, layer, jobs, layout, groupedMembers, state) {
  const link = d3
    .linkHorizontal()
    .x((p) => p[0])
    .y((p) => p[1]);
  for (const job of jobs) {
    const target = layout.get(jobKey(job.id));
    // Transitively reduced edges (computed by Seiton.Core) keep the DAG readable;
    // the hover closure still uses the full `needs` semantics.
    for (const dep of job.reducedNeeds ?? job.needs ?? []) {
      const source = layout.get(jobKey(dep));
      if (!source) continue;
      const path = layer
        .append('path')
        // Members of a same-needs group hide their incoming edges at lod0;
        // the group edge drawn by drawNeedsGroups stands in for them.
        .attr('class', groupedMembers.has(jobKey(job.id)) ? 'flow-edge flow-edge--in-group' : 'flow-edge')
        .attr('d', link({
          source: [source.x + source.width, source.y + source.height / 2],
          target: [target.x, target.y + target.height / 2],
        }));
      state.edgeSelections.push({ kind: 'job', from: jobKey(dep), to: jobKey(job.id), path });
    }
  }
}

// ─── Needs-chain hover highlighting ───

/**
 * Hovering a job highlights its transitive `needs` closure — everything it depends
 * on (upstream) and everything that depends on it (downstream) — plus the edges
 * between related jobs, while dimming the rest of the graph.
 */
function wireNeedsHover(svg, jobs, jobGroups, edgeSelections) {
  const dependsOn = new Map();
  const dependedBy = new Map();
  for (const job of jobs) {
    dependsOn.set(
      jobKey(job.id),
      (job.needs ?? []).map(jobKey).filter((id) => jobGroups.has(id)),
    );
  }
  for (const [id, needs] of dependsOn) {
    for (const dep of needs) {
      if (!dependedBy.has(dep)) dependedBy.set(dep, []);
      dependedBy.get(dep).push(id);
    }
  }

  const closure = (start, adjacency) => {
    const seen = new Set();
    const queue = [...(adjacency.get(start) ?? [])];
    while (queue.length > 0) {
      const id = queue.pop();
      if (seen.has(id)) continue;
      seen.add(id);
      queue.push(...(adjacency.get(id) ?? []));
    }
    return seen;
  };

  const clearHover = () => {
    svg.classed('flow-svg--hovering', false);
    for (const node of jobGroups.values()) {
      node.group.classed('flow-hover-related', false).classed('flow-hover-focus', false);
    }
    for (const edge of edgeSelections) {
      edge.path.classed('flow-hover-related', false);
    }
  };

  const hoverJob = (jobId) => {
    const related = new Set([jobId, ...closure(jobId, dependsOn), ...closure(jobId, dependedBy)]);
    svg.classed('flow-svg--hovering', true);
    for (const [id, node] of jobGroups) {
      node.group
        .classed('flow-hover-related', related.has(id))
        .classed('flow-hover-focus', id === jobId);
    }
    for (const edge of edgeSelections) {
      const targets = edge.kind === 'group' ? edge.group.members : [edge.to];
      edge.path.classed(
        'flow-hover-related',
        related.has(edge.from) && targets.some((id) => related.has(id)),
      );
    }
  };

  for (const [id, node] of jobGroups) {
    node.group
      .on('mouseenter', () => hoverJob(id))
      .on('mouseleave', clearHover);
  }
}

// ─── Intra-job step flow graph ───

/**
 * Builds the intra-job flow: main lane chain, background forks, wait/wait-all joins,
 * cancel cuts, parallel boundary groups.
 * @param {object[]} steps
 * @returns {{ nodes: object[], edges: {from: string, to: string, kind: string}[] }}
 */
function buildStepGraph(steps) {
  const nodes = [];
  const edges = [];
  let prevMain = null;
  /** Background steps not yet joined: {stepId, nodeId, lane}. */
  const activeBg = [];
  const usedLanes = new Set();

  const allocLane = () => {
    let lane = 1;
    while (usedLanes.has(lane)) lane++;
    usedLanes.add(lane);
    return lane;
  };
  const releaseBg = (entry) => {
    usedLanes.delete(entry.lane);
    activeBg.splice(activeBg.indexOf(entry), 1);
  };

  (steps ?? []).forEach((step, i) => {
    const id = `n${i}`;

    if (step.kind === 'parallel') {
      nodes.push({ id, step, kind: 'parallel', lane: 0, children: step.steps ?? [] });
      if (prevMain) edges.push({ from: prevMain, to: id, kind: 'seq' });
      prevMain = id;
      return;
    }

    if (step.background) {
      const lane = allocLane();
      nodes.push({ id, step, kind: 'step', lane });
      if (prevMain) edges.push({ from: prevMain, to: id, kind: 'bg' });
      activeBg.push({ stepId: step.id ?? null, nodeId: id, lane });
      return; // main lane continues without waiting
    }

    nodes.push({ id, step, kind: 'step', lane: 0 });
    if (prevMain) edges.push({ from: prevMain, to: id, kind: 'seq' });

    if (step.kind === 'wait') {
      for (const target of step.targets ?? []) {
        const entry = activeBg.find((b) => b.stepId === target);
        if (entry) {
          edges.push({ from: entry.nodeId, to: id, kind: 'wait' });
          releaseBg(entry);
        }
      }
    } else if (step.kind === 'wait-all') {
      for (const entry of [...activeBg]) {
        edges.push({ from: entry.nodeId, to: id, kind: 'wait' });
        releaseBg(entry);
      }
    } else if (step.kind === 'cancel' && step.target) {
      const entry = activeBg.find((b) => b.stepId === step.target);
      if (entry) {
        edges.push({ from: id, to: entry.nodeId, kind: 'cancel' });
        releaseBg(entry);
      }
    }

    prevMain = id;
  });

  return { nodes, edges };
}

function parallelSize(children) {
  const count = Math.max(1, children.length);
  const perRow = Math.min(PAR_CHILDREN_PER_ROW, count);
  const rows = Math.ceil(count / PAR_CHILDREN_PER_ROW);
  return {
    width: PAR_PAD * 2 + perRow * PAR_CHILD_W + (perRow - 1) * PAR_PAD,
    height: PAR_HEADER_H + PAR_PAD + rows * (PAR_CHILD_H + PAR_PAD),
  };
}

/** Assigns x/y/width/height to each node; vertical position = document order. */
function layoutStepGraph(graph, profile = LOD2_PROFILE) {
  let y = 0;
  let maxX = profile.nodeW;
  for (const node of graph.nodes) {
    if (node.kind === 'parallel') {
      const size = parallelSize(node.children);
      node.x = 0;
      node.y = y;
      node.width = size.width;
      node.height = size.height;
      y += size.height + profile.stepGapY;
    } else if (node.lane > 0) {
      node.x = profile.nodeW + BG_LANE_GAP + (node.lane - 1) * (profile.bgNodeW + BG_LANE_GAP);
      node.y = y;
      node.width = profile.bgNodeW;
      node.height = profile.nodeH;
      y += profile.nodeH + profile.stepGapY;
    } else {
      node.x = 0;
      node.y = y;
      node.width = profile.nodeW;
      node.height = profile.nodeH;
      y += profile.nodeH + profile.stepGapY;
    }
    maxX = Math.max(maxX, node.x + node.width);
  }
  return { width: maxX, height: Math.max(0, y - profile.stepGapY) };
}

function jobMetaText(job) {
  if (job.kind === 'reusable') return `⧉ reusable · uses: ${job.uses ?? ''}`;
  const parts = [];
  if (job.name) parts.push(job.name);
  if (job.strategy?.hasMatrix) {
    parts.push(job.strategy.matrixIsExpression ? 'matrix: ${{ … }}' : `matrix: ${(job.strategy.matrixKeys ?? []).join(' × ')}`);
  }
  if (job.if) parts.push('if ⛊');
  return parts.join(' · ');
}

function jobLegs(job) {
  return job.strategy?.combinations ?? [];
}

function matrixFolderTabText(count) {
  return count > 0 ? `Matrix (${count})` : 'Matrix';
}

function matrixVariantCountText(count) {
  return `${count} variant${count === 1 ? '' : 's'}`;
}

/**
 * Runtime settings line (timeout / environment), shown when zoomed in.
 * Permissions are intentionally detail-panel only — even two scopes truncate on the node.
 */
function jobInfoText(job) {
  const parts = [];
  if (job.timeoutMinutes !== undefined && job.timeoutMinutes !== null) {
    parts.push(`⏱ ${job.timeoutMinutes}m`);
  }
  if (job.environment) {
    parts.push(`env: ${job.environment}`);
  }
  return parts.join(' · ');
}

function measureJobHeader(job, lod) {
  const metaH = lod >= 1 && jobMetaText(job) ? META_H : 0;
  const infoH = lod >= 2 && jobInfoText(job) ? META_H : 0;
  const legsH = lod >= 2 && jobLegs(job).length > 0 ? LEGS_H : 0;
  return HEADER_H + metaH + infoH + legsH;
}

function measureJob(job, lod) {
  if (lod === 0) {
    const headerH = HEADER_H;
    const stepCount = countSteps(job.steps ?? []);
    const summaryH = stepCount > 0 ? SUMMARY_H : 0;
    const height = headerH + summaryH + JOB_PAD;
    return {
      graph: null,
      headerH,
      height,
      width: MIN_JOB_W_LOD0,
    };
  }

  const graph = buildStepGraph(job.steps ?? []);
  const content = layoutStepGraph(graph, LOD2_PROFILE);
  const headerH = measureJobHeader(job, lod);
  const innerScale = lod === 1 ? INNER_SCALE_LOD1 : 1;
  const innerBody = graph.nodes.length > 0 ? JOB_PAD + content.height + JOB_PAD : 0;
  const height = headerH + innerBody * innerScale;
  const minW = lod === 1 ? MIN_JOB_W_LOD1 : MIN_JOB_W;
  const width = Math.max(minW, JOB_PAD * 2 + content.width * innerScale);
  return { graph, headerH, height, width };
}

// ─── Job node rendering ───

function drawJobNode(d3, layer, job, pos, select, diagMap) {
  const g = layer
    .append('g')
    .attr('class', job.kind === 'reusable' ? 'flow-job flow-job--reusable' : 'flow-job')
    .attr('transform', `translate(${pos.x},${pos.y})`);

  // Matrix jobs get a GitHub-like folder tab above the card.
  if (job.strategy?.hasMatrix) {
    const legCount = jobLegs(job).length;
    const tabLabel = matrixFolderTabText(legCount);
    const tabWidth = Math.max(56, Math.min(108, tabLabel.length * 6.2 + 14));
    g.append('rect')
      .attr('class', 'flow-job__folder-tab')
      .attr('x', 0)
      .attr('y', -17)
      .attr('width', tabWidth)
      .attr('height', 24)
      .attr('rx', 5);
    g.append('text')
      .attr('class', 'flow-job__folder-tab-text')
      .attr('x', 8)
      .attr('y', -5)
      .text(tabLabel);
  }

  g.append('rect')
    .attr('class', 'flow-job__box')
    .attr('width', pos.width)
    .attr('height', pos.height)
    .attr('rx', 8);

  const header = g
    .append('rect')
    .attr('class', 'flow-job__header')
    .attr('width', pos.width)
    .attr('height', pos.headerH)
    .attr('rx', 8)
    .on('click', () => select(g, { type: 'job', data: job }));

  const titleText = job.kind === 'reusable' ? `⧉ ${job.id}` : job.id;
  let diagbadge = null;
  let diagCounts = null;

  const title = g
    .append('text')
    .attr('class', 'flow-job__title')
    .attr('x', JOB_PAD)
    .attr('y', 20);

  // Aggregated diagnostics badge (visible at every LOD, including lod0).
  const jobDiags = diagMap.get(job) ?? [];
  if (jobDiags.length > 0) {
    diagCounts = countBySeverity(jobDiags);
    diagbadge = g
      .append('text')
      .attr('class', 'flow-job__diagbadge')
      .attr('x', pos.width - JOB_PAD)
      .attr('y', 20)
      .attr('text-anchor', 'end');
    if (diagCounts.error > 0) {
      diagbadge.append('tspan').attr('class', 'flow-marker--error').text(`✖${diagCounts.error}`);
    }
    if (diagCounts.warning > 0) {
      diagbadge.append('tspan').attr('class', 'flow-marker--warning').attr('dx', 6).text(`⚠${diagCounts.warning}`);
    }
    if (diagCounts.info > 0) {
      diagbadge.append('tspan').attr('class', 'flow-marker--info').attr('dx', 6).text(`ℹ${diagCounts.info}`);
    }
    diagbadge.append('title').text(jobDiags.map((d) => `${d.severity}: ${d.message}`).join('\n'));
  }

  title.text(truncate(titleText, titleMaxChars(pos.width, diagCounts)));

  const metaText = jobMetaText(job);
  if (metaText) {
    g.append('text')
      .attr('class', 'flow-job__meta')
      .attr('x', JOB_PAD)
      .attr('y', HEADER_H + 12)
      .text(truncate(metaText, 44));
  }

  const metaH = metaText ? META_H : 0;
  const infoText = jobInfoText(job);
  if (infoText) {
    const info = g
      .append('text')
      .attr('class', 'flow-job__info')
      .attr('x', JOB_PAD)
      .attr('y', HEADER_H + metaH + 13)
      .text(truncate(infoText, 48));
    info.append('title').text(infoText);
  }

  const infoH = infoText ? META_H : 0;
  const legs = jobLegs(job);
  if (legs.length > 0) {
    const chips = legs.slice(0, MAX_LEG_CHIPS).map((c) => legLabel(c));
    if (legs.length > MAX_LEG_CHIPS) chips.push(`+${legs.length - MAX_LEG_CHIPS}`);
    g.append('text')
      .attr('class', 'flow-job__legs')
      .attr('x', JOB_PAD)
      .attr('y', HEADER_H + metaH + infoH + 13)
      .text(truncate(`${matrixVariantCountText(legs.length)}: ${chips.join(' | ')}`, 52));
  }

  // Far-zoom summary shown instead of the intra-job flow at lod0.
  const stepCount = countSteps(job.steps ?? []);
  let summary = null;
  if (stepCount > 0) {
    summary = g
      .append('text')
      .attr('class', 'flow-job__summary')
      .attr('x', JOB_PAD)
      .attr('y', pos.headerH + 14)
      .text(`${stepCount} steps`);
  }

  const inner = g
    .append('g')
    .attr('class', 'flow-job__inner')
    .attr('transform', `translate(${JOB_PAD},${pos.headerH + JOB_PAD})`);

  if (pos.graph) {
    drawStepEdges(d3, inner, pos.graph);
    for (const node of pos.graph.nodes) {
      if (node.kind === 'parallel') {
        drawParallelNode(inner, node, select, diagMap);
      } else {
        drawStepNode(inner, node, select, diagMap);
      }
    }
  }

  const box = g.select('.flow-job__box');
  return { group: g, box, header, inner, summary, title, titleText, diagbadge, diagCounts };
}

function countSteps(steps) {
  let count = 0;
  for (const step of steps) {
    count += step.kind === 'parallel' ? 1 + (step.steps?.length ?? 0) : 1;
  }
  return count;
}

function legLabel(combination) {
  return Object.values(combination).join('·');
}

function drawStepEdges(d3, layer, graph) {
  const byId = new Map(graph.nodes.map((n) => [n.id, n]));
  const vlink = d3.linkVertical().x((p) => p[0]).y((p) => p[1]);
  for (const edge of graph.edges) {
    const s = byId.get(edge.from);
    const t = byId.get(edge.to);
    if (!s || !t) continue;
    let source;
    let target;
    if (edge.kind === 'bg') {
      source = [s.x + s.width, s.y + s.height / 2];
      target = [t.x + t.width / 2, t.y];
    } else if (edge.kind === 'wait' || edge.kind === 'cancel') {
      source = [s.x + (s.lane > 0 ? s.width / 2 : s.width), s.y + s.height];
      target = [t.x + (t.lane > 0 ? t.width / 2 : t.width), t.y + (t.lane > 0 ? 0 : t.height / 2)];
    } else {
      source = [s.x + Math.min(s.width, NODE_W) / 2, s.y + s.height];
      target = [t.x + Math.min(t.width, NODE_W) / 2, t.y];
    }
    layer
      .append('path')
      .attr('class', `flow-step-edge flow-step-edge--${edge.kind}`)
      .attr('d', vlink({ source, target }));
  }
}

function drawStepNode(layer, node, select, diagMap) {
  const step = node.step;
  const g = layer
    .append('g')
    .attr('class', node.lane > 0 ? 'flow-step flow-step--bg' : 'flow-step')
    .on('click', () => select(g, { type: 'step', data: step }));

  g.append('rect')
    .attr('class', 'flow-step-node')
    .attr('x', node.x)
    .attr('y', node.y)
    .attr('width', node.width)
    .attr('height', node.height)
    .attr('rx', 5);

  const text = g
    .append('text')
    .attr('class', 'flow-step__text')
    .attr('x', node.x + 8)
    .attr('y', node.y + 17);
  appendStepLabelTspans(text, step, node.lane > 0 ? 18 : 22);

  drawMarker(g, diagMap.get(step), node.x + node.width - 2, node.y + 2);
}

function drawParallelNode(layer, node, select, diagMap) {
  const g = layer.append('g').attr('class', 'flow-step flow-step--parallel');

  g.append('rect')
    .attr('class', 'flow-parallel-boundary')
    .attr('x', node.x)
    .attr('y', node.y)
    .attr('width', node.width)
    .attr('height', node.height)
    .attr('rx', 6)
    .on('click', () => select(g, { type: 'step', data: node.step }));

  g.append('text')
    .attr('class', 'flow-step__text flow-step__kind flow-step__kind--parallel')
    .attr('x', node.x + PAR_PAD)
    .attr('y', node.y + 13)
    .text('⇉ parallel');

  node.children.forEach((child, i) => {
    const col = i % PAR_CHILDREN_PER_ROW;
    const row = Math.floor(i / PAR_CHILDREN_PER_ROW);
    const cx = node.x + PAR_PAD + col * (PAR_CHILD_W + PAR_PAD);
    const cy = node.y + PAR_HEADER_H + PAR_PAD + row * (PAR_CHILD_H + PAR_PAD);
    const childG = g
      .append('g')
      .attr('class', 'flow-step')
      .on('click', (ev) => {
        ev.stopPropagation();
        select(childG, { type: 'step', data: child });
      });
    childG
      .append('rect')
      .attr('class', 'flow-step-node flow-parallel-child')
      .attr('x', cx)
      .attr('y', cy)
      .attr('width', PAR_CHILD_W)
      .attr('height', PAR_CHILD_H)
      .attr('rx', 4);
    const text = childG
      .append('text')
      .attr('class', 'flow-step__text')
      .attr('x', cx + 6)
      .attr('y', cy + 16);
    appendStepLabelTspans(text, child, 14, 4);

    drawMarker(childG, diagMap.get(child), cx + PAR_CHILD_W - 2, cy + 2);
  });
}

function kindLabel(step) {
  switch (step.kind) {
    case 'run': return step.background ? 'run⇡' : 'run';
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
  return step.name ?? step.id ?? step.uses ?? firstLine(step.run) ?? '';
}

/** Suffix markers (if / timeout / continue-on-error) hidden at lod1. */
function stepMarks(step) {
  let marks = '';
  if (step.if) marks += ' ⛊';
  if (step.timeoutMinutes !== undefined && step.timeoutMinutes !== null) marks += ` ⏱${step.timeoutMinutes}m`;
  if (step.continueOnError) marks += ' ↷';
  return marks;
}

/** Base label + optional marks, with base truncated so markers fit at lod2. */
function stepLabelParts(step, max) {
  const marks = stepMarks(step);
  return {
    base: truncate(stepLabel(step), Math.max(4, max - marks.length)),
    marks,
  };
}

function appendStepLabelTspans(text, step, max, labelDx = 5) {
  const { base, marks } = stepLabelParts(step, max);
  text
    .append('tspan')
    .attr('class', `flow-step__kind flow-step__kind--${step.kind}`)
    .text(kindLabel(step));
  text
    .append('tspan')
    .attr('class', 'flow-step__label')
    .attr('dx', labelDx)
    .text(base);
  if (marks) {
    text.append('tspan').attr('class', 'flow-step__marks').text(marks);
  }
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

/** Fits the graph at lod2 and sets the baseline display scale (toolbar zoom only). */
function fitToView(d3, svg, zoom, state, container) {
  const lod = 2;
  applyGraphLayout(state, lod, d3);
  const bounds = layoutBounds(state.layouts[lod].layout, state.layouts[lod].groups);
  const cw = container.clientWidth || 600;
  const ch = container.clientHeight || 480;
  const pad = 20;
  const rawK = Math.min(
    1,
    Math.max(1, cw - pad * 2) / bounds.width,
    Math.max(1, ch - pad * 2) / bounds.height,
  );
  const k = Math.max(DISPLAY_SCALE_MIN, Math.min(DISPLAY_SCALE_MAX, rawK));
  const tx = (cw - bounds.width * k) / 2 - bounds.x * k;
  const ty = (ch - bounds.height * k) / 2 - bounds.y * k;
  svg.attr('class', `flow-svg ${LOD_CLASSES[lod]}`);
  svg.call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(k));
}
