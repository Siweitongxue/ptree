/* 项目管维插件 · 交互式网格视图
 * 零依赖，直接双击 index.html 打开。
 * 数据来自同目录 data.js（window.GRID_DATA），由 00-程序树/工具/适配/grid_export.py 生成。
 */
(function () {
  "use strict";

  var D = window.GRID_DATA;
  if (!D) {
    document.body.innerHTML = '<div style="padding:40px;color:#dbe2ec">没有读到 window.GRID_DATA，请确认 data.js 与本文件在同一目录。</div>';
    return;
  }

  var NODES = D.nodes || [];
  var LINKS = D.links || [];
  var GRIDS = D.grids || [];
  var GROUPS = D.groups || [];
  var PHASES = D.phases || [];

  // ------------------------------------------------------------------
  // 数据来源：?api > 同源 http(s) > data.js 只读
  // ------------------------------------------------------------------
  function normalizeBase(value) {
    if (!value) return "";
    var text = String(value).trim().replace(/\/+$/, "");
    return /^https?:\/\/[^\/]+/i.test(text) ? text : "";
  }
  var API_PARAM = "";
  try { API_PARAM = new URLSearchParams(location.search).get("api") || ""; } catch (e) { API_PARAM = ""; }
  var SAME_ORIGIN = (location.protocol === "http:" || location.protocol === "https:") ? location.origin : "";
  var API_BASE = normalizeBase(API_PARAM) || SAME_ORIGIN;
  var LIVE = !!API_BASE;
  var live = { ok: false, health: null, status: null, version: "", root: "", error: "" };

  var NODE = new Map();
  NODES.forEach(function (n) { NODE.set(n.id, n); });
  var GROUP_NAME = new Map();
  GROUPS.forEach(function (g) { GROUP_NAME.set(g.id, g.name || g.id); });
  var PHASE_NAME = new Map();
  PHASES.forEach(function (p) { PHASE_NAME.set(p.id, p.name || p.id); });

  var GROUP_DESC = new Map();
  GROUPS.forEach(function (g) { GROUP_DESC.set(g.id, g.description || ""); });

  // 自动部门组：group.all 与 group.dept-*。默认不画框，否则每张网格被一个大框套住。
  function isAutoGroup(id) { return id === "group.all" || /^group\.dept-/.test(id); }

  // 短介绍：程序节点用 nodes[].short；网格条目用 graph.nodes[].short / fields.short；策划条目用 rows.short
  function shortOf(id) {
    var n = NODE.get(id);
    if (n && n.short) return n.short;
    if (GRID && GRID.byId) {
      var g = GRID.byId.get(id);
      if (g && g.short) return g.short;
      if (g && g.fields && g.fields["short"]) return g.fields["short"];
    }
    var pr = PLAN_ROW.get(id);
    return pr && pr.short ? pr.short : "";
  }

  // 节点归属组：优先部门组，根组兜底
  var GROUP_OF = new Map();
  GROUPS.slice().sort(function (a, b) { return (a.parent ? 0 : 1) - (b.parent ? 0 : 1); })
    .forEach(function (g) {
      (g.members || []).forEach(function (m) {
        if (NODE.has(m) && !GROUP_OF.has(m)) GROUP_OF.set(m, g.id);
      });
    });
  NODES.forEach(function (n) {
    if (!GROUP_OF.has(n.id)) GROUP_OF.set(n.id, "group.all");
  });

  var CHILDREN = new Map();
  NODES.forEach(function (n) { CHILDREN.set(n.id, []); });
  NODES.forEach(function (n) {
    if (n.parent && CHILDREN.has(n.parent)) CHILDREN.get(n.parent).push(n.id);
  });
  function parentOf(n) { return n && n.parent && NODE.has(n.parent) ? n.parent : null; }

  var LINKS_BY = { contains: [], depends_on: [], flow: [], plans: [], block: [] };
  var GRID_CACHE = new Map();
  var GRID = null;

  function nodeStatus(n) {
    if (!n) return "未完成";
    if (GRID && GRID.kind === "program" && NODE.has(n.id)) return NODE.get(n.id).status || n.status || "未完成";
    return n.status || (n.fields && n.fields["状态"]) || "未完成";
  }

  // 功能块：优先用导出器给的 graph.blocks；旧数据没有就从 groups 现算，成员只留本网格存在的
  function gridBlocks(graph, nodes) {
    var ids = new Set(nodes.map(function (n) { return n.id; }));
    var list = (graph && graph.blocks) || [];
    if (!list.length) {
      list = GROUPS.map(function (g) {
        var members = (g.members || []).filter(function (m) { return ids.has(m); });
        return members.length ? { id: g.id, name: g.name || g.id, description: g.description || "", members: members, parent: g.parent || null } : null;
      }).filter(Boolean);
    }
    return list.map(function (b) {
      return {
        id: b.id,
        name: b.name || b.id,
        description: b.description || "",
        members: (b.members || []).filter(function (m) { return ids.has(m); }),
        parent: b.parent || null
      };
    }).filter(function (b) { return b.members.length > 0; });
  }

  function buildGrid(kind) {
    if (GRID_CACHE.has(kind)) return GRID_CACHE.get(kind);
    var grid = GRIDS.find(function (g) { return g.kind === kind; });
    if (!grid) return null;
    var graph = grid.graph || { root: null, nodes: [], links: [] };
    var nodes = (graph.nodes || []).slice();
    var byId = new Map(nodes.map(function (n) { return [n.id, n]; }));
    var children = new Map(nodes.map(function (n) { return [n.id, []]; }));
    nodes.forEach(function (n) {
      if (n.parent && children.has(n.parent)) children.get(n.parent).push(n.id);
    });
    var linksBy = { contains: [], depends_on: [], flow: [], plans: [], block: [] };
    if (kind === "program") {
      linksBy = LINKS_BY;
    } else {
      (graph.links || []).forEach(function (link) {
        var bucket = linksBy[link.type] || linksBy.contains;
        bucket.push(link);
      });
    }
    var result = {
      kind: kind,
      grid: grid,
      graph: graph,
      nodes: nodes,
      byId: byId,
      children: children,
      linksBy: linksBy,
      blocks: gridBlocks(graph, nodes),
      fields: grid.fields || [],
      rows: grid.rows || [],
      edgeCount: kind === "program" ? LINKS.length : (graph.links || []).length
    };
    GRID_CACHE.set(kind, result);
    return result;
  }
  LINKS.forEach(function (l) { if (LINKS_BY[l.type]) LINKS_BY[l.type].push(l); });

  var PLAN_GRID = GRIDS.find(function (g) { return g.kind === "plan"; });
  var PLAN_ROWS = (PLAN_GRID ? PLAN_GRID.rows : []).filter(function (r) {
    return /^D\d{2,}(\.\d+)?$/.test(String(r["编号"] || "").trim());
  });
  var PLAN_ROW = new Map();
  PLAN_ROWS.forEach(function (r) { PLAN_ROW.set(String(r["编号"]).trim(), r); });
  function planKey(id) {
    var m = /^D(\d+)(?:\.(\d+))?$/.exec(id);
    return m ? [Number(m[1]), m[2] ? Number(m[2]) : -1] : [9999, 9999];
  }
  var PLAN_IDS = Array.from(new Set(LINKS_BY.plans.map(function (l) { return l.from; })))
    .filter(function (id) { return PLAN_ROW.has(id); })
    .sort(function (a, b) {
      var ka = planKey(a), kb = planKey(b);
      return ka[0] - kb[0] || ka[1] - kb[1] || a.localeCompare(b);
    });

  var ROW_RE = {
    program: /^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$/,
    plan: /^D\d{2,}(\.\d+)?$/,
    art: /^M\d{2,}$/,
    ui: /^U\d+$/
  };
  var ID_FIELD = { program: "id", plan: "编号", art: "编号", ui: "编号" };

  var LAYER_NAME = ["根", "部门", "程序", "步骤"];
  var NODE_W = 152, NODE_H = 54, STEP_X = NODE_W + 18, COLS = 10;
  var PLAN_W = 140, PLAN_H = 30, PLAN_COLS = 9;

  var STYLE = {
    "未完成": { fill: "#222a37", stroke: "#5b6678", text: "#c3cbd8", badge: "todo" },
    "已完成": { fill: "#17324d", stroke: "#4f9bd9", text: "#dcecff", badge: "done" },
    "已验证": { fill: "#14352a", stroke: "#3fbf7f", text: "#d8ffe9", badge: "verified" },
    "退役": { fill: "#191c22", stroke: "#454b55", text: "#77808d", badge: "retired" }
  };
  // 四类边：层级（淡细线、无箭头）/ 依赖（实线箭头）/ 契约流（虚线箭头，带契约 id）/ 对应策划（点线）
  var EDGE_TYPES = [
    { id: "contains", label: "包含（层级）", short: "层级", color: "#44506b", width: 1.1, dash: null, arrow: false, alpha: 0.8, cls: "" },
    { id: "depends_on", label: "依赖", short: "依赖", color: "#c98a3c", width: 1.3, dash: null, arrow: true, alpha: 0.7, cls: "depends" },
    { id: "flow", label: "契约流", short: "契约流", color: "#4f9bd9", width: 1.2, dash: [6, 4], arrow: true, alpha: 0.6, cls: "flow" },
    { id: "plans", label: "对应策划", short: "对应策划", color: "#a07ad6", width: 1.2, dash: [1.5, 4], arrow: true, alpha: 0.7, cls: "plans" }
  ];
  function edgeType(id) {
    for (var i = 0; i < EDGE_TYPES.length; i++) if (EDGE_TYPES[i].id === id) return EDGE_TYPES[i];
    return null;
  }

  var canvas = document.getElementById("graph");
  var ctx = canvas.getContext("2d");
  var els = {
    tabs: document.getElementById("gridTabs"),
    search: document.getElementById("searchInput"),
    searchCount: document.getElementById("searchCount"),
    status: document.getElementById("statusFilter"),
    dept: document.getElementById("deptFilter"),
    phase: document.getElementById("phaseFilter"),
    depth: document.getElementById("depthSelect"),
    fit: document.getElementById("fitBtn"),
    reset: document.getElementById("resetBtn"),
    zoomIn: document.getElementById("zoomIn"),
    zoomOut: document.getElementById("zoomOut"),
    graphWrap: document.getElementById("graphWrap"),
    tableWrap: document.getElementById("tableWrap"),
    tableTitle: document.getElementById("tableTitle"),
    tableCount: document.getElementById("tableCount"),
    table: document.getElementById("gridTable"),
    detail: document.getElementById("detail"),
    legend: document.getElementById("legend"),
    statLine: document.getElementById("statLine"),
    metaLine: document.getElementById("metaLine"),
    modeBadge: document.getElementById("modeBadge"),
    modeRoot: document.getElementById("modeRoot"),
    emptyHint: document.getElementById("emptyHint"),
    gridEmptyHint: document.getElementById("gridEmptyHint"),
    viewSwitch: document.getElementById("viewSwitch"),
    tip: document.getElementById("canvasTip")
  };

  var state = {
    tab: "program",
    view: "graph",
    viewPref: {},
    status: "",
    dept: "",
    phase: "",
    depth: 4,
    search: "",
    matches: new Set(),
    selected: null,
    selectedPlan: null,
    hover: null,
    lastOpHtml: "",
    lastOpOk: null,
    edges: { contains: true, depends_on: true, flow: true, plans: false },
    groups: true,        // 功能块（手建组）默认画框
    autoGroups: false,   // 自动部门组默认不画
    collapsed: {},       // 折叠的功能块 id -> true
    selectedBlock: null,
    selectedEdge: null,
    hoverEdge: null,
    hoverBlock: null,
    scale: 1,
    tx: 0,
    ty: 0,
    panning: false,
    moved: false,
    startX: 0,
    startY: 0,
    lastX: 0,
    lastY: 0
  };

  var layout = {
    pos: new Map(),
    planPos: new Map(),
    activePlans: [],
    layerBands: [],
    blocks: [],
    blockHits: [],
    hidden: new Set(),
    edgeGeo: [],
    bounds: { x: 0, y: 0, w: 1, h: 1 }
  };

  // 开关偏好：边类型 / 功能块 / 部门组写进 localStorage，刷新后保持
  var PREF_KEY = "ptree.viewer.prefs.v1";
  function savePrefs() {
    try {
      localStorage.setItem(PREF_KEY, JSON.stringify({ edges: state.edges, groups: state.groups, autoGroups: state.autoGroups }));
    } catch (e) { /* file:// 或隐私模式禁用时忽略 */ }
  }
  function loadPrefs() {
    var pref = null;
    try { pref = JSON.parse(localStorage.getItem(PREF_KEY) || "null"); } catch (e) { pref = null; }
    if (!pref) return;
    if (pref.edges) {
      Object.keys(pref.edges).forEach(function (k) {
        if (typeof pref.edges[k] === "boolean" && k in state.edges) state.edges[k] = pref.edges[k];
      });
    }
    if (typeof pref.groups === "boolean") state.groups = pref.groups;
    if (typeof pref.autoGroups === "boolean") state.autoGroups = pref.autoGroups;
  }
  function syncPrefInputs() {
    document.querySelectorAll("[data-edge]").forEach(function (input) {
      var k = input.getAttribute("data-edge");
      if (k in state.edges) input.checked = state.edges[k];
    });
    document.querySelectorAll("[data-groups]").forEach(function (input) {
      var k = input.getAttribute("data-groups");
      input.checked = k === "auto" ? state.autoGroups : state.groups;
    });
  }

  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }
  function escRe(s) { return String(s).replace(/[.*+?^$()|[\]\\]|\{|\}/g, "\\$&"); }
  function statusStyle(s) { return STYLE[s] || STYLE["未完成"]; }
  function nodeName(id) { var n = NODE.get(id); return n ? n.name : id; }

  function nodeVisible(n) {
    if (!GRID) return false;
    if ((n.layer | 0) > state.depth - 1) return false;
    if (state.status && nodeStatus(n) !== state.status) return false;
    if (GRID.kind === "program") {
      if (state.dept && GROUP_OF.get(n.id) !== state.dept) return false;
      if (state.phase && (NODE.get(n.id) || {}).phase !== state.phase) return false;
    }
    return true;
  }

  // ------------------------------------------------------------------
  // 布局：层 0 根、层 1 部门、层 2 程序、层 3 步骤；同层按父节点顺序排成行
  // ------------------------------------------------------------------
  function computeLayout() {
    if (!GRID) return;
    var vis = GRID.nodes.filter(nodeVisible);
    var visSet = new Set(vis.map(function (n) { return n.id; }));
    var order = [[], [], [], []];
    var seen = new Set();
    function walk(id) {
      if (seen.has(id)) return;
      seen.add(id);
      var n = GRID.byId.get(id);
      if (!n) return;
      var layer = clamp(n.layer | 0, 0, 3);
      order[layer].push(id);
      (GRID.children.get(id) || []).forEach(function (c) {
        if (visSet.has(c)) walk(c);
      });
    }
    GRID.nodes.filter(function (n) {
      var p = n.parent;
      return !p || !visSet.has(p);
    }).forEach(function (n) { walk(n.id); });
    vis.forEach(function (n) { if (!seen.has(n.id)) walk(n.id); });

    var pos = new Map();
    var y = 42;
    var layerBands = [];
    order.forEach(function (ids, layer) {
      if (!ids.length) return;
      var rows = Math.ceil(ids.length / COLS);
      var bandTop = y;
      for (var i = 0; i < ids.length; i++) {
        var r = Math.floor(i / COLS);
        var c = i % COLS;
        var remaining = Math.min(COLS, ids.length - r * COLS);
        var rowWidth = remaining * STEP_X;
        var x0 = (COLS * STEP_X - rowWidth) / 2 + 16;
        pos.set(ids[i], {
          x: x0 + c * STEP_X,
          y: y + r * (NODE_H + 48),
          w: NODE_W,
          h: NODE_H,
          layer: layer
        });
      }
      y += rows * (NODE_H + 48) + 44;
      layerBands.push({ layer: layer, y: bandTop, ids: ids.length });
    });

    // 只渲染与可见程序节点相连的策划条目（仅程序网格）
    var activePlans = [];
    if (GRID.kind === "program" && state.edges.plans) {
      var seenPlan = new Set();
      LINKS_BY.plans.forEach(function (l) {
        if (visSet.has(l.to) && PLAN_ROW.has(l.from) && !seenPlan.has(l.from)) {
          seenPlan.add(l.from);
          activePlans.push(l.from);
        }
      });
      activePlans.sort(function (a, b) {
        var ka = planKey(a), kb = planKey(b);
        return ka[0] - kb[0] || ka[1] - kb[1] || a.localeCompare(b);
      });
    }
    var planPos = new Map();
    if (activePlans.length) {
      var py = y + 18;
      activePlans.forEach(function (id, i) {
        var r = Math.floor(i / PLAN_COLS);
        var c = i % PLAN_COLS;
        var remaining = Math.min(PLAN_COLS, activePlans.length - r * PLAN_COLS);
        var rowWidth = remaining * (PLAN_W + 12);
        var x0 = (PLAN_COLS * (PLAN_W + 12) - rowWidth) / 2 + 16;
        planPos.set(id, { x: x0 + c * (PLAN_W + 12), y: py + r * (PLAN_H + 16), w: PLAN_W, h: PLAN_H });
      });
      y = py + Math.ceil(activePlans.length / PLAN_COLS) * (PLAN_H + 16) + 20;
    }

    // 功能块包围盒：只包住本网格里可见的成员；折叠的组连成员一起收进 hidden
    var shownBlocks = [];
    var hidden = new Set();
    (GRID.blocks || []).forEach(function (b) {
      if (isAutoGroup(b.id) ? !state.autoGroups : !state.groups) return;
      var rects = [];
      b.members.forEach(function (m) { var q = pos.get(m); if (q) rects.push(q); });
      if (!rects.length) return;
      var x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
      rects.forEach(function (q) {
        x0 = Math.min(x0, q.x); y0 = Math.min(y0, q.y);
        x1 = Math.max(x1, q.x + q.w); y1 = Math.max(y1, q.y + q.h);
      });
      var pad = 12, head = 20;
      var collapsed = !!state.collapsed[b.id];
      if (collapsed) b.members.forEach(function (m) { hidden.add(m); });
      shownBlocks.push({
        block: b,
        collapsed: collapsed,
        box: { x: x0 - pad, y: y0 - head - 4, w: (x1 - x0) + pad * 2, h: (y1 - y0) + head + 4 + pad },
        memberCount: rects.length
      });
    });
    // 大框先画，小框套在上面；节点最后画，保证节点在组框上层
    shownBlocks.sort(function (a, b) { return (b.box.w * b.box.h) - (a.box.w * a.box.h); });

    var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    function include(p) {
      minX = Math.min(minX, p.x); minY = Math.min(minY, p.y);
      maxX = Math.max(maxX, p.x + p.w); maxY = Math.max(maxY, p.y + p.h);
    }
    pos.forEach(include);
    planPos.forEach(include);
    shownBlocks.forEach(function (it) { include(it.box); });
    if (!isFinite(minX)) { minX = 0; minY = 0; maxX = 1; maxY = 1; }
    layout.pos = pos;
    layout.planPos = planPos;
    layout.activePlans = activePlans;
    layout.layerBands = layerBands;
    layout.blocks = shownBlocks;
    layout.hidden = hidden;
    layout.blockHits = [];
    layout.bounds = { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
  }

  function relayout(fitAfter) {
    computeLayout();
    if (fitAfter) fitView();
    requestRender();
    updateStats();
  }

  // ------------------------------------------------------------------
  // 画布
  // ------------------------------------------------------------------
  var raf = null;
  function requestRender() {
    if (raf) return;
    raf = requestAnimationFrame(function () { raf = null; render(); });
  }

  function resizeCanvas() {
    var r = canvas.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    var w = Math.max(1, Math.floor(r.width));
    var h = Math.max(1, Math.floor(r.height));
    var pw = Math.round(w * dpr), ph = Math.round(h * dpr);
    if (canvas.width !== pw || canvas.height !== ph) {
      canvas.width = pw;
      canvas.height = ph;
    }
    canvas._w = w; canvas._h = h; canvas._dpr = dpr;
    return { w: w, h: h, dpr: dpr };
  }

  function render() {
    if (!GRID || state.view !== "graph") return;
    if (!canvas.getBoundingClientRect().width) return;
    var size = resizeCanvas();
    ctx.setTransform(size.dpr, 0, 0, size.dpr, 0, 0);
    ctx.clearRect(0, 0, size.w, size.h);

    ctx.save();
    ctx.fillStyle = "rgba(140,160,190,0.10)";
    for (var gx = 24; gx < size.w; gx += 32) {
      for (var gy = 24; gy < size.h; gy += 32) ctx.fillRect(gx, gy, 1, 1);
    }
    ctx.restore();

    ctx.save();
    ctx.translate(state.tx, state.ty);
    ctx.scale(state.scale, state.scale);
    drawBlocks();
    drawEdges();
    drawLayerLabels();
    drawNodes();
    drawPlanNodes();
    ctx.restore();
    els.emptyHint.hidden = layout.pos.size > 0;
  }

  function centerOf(p) { return { x: p.x + p.w / 2, y: p.y + p.h / 2 }; }

  function rectAnchor(rect, toward) {
    var c = centerOf(rect);
    var dx = toward.x - c.x, dy = toward.y - c.y;
    if (!dx && !dy) return c;
    var tx = dx ? (rect.w / 2 + 2) / Math.abs(dx) : Infinity;
    var ty = dy ? (rect.h / 2 + 2) / Math.abs(dy) : Infinity;
    var t = Math.min(tx, ty);
    return { x: c.x + dx * t, y: c.y + dy * t };
  }

  function arrowHead(x, y, angle, color) {
    var size = 5.5;
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(-size, -size * 0.6);
    ctx.lineTo(-size, size * 0.6);
    ctx.closePath();
    ctx.fillStyle = color;
    ctx.fill();
    ctx.restore();
  }

  function edgeKey(typeId, l) { return typeId + "|" + l.from + "|" + l.to + "|" + (l.note || ""); }

  function drawEdge(a, b, type) {
    var ca = centerOf(a), cb = centerOf(b);
    var dx = cb.x - ca.x, dy = cb.y - ca.y;
    var c1, c2;
    if (type.id === "contains") {
      c1 = { x: ca.x, y: ca.y + Math.abs(dy) * 0.45 };
      c2 = { x: cb.x, y: cb.y - Math.abs(dy) * 0.45 };
    } else {
      c1 = { x: ca.x + dx * 0.35, y: ca.y + dy * 0.12 };
      c2 = { x: cb.x - dx * 0.35, y: cb.y - dy * 0.12 };
    }
    var start = rectAnchor(a, c1);
    var end = rectAnchor(b, c2);
    ctx.beginPath();
    ctx.moveTo(start.x, start.y);
    ctx.bezierCurveTo(c1.x, c1.y, c2.x, c2.y, end.x, end.y);
    ctx.stroke();
    if (type.arrow) {
      var ang = Math.atan2(end.y - c2.y, end.x - c2.x);
      arrowHead(end.x, end.y, ang, type.color);
    }
    // 采样贝塞尔曲线，供边悬停/点选命中
    var pts = [];
    for (var i = 0; i <= 10; i++) {
      var t = i / 10, mt = 1 - t;
      pts.push({
        x: mt * mt * mt * start.x + 3 * mt * mt * t * c1.x + 3 * mt * t * t * c2.x + t * t * t * end.x,
        y: mt * mt * mt * start.y + 3 * mt * mt * t * c1.y + 3 * mt * t * t * c2.y + t * t * t * end.y
      });
    }
    return pts;
  }

  function drawEdges() {
    layout.edgeGeo = [];
    EDGE_TYPES.forEach(function (type) {
      if (!state.edges[type.id]) return;
      var list = (GRID.linksBy && GRID.linksBy[type.id]) || [];
      if (!list.length) return;
      list.forEach(function (l) {
        var a = type.id === "plans" ? layout.planPos.get(l.from) : layout.pos.get(l.from);
        var b = layout.pos.get(l.to);
        if (!a || !b) return;
        if (type.id !== "plans" && (layout.hidden.has(l.from) || layout.hidden.has(l.to))) return;
        var key = edgeKey(type.id, l);
        var active = key === state.hoverEdge || key === state.selectedEdge;
        ctx.save();
        ctx.strokeStyle = active ? "#ffd166" : type.color;
        ctx.globalAlpha = active ? 1 : type.alpha;
        ctx.lineWidth = active ? type.width + 0.9 : type.width;
        ctx.setLineDash(type.dash || []);
        var pts = drawEdge(a, b, type);
        ctx.restore();
        layout.edgeGeo.push({ key: key, type: type.id, link: l, pts: pts });
      });
    });
  }

  // 功能块框：半透明底 + 虚线边 + 顶部标题条（组名 · 成员数）；折叠后只留标题条
  function drawBlocks() {
    layout.blockHits = [];
    (layout.blocks || []).forEach(function (it) {
      var b = it.block, box = it.box;
      var selected = state.selectedBlock === b.id;
      var hover = state.hoverBlock === b.id && !it.collapsed;
      var auto = isAutoGroup(b.id);
      ctx.save();
      ctx.font = '11px system-ui, "Microsoft YaHei", sans-serif';
      ctx.textBaseline = "middle";
      if (it.collapsed) {
        var label = b.name + " · " + it.memberCount + " 个成员（已折叠）";
        var tw = Math.max(130, ctx.measureText(label).width + 40);
        roundRect(box.x, box.y, tw, 20, 5);
        ctx.fillStyle = "rgba(58,96,142,0.55)";
        ctx.fill();
        ctx.strokeStyle = selected ? "#ffd166" : "#5d8fbf";
        ctx.lineWidth = selected ? 2 : 1;
        ctx.setLineDash([]);
        ctx.stroke();
        ctx.fillStyle = "#cfe2f5";
        ctx.fillText("▸ " + label, box.x + 8, box.y + 11);
        layout.blockHits.push({
          id: b.id, title: { x: box.x, y: box.y, w: tw, h: 20 }, collapsed: true,
          toggle: { x: box.x, y: box.y, w: 18, h: 20 }
        });
      } else {
        roundRect(box.x, box.y, box.w, box.h, 10);
        ctx.fillStyle = auto ? "rgba(90,110,140,0.07)" : "rgba(78,125,180,0.12)";
        ctx.fill();
        ctx.setLineDash([5, 4]);
        ctx.strokeStyle = selected ? "#ffd166" : (hover ? "#9fb4d1" : (auto ? "#46586e" : "#5d8fbf"));
        ctx.lineWidth = selected ? 2 : 1.2;
        ctx.stroke();
        ctx.setLineDash([]);
        // 标题条贴在框顶
        roundRect(box.x, box.y, box.w, 20, 10);
        ctx.fillStyle = auto ? "rgba(70,88,110,0.5)" : "rgba(63,104,150,0.55)";
        ctx.fill();
        ctx.fillStyle = auto ? "#aebccf" : "#dbe9fb";
        ctx.fillText("▾ " + b.name + " · " + it.memberCount + " 个成员", box.x + 8, box.y + 11);
        layout.blockHits.push({
          id: b.id, title: { x: box.x, y: box.y, w: box.w, h: 20 }, collapsed: false,
          toggle: { x: box.x, y: box.y, w: 18, h: 20 }
        });
      }
      ctx.restore();
    });
  }

  function drawLayerLabels() {
    ctx.save();
    ctx.fillStyle = "#5d6a7d";
    ctx.font = '11px system-ui, "Microsoft YaHei", sans-serif';
    ctx.textBaseline = "middle";
    (layout.layerBands || []).forEach(function (band) {
      var label = "层" + band.layer + " " + (LAYER_NAME[band.layer] || "");
      ctx.fillText(label + " · " + band.ids, 10, band.y - 12);
    });
    if (layout.activePlans && layout.activePlans.length && layout.planPos.size) {
      var first = null;
      layout.planPos.forEach(function (p) { if (!first) first = p; });
      if (first) ctx.fillText("对应策划条目 · " + layout.activePlans.length, 10, first.y - 12);
    }
    ctx.restore();
  }

  function roundRect(x, y, w, h, r) {
    r = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y);
    ctx.quadraticCurveTo(x + w, y, x + w, y + r);
    ctx.lineTo(x + w, y + h - r);
    ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
    ctx.lineTo(x + r, y + h);
    ctx.quadraticCurveTo(x, y + h, x, y + h - r);
    ctx.lineTo(x, y + r);
    ctx.quadraticCurveTo(x, y, x + r, y);
    ctx.closePath();
  }

  function fitText(text, maxWidth) {
    text = String(text || "");
    if (ctx.measureText(text).width <= maxWidth) return text;
    var lo = 0, hi = text.length;
    while (lo < hi) {
      var mid = Math.ceil((lo + hi) / 2);
      if (ctx.measureText(text.slice(0, mid) + "…").width <= maxWidth) lo = mid;
      else hi = mid - 1;
    }
    return text.slice(0, lo) + "…";
  }

  function drawNodes() {
    layout.pos.forEach(function (p, id) {
      if (layout.hidden && layout.hidden.has(id)) return;
      var n = GRID.byId.get(id);
      if (!n) return;
      var base = NODE.get(id) || {};
      var nstatus = nodeStatus(n);
      var st = statusStyle(nstatus);
      var selected = state.selected === id;
      var matched = state.matches.has(id);
      var hover = state.hover === id;
      var synthetic = n.synthetic === true || n.kind === "root" && GRID.kind !== "program";
      var displayName = n.name + (synthetic ? "（" + ((GRID.children.get(id) || []).length ? "视图根" : "预留") + "）" : "");

      ctx.save();
      roundRect(p.x, p.y, p.w, p.h, 7);
      ctx.fillStyle = st.fill;
      ctx.fill();

      if (selected) {
        ctx.strokeStyle = "#ffd166";
        ctx.lineWidth = 2.4;
        ctx.setLineDash([]);
      } else if (matched) {
        ctx.strokeStyle = "#ffd166";
        ctx.lineWidth = 2;
        ctx.setLineDash([]);
      } else if (hover) {
        ctx.strokeStyle = "#9fb4d1";
        ctx.lineWidth = 1.8;
        ctx.setLineDash([]);
      } else if (n.preset || base.preset || synthetic) {
        ctx.strokeStyle = synthetic ? "#6b7a90" : "#d9a94a";
        ctx.lineWidth = 1.5;
        ctx.setLineDash([5, 4]);
      } else {
        ctx.strokeStyle = st.stroke;
        ctx.lineWidth = 1.2;
        ctx.setLineDash([]);
      }
      ctx.stroke();

      ctx.setLineDash([]);
      ctx.fillStyle = st.stroke;
      ctx.fillRect(p.x + 5, p.y + 10, 3, p.h - 20);

      ctx.fillStyle = st.text;
      ctx.font = '600 13px system-ui, "Microsoft YaHei", sans-serif';
      ctx.textBaseline = "alphabetic";
      var name = fitText(displayName, p.w - 22);
      ctx.fillText(name, p.x + 12, p.y + 23);

      ctx.fillStyle = nstatus === "退役" ? "#656c78" : "#7f8b9e";
      ctx.font = '10px ui-monospace, Consolas, monospace';
      ctx.fillText(fitText(n.id, p.w - 22), p.x + 12, p.y + 40);

      if (nstatus === "退役") {
        ctx.strokeStyle = "#6f7682";
        ctx.lineWidth = 1;
        var tw = Math.min(ctx.measureText(displayName).width, p.w - 24);
        ctx.beginPath();
        ctx.moveTo(p.x + 12, p.y + 18.5);
        ctx.lineTo(p.x + 12 + tw, p.y + 18.5);
        ctx.stroke();
      }
      if (n.preset || base.preset) {
        ctx.fillStyle = "#d9a94a";
        ctx.font = '9px system-ui, "Microsoft YaHei", sans-serif';
        ctx.fillText("预设", p.x + p.w - 26, p.y + 12);
      }
      ctx.restore();
    });
  }

  function drawPlanNodes() {
    layout.planPos.forEach(function (p, id) {
      var row = PLAN_ROW.get(id);
      var selected = state.selectedPlan === id;
      ctx.save();
      roundRect(p.x, p.y, p.w, p.h, 5);
      ctx.fillStyle = "#1a1730";
      ctx.fill();
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = selected ? "#ffd166" : "#6f56a0";
      ctx.lineWidth = selected ? 2 : 1;
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = "#bda9e6";
      ctx.font = '11px system-ui, "Microsoft YaHei", sans-serif';
      ctx.textBaseline = "middle";
      ctx.fillText(fitText(id + " " + (row ? row["名称"] : ""), p.w - 12), p.x + 7, p.y + p.h / 2);
      ctx.restore();
    });
  }

  // ------------------------------------------------------------------
  // 视图操作
  // ------------------------------------------------------------------
  function fitView() {
    var r = canvas.getBoundingClientRect();
    if (!r.width || !r.height) return;
    var b = layout.bounds;
    var pad = 36;
    var sw = Math.max(1, r.width - pad * 2);
    var sh = Math.max(1, r.height - pad * 2);
    var maxScale = GRID && GRID.nodes.length <= 3 ? 1.15 : 2.2;
    state.scale = clamp(Math.min(sw / b.w, sh / b.h), 0.12, maxScale);
    state.tx = (r.width - b.w * state.scale) / 2 - b.x * state.scale;
    state.ty = (r.height - b.h * state.scale) / 2 - b.y * state.scale;
    requestRender();
  }

  function setZoom(next, cx, cy) {
    var wx = (cx - state.tx) / state.scale;
    var wy = (cy - state.ty) / state.scale;
    state.scale = clamp(next, 0.12, 3.5);
    state.tx = cx - wx * state.scale;
    state.ty = cy - wy * state.scale;
    requestRender();
  }

  function centerOn(id) {
    var p = layout.pos.get(id);
    if (!p) return;
    var r = canvas.getBoundingClientRect();
    var c = centerOf(p);
    state.tx = r.width / 2 - c.x * state.scale;
    state.ty = r.height / 2 - c.y * state.scale;
    requestRender();
  }

  function pointSegDist(px, py, a, b) {
    var dx = b.x - a.x, dy = b.y - a.y;
    var len = dx * dx + dy * dy;
    var t = len ? ((px - a.x) * dx + (py - a.y) * dy) / len : 0;
    t = Math.max(0, Math.min(1, t));
    var qx = a.x + dx * t, qy = a.y + dy * t;
    return Math.sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
  }

  // 边命中：到采样折线的距离（阈值按屏幕 7px 折算成世界坐标）
  function hitEdge(wx, wy) {
    var best = null;
    var bestD = 7 / Math.max(state.scale, 0.15);
    (layout.edgeGeo || []).forEach(function (g) {
      for (var i = 0; i < g.pts.length - 1; i++) {
        var d = pointSegDist(wx, wy, g.pts[i], g.pts[i + 1]);
        if (d < bestD) { bestD = d; best = g; }
      }
    });
    return best;
  }

  function hitTest(mx, my) {
    var wx = (mx - state.tx) / state.scale;
    var wy = (my - state.ty) / state.scale;
    if (state.edges.plans && layout.planPos.size) {
      var hitPlan = null;
      layout.planPos.forEach(function (p, id) {
        if (!hitPlan && wx >= p.x && wx <= p.x + p.w && wy >= p.y && wy <= p.y + p.h) hitPlan = id;
      });
      if (hitPlan) return { kind: "plan", id: hitPlan };
    }
    // 节点在组框上层：先命中节点
    var hit = null;
    layout.pos.forEach(function (p, id) {
      if (hit || (layout.hidden && layout.hidden.has(id))) return;
      if (wx >= p.x && wx <= p.x + p.w && wy >= p.y && wy <= p.y + p.h) hit = id;
    });
    if (hit) return { kind: "node", id: hit };
    // 组框标题条：左端 18px 是折叠按钮，其余地方弹组卡
    var hitBlock = null;
    (layout.blockHits || []).forEach(function (h) {
      if (hitBlock) return;
      var t = h.title;
      if (wx >= t.x && wx <= t.x + t.w && wy >= t.y && wy <= t.y + t.h) {
        hitBlock = { kind: "block", id: h.id, collapse: wx <= h.toggle.x + h.toggle.w };
      }
    });
    if (hitBlock) return hitBlock;
    var g = hitEdge(wx, wy);
    if (g) return { kind: "edge", key: g.key, geo: g };
    return null;
  }

  // ------------------------------------------------------------------
  // 浮动提示：悬停节点看短介绍，悬停边看类型与说明
  // ------------------------------------------------------------------
  function ensureTip() {
    if (els.tip) return els.tip;
    var d = document.createElement("div");
    d.className = "canvas-tip";
    d.hidden = true;
    els.graphWrap.appendChild(d);
    els.tip = d;
    return d;
  }
  function showTip(html, mx, my) {
    var tip = ensureTip();
    tip.innerHTML = html;
    tip.hidden = false;
    var wrap = els.graphWrap.getBoundingClientRect();
    var tw = tip.offsetWidth, th = tip.offsetHeight;
    var left = mx + 14, top = my + 16;
    if (left + tw > wrap.width - 8) left = mx - tw - 12;
    if (top + th > wrap.height - 8) top = my - th - 10;
    tip.style.left = Math.max(6, left) + "px";
    tip.style.top = Math.max(6, top) + "px";
  }
  function hideTip() { if (els.tip) els.tip.hidden = true; }

  function findBlock(id) {
    var list = (GRID && GRID.blocks) || [];
    for (var i = 0; i < list.length; i++) if (list[i].id === id) return list[i];
    return null;
  }
  function findEdgeGeo(key) {
    var list = layout.edgeGeo || [];
    for (var i = 0; i < list.length; i++) if (list[i].key === key) return list[i];
    return null;
  }
  function edgeLabel(id) {
    var t = edgeType(id);
    return t ? t.label : id;
  }
  function edgeEndName(id) {
    if (NODE.has(id)) return nodeName(id);
    var pr = PLAN_ROW.get(id);
    if (pr) return pr["名称"] || id;
    var blk = findBlock(id);
    if (blk) return blk.name;
    if (GRID && GRID.byId.has(id)) return GRID.byId.get(id).name || id;
    return id;
  }
  function edgeNoteHtml(geo) {
    if (!geo || !geo.link.note) return "";
    var kind = geo.type === "flow" ? "契约 id" : (geo.type === "plans" ? "策划条目" : (geo.type === "block" ? "功能块" : "说明"));
    return '<div class="tip-note">' + esc(kind) + "：" + esc(geo.link.note) + '</div>';
  }
  function edgeTipHtml(geo) {
    if (!geo) return "";
    var type = edgeType(geo.type);
    var color = type ? type.color : "#888";
    return '<div class="tip-line"><span class="tip-swatch" style="background:' + color + '"></span>' + esc(edgeLabel(geo.type)) + '</div>' +
      '<div class="tip-line">' + esc(edgeEndName(geo.link.from)) + ' → ' + esc(edgeEndName(geo.link.to)) + '</div>' +
      edgeNoteHtml(geo);
  }

  // ------------------------------------------------------------------
  // 详情面板
  // ------------------------------------------------------------------
  function chip(id, kind) {
    var name = kind === "plan" ? (PLAN_ROW.get(id) ? PLAN_ROW.get(id)["名称"] : id) : nodeName(id);
    var attr = kind === "plan" ? "data-plan" : "data-node";
    return '<button class="chip' + (kind === "plan" ? " plan" : "") + '" ' + attr + '="' + esc(id) + '" title="' + esc(id + " " + name) + '">' +
      '<span>' + esc(name) + '</span><span class="cid">' + esc(id) + '</span></button>';
  }
  function chips(ids, kind) {
    ids = Array.from(new Set(ids)).sort();
    if (!ids.length) return '<span class="muted">无</span>';
    return '<div class="chips">' + ids.map(function (id) { return chip(id, kind); }).join("") + '</div>';
  }
  function section(title, body) {
    return '<div class="section"><h3>' + esc(title) + '</h3>' + body + '</div>';
  }
  function listBlock(items) {
    if (!items || !items.length) return '<span class="muted">无</span>';
    return '<ul class="list">' + items.map(function (x) { return "<li>" + esc(x) + "</li>"; }).join("") + '</ul>';
  }
  function pills(items) {
    if (!items || !items.length) return '<span class="muted">无</span>';
    return items.map(function (x) { return '<span class="pill">' + esc(x) + "</span>"; }).join("");
  }

  function impactOf(id) {
    var dist = new Map();
    var seen = new Set([id]);
    var queue = [{ id: id, d: 0 }];
    while (queue.length) {
      var cur = queue.shift();
      if (cur.d >= 4) continue;
      ["depends_on", "flow"].forEach(function (type) {
        (LINKS_BY[type] || []).forEach(function (l) {
          if (l.to === cur.id && !seen.has(l.from) && NODE.has(l.from)) {
            seen.add(l.from);
            dist.set(l.from, cur.d + 1);
            queue.push({ id: l.from, d: cur.d + 1 });
          }
        });
      });
    }
    return dist;
  }

  // 画布只画两端都在当前网格的边；其余在这里列成文字，不画线
  function undrawnLinksHtml(id) {
    var items = [];
    var inGrid = GRID.byId;
    ["contains", "depends_on", "flow", "plans"].forEach(function (type) {
      var enabled = !!state.edges[type];
      (LINKS_BY[type] || []).forEach(function (l) {
        if (l.from !== id && l.to !== id) return;
        var other = l.from === id ? l.to : l.from;
        var drawn;
        if (type === "plans") drawn = enabled && layout.planPos.has(other);
        else drawn = enabled && layout.pos.has(other) && !layout.hidden.has(other);
        if (drawn) return;
        var reason = !inGrid.has(other) ? "跨网格" : (!enabled ? "开关关闭" : "被筛选或层级隐藏");
        var note = l.note ? "（" + (type === "flow" ? "契约 " : type === "plans" ? "策划 " : "") + l.note + "）" : "";
        items.push('<li><span class="tag">' + esc(edgeLabel(type)) + '</span> ' + esc(edgeEndName(other)) +
          ' <span class="muted">' + esc(other) + " · " + esc(reason) + esc(note) + '</span></li>');
      });
    });
    return items.length ? '<ul class="list">' + items.join("") + '</ul>' : '<span class="muted">无</span>';
  }

  function renderNodeDetail(id) {
    var n = NODE.get(id);
    if (!n) return;
    var st = statusStyle(n.status);
    var group = GROUP_OF.get(id) || "";
    var parent = parentOf(n);
    var kids = CHILDREN.get(id) || [];
    var siblings = parent ? (CHILDREN.get(parent) || []).filter(function (x) { return x !== id; }) : [];
    var up = new Set(n.depends_on || []);
    var down = new Set();
    var flowOut = [], flowIn = [];
    LINKS_BY.depends_on.forEach(function (l) {
      if (l.from === id) up.add(l.to);
      if (l.to === id) down.add(l.from);
    });
    LINKS_BY.flow.forEach(function (l) {
      if (l.from === id) flowOut.push(l);
      if (l.to === id) flowIn.push(l);
    });
    var plans = [];
    LINKS_BY.plans.forEach(function (l) { if (l.to === id && PLAN_ROW.has(l.from)) plans.push(l.from); });
    var impact = impactOf(id);
    var impactGroups = {};
    impact.forEach(function (d, nid) {
      if (!impactGroups[d]) impactGroups[d] = [];
      impactGroups[d].push(nid);
    });
    var impactHtml = "";
    Object.keys(impactGroups).sort(function (a, b) { return Number(a) - Number(b); }).forEach(function (d) {
      impactHtml += '<div class="impact-row"><span class="impact-level">' + esc(d) + ' 层</span>' +
        chips(impactGroups[d]) + '</div>';
    });
    if (!impactHtml) impactHtml = '<span class="muted">无</span>';

    var html = '<div class="detail-head"><div><h2>' + esc(n.name) + '</h2>' +
      '<div class="sub">' + esc(n.id) + ' · ' + esc(n.kind || "") + ' · 层 ' + esc(n.layer) + '</div></div></div>';
    html += '<div class="chips">' +
      '<span class="badge ' + st.badge + '">' + esc(n.status) + '</span>' +
      (n.preset ? '<span class="badge preset">预设节点</span>' : '') +
      (n.phase ? '<span class="badge">' + esc(n.phase + " " + (PHASE_NAME.get(n.phase) || "")) + '</span>' : '') +
      (group ? '<span class="badge">' + esc(GROUP_NAME.get(group) || group) + '</span>' : '') +
      '</div>';
    var short = shortOf(id);
    html += section("短介绍", short ? '<div class="short-text">' + esc(short) + '</div>' :
      '<span class="muted">未写短介绍（00-程序树/短介绍/' + esc(id) + '.txt）</span>');
    html += section("基本信息", '<div class="kv">' +
      '<span class="k">职责</span><span class="v">' + esc(n.responsibility || "未写") + '</span>' +
      '<span class="k">父节点</span><span class="v">' + (parent ? chip(parent) : '<span class="muted">无（根）</span>') + '</span>' +
      '<span class="k">组</span><span class="v">' + esc(group || "未归组") + ' ' + esc(GROUP_NAME.get(group) || "") + '</span>' +
      '<span class="k">阶段</span><span class="v">' + esc(n.phase || "未写") + '</span>' +
      '<span class="k">负责人</span><span class="v">' + esc(n.owner || "未写") + '</span>' +
      '<span class="k">平台</span><span class="v">' + esc(n.platform || "未写") + '</span>' +
      '<span class="k">创建 / 更新</span><span class="v">' + esc(n.created_at || "?") + ' / ' + esc(n.updated_at || "?") + '</span>' +
      '<span class="k">规格文档</span><span class="v">' + esc(n.spec || "未写") + '</span>' +
      '</div>');
    html += section("不做什么", listBlock(n.not_responsible));
    html += section("输入 / 输出", '<div class="kv"><span class="k">输入</span><span class="v">' + pills(n.inputs) + '</span>' +
      '<span class="k">输出</span><span class="v">' + pills(n.outputs) + '</span></div>');
    html += section("涉及文件", listBlock(n.files));
    html += section("验收条件", listBlock(n.acceptance));
    if (n.notes) html += section("备注", '<div class="muted" style="font-size:12px;white-space:pre-wrap">' + esc(n.notes) + '</div>');
    if (n.legacy_reason) html += section("退役原因", '<div class="muted" style="font-size:12px">' + esc(n.legacy_reason) + '</div>');

    html += section("父", parent ? chip(parent) : '<span class="muted">无</span>');
    html += section("子", chips(kids));
    html += section("兄弟", chips(siblings));
    html += section("依赖（上游）", chips(Array.from(up)));
    html += section("被依赖（下游）", chips(Array.from(down)));
    var fo = flowOut.map(function (l) { return chip(l.to) + (l.note ? ' <span class="tag">' + esc(l.note) + '</span>' : ""); });
    var fi = flowIn.map(function (l) { return chip(l.from) + (l.note ? ' <span class="tag">' + esc(l.note) + '</span>' : ""); });
    html += section("契约流 · 产出给谁", fo.length ? '<div class="chips">' + fo.join("") + '</div>' : '<span class="muted">无</span>');
    html += section("契约流 · 从谁来", fi.length ? '<div class="chips">' + fi.join("") + '</div>' : '<span class="muted">无</span>');
    html += section("对应策划（plans）", plans.length ? '<div class="chips">' + plans.map(function (p) { return chip(p, "plan"); }).join("") + '</div>' : '<span class="muted">无</span>');
    html += section("跨网格 / 未画出的联系（只列文字，不画线）", undrawnLinksHtml(id));
    html += section("波及面（沿反向依赖/契约流可达，带层数）", impactHtml);
    html += opsHtml(id);

    els.detail.innerHTML = html;
  }

  function renderPlanDetail(id) {
    var row = PLAN_ROW.get(id);
    if (!row) return;
    var linked = [];
    LINKS_BY.plans.forEach(function (l) { if (l.from === id && NODE.has(l.to)) linked.push(l.to); });
    var fields = (PLAN_GRID && PLAN_GRID.fields) || [];
    var kv = fields.map(function (f) {
      if (f === "编号" || f === "名称") return "";
      return '<span class="k">' + esc(f) + '</span><span class="v">' + esc(row[f] || "未写") + '</span>';
    }).join("");
    var html = '<div class="detail-head"><div><h2>' + esc(row["名称"] || id) + '</h2>' +
      '<div class="sub">' + esc(id) + ' · 策划条目</div></div></div>';
    html += '<div class="chips"><span class="badge plan">策划</span>' +
      '<span class="badge">' + esc(row["文档形态"] || "") + '</span>' +
      '<span class="badge">' + esc(row["确认状态"] || "") + '</span></div>';
    html += section("短介绍", row.short ? '<div class="short-text">' + esc(row.short) + '</div>' :
      '<span class="muted">未写短介绍（00-程序树/短介绍/' + esc(id) + '.txt）</span>');
    html += section("策划字段", '<div class="kv">' + kv + '</div>');
    html += section("对应程序（plans 反向）", chips(linked));
    els.detail.innerHTML = html;
  }

  function renderGridNodeDetail(id) {
    var n = GRID.byId.get(id);
    if (!n) return;
    var fields = n.fields || {};
    var names = GRID.fields.length ? GRID.fields.slice() : Object.keys(fields);
    names = names.filter(function (f) { return f !== "_cells"; });
    if (!names.length) names = Object.keys(fields).filter(function (f) { return f !== "_cells"; });
    var synthetic = n.synthetic === true || n.kind === "root" && GRID.kind !== "program";
    var suffix = synthetic ? "（" + ((GRID.children.get(id) || []).length ? "视图根" : "预留") + "）" : "";
    var nstatus = nodeStatus(n);
    var kv = names.map(function (f) {
      return '<span class="k">' + esc(f) + '</span><span class="v">' + esc(fields[f] || "—") + '</span>';
    }).join("");
    var html = '<div class="detail-head"><div><h2>' + esc(n.name + suffix) + '</h2>' +
      '<div class="sub">' + esc(n.id) + ' · ' + esc(GRID.grid.name) + ' · ' + esc(n.kind || "item") + ' · 层 ' + esc(n.layer == null ? "?" : n.layer) + '</div></div></div>';
    html += '<div class="chips">' +
      (n.status ? '<span class="badge ' + statusStyle(nstatus).badge + '">' + esc(nstatus) + '</span>' : '') +
      (synthetic ? '<span class="badge preset">' + (suffix === "（预留）" ? "预留网格" : "视图根") + '</span>' : '') +
      '</div>';
    if ((GRID.children.get(id) || []).length === 0 && synthetic) {
      html += section("提示", '<div class="muted">预留网格：字段与规则已定，暂无条目。</div>');
    }
    html += section("短介绍", n.short ? '<div class="short-text">' + esc(n.short) + '</div>' :
      '<span class="muted">未写短介绍（00-程序树/短介绍/' + esc(id) + '.txt）</span>');
    html += section("字段", kv ? '<div class="kv">' + kv + '</div>' : '<div class="muted">这个节点没有字段。</div>');
    els.detail.innerHTML = html;
  }

  // ------------------------------------------------------------------
  // 功能块组卡 / 联系卡
  // ------------------------------------------------------------------
  function selectBlock(id) {
    state.selected = null; state.selectedPlan = null; state.selectedEdge = null;
    state.selectedBlock = id;
    renderDetail(); requestRender();
  }
  function selectEdge(key) {
    state.selected = null; state.selectedPlan = null; state.selectedBlock = null;
    state.selectedEdge = key;
    renderDetail(); requestRender();
  }
  function toggleBlock(id) {
    state.collapsed[id] = !state.collapsed[id];
    relayout(false);
    if (state.selectedBlock === id) renderDetail();
    hideTip();
  }

  function renderBlockDetail(id) {
    var b = findBlock(id);
    if (!b) return;
    var auto = isAutoGroup(id);
    var parentBlock = b.parent ? findBlock(b.parent) : null;
    var html = '<div class="detail-head"><div><h2>' + esc(b.name) + '</h2>' +
      '<div class="sub">' + esc(b.id) + ' · 功能块 · ' + b.members.length + ' 个成员</div></div></div>';
    html += '<div class="chips">' +
      '<span class="badge ' + (auto ? "preset" : "done") + '">' + (auto ? "自动部门组" : "手建功能块") + '</span>' +
      (parentBlock ? '<span class="badge">父组 ' + esc(parentBlock.name) + '</span>' : '') +
      (state.collapsed[id] ? '<span class="badge">已折叠</span>' : '') +
      '</div>';
    html += section("一行简介", b.description ? '<div class="short-text">' + esc(b.description) + '</div>' : '<span class="muted">清单里没写 description</span>');
    html += section("成员清单（" + b.members.length + "）", chips(b.members));
    html += section("详细说明", '<div class="kv"><span class="k">路径</span><span class="v"><code>00-程序树/说明/' + esc(b.id) + '.txt</code></span></div>' +
      '<div class="muted" style="font-size:11px">导出器按这个路径取一行简介；文件不存在时只有清单里的 description 可用。</div>');
    html += section("怎么操作", '<div class="muted" style="font-size:12px">点组框标题条左端的 ▾ / ▸ 折叠或展开；点标题条其它位置回到这张组卡。</div>');
    els.detail.innerHTML = html;
  }

  function renderEdgeDetail(key) {
    var geo = findEdgeGeo(key);
    if (!geo) {
      els.detail.innerHTML = '<div class="detail-empty"><b>这条边已不在画布上</b><p>切换网格或开关后重新点选。</p></div>';
      return;
    }
    var l = geo.link;
    var type = edgeType(geo.type);
    var html = '<div class="detail-head"><div><h2>联系 · ' + esc(edgeLabel(geo.type)) + '</h2>' +
      '<div class="sub">' + esc(geo.type) + ' · ' + esc(l.from) + ' → ' + esc(l.to) + '</div></div></div>';
    html += '<div class="chips"><span class="badge" style="border-color:' + esc(type ? type.color : "#888") + '">' + esc(edgeLabel(geo.type)) + '</span></div>';
    html += section("两端", '<div class="kv"><span class="k">起点</span><span class="v">' + esc(edgeEndName(l.from)) + '（' + esc(l.from) + '）</span>' +
      '<span class="k">终点</span><span class="v">' + esc(edgeEndName(l.to)) + '（' + esc(l.to) + '）</span></div>');
    var noteTitle = geo.type === "flow" ? "契约 id" : (geo.type === "plans" ? "策划条目名" : "说明");
    html += section(noteTitle, l.note ? '<div class="short-text">' + esc(l.note) + '</div>' : '<span class="muted">这条边没有说明</span>');
    els.detail.innerHTML = html;
  }

  function renderDetail() {
    if (state.selectedBlock) { renderBlockDetail(state.selectedBlock); return; }
    if (state.selectedEdge) { renderEdgeDetail(state.selectedEdge); return; }
    if (state.selectedPlan) {
      renderPlanDetail(state.selectedPlan);
      return;
    }
    if (state.selected && GRID && GRID.byId.has(state.selected)) {
      if (GRID.kind === "program" && NODE.has(state.selected)) renderNodeDetail(state.selected);
      else renderGridNodeDetail(state.selected);
      return;
    }
    els.detail.innerHTML = '<div class="detail-empty"><b>点一个节点</b>' +
      '<p>右侧会列出基本信息与一圈联系：父、子、兄弟、依赖上下游、契约流、对应策划、波及面。</p></div>';
  }

  function selectNode(id, doCenter) {
    if (!GRID || !GRID.byId.has(id)) return;
    state.selected = id;
    state.selectedPlan = null;
    renderDetail();
    if (doCenter) centerOn(id);
    requestRender();
    if (GRID.kind === "program") refreshLiveNode(id);
  }
  function selectPlan(id) {
    state.selectedPlan = id;
    state.selected = null;
    renderDetail();
    requestRender();
  }

  // ------------------------------------------------------------------
  // 表格视图（策划 / 美术 / UI）
  // ------------------------------------------------------------------
  function gridRows(grid) {
    var f = ID_FIELD[grid.kind];
    var re = ROW_RE[grid.kind];
    return grid.rows.filter(function (r) { return f && re && re.test(String(r[f] || "").trim()); });
  }

  function mark(text, q) {
    var s = esc(text);
    if (!q) return s;
    try {
      return s.replace(new RegExp("(" + escRe(esc(q)) + ")", "gi"), "<mark>$1</mark>");
    } catch (e) {
      return s;
    }
  }

  function renderTable() {
    var grid = GRIDS.find(function (g) { return g.kind === state.tab; });
    if (!grid) return;
    var rows = gridRows(grid);
    if (grid.fields.indexOf("状态") >= 0 && state.status) {
      rows = rows.filter(function (r) { return r["状态"] === state.status; });
    }
    var q = state.search;
    if (q) {
      var lower = q.toLowerCase();
      rows = rows.filter(function (r) {
        return grid.fields.some(function (f) { return String(r[f] || "").toLowerCase().indexOf(lower) >= 0; });
      });
    }
    els.tableTitle.textContent = grid.name + " · " + grid.instance;
    els.tableCount.textContent = rows.length + " 行" + (gridRows(grid).length !== rows.length ? "（已筛选，共 " + gridRows(grid).length + "）" : "");
    if (state.search) els.searchCount.textContent = rows.length + " 行匹配";
    // 行里有 short 就补一列“短介绍”（策划 / 美术 / UI / 全文件 都有）
    var cols = grid.fields.slice();
    var hasShort = rows.some(function (r) { return r.short; });
    if (hasShort) cols.push("短介绍");
    els.table.querySelector("thead").innerHTML = "<tr>" + cols.map(function (f) {
      return "<th>" + esc(f) + "</th>";
    }).join("") + "</tr>";
    if (!rows.length) {
      els.table.querySelector("tbody").innerHTML = '<tr><td colspan="' + cols.length + '" class="muted">没有匹配的行</td></tr>';
      return;
    }
    var statusField = grid.fields.indexOf("状态") >= 0;
    els.table.querySelector("tbody").innerHTML = rows.map(function (r) {
      var id = String(r[ID_FIELD[grid.kind]] || "").trim();
      var cls = statusField ? ("status-" + String(r["状态"] || "")) : "";
      var attrs = grid.kind === "plan" ? ' data-plan="' + esc(id) + '"' : "";
      return "<tr" + attrs + ">" + cols.map(function (f) {
        var v = f === "短介绍" ? (r.short || "") : (r[f] == null ? "" : String(r[f]));
        var c = f === "状态" ? (' class="' + esc(cls) + '"') : (f === "短介绍" ? ' class="short-col"' : "");
        return "<td" + c + ' title="' + esc(v) + '">' + mark(v, q) + "</td>";
      }).join("") + "</tr>";
    }).join("");
  }

  // ------------------------------------------------------------------
  // 控件与事件
  // ------------------------------------------------------------------

  // ------------------------------------------------------------------
  // 服务模式：/api/health、/api/status、/api/node、写操作
  // ------------------------------------------------------------------
  var bridgeFrame = null;
  var bridgeReady = null;
  var bridgeSeq = 0;
  var bridgePending = new Map();

  function ensureBridge() {
    if (bridgeReady) return bridgeReady;
    bridgeReady = new Promise(function (resolve, reject) {
      var frame = document.createElement("iframe");
      frame.style.display = "none";
      frame.title = "ptree-serve 桥接";
      frame.src = API_BASE + "/bridge.html";
      frame.onload = function () { resolve(frame); };
      frame.onerror = function () { reject(new Error("桥接页加载失败：" + API_BASE + "/bridge.html")); };
      document.body.appendChild(frame);
      bridgeFrame = frame;
    });
    return bridgeReady;
  }

  window.addEventListener("message", function (event) {
    var data = event.data;
    if (!data || data.__ptreeBridgeReply !== 1) return;
    var pending = bridgePending.get(data.id);
    if (!pending) return;
    bridgePending.delete(data.id);
    if (data.error) { pending.reject(new Error(data.error)); return; }
    var json = null;
    try { json = JSON.parse(data.text); } catch (e) { json = null; }
    pending.resolve({ status: data.status, ok: data.status >= 200 && data.status < 300, json: json, text: data.text || "" });
  });

  function bridgeFetch(path, options) {
    var opts = options || {};
    return ensureBridge().then(function (frame) {
      return new Promise(function (resolve, reject) {
        var id = ++bridgeSeq;
        bridgePending.set(id, { resolve: resolve, reject: reject });
        frame.contentWindow.postMessage({
          __ptreeBridge: 1,
          id: id,
          path: path,
          method: opts.method || "GET",
          body: opts.body || null
        }, "*");
        setTimeout(function () {
          if (bridgePending.delete(id)) reject(new Error("桥接请求超时：" + path));
        }, 10000);
      });
    });
  }

  function apiFetch(path, options) {
    var opts = options || {};
    opts.headers = Object.assign({ "Content-Type": "application/json" }, opts.headers || {});
    if (location.protocol === "file:" && API_BASE) return bridgeFetch(path, opts);
    return fetch(API_BASE + path, opts).then(function (response) {
      return response.text().then(function (text) {
        var json = null;
        try { json = JSON.parse(text); } catch (e) { json = null; }
        return { status: response.status, ok: response.ok, json: json, text: text };
      });
    });
  }

  function setModeBadge(text, cls) {
    els.modeBadge.textContent = text;
    els.modeBadge.className = "mode-badge " + cls;
  }

  function initMode() {
    if (!LIVE) {
      setModeBadge("只读模式 · file://", "readonly");
      els.modeRoot.textContent = "数据来自 data.js 快照；写操作不可用。加 ?api=http://127.0.0.1:7788 可连本地服务。";
      return;
    }
    setModeBadge("实时模式 · 连接中", "connecting");
    els.modeRoot.textContent = "服务 " + API_BASE;
    apiFetch("/api/health").then(function (res) {
      if (res.json && res.json.ok === true) {
        live.ok = true;
        live.health = res.json;
        live.root = res.json.root || "";
        setModeBadge("实时模式 · 可写", "live");
        els.modeRoot.textContent = "项目根 " + live.root + " · 服务 " + API_BASE;
      } else {
        throw new Error((res.json && res.json.error) || ("HTTP " + res.status));
      }
    }).catch(function (err) {
      live.ok = false;
      live.error = err && err.message ? err.message : String(err);
      setModeBadge("实时模式 · 服务不可用", "error");
      els.modeRoot.textContent = "连不上 " + API_BASE + "：" + live.error;
    });
    fetchLiveStatus();
  }

  function fetchLiveStatus() {
    if (!LIVE) return Promise.resolve(null);
    return apiFetch("/api/status").then(function (res) {
      if (res.json && res.json.counts) {
        live.status = res.json;
        live.version = res.json.tree_version || live.version;
        updateStats();
      }
      return res;
    }).catch(function (err) {
      live.error = err && err.message ? err.message : String(err);
      return null;
    });
  }

  function fmtErrors(json) {
    var errors = (json && json.errors) || [];
    if (!errors.length) return "";
    return errors.map(function (item) {
      if (typeof item === "string") return item;
      var code = item.code ? "[" + item.code + "] " : "";
      return code + (item.message || item.error || JSON.stringify(item));
    }).join("；");
  }

  function showWriteResult(ok, label, json, before) {
    json = json || {};
    var lines = [(ok ? "成功" : "失败") + "：" + label];
    var version = json.tree_version || live.version || "?";
    lines.push("tree_version：" + (before || "?") + " → " + version);
    if (json.written_files && json.written_files.length) lines.push("written_files：" + json.written_files.join("、"));
    if (json.ledger) lines.push("ledger：" + json.ledger);
    var errors = fmtErrors(json);
    if (errors) lines.push("errors：" + errors);
    if (json.error && !errors) lines.push("error：" + json.error);
    state.lastOpOk = ok;
    state.lastOpHtml = lines.map(esc).join("<br>");
    var box = document.getElementById("opResult");
    if (box) {
      box.className = "op-result " + (ok ? "ok" : "fail");
      box.innerHTML = state.lastOpHtml;
    }
  }

  function opsHtml(id) {
    if (!LIVE) {
      return '<div class="section ops"><h3>写操作</h3><div class="muted">只读模式。用 http 打开本页，或在 file:// 地址后加 ?api=http://127.0.0.1:7788 连接本地服务后即可写。</div></div>';
    }
    if (!live.ok) {
      return '<div class="section ops"><h3>写操作（未连接）</h3><div class="muted">服务不可用：' + esc(live.error || API_BASE) + '</div></div>';
    }
    var statusBtns = ["未完成", "已完成", "退役"].map(function (s) {
      return '<button type="button" data-set-status="' + esc(s) + '">' + esc(s) + '</button>';
    }).join("");
    var parentKind = NODE.get(id) ? NODE.get(id).kind : "";
    var defaultKind = parentKind === "program" ? "step" : parentKind === "department" ? "program" : parentKind === "root" ? "department" : "step";
    var kindOpts = ["program", "step", "department", "external"].map(function (k) {
      return '<option value="' + k + '"' + (k === defaultKind ? " selected" : "") + '>' + k + '</option>';
    }).join("");
    var typeOpts = ["引用", "测试", "探针", "覆盖", "数据", "控制"].map(function (t) {
      return '<option value="' + t + '">' + t + '</option>';
    }).join("");
    var datalist = NODES.map(function (n) {
      return '<option value="' + esc(n.id) + '"></option>';
    }).join("");
    return '<div class="section ops"><h3>写操作（实时模式）</h3>' +
      '<div class="status-btns"><span class="hint">改状态</span>' + statusBtns +
      '<button type="button" disabled title="已验证只能由用户确认">已验证（只能由用户确认）</button></div>' +
      '<form id="addNodeForm" class="op-form" autocomplete="off">' +
      '<h4>加子节点</h4>' +
      '<label>名称<input name="name" maxlength="80" placeholder="例如 新增视图测试"></label>' +
      '<label>slug<input name="slug" maxlength="60" placeholder="小写英文；留空自动转写"></label>' +
      '<label>kind<select name="kind">' + kindOpts + '</select></label>' +
      '<label>职责<input name="responsibility" maxlength="300" placeholder="一句话职责"></label>' +
      '<label>说明<textarea name="intro" rows="5" placeholder="≥200 字，写清定位、代码、连接、怎么改"></textarea></label>' +
      '<div class="hint" data-intro-count>0 字（去空白）</div>' +
      '<div class="hint">program 新增需要 outputs 契约；若被拒，先加 step 或由 API 补 outputs。</div>' +
      '<button type="submit">提交新增</button>' +
      '</form>' +
      '<form id="addLinkForm" class="op-form" autocomplete="off">' +
      '<h4>加联系</h4>' +
      '<label>from<input name="from" value="' + esc(id) + '"></label>' +
      '<label>to<input name="to" list="nodeIds" placeholder="目标节点 id"></label>' +
      '<label>type<select name="type">' + typeOpts + '</select></label>' +
      '<button type="submit">提交联系</button>' +
      '</form>' +
      '<datalist id="nodeIds">' + datalist + '</datalist>' +
      '<div class="op-result ' + (state.lastOpOk === true ? "ok" : state.lastOpOk === false ? "fail" : "") + '" id="opResult">' + state.lastOpHtml + '</div>' +
      '</div>';
  }

  function summaryIds(list) {
    return (list || []).map(function (item) {
      if (item == null) return null;
      if (typeof item === "string") return item;
      return item.id || item["编号"] || null;
    }).filter(Boolean);
  }

  function relatedIds(list) {
    return (list || []).map(function (item) {
      if (item == null) return null;
      if (typeof item === "string") return item;
      return item.id || item["编号"] || item.entry || null;
    }).filter(Boolean);
  }

  function renderLiveNodeDetail(payload) {
    var n = payload.node || {};
    var nb = payload.neighbors || {};
    var id = n.id || state.selected;
    var st = statusStyle(n.status);
    var parent = nb.parent && (nb.parent.id || nb.parent);
    var kids = summaryIds(nb.children);
    var siblings = summaryIds(nb.siblings);
    var depends = summaryIds(nb.depends_on);
    var dependedBy = summaryIds(nb.depended_by);
    var plans = (nb.related_plan || []).map(function (x) { return x.id || x; }).filter(Boolean);
    var art = relatedIds(nb.related_art);
    var ui = relatedIds(nb.related_ui);
    var groups = (payload.groups || []).map(function (g) { return g.id || g.name || g; });
    var impact = nb.impact || {};
    var upstream = (nb.upstream || []).map(function (c) { return (c.contract || "") + " " + (c.name || "") + (c.purpose ? "：" + c.purpose : ""); });
    var downstream = (nb.downstream || []).map(function (c) { return (c.contract || "") + " " + (c.name || "") + (c.purpose ? "：" + c.purpose : ""); });
    var linksOut = (nb.links_out || []).map(function (l) { return (l.from || "") + " -[" + (l.type || "") + "]-> " + (l.to || ""); });
    var linksIn = (nb.links_in || []).map(function (l) { return (l.from || "") + " -[" + (l.type || "") + "]-> " + (l.to || ""); });
    var intro = payload.intro && payload.intro.text;

    var html = '<div class="detail-head"><div><h2>' + esc(n.name || id) + '</h2>' +
      '<div class="sub">' + esc(id) + ' · ' + esc(n.kind || "") + ' · 层 ' + esc(n.layer == null ? "?" : n.layer) + ' · 服务实时</div></div></div>';
    html += '<div class="chips">' +
      '<span class="badge ' + st.badge + '">' + esc(n.status || "?") + '</span>' +
      (n.preset ? '<span class="badge preset">预设节点</span>' : '') +
      (n.phase ? '<span class="badge">' + esc(n.phase) + '</span>' : '') +
      (groups.length ? '<span class="badge">' + esc(groups.join(" / ")) + '</span>' : '') +
      '</div>';
    // 实时 payload 没带 short 时回退 data.js 的 short（C2）
    var liveShort = n.short || shortOf(id);
    html += section("短介绍", liveShort ? '<div class="short-text">' + esc(liveShort) + '</div>' :
      '<span class="muted">未写短介绍（00-程序树/短介绍/' + esc(id) + '.txt）</span>');
    html += section("节点说明（intro 全文）", intro ? '<div class="intro-text">' + esc(intro) + '</div>' : '<span class="muted">未写说明</span>');
    html += section("基本信息", '<div class="kv">' +
      '<span class="k">职责</span><span class="v">' + esc(n.responsibility || "未写") + '</span>' +
      '<span class="k">父节点</span><span class="v">' + (parent ? chip(parent) : '<span class="muted">无（根）</span>') + '</span>' +
      '<span class="k">组</span><span class="v">' + esc(groups.join(" / ") || "未归组") + '</span>' +
      '<span class="k">阶段</span><span class="v">' + esc(n.phase || "未写") + '</span>' +
      '<span class="k">负责人</span><span class="v">' + esc(n.owner || "未写") + '</span>' +
      '<span class="k">平台</span><span class="v">' + esc(n.platform || "未写") + '</span>' +
      '<span class="k">创建 / 更新</span><span class="v">' + esc(n.created_at || "?") + ' / ' + esc(n.updated_at || "?") + '</span>' +
      '</div>');
    html += section("不做什么", listBlock(n.not_responsible));
    html += section("输入 / 输出", '<div class="kv"><span class="k">输入</span><span class="v">' + pills(n.inputs) + '</span>' +
      '<span class="k">输出</span><span class="v">' + pills(n.outputs) + '</span></div>');
    html += section("父 / 子 / 兄弟", '<div class="kv"><span class="k">父</span><span class="v">' + (parent ? chip(parent) : '<span class="muted">无</span>') + '</span>' +
      '<span class="k">子</span><span class="v">' + chips(kids) + '</span>' +
      '<span class="k">兄弟</span><span class="v">' + chips(siblings) + '</span></div>');
    html += section("依赖（上游 / 下游）", '<div class="kv"><span class="k">上游</span><span class="v">' + chips(depends) + '</span>' +
      '<span class="k">下游</span><span class="v">' + chips(dependedBy) + '</span></div>');
    html += section("契约（上游 / 下游）", '<div class="kv"><span class="k">上游</span><span class="v">' + (upstream.length ? listBlock(upstream) : '<span class="muted">无</span>') + '</span>' +
      '<span class="k">下游</span><span class="v">' + (downstream.length ? listBlock(downstream) : '<span class="muted">无</span>') + '</span></div>');
    html += section("链接（出 / 入）", '<div class="kv"><span class="k">出</span><span class="v">' + (linksOut.length ? listBlock(linksOut) : '<span class="muted">无</span>') + '</span>' +
      '<span class="k">入</span><span class="v">' + (linksIn.length ? listBlock(linksIn) : '<span class="muted">无</span>') + '</span></div>');
    html += section("对应策划 / 美术 / UI", '<div class="kv"><span class="k">策划</span><span class="v">' + (plans.length ? '<div class="chips">' + plans.map(function (p) { return chip(p, "plan"); }).join("") + '</div>' : '<span class="muted">无</span>') + '</span>' +
      '<span class="k">美术</span><span class="v">' + chips(art) + '</span>' +
      '<span class="k">UI</span><span class="v">' + chips(ui) + '</span></div>');
    html += section("波及面（服务实时）", '<div class="kv"><span class="k">直接依赖者</span><span class="v">' + chips(impact.direct_dependents || []) + '</span>' +
      '<span class="k">契约消费者</span><span class="v">' + chips(impact.contract_consumers || []) + '</span>' +
      '<span class="k">后代数量</span><span class="v">' + esc(impact.descendants == null ? "?" : impact.descendants) + '</span></div>');
    html += opsHtml(id);
    els.detail.innerHTML = html;
  }

  function refreshLiveNode(id) {
    if (!LIVE || !live.ok) return Promise.resolve(null);
    return apiFetch("/api/node/" + encodeURIComponent(id) + "?depth=3").then(function (res) {
      if (res.json && res.json.ok && state.selected === id) renderLiveNodeDetail(res.json);
      return res;
    }).catch(function () { return null; });
  }

  function postUpdateCompat(id, body) {
    var primary = "/api/node/" + encodeURIComponent(id);
    return apiFetch(primary, { method: "POST", body: JSON.stringify(body) }).then(function (res) {
      var routeMissing = res.status === 404 || res.status === 405 ||
        (res.json && /路由不存在|not found/i.test(String(res.json.error || "")));
      if (!routeMissing) return res;
      return apiFetch("/api/node/update", { method: "POST", body: JSON.stringify(Object.assign({ id: id }, body)) });
    });
  }

  function addLocalNode(node) {
    if (!node || !node.id || NODE.has(node.id)) return;
    NODES.push(node);
    NODE.set(node.id, node);
    if (!CHILDREN.has(node.id)) CHILDREN.set(node.id, []);
    if (node.parent && CHILDREN.has(node.parent)) CHILDREN.get(node.parent).push(node.id);
    if (node.parent && NODE.has(node.parent)) {
      var edge = { from: node.parent, to: node.id, type: "contains", note: "" };
      LINKS.push(edge);
      LINKS_BY.contains.push(edge);
      GROUP_OF.set(node.id, GROUP_OF.get(node.parent) || "group.all");
    }
    computeLayout();
    requestRender();
    updateStats();
  }

  function afterWrite(newId) {
    fetchLiveStatus();
    if (newId && !NODE.has(newId)) {
      apiFetch("/api/node/" + encodeURIComponent(newId) + "?depth=3").then(function (res) {
        if (res.json && res.json.ok) {
          addLocalNode(res.json.node);
          state.selected = newId;
          state.selectedPlan = null;
          renderLiveNodeDetail(res.json);
          centerOn(newId);
        }
      });
    } else if (state.selected) {
      refreshLiveNode(state.selected);
    }
  }

  function setNodeStatus(id, status) {
    if (!id || status === "已验证") return;
    var before = live.version;
    postUpdateCompat(id, { status: status }).then(function (res) {
      var json = res.json || {};
      if (json.ok) {
        showWriteResult(true, "状态改为 " + status, json, before);
        if (NODE.has(id)) NODE.get(id).status = status;
        afterWrite(null);
      } else {
        showWriteResult(false, "状态修改被拒绝", json, before);
      }
    }).catch(function (err) {
      showWriteResult(false, "请求失败：" + (err.message || err), {}, before);
    });
  }

  function submitAddNode(form) {
    var payload = {
      parent: state.selected,
      name: form.elements.name.value.trim(),
      slug: form.elements.slug.value.trim(),
      kind: form.elements.kind.value,
      responsibility: form.elements.responsibility.value.trim(),
      intro: form.elements.intro.value
    };
    if (!payload.parent) { showWriteResult(false, "没有选中的父节点", {}, live.version); return; }
    if (!payload.name) { showWriteResult(false, "名称不能为空", {}, live.version); return; }
    var before = live.version;
    apiFetch("/api/node", { method: "POST", body: JSON.stringify(payload) }).then(function (res) {
      var json = res.json || {};
      if (json.ok) {
        showWriteResult(true, "新增节点 " + (json.id || ""), json, before);
        afterWrite(json.id);
      } else {
        showWriteResult(false, "新增节点被拒绝", json, before);
      }
    }).catch(function (err) {
      showWriteResult(false, "请求失败：" + (err.message || err), {}, before);
    });
  }

  function submitAddLink(form) {
    var payload = {
      from: form.elements.from.value.trim(),
      to: form.elements.to.value.trim(),
      type: form.elements.type.value,
      note: "viewer 手动新增"
    };
    var before = live.version;
    apiFetch("/api/link", { method: "POST", body: JSON.stringify(payload) }).then(function (res) {
      var json = res.json || {};
      if (json.ok) {
        showWriteResult(true, "新增联系 " + payload.from + " -[" + payload.type + "]-> " + payload.to, json, before);
        afterWrite(null);
      } else {
        showWriteResult(false, "新增联系被拒绝", json, before);
      }
    }).catch(function (err) {
      showWriteResult(false, "请求失败：" + (err.message || err), {}, before);
    });
  }

  function updateIntroCount() {
    var area = document.querySelector("#detail textarea[name=intro]");
    var hint = document.querySelector("#detail [data-intro-count]");
    if (!area || !hint) return;
    var count = area.value.replace(/\s/g, "").length;
    hint.textContent = count + " 字（去空白）；少于 200 会被内核拒绝（R31）";
    hint.style.color = count >= 200 ? "#8ee6b8" : "#e6cf91";
  }

  function updateStats() {
    if (!GRID) return;
    if (state.view === "table") {
      els.statLine.textContent = "表格视图 · " + GRID.grid.name + " · " + gridRows(GRID.grid).length + " 行";
      els.searchCount.textContent = state.search ? (state.matches.size + " 个命中") : "";
      return;
    }
    var vis = GRID.nodes.filter(nodeVisible).length;
    var liveCount = (GRID.kind === "program" && LIVE && live.status && live.status.counts) ? live.status.counts.nodes : null;
    var nodeCount = liveCount == null ? GRID.nodes.length : liveCount;
    var parts = [
      "节点 " + nodeCount + (liveCount != null && liveCount !== GRID.nodes.length ? "（快照 " + GRID.nodes.length + "）" : ""),
      "关系 " + GRID.edgeCount,
      "可见 " + vis
    ];
    if (GRID.blocks && GRID.blocks.length) parts.push("功能块 " + layout.blocks.length + "/" + GRID.blocks.length);
    if (GRID.kind === "program") parts.push("策划条目 " + PLAN_ROWS.length);
    if (state.edges.plans && GRID.kind === "program") parts.push("画布策划 " + layout.planPos.size);
    if (LIVE && live.status && GRID.kind === "program") parts.push("树版本 " + (live.status.tree_version || "?"));
    els.statLine.textContent = parts.join(" · ");
    els.searchCount.textContent = state.search ? (state.matches.size + " 个命中") : "";
  }

  function renderLegend() {
    var items = [
      '<span class="legend-item"><span class="legend-dot" style="background:' + STYLE["未完成"].stroke + '"></span>未完成</span>',
      '<span class="legend-item"><span class="legend-dot" style="background:' + STYLE["已完成"].stroke + '"></span>已完成</span>',
      '<span class="legend-item"><span class="legend-dot" style="background:' + STYLE["已验证"].stroke + '"></span>已验证</span>',
      '<span class="legend-item"><span class="legend-dot" style="background:' + STYLE["退役"].stroke + '"></span>退役（删除线）</span>',
      '<span class="legend-item"><span class="legend-dash"></span>预设 / 视图根</span>'
    ];
    // 四类边 + 功能块：边用线型区分，组框用虚线框表示
    EDGE_TYPES.forEach(function (t) {
      items.push('<span class="legend-item"><span class="legend-line ' + t.cls + '"></span>' + esc(t.label) + '</span>');
    });
    items.push('<span class="legend-item"><span class="legend-box"></span>功能块（虚线框）</span>');
    items.push('<span class="legend-item"><span class="legend-box auto"></span>部门组（默认关）</span>');
    els.legend.innerHTML = items.join("");
  }

  function renderTabs() {
    els.tabs.innerHTML = GRIDS.map(function (g) {
      return '<button type="button" data-tab="' + esc(g.kind) + '"' + (g.kind === state.tab ? ' class="active"' : "") + '>' + esc(g.name) + "</button>";
    }).join("");
  }

  function updateEmptyHint() {
    if (!GRID) return;
    var onlySynthetic = GRID.nodes.length === 1 && GRID.nodes[0].synthetic === true;
    els.gridEmptyHint.hidden = !(state.view === "graph" && onlySynthetic);
  }

  function updateViewButtons() {
    if (!els.viewSwitch) return;
    els.viewSwitch.querySelectorAll("[data-view]").forEach(function (btn) {
      btn.classList.toggle("active", btn.getAttribute("data-view") === state.view);
    });
  }

  function applyView() {
    var graph = state.view === "graph";
    els.graphWrap.hidden = !graph;
    els.tableWrap.hidden = graph;
    document.querySelectorAll(".graph-only").forEach(function (el) { el.classList.toggle("dim", !graph); });
    els.depth.disabled = !graph;
    els.fit.disabled = !graph;
    els.reset.disabled = !graph;
    els.zoomIn.disabled = !graph;
    els.zoomOut.disabled = !graph;
    els.dept.disabled = !graph || !GRID || GRID.kind !== "program";
    els.phase.disabled = !graph || !GRID || GRID.kind !== "program";
    document.getElementById("edgeToggles").hidden = !(GRID && GRID.edgeCount > 0);
    var gt = document.getElementById("groupToggles");
    if (gt) gt.hidden = !(GRID && GRID.blocks && GRID.blocks.length);
    updateViewButtons();
    updateEmptyHint();
    if (graph) {
      relayout(true);
      renderLegend();
      renderDetail();
    } else {
      renderTable();
      updateStats();
    }
  }

  function setTab(kind) {
    state.tab = kind;
    GRID = buildGrid(kind);
    state.selected = null;
    state.selectedPlan = null;
    state.selectedBlock = null;
    state.selectedEdge = null;
    state.hover = null;
    state.hoverBlock = null;
    state.hoverEdge = null;
    hideTip();
    state.matches = new Set();
    state.search = "";
    els.search.value = "";
    state.depth = 4;
    els.depth.value = "4";
    state.view = state.viewPref[kind] || "graph";
    state.viewPref[kind] = state.view;
    renderTabs();
    applyView();
  }

  function refreshMatches() {
    var q = state.search.toLowerCase();
    state.matches = new Set();
    if (!q || !GRID) return;
    GRID.nodes.forEach(function (n) {
      var parts = [n.id, n.name];
      if (n.fields) {
        Object.keys(n.fields).forEach(function (key) {
          if (key !== "_cells") parts.push(String(n.fields[key] == null ? "" : n.fields[key]));
        });
      }
      if (GRID.kind === "program" && NODE.has(n.id)) parts.push(NODE.get(n.id).responsibility || "");
      if (parts.join(" ").toLowerCase().indexOf(q) >= 0) state.matches.add(n.id);
    });
  }

  function focusFirstMatch() {
    if (!state.search || !state.matches.size || !GRID) return;
    var list = GRID.nodes.filter(function (n) { return state.matches.has(n.id) && nodeVisible(n); });
    if (!list.length) list = GRID.nodes.filter(function (n) { return state.matches.has(n.id); });
    if (list.length) selectNode(list[0].id, true);
  }

  function applyFilters() {
    state.status = els.status.value;
    state.dept = els.dept.value;
    state.phase = els.phase.value;
    if (state.view === "graph") relayout(false);
    else renderTable();
  }

  function initOptions() {
    var deptOpts = GROUPS.filter(function (g) { return /^group\.dept-/.test(g.id); });
    els.dept.innerHTML = '<option value="">全部</option>' + deptOpts.map(function (g) {
      return '<option value="' + esc(g.id) + '">' + esc(g.name || g.id) + "</option>";
    }).join("");
    els.phase.innerHTML = '<option value="">全部</option>' + PHASES.map(function (p) {
      return '<option value="' + esc(p.id) + '">' + esc(p.id + " " + (p.name || "")) + "</option>";
    }).join("");
    var meta = D.meta || {};
    els.metaLine.textContent = "tree_version " + (meta.tree_version || "?") + " · 生成 " + (meta.generated_at || "?") +
      " · 数据源 " + (meta.source || "?") + " · 节点 " + NODES.length + " / 关系 " + LINKS.length;
  }

  function bindEvents() {
    els.tabs.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-tab]");
      if (btn) setTab(btn.getAttribute("data-tab"));
    });
    els.viewSwitch.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-view]");
      if (!btn) return;
      state.view = btn.getAttribute("data-view");
      state.viewPref[state.tab] = state.view;
      applyView();
    });
    els.search.addEventListener("input", function () {
      clearTimeout(bindEvents.timer);
      bindEvents.timer = setTimeout(function () {
        state.search = els.search.value.trim();
        refreshMatches();
        if (state.view === "graph") {
          if (state.search) focusFirstMatch();
          requestRender();
          updateStats();
        } else {
          renderTable();
          els.searchCount.textContent = state.search ? (state.matches.size + " 个命中") : "";
        }
      }, 100);
    });
    els.search.addEventListener("keydown", function (e) {
      if (e.key === "Enter" && state.view === "graph") focusFirstMatch();
      if (e.key === "Escape") { els.search.value = ""; state.search = ""; refreshMatches(); requestRender(); updateStats(); }
    });
    [els.status, els.dept, els.phase].forEach(function (el) { el.addEventListener("change", applyFilters); });
    els.depth.addEventListener("change", function () {
      state.depth = Number(els.depth.value) || 4;
      relayout(true);
    });
    els.fit.addEventListener("click", fitView);
    els.reset.addEventListener("click", function () {
      state.scale = 1;
      var root = GRID && GRID.nodes.find(function (n) { return (n.layer | 0) === 0; });
      if (root) centerOn(root.id); else fitView();
    });
    els.zoomIn.addEventListener("click", function () {
      var r = canvas.getBoundingClientRect();
      setZoom(state.scale * 1.2, r.width / 2, r.height / 2);
    });
    els.zoomOut.addEventListener("click", function () {
      var r = canvas.getBoundingClientRect();
      setZoom(state.scale / 1.2, r.width / 2, r.height / 2);
    });
    document.querySelectorAll("[data-edge]").forEach(function (input) {
      input.addEventListener("change", function () {
        var key = input.getAttribute("data-edge");
        state.edges[key] = input.checked;
        state.selectedEdge = null;
        state.hoverEdge = null;
        hideTip();
        savePrefs();
        if (key === "plans") relayout(true);
        else { requestRender(); updateStats(); }
      });
    });
    document.querySelectorAll("[data-groups]").forEach(function (input) {
      input.addEventListener("change", function () {
        if (input.getAttribute("data-groups") === "auto") state.autoGroups = input.checked;
        else state.groups = input.checked;
        state.selectedBlock = null;
        state.hoverBlock = null;
        hideTip();
        savePrefs();
        relayout(true);
        renderDetail();
      });
    });

    canvas.addEventListener("pointerdown", function (e) {
      if (e.button !== 0) return;
      state.panning = true;
      state.moved = false;
      state.startX = e.clientX; state.startY = e.clientY;
      state.lastX = e.clientX; state.lastY = e.clientY;
      canvas.setPointerCapture(e.pointerId);
      canvas.classList.add("dragging");
    });
    canvas.addEventListener("pointermove", function (e) {
      var rect = canvas.getBoundingClientRect();
      if (state.panning) {
        var dx = e.clientX - state.lastX, dy = e.clientY - state.lastY;
        state.lastX = e.clientX; state.lastY = e.clientY;
        if (Math.abs(e.clientX - state.startX) + Math.abs(e.clientY - state.startY) > 3) state.moved = true;
        state.tx += dx; state.ty += dy;
        requestRender();
        return;
      }
      var mx = e.clientX - rect.left, my = e.clientY - rect.top;
      var hit = hitTest(mx, my);
      var hoverNode = hit && hit.kind === "node" ? hit.id : null;
      var hoverPlan = hit && hit.kind === "plan" ? hit.id : null;
      var hoverBlock = hit && hit.kind === "block" ? hit.id : null;
      var hoverEdge = hit && hit.kind === "edge" ? hit.key : null;
      var changed = hoverNode !== state.hover || hoverBlock !== state.hoverBlock || hoverEdge !== state.hoverEdge;
      state.hover = hoverNode;
      state.hoverBlock = hoverBlock;
      state.hoverEdge = hoverEdge;
      if (changed) requestRender();
      canvas.style.cursor = hit ? "pointer" : "grab";
      // 浮动提示：节点/策划条目显示短介绍（CSS 限 2 行），边显示类型与说明
      if (hoverNode) {
        var sh = shortOf(hoverNode);
        showTip("<b>" + esc(nodeName(hoverNode)) + "</b>" + (sh ? '<div class="tip-short">' + esc(sh) + "</div>" : ""), mx, my);
      } else if (hoverPlan) {
        var prow = PLAN_ROW.get(hoverPlan) || {};
        var psh = prow.short || "";
        showTip("<b>" + esc(hoverPlan + " " + (prow["名称"] || "")) + "</b>" + (psh ? '<div class="tip-short">' + esc(psh) + "</div>" : ""), mx, my);
      } else if (hoverBlock) {
        var blk = findBlock(hoverBlock);
        var blkTip = "<b>" + esc(blk ? blk.name : hoverBlock) + "</b>" + (blk && blk.description ? '<div class="tip-short">' + esc(blk.description) + "</div>" : "");
        if (blk && state.collapsed[blk.id]) blkTip += '<div class="tip-note">已折叠 · 点标题条左端 ▾ 展开</div>';
        showTip(blkTip, mx, my);
      } else if (hoverEdge) {
        showTip(edgeTipHtml(findEdgeGeo(hoverEdge)), mx, my);
      } else {
        hideTip();
      }
    });
    function endPan(e) {
      if (!state.panning) return;
      state.panning = false;
      canvas.classList.remove("dragging");
      if (!state.moved) {
        var rect = canvas.getBoundingClientRect();
        var hit = hitTest(e.clientX - rect.left, e.clientY - rect.top);
        if (hit && hit.kind === "node") selectNode(hit.id, false);
        else if (hit && hit.kind === "plan") selectPlan(hit.id);
        else if (hit && hit.kind === "block") {
          if (hit.collapse) toggleBlock(hit.id);
          else selectBlock(hit.id);
        }
        else if (hit && hit.kind === "edge") selectEdge(hit.key);
        else {
          state.selected = null; state.selectedPlan = null;
          state.selectedBlock = null; state.selectedEdge = null;
          renderDetail(); requestRender();
        }
      }
    }
    canvas.addEventListener("pointerup", endPan);
    canvas.addEventListener("pointercancel", function () { state.panning = false; canvas.classList.remove("dragging"); });
    canvas.addEventListener("pointerleave", function () {
      state.hover = null; state.hoverBlock = null; state.hoverEdge = null;
      hideTip();
      requestRender();
    });
    canvas.addEventListener("wheel", function (e) {
      e.preventDefault();
      var rect = canvas.getBoundingClientRect();
      setZoom(state.scale * Math.exp(-e.deltaY * 0.0015), e.clientX - rect.left, e.clientY - rect.top);
    }, { passive: false });
    canvas.addEventListener("dblclick", fitView);

    els.detail.addEventListener("click", function (e) {
      var sb = e.target.closest("[data-set-status]");
      if (sb) { setNodeStatus(state.selected, sb.getAttribute("data-set-status")); return; }
      var nb = e.target.closest("[data-node]");
      if (nb) { selectNode(nb.getAttribute("data-node"), true); return; }
      var pb = e.target.closest("[data-plan]");
      if (pb) selectPlan(pb.getAttribute("data-plan"));
    });
    els.detail.addEventListener("submit", function (e) {
      e.preventDefault();
      if (!LIVE || !live.ok) return;
      if (e.target.id === "addNodeForm") submitAddNode(e.target);
      else if (e.target.id === "addLinkForm") submitAddLink(e.target);
    });
    els.detail.addEventListener("input", function (e) {
      if (e.target && e.target.name === "intro") updateIntroCount();
    });
    els.table.querySelector("tbody").addEventListener("click", function (e) {
      var tr = e.target.closest("tr[data-plan]");
      if (tr) selectPlan(tr.getAttribute("data-plan"));
    });
    window.addEventListener("resize", function () { requestRender(); });
  }

  // ------------------------------------------------------------------
  // 启动
  // ------------------------------------------------------------------
  loadPrefs();
  initOptions();
  initMode();
  renderTabs();
  bindEvents();
  syncPrefInputs();
  setTab("program");
  requestAnimationFrame(function () { fitView(); });
})();
