// Bridge to the .NET host via WebView2 postMessage.
const pending = new Map();
let seq = 0;
const api = new Proxy({}, {
  get: (_, method) => (...args) => new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, args });
  }),
});

window.chrome.webview.addEventListener('message', e => {
  const m = e.data;
  if (m && m.event) { onEvent(m.event, m.data); return; }
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  m.error ? p.reject(new Error(m.error)) : p.resolve(m.result);
});

const $ = id => document.getElementById(id);
const DIRECT_EDITORS = ['capcut', 'shotcut', 'openshot'];
const ORIGIN = 'https://aive.local';

let appState = null;
let overview = null;
let currentProjectPath = null;
let updateInfo = null;
let busy = false;
let pendingSendText = null;
let typingEl = null;
let activeProvider = 'gemini';

let binTab = 'media';
let binFilter = 'all';
let binSearch = '';
let localMediaPool = [];
let selectedBinPaths = new Set();
let catalogCache = {};

let inspTab = 'project';
let detailSubTab = 'basic';
let selectedClipId = null;
let selectedClipIds = new Set();
let copiedAttributes = null;

let toolMode = 'select'; // select | blade
let rippleDelete = true;
let snapEnabled = true;
let pxPerSec = 36;
let needsTimelineFit = false;
let playheadSec = 0;
let playing = false;
let rafId = 0;
let lastFrameTs = 0;
let lockedTracks = new Set();
let markers = [];

let dragState = null;
let gizmoDrag = null;
let draggingBinItems = null;
let currentDropPlan = null;
let pendingExternalDrop = null;

let activeAudioCtx = null;
const waveformCache = new Map();
const activeVideos = new Map();
const activeAudios = new Map();

function esc(s) {
  return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function showModal(id) { $(id).style.display = 'flex'; }
function hideModal(id) { $(id).style.display = 'none'; }

const SVG = {
  eye: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/></svg>',
  eyeOff: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/><line x1="1" y1="1" x2="23" y2="23"/></svg>',
  lock: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>',
  unlock: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 7.4-2"/></svg>',
  vol: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14"/></svg>',
  mute: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><line x1="23" y1="9" x2="17" y2="15"/><line x1="17" y1="9" x2="23" y2="15"/></svg>',
  cover: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="3" width="18" height="18" rx="3"/><path d="M12 8v8M8 12h8"/></svg>',
  sparkle: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 2l2.4 7.2L22 12l-7.6 2.8L12 22l-2.4-7.2L2 12l7.6-2.8z"/></svg>',
  palette: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="13.5" cy="6.5" r="1.5"/><circle cx="17.5" cy="10.5" r="1.5"/><circle cx="8.5" cy="7.5" r="1.5"/><circle cx="6.5" cy="12.5" r="1.5"/><path d="M12 2a10 10 0 0 0 0 20c1.4 0 2-.7 2-1.8 0-.5-.2-1-.5-1.3-.3-.3-.5-.8-.5-1.4 0-1.1.9-2 2-2h2.3A4.7 4.7 0 0 0 22 10.8C22 5.9 17.5 2 12 2z"/></svg>',
  sliders: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><line x1="4" y1="21" x2="4" y2="14"/><line x1="4" y1="10" x2="4" y2="3"/><line x1="12" y1="21" x2="12" y2="12"/><line x1="12" y1="8" x2="12" y2="3"/><line x1="20" y1="21" x2="20" y2="16"/><line x1="20" y1="12" x2="20" y2="3"/><line x1="1" y1="14" x2="7" y2="14"/><line x1="9" y1="8" x2="15" y2="8"/><line x1="17" y1="16" x2="23" y2="16"/></svg>',
  mic: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="9" y="2" width="6" height="11" rx="3"/><path d="M5 10a7 7 0 0 0 14 0"/><line x1="12" y1="17" x2="12" y2="21"/><line x1="8" y1="21" x2="16" y2="21"/></svg>',
  trans: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><polygon points="4 6 11 12 4 18 4 6"/><polygon points="20 6 13 12 20 18 20 6"/></svg>',
  motion: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><polyline points="15 3 21 3 21 9"/><polyline points="9 21 3 21 3 15"/><line x1="21" y1="3" x2="14" y2="10"/><line x1="3" y1="21" x2="10" y2="14"/></svg>',
  refresh: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/></svg>',
  check: '<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="20 6 9 17 4 12"/></svg>',
  download: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v12m0 0l-4-4m4 4l4-4"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/></svg>',
};

let toastTimer = null;
function toast(msg) {
  const t = $('toast');
  t.textContent = msg;
  t.style.display = 'block';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.style.display = 'none'; }, 2400);
}

function updateAutoSaveStamp() {
  const now = new Date();
  const hh = String(now.getHours()).padStart(2, '0');
  const mm = String(now.getMinutes()).padStart(2, '0');
  const ss = String(now.getSeconds()).padStart(2, '0');
  $('autoSaveLabel').querySelector('span').textContent = `Auto-saved ${hh}:${mm}:${ss}`;
}

function edInfo() { return appState?.editors.find(e => e.id === appState.editor); }
function provInfo(id) { return appState?.providers.find(p => p.id === id); }
function projectDuration() { return Math.max(0, overview?.duration_s || 0); }
function projectFps() { return Math.max(1, overview?.fps || 30); }

function fmtTimeShort(s) {
  s = Math.max(0, Math.round(s || 0));
  const m = Math.floor(s / 60);
  const sec = s % 60;
  return `${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
}

function fmtSmpte(sec, fps) {
  sec = Math.max(0, sec || 0);
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.floor(sec % 60);
  const f = Math.floor((sec - Math.floor(sec)) * (fps || 30));
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}:${String(f).padStart(2, '0')}`;
}

function mediaUrl(path) { return `${ORIGIN}/media?path=${encodeURIComponent(path)}`; }
function thumbUrl(path, w = 200) { return `${ORIGIN}/thumb?path=${encodeURIComponent(path)}&w=${w}`; }

// ---------------- Top Bar & Menus ----------------
function toggleMenu() { $('topMenu').classList.toggle('open'); }
function closeMenu() { $('topMenu').classList.remove('open'); }
function closeCtxMenu() { $('clipCtxMenu').classList.remove('open'); }
document.addEventListener('click', e => {
  if (!e.target.closest('.menu-wrap')) closeMenu();
  if (!e.target.closest('#clipCtxMenu')) closeCtxMenu();
});

function renderTopStatus() {
  if (!appState) return;
  const pi = provInfo(appState.provider);
  const name = pi ? pi.display.replace(' (free models)', '').replace(/ \(.*\)/, '') : appState.provider;
  $('provpill').textContent = `AI: ${name}`;
  $('provpill').className = 'pill ' + (appState.hasKey ? 'ok' : 'warn');
  renderUpdateButton();
  const ed = edInfo();
  if (ed) {
    $('btnOpenEditor').textContent = `Open in ${ed.display.replace(' (export all formats)', '')}`;
    $('btnOpenEditor').style.display = (ed.id === 'finalcut' || ed.id === 'other') ? 'none' : '';
  }
}

async function pollEditor() {
  try {
    const s = await api.editor_status();
    const ed = edInfo();
    if (!ed) return false;
    const direct = DIRECT_EDITORS.includes(ed.id);
    const el = $('edstatus');
    el.style.display = direct && s.installed ? '' : 'none';
    el.textContent = s.running ? `${ed.display} Open` : `${ed.display} Closed`;
    el.className = 'pill ' + (s.running ? 'warn' : 'ok');
    $('btnCloseEditor').style.display = direct && s.running ? '' : 'none';
    return direct && s.running;
  } catch { return false; }
}

async function closeEditor() {
  toast(`Closing ${edInfo()?.display || 'editor'}...`);
  await api.close_editor();
  await pollEditor();
  await refreshOverview();
}

async function openEditor() {
  try {
    await api.open_editor();
    const ed = edInfo();
    toast(`Launched ${ed.display}`);
    setTimeout(pollEditor, 2500);
  } catch (e) { toast(e.message || String(e)); }
}

// ---------------- Overview & Unified Refresh ----------------
async function refreshOverview() {
  try {
    const ov = await api.overview();
    applyOverview(ov);
  } catch { }
}

function applyOverview(ov) {
  overview = ov;
  if (!ov) {
    $('ptitle').textContent = 'No Project Open';
    $('stageTitle').textContent = 'Player · No Project';
    $('stageMeta').textContent = '—';
    $('btnUndo').disabled = true;
    $('btnRedo').disabled = true;
    selectedClipId = null;
    selectedClipIds.clear();
    renderTimeline();
    renderStage();
    renderInspector();
    renderBin();
    return;
  }
  $('ptitle').textContent = ov.project || 'Untitled';
  $('stageTitle').textContent = `Player · ${ov.project || 'Timeline 01'}`;
  $('stageMeta').textContent = `${ov.width || 1920}×${ov.height || 1080} · ${Math.round(ov.fps || 30)}fps`;
  if ($('ratioSelect') && ov.ratio) $('ratioSelect').value = ov.ratio;
  $('btnUndo').disabled = !ov.can_undo;
  $('btnRedo').disabled = !ov.can_redo;

  const map = new Map();
  for (const item of localMediaPool) map.set(item.path.toLowerCase(), { ...item, in_timeline: false });
  for (const item of (ov.media_pool || [])) {
    const key = (item.path || '').toLowerCase();
    if (!key) continue;
    map.set(key, { ...item });
  }
  localMediaPool = Array.from(map.values());

  if (playheadSec > projectDuration() && projectDuration() > 0) playheadSec = projectDuration();
  for (const id of Array.from(selectedClipIds)) {
    if (!findClipById(id)) selectedClipIds.delete(id);
  }
  if (selectedClipId && !findClipById(selectedClipId)) {
    selectedClipId = selectedClipIds.size ? Array.from(selectedClipIds)[0] : null;
  }

  updateAutoSaveStamp();
  renderBin();
  renderTimeline();
  if (needsTimelineFit && overview) {
    needsTimelineFit = false;
    requestAnimationFrame(() => fitTimeline());
  }
  renderStage();
  renderInspector();
}

function visualTracksOrdered() {
  if (!overview?.tracks) return [];
  const videos = [], overlays = [], audios = [];
  for (const t of overview.tracks) {
    if (t.is_main) videos.push(t);
    else if (t.type === 'audio') audios.push(t);
    else overlays.push(t);
  }
  return [...overlays.reverse(), ...videos, ...audios];
}

function findClipById(id) {
  if (!overview?.tracks || !id) return null;
  for (const t of overview.tracks) {
    for (const c of t.segments) {
      if (c.id === id) return { track: t, clip: c };
    }
  }
  return null;
}

function activeSelectionIds() {
  if (selectedClipIds.size > 0) return Array.from(selectedClipIds);
  return selectedClipId ? [selectedClipId] : [];
}

function selectSingleClip(id) {
  selectedClipId = id;
  selectedClipIds.clear();
  if (id) selectedClipIds.add(id);
}

// ---------------- Tool Execution Helper ----------------
async function runTool(name, args, label) {
  if (busy) { toast('Wait for EditPilot to finish'); return null; }
  if (!overview) { toast('Open or create a project first'); return null; }
  if (await pollEditor()) {
    toast(`Close ${edInfo().display} before editing`);
    return null;
  }
  try {
    const res = await api.run_tool(name, args || {}, label || name);
    if (res?.overview) applyOverview(res.overview);
    return res;
  } catch (e) {
    toast(e.message || String(e));
    return null;
  }
}

async function doUndo() {
  if (busy) return;
  try {
    const r = await api.undo();
    toast(`Undid: ${r.label || 'last edit'}`);
    if (r.overview) applyOverview(r.overview); else await refreshOverview();
  } catch (e) { toast(e.message || String(e)); }
}

async function doRedo() {
  if (busy) return;
  try {
    const r = await api.redo();
    toast(`Redid: ${r.label || 'edit'}`);
    if (r.overview) applyOverview(r.overview); else await refreshOverview();
  } catch (e) { toast(e.message || String(e)); }
}

// ---------------- Left Media & CapCut Asset Bin ----------------
function setBinTab(tab) {
  binTab = tab;
  document.querySelectorAll('.bin-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
  renderBin();
  if (['effects', 'transitions', 'filters'].includes(tab)) loadCatalogTab(tab);
}

async function loadCatalog(cat, query = '') {
  try {
    const items = await api.search_catalog(cat, query);
    catalogCache[cat] = Array.isArray(items) ? items : [];
    return catalogCache[cat];
  } catch { return []; }
}

async function loadCatalogTab(tab) {
  const cat = tab === 'effects' ? 'effect' : tab === 'transitions' ? 'transition' : 'filter';
  await loadCatalog(cat, binSearch);
  renderBin();
}

function renderBin() {
  const body = $('binBody');
  if (binTab === 'projects') { renderProjectsBin(body); return; }
  if (binTab === 'text') { renderTextBin(body); return; }
  if (binTab === 'stickers') { renderStickersBin(body); return; }
  if (binTab === 'captions') { renderCaptionsBin(body); return; }
  if (binTab === 'adjustment') { renderAdjustmentBin(body); return; }
  if (binTab === 'templates') { renderTemplatesBin(body); return; }
  if (['effects', 'transitions', 'filters'].includes(binTab)) { renderCatalogBin(body, binTab); return; }

  const isAudioTab = binTab === 'audio';
  const subItems = isAudioTab
    ? [{ id: 'audio', label: 'All Audio' }, { id: 'timeline', label: 'In Timeline' }]
    : [{ id: 'all', label: 'All' }, { id: 'video', label: 'Video' }, { id: 'photo', label: 'Photos' }, { id: 'audio', label: 'Audio' }, { id: 'timeline', label: 'In Timeline' }];

  if (isAudioTab && binFilter !== 'timeline') binFilter = 'audio';

  const list = localMediaPool.filter(item => {
    if (binFilter === 'timeline' && !item.in_timeline) return false;
    if (binFilter === 'video' && item.kind !== 'video') return false;
    if (binFilter === 'photo' && item.kind !== 'photo') return false;
    if (binFilter === 'audio' && item.kind !== 'audio') return false;
    if (binSearch && !item.name.toLowerCase().includes(binSearch.toLowerCase())) return false;
    return true;
  });
  window._visibleBinItems = list;

  const subNavHtml = `
    <div class="bin-subnav">
      <div class="subnav-label">Import</div>
      <button class="subnav-btn" onclick="importMediaFiles()"><span>Files</span><span>+</span></button>
      <button class="subnav-btn" onclick="importMediaFolder()"><span>Folder</span><span>+</span></button>
      <button class="subnav-btn" onclick="showYouTubeModal()"><span>YouTube</span><span>+</span></button>
      <div class="subnav-label">Library</div>
      ${subItems.map(s => `<button class="subnav-btn ${binFilter === s.id ? 'active' : ''}" onclick="setBinFilter('${s.id}')"><span>${s.label}</span></button>`).join('')}
      <button class="subnav-btn ${binTab === 'projects' ? 'active' : ''}" onclick="setBinTab('projects')"><span>Projects</span></button>
      <div class="subnav-label">Generate</div>
      <button class="subnav-btn" onclick="openEditPilot()"><span>EditPilot</span><span class="ai-mini">AI</span></button>
    </div>`;

  const cardsHtml = list.length ? list.map((item, idx) => {
    const durBadge = item.kind !== 'photo' && item.duration_s ? `<span class="badge-dur">${fmtTimeShort(item.duration_s)}</span>` : '';
    const addedBadge = item.in_timeline ? `<span class="badge-added">Added</span>` : '';
    const isSel = selectedBinPaths.has(item.path) ? 'bin-selected' : '';
    const thumbHtml = item.kind === 'audio'
      ? `<div class="media-thumb audio-thumb"><div class="audio-bars">${[12, 22, 16, 25, 14, 20, 18, 11].map(h => `<span style="height:${h}px"></span>`).join('')}</div>${addedBadge}${durBadge}<button class="quick-add" onclick="event.stopPropagation(); quickAddMediaByIdx(${idx})" title="Add to timeline">+</button></div>`
      : `<div class="media-thumb"><img src="${thumbUrl(item.path, 220)}" loading="lazy" alt="">${addedBadge}${durBadge}<button class="quick-add" onclick="event.stopPropagation(); quickAddMediaByIdx(${idx})" title="Add to timeline">+</button></div>`;
    return `<div class="media-card ${isSel}" data-idx="${idx}" draggable="true" onclick="onMediaCardClick(event, ${idx})" ondragstart="onMediaDragStartByIdx(event, ${idx})" ondragend="onMediaDragEnd()" ondblclick="quickAddMediaByIdx(${idx})" title="${esc(item.path)} (Drag onto timeline or Ctrl+Click multi-select)">
      ${thumbHtml}
      <div class="media-name">${esc(item.name)}</div>
    </div>`;
  }).join('') : `<div style="grid-column:1/-1;padding:24px 8px;text-align:center;color:var(--text-muted)">
    No media yet. Click <b>+ Import</b> or drop files anywhere.
  </div>`;

  body.innerHTML = `
    ${subNavHtml}
    <div class="bin-main">
      <div class="bin-controls">
        <button class="cyan" style="padding:4px 9px;font-size:11px" onclick="importMediaFiles()">+ Import</button>
        <button style="padding:4px 8px;font-size:11px" onclick="importMediaFolder()">Folder</button>
        <button style="padding:4px 8px;font-size:11px" onclick="showYouTubeModal()" title="Download song or video from YouTube">YouTube</button>
        <div class="search-box">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/></svg>
          <input value="${esc(binSearch)}" placeholder="Search media" oninput="binSearch=this.value; renderBin()">
        </div>
      </div>
      <div class="bin-section-head"><span>${subItems.find(s => s.id === binFilter)?.label || 'All'}${selectedBinPaths.size > 1 ? ` · ${selectedBinPaths.size} selected` : ''}</span><span>${list.length} items</span></div>
      <div class="media-grid">${cardsHtml}</div>
    </div>`;
}

function setBinFilter(f) { binFilter = f; renderBin(); }

function onMediaCardClick(ev, idx) {
  const item = window._visibleBinItems?.[idx];
  if (!item) return;
  if (ev.ctrlKey || ev.metaKey) {
    if (selectedBinPaths.has(item.path)) selectedBinPaths.delete(item.path);
    else selectedBinPaths.add(item.path);
  } else {
    selectedBinPaths.clear();
    selectedBinPaths.add(item.path);
  }
  renderBin();
}

function onMediaDragStartByIdx(ev, idx) {
  const item = window._visibleBinItems?.[idx];
  if (!item) return;
  let items = [item];
  if (selectedBinPaths.has(item.path) && selectedBinPaths.size > 1) {
    const multi = (window._visibleBinItems || []).filter(x => selectedBinPaths.has(x.path));
    if (multi.length) items = multi;
  } else {
    selectedBinPaths.clear();
    selectedBinPaths.add(item.path);
  }
  draggingBinItems = items;
  ev.dataTransfer.setData('application/x-aive-media', JSON.stringify(items));
  ev.dataTransfer.effectAllowed = 'copy';
}

function onMediaDragEnd() {
  draggingBinItems = null;
  clearTimelineDropIndicators();
}

function quickAddMediaByIdx(idx) {
  const item = window._visibleBinItems?.[idx];
  if (item) quickAddMedia(item.path, item.kind);
}

function renderTextBin(body) {
  const presets = [
    { name: 'Default Title', desc: 'Clean centered white heading', args: { text: 'New Title', font_size: 14, color: '#FFFFFF', bold: true, x: 0, y: 0.55 } },
    { name: 'Lower Third', desc: 'Bottom left caption', args: { text: 'Subtitle / Name', font_size: 9, color: '#FFFFFF', bold: false, x: -0.45, y: -0.7, background_color: '#000000' } },
    { name: 'Bold Highlight', desc: 'High-contrast yellow subtitle', args: { text: 'Highlight Text', font_size: 11, color: '#FFD700', bold: true, x: 0, y: -0.7, stroke_color: '#000000' } },
    { name: 'Cyan Accent', desc: 'CapCut teal headline', args: { text: 'Chapter Title', font_size: 13, color: '#00D2C8', bold: true, x: 0, y: 0.6 } },
    { name: 'Cinematic Shadow', desc: 'Italic serif with drop shadow', args: { text: 'Scene One', font_size: 15, color: '#F8FAFC', italic: true, shadow: true, x: 0, y: 0 } },
    { name: 'Boxed Pill', desc: 'Dark rounded caption tag', args: { text: 'KEY POINT', font_size: 10, color: '#FFFFFF', bold: true, background_color: '#111827', x: 0, y: -0.65 } },
  ];
  window._textPresets = presets;
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-section-head"><span>Text Presets (click to add at playhead)</span></div>
      <div class="catalog-list">
        ${presets.map((p, i) => `<button class="cat-card" onclick="addTextPreset(${i})"><div class="cname">${esc(p.name)}</div><div class="cdesc">${esc(p.desc)}</div></button>`).join('')}
      </div>
    </div>`;
}

async function addTextPreset(idx) {
  const p = window._textPresets[idx];
  if (!p) return;
  await runTool('add_text', { ...p.args, start: playheadSec, duration: 3.0 }, `Add text: ${p.name}`);
}

function renderStickersBin(body) {
  const stickers = [
    { name: '✦ Sparkle', args: { text: '✦', font_size: 22, color: '#00D2C8', bold: true, x: 0.55, y: 0.55 } },
    { name: '➜ Arrow Right', args: { text: '➜', font_size: 20, color: '#F59E0B', bold: true, x: 0, y: 0 } },
    { name: '★ Star Badge', args: { text: '★', font_size: 22, color: '#FFD700', bold: true, x: -0.5, y: 0.5 } },
    { name: '● REC', args: { text: '● REC', font_size: 10, color: '#EF4444', bold: true, background_color: '#111216', x: -0.65, y: 0.72 } },
    { name: 'SUBSCRIBE', args: { text: 'SUBSCRIBE', font_size: 11, color: '#FFFFFF', bold: true, background_color: '#DC2626', x: 0, y: -0.72 } },
    { name: 'NEW', args: { text: 'NEW', font_size: 11, color: '#071314', bold: true, background_color: '#00D2C8', x: 0.65, y: 0.72 } },
    { name: '✓ Verified', args: { text: '✓', font_size: 22, color: '#10B981', bold: true, x: 0.5, y: -0.5 } },
    { name: '♥ Like', args: { text: '♥', font_size: 22, color: '#F43F5E', bold: true, x: 0, y: 0.4 } },
  ];
  window._stickerPresets = stickers;
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-section-head"><span>Overlay Stickers (insert at playhead)</span></div>
      <div class="catalog-list">
        ${stickers.map((s, i) => `<button class="cat-card" onclick="addStickerPreset(${i})"><div class="cname">${esc(s.name)}</div><div class="cdesc">3.0s overlay</div></button>`).join('')}
      </div>
    </div>`;
}

async function addStickerPreset(idx) {
  const s = window._stickerPresets[idx];
  if (!s) return;
  await runTool('add_text', { ...s.args, start: playheadSec, duration: 3.0 }, `Add sticker ${s.name}`);
}

function renderCaptionsBin(body) {
  body.innerHTML = `
    <div class="bin-main">
      <div style="display:flex;gap:6px">
        <button class="cyan" style="flex:1" onclick="importSrtDialog()">+ Import .SRT</button>
        <button style="flex:1" onclick="runTool('add_text',{text:'Caption line',start:playheadSec,duration:2.5,font_size:9,color:'#FFFFFF',y:-0.75,stroke_color:'#000000'},'Add caption')">+ Add Line</button>
      </div>
      <div class="bin-section-head"><span>Caption Styles</span></div>
      <div class="catalog-list">
        <button class="cat-card" onclick="runTool('add_text',{text:'Bold Viral Caption',start:playheadSec,duration:2.5,font_size:11,color:'#FFD700',bold:true,y:-0.7,stroke_color:'#000000'},'Viral caption')">
          <div class="cname">Viral Yellow</div><div class="cdesc">Bold yellow + black stroke</div>
        </button>
        <button class="cat-card" onclick="runTool('add_text',{text:'Clean Subtitle',start:playheadSec,duration:2.5,font_size:9,color:'#FFFFFF',y:-0.75,stroke_color:'#000000'},'Clean caption')">
          <div class="cname">Clean White</div><div class="cdesc">Classic outlined subtitle</div>
        </button>
        <button class="cat-card" onclick="runTool('add_text',{text:'Dark Box Caption',start:playheadSec,duration:2.5,font_size:9,color:'#FFFFFF',y:-0.75,background_color:'#000000'},'Boxed caption')">
          <div class="cname">Dark Pill</div><div class="cdesc">White text on dark box</div>
        </button>
        <button class="cat-card" onclick="openEditPilot(); quickPrompt('Add timed captions in the style of clean subtitles at the bottom of the video')">
          <div class="cname">AI Captions</div><div class="cdesc">Generate via EditPilot</div>
        </button>
      </div>
    </div>`;
}

async function importSrtDialog() {
  if (!overview) { toast('Open a project first'); return; }
  const res = await api.pick_srt();
  if (!res?.text) return;
  await runTool('import_srt', { srt_text: res.text, font_size: 9, color: '#FFFFFF', y: -0.75, stroke_color: '#000000' }, 'Import SRT');
}

function renderAdjustmentBin(body) {
  const presets = [
    { name: 'Cinematic Teal & Orange', desc: 'High contrast + warm/cool split', vals: { contrast: 0.2, saturation: 0.15, temperature: -0.1, vignette: 0.3, shadows: -0.1 } },
    { name: 'Golden Hour', desc: 'Warm sunlight glow', vals: { brightness: 0.08, temperature: 0.35, saturation: 0.2, highlights: 0.1 } },
    { name: 'Cool Moody', desc: 'Crisp blue shadows', vals: { temperature: -0.35, contrast: 0.15, saturation: -0.15, vignette: 0.25 } },
    { name: 'Vivid Pop', desc: 'Punchy color & sharpness', vals: { contrast: 0.18, saturation: 0.35, sharpen: 0.3, brightness: 0.05 } },
    { name: 'High Contrast B&W', desc: 'Classic monochrome film', vals: { saturation: -1, contrast: 0.35, vignette: 0.25 } },
    { name: 'Vintage Fade', desc: 'Lifted matte film fade', vals: { fade: 0.35, temperature: 0.18, saturation: -0.2, contrast: -0.1 } },
    { name: 'Soft Pastel', desc: 'Bright & airy highlights', vals: { brightness: 0.12, contrast: -0.15, saturation: -0.1, fade: 0.15 } },
    { name: 'Reset Color', desc: 'Clear all color adjustments', vals: { reset: true } },
  ];
  window._adjustPresets = presets;
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-section-head"><span>Color Presets (${selectedClipId ? 'Selected Clip' : 'All Main Clips'})</span></div>
      <div class="catalog-list">
        ${presets.map((p, i) => `<button class="cat-card" onclick="applyAdjustPreset(${i})"><div class="cname">${esc(p.name)}</div><div class="cdesc">${esc(p.desc)}</div></button>`).join('')}
      </div>
    </div>`;
}

async function applyAdjustPreset(idx) {
  const p = window._adjustPresets[idx];
  if (!p) return;
  const ids = activeSelectionIds();
  const args = ids.length ? { ids, ...p.vals } : { all_main: true, ...p.vals };
  await runTool('set_adjustments', args, `Preset: ${p.name}`);
}

function renderTemplatesBin(body) {
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-section-head"><span>1-Click Editing Templates</span></div>
      <div class="catalog-list">
        <button class="cat-card" onclick="applySlideshowTemplate()"><div class="cname">Slideshow + Ken Burns</div><div class="cdesc">Alternating zoom/pan + dissolves</div></button>
        <button class="cat-card" onclick="runTool('set_canvas',{ratio:'9:16'},'Vertical 9:16')"><div class="cname">Reels / Shorts 9:16</div><div class="cdesc">Switch canvas to vertical</div></button>
        <button class="cat-card" onclick="runTool('set_canvas',{ratio:'21:9'},'Cinema 21:9')"><div class="cname">Cinematic 21:9</div><div class="cdesc">Ultrawide cinema frame</div></button>
        <button class="cat-card" onclick="runTool('set_track_mute',{track:'video#0',muted:true},'Mute video')"><div class="cname">Silent B-Roll</div><div class="cdesc">Mute main video audio</div></button>
        <button class="cat-card" onclick="closeMainGaps()"><div class="cname">Magnetic Pack</div><div class="cdesc">Close all gaps on main track</div></button>
        <button class="cat-card" onclick="runTool('set_adjustments',{all_main:true,reset:true},'Reset color')"><div class="cname">Clean Grade</div><div class="cdesc">Reset main-track color</div></button>
      </div>
    </div>`;
}

async function applySlideshowTemplate() {
  await runTool('add_motion', { all_main_photos: true, preset: 'alternate', strength: 0.15 }, 'Ken Burns photos');
  await runTool('set_transitions', { all_main: true, name: 'Cross Dissolve', duration: 0.6 }, 'Dissolve transitions');
}

function renderCatalogBin(body, tab) {
  const cat = tab === 'effects' ? 'effect' : tab === 'transitions' ? 'transition' : 'filter';
  const items = catalogCache[cat] || [];
  const filtered = items.filter(it => !binSearch || (it.name || '').toLowerCase().includes(binSearch.toLowerCase()));
  window._visibleCatalogItems = filtered;
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-controls">
        <div class="search-box">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/></svg>
          <input value="${esc(binSearch)}" placeholder="Search ${tab}..." oninput="binSearch=this.value; loadCatalogTab('${tab}')">
        </div>
      </div>
      <div class="bin-section-head"><span>${tab.charAt(0).toUpperCase() + tab.slice(1)}</span><span>${filtered.length}</span></div>
      <div class="catalog-list">
        ${filtered.length ? filtered.map((it, idx) => {
          if (it.note) return `<div style="grid-column:1/-1;color:var(--text-muted);padding:10px">${esc(it.note)}</div>`;
          const desc = it.description || (it.used_before ? 'Used before in CapCut' : (it.vip ? 'Pro item' : 'Ready'));
          return `<button class="cat-card" onclick="applyCatalogByIdx('${cat}', ${idx})"><div class="cname">${esc(it.name)}</div><div class="cdesc">${esc(desc)}</div></button>`;
        }).join('') : `<div style="grid-column:1/-1;color:var(--text-muted);padding:16px;text-align:center">No ${tab} found in catalog.</div>`}
      </div>
    </div>`;
}

function applyCatalogByIdx(cat, idx) {
  const it = window._visibleCatalogItems?.[idx];
  if (it?.name) applyCatalogItem(cat, it.name);
}

async function applyCatalogItem(cat, name) {
  if (!overview) { toast('Open a project first'); return; }
  if (cat === 'transition') {
    const ids = activeSelectionIds();
    if (ids.length) await runTool('set_transitions', { ids, name, duration: 0.6 }, `Transition ${name}`);
    else await runTool('set_transitions', { all_main: true, name, duration: 0.6 }, `Transitions ${name}`);
  } else {
    await runTool('add_filter_or_effect', { category: cat, name, start: playheadSec, duration: 3.0, intensity: 1.0 }, `Add ${cat} ${name}`);
  }
}

function renderProjectsBin(body) {
  const edOpts = (appState?.editors || []).map(e =>
    `<option value="${e.id}" ${e.id === appState.editor ? 'selected' : ''}>${esc(e.display)}${e.installed ? ' (Installed)' : ''}${e.exportOnly ? ' (Import)' : ''}</option>`).join('');
  const ed = edInfo();
  const hint = ed ? (DIRECT_EDITORS.includes(ed.id) ? `Direct editing of ${ed.display} project files.` : `Writes import files (${ed.hint}).`) : '';
  const listHtml = (window._projectList || []).map((p, idx) =>
    `<div class="pitem ${currentProjectPath === p.path ? 'on' : ''}" onclick="openProjectByIdx(${idx})" title="${esc(p.path)}">
      <span>${esc(p.name)}</span>
      <small>${p.duration ? fmtTimeShort(p.duration) : esc((p.modified || '').split(' ')[0])}</small>
    </div>`).join('') || '<div style="color:var(--text-muted);padding:10px">No projects found — create a new project or open a file.</div>';

  body.innerHTML = `
    <div class="proj-pane">
      <label style="font-size:11px;color:var(--text-muted)">Target Video Editor</label>
      <select onchange="switchEditor(this.value)">${edOpts}</select>
      <div style="font-size:11px;color:var(--text-muted)">${esc(hint)}</div>
      <div style="display:flex;gap:6px">
        <button class="cyan" style="flex:1" onclick="showNewProject()">+ New</button>
        <button style="flex:1" onclick="openFile()">Open File</button>
        <button onclick="loadProjects()" title="Refresh project list">${SVG.refresh}</button>
      </div>
      <div class="plist">${listHtml}</div>
    </div>`;
}

function openProjectByIdx(idx) {
  const p = window._projectList?.[idx];
  if (p?.path) openProject(p.path);
}

async function importMediaFiles() {
  const files = await api.pick_files();
  if (!files || !files.length) return;
  await probeAndAddToBin(files);
}

async function importMediaFolder() {
  const folder = await api.pick_folder();
  if (!folder) return;
  await probeAndAddToBin([folder]);
}

async function probeAndAddToBin(paths) {
  const probed = await api.probe_media(paths);
  if (!probed || !probed.length) { toast('No supported media files found'); return; }
  const map = new Map(localMediaPool.map(x => [x.path.toLowerCase(), x]));
  for (const item of probed) {
    const k = item.path.toLowerCase();
    if (!map.has(k)) map.set(k, { ...item, in_timeline: false });
  }
  localMediaPool = Array.from(map.values());
  toast(`Imported ${probed.length} media item(s)`);
  if (binTab !== 'media' && binTab !== 'audio') setBinTab('media');
  else renderBin();
}

async function quickAddMedia(path, kind, startSec = null, target = 'main') {
  if (!overview) { toast('Open or create a project first'); return; }
  if (kind === 'audio') {
    const args = { path, start: startSec ?? 0 };
    if (startSec == null) args.fit_to_video = projectDuration() > 0;
    await runTool('add_audio', args, 'Add audio');
  } else {
    const args = { paths: [path], target };
    if (startSec != null) args.start = startSec;
    await runTool('add_media', args, 'Add media');
  }
}

// ---------------- Preview Stage & Live Transform Gizmo ----------------
function toggleGrid() {
  const g = $('safeGrid');
  g.classList.toggle('on');
  $('btnGrid').classList.toggle('active', g.classList.contains('on'));
}

function toggleFullscreenStage() {
  const vp = $('stageWrap');
  if (!document.fullscreenElement) vp.requestFullscreen?.();
  else document.exitFullscreen?.();
}

function layoutStage() {
  const wrap = $('stageWrap');
  const vp = $('stageViewport');
  const w = overview?.width || 1920;
  const h = overview?.height || 1080;
  const aw = Math.max(100, wrap.clientWidth - 20);
  const ah = Math.max(100, wrap.clientHeight - 20);
  const zoomVal = $('stageZoom').value;
  const scale = zoomVal === 'fit' ? Math.min(aw / w, ah / h) : Math.min(aw / w, ah / h) * Number(zoomVal);
  vp.style.width = `${Math.round(w * scale)}px`;
  vp.style.height = `${Math.round(h * scale)}px`;
}
window.addEventListener('resize', () => { layoutStage(); renderTimeline(); });

async function changeCanvasRatio(ratio) {
  await runTool('set_canvas', { ratio }, `Canvas ${ratio}`);
  layoutStage();
}

function buildCssFilter(adj) {
  if (!adj) return '';
  const br = 1 + (adj.brightness || 0) * 0.6 + (adj.highlights || 0) * 0.12 + (adj.shadows || 0) * 0.12;
  const ct = 1 + (adj.contrast || 0) * 0.8 - (adj.fade || 0) * 0.22;
  const sat = Math.max(0, 1 + (adj.saturation || 0));
  const hue = (adj.hue || 0) + (adj.temperature || 0) * -14;
  const sep = Math.max(0, (adj.temperature || 0) * 0.25 + (adj.fade || 0) * 0.22);
  return `brightness(${br.toFixed(2)}) contrast(${ct.toFixed(2)}) saturate(${sat.toFixed(2)}) hue-rotate(${hue.toFixed(1)}deg) sepia(${sep.toFixed(2)})`;
}

function positionStageGizmo(clip, vp) {
  const gizmo = $('stageGizmo');
  if (!clip || clip.id !== selectedClipId || clip.kind === 'audio') {
    gizmo.style.display = 'none';
    return;
  }
  const [px, py] = clip.pos || (clip.kind === 'text' ? [0, -0.7] : [0, 0]);
  const sc = clip.scale ?? 1;
  const w = vp.clientWidth || 640;
  const h = vp.clientHeight || 360;
  const bw = clip.kind === 'text' ? Math.max(120, Math.round(w * 0.48 * sc)) : Math.max(40, Math.round(w * sc));
  const bh = clip.kind === 'text' ? Math.max(42, Math.round(h * 0.18 * sc)) : Math.max(30, Math.round(h * sc));
  const cx = w * (0.5 + px * 0.5);
  const cy = h * (0.5 - py * 0.5);
  gizmo.style.display = 'block';
  gizmo.style.width = `${bw}px`;
  gizmo.style.height = `${bh}px`;
  gizmo.style.left = `${Math.round(cx - bw / 2)}px`;
  gizmo.style.top = `${Math.round(cy - bh / 2)}px`;
  gizmo.style.transform = `rotate(${clip.rot ?? 0}deg)`;
  gizmo.dataset.clipId = clip.id;
}

function renderStage() {
  layoutStage();
  const vp = $('stageViewport');
  const empty = $('stageEmpty');
  const vig = $('stageVignette');
  const gizmo = $('stageGizmo');
  const fps = projectFps();
  $('tcCur').textContent = fmtSmpte(playheadSec, fps);
  $('tcTot').textContent = fmtSmpte(projectDuration(), fps);

  if (!overview) {
    empty.style.display = 'flex';
    vig.style.background = 'none';
    gizmo.style.display = 'none';
    vp.style.background = '#000';
    clearStageMedia();
    return;
  }
  empty.style.display = 'none';

  const visualClips = [];
  const audioClips = [];
  for (const t of overview.tracks) {
    if (t.hidden) continue;
    for (const c of t.segments) {
      if (playheadSec >= c.start && playheadSec < c.end) {
        if (t.type === 'audio' || c.kind === 'audio') {
          if (!t.muted) audioClips.push({ track: t, clip: c });
        } else if (c.kind === 'video' || c.kind === 'photo' || c.kind === 'text') {
          visualClips.push({ track: t, clip: c });
        }
      }
    }
  }
  visualClips.sort((a, b) => (a.track.is_main ? -1 : b.track.is_main ? 1 : 0));

  vp.querySelectorAll('.stage-layer, .stage-text-layer, .stage-bg-blur').forEach(n => n.remove());

  const activeVidIds = new Set();
  const stageW = vp.clientWidth || 640;
  let maxVignette = 0;
  let gizmoTarget = null;
  vp.style.background = '#000';

  for (const { track, clip } of visualClips) {
    if (clip.visible === false || clip.opacity === 0) continue;
    if (clip.adjust?.vignette) maxVignette = Math.max(maxVignette, clip.adjust.vignette);
    if (clip.id === selectedClipId) gizmoTarget = clip;

    if (clip.kind === 'text') {
      const div = document.createElement('div');
      div.className = 'stage-text-layer';
      div.dataset.clipId = clip.id;
      const [px, py] = clip.pos || [0, -0.7];
      const scale = clip.scale ?? 1;
      const rot = clip.rot ?? 0;
      div.style.left = `${50 + px * 50}%`;
      div.style.top = `${50 - py * 50}%`;
      div.style.transform = `translate(-50%, -50%) scale(${scale}) rotate(${rot}deg)`;
      div.style.opacity = String(clip.opacity ?? 1);
      div.style.color = clip.color || '#FFFFFF';
      div.style.fontWeight = clip.bold ? '700' : '500';
      div.style.fontStyle = clip.italic ? 'italic' : 'normal';
      div.style.textAlign = clip.align || 'center';
      if (clip.font) div.style.fontFamily = clip.font;
      if (clip.letter_spacing) div.style.letterSpacing = `${clip.letter_spacing * 12}px`;
      if (clip.line_spacing) div.style.lineHeight = String(1.2 + clip.line_spacing * 4);
      if (clip.stroke_color) div.style.webkitTextStroke = `${Math.max(1, (clip.stroke_width || 0.08) * 18)}px ${clip.stroke_color}`;
      if (clip.background_color) {
        div.style.backgroundColor = clip.background_color;
        div.style.padding = '4px 12px';
        div.style.borderRadius = '6px';
      }
      div.style.textShadow = clip.shadow ? '0 3px 10px rgba(0,0,0,0.95)' : '0 2px 6px rgba(0,0,0,0.7)';
      div.style.fontSize = `${Math.max(12, Math.round((clip.font_size || 8) * stageW / 260))}px`;
      div.textContent = clip.text || '';
      vp.appendChild(div);
      continue;
    }

    if (track.is_main) {
      if (clip.canvas_color) vp.style.background = clip.canvas_color;
      if (clip.canvas_blur > 0 && clip.path) {
        const bg = document.createElement('div');
        bg.className = 'stage-bg-blur';
        bg.style.cssText = `position:absolute;inset:-14px;background:url('${thumbUrl(clip.path, 320)}') center/cover no-repeat;filter:blur(${Math.round(clip.canvas_blur * 28)}px) brightness(0.75);pointer-events:none;`;
        vp.appendChild(bg);
      }
    }

    const layer = document.createElement('div');
    layer.className = 'stage-layer';
    layer.dataset.clipId = clip.id;
    const [px, py] = clip.pos || [0, 0];
    const sc = clip.scale ?? 1;
    const rot = clip.rot ?? 0;
    const fx = clip.flip_h ? -1 : 1;
    const fy = clip.flip_v ? -1 : 1;
    layer.style.opacity = String(clip.opacity ?? 1);
    layer.style.transform = `translate(${px * 50}%, ${-py * 50}%) scale(${sc * fx}, ${sc * fy}) rotate(${rot}deg)`;
    if (clip.blend_mode && clip.blend_mode !== 'normal') layer.style.mixBlendMode = clip.blend_mode;
    const filterStr = buildCssFilter(clip.adjust);
    if (filterStr) layer.style.filter = filterStr;
    if (clip.crop) {
      const [cl, cr, ct, cb] = clip.crop;
      layer.style.clipPath = `inset(${ct * 100}% ${cr * 100}% ${cb * 100}% ${cl * 100}%)`;
    }
    if (clip.corner_radius > 0) layer.style.borderRadius = `${Math.round(clip.corner_radius * 80)}px`;

    if (clip.kind === 'photo' && clip.path) {
      const img = document.createElement('img');
      img.src = mediaUrl(clip.path);
      if (clip.corner_radius > 0) img.style.borderRadius = `${Math.round(clip.corner_radius * 80)}px`;
      layer.appendChild(img);
    } else if (clip.kind === 'video' && clip.path) {
      activeVidIds.add(clip.id);
      const elapsed = Math.max(0, playheadSec - clip.start) * (clip.speed || 1);
      const targetTime = clip.reverse
        ? Math.max(0, (clip.source_in || 0) + (clip.dur * (clip.speed || 1)) - elapsed)
        : Math.max(0, (clip.source_in || 0) + elapsed);
      const posterImg = document.createElement('img');
      posterImg.src = thumbUrl(clip.path, 960);
      posterImg.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:contain;pointer-events:none;';
      layer.appendChild(posterImg);

      let vid = activeVideos.get(clip.id);
      if (!vid || vid.dataset.path !== clip.path) {
        vid = document.createElement('video');
        vid.dataset.path = clip.path;
        vid.src = mediaUrl(clip.path);
        vid.playsInline = true;
        vid.preload = 'auto';
        vid._targetTime = targetTime;
        vid._decodeFailed = false;
        vid.addEventListener('loadedmetadata', () => {
          try { vid.currentTime = Math.max(0.01, vid._targetTime || 0.01); } catch { }
        }, { once: true });
        vid.addEventListener('loadeddata', () => {
          if (vid.videoWidth > 0) posterImg.style.display = 'none';
        });
        vid.addEventListener('error', () => {
          vid._decodeFailed = true;
          vid.style.display = 'none';
          posterImg.style.display = '';
        });
        activeVideos.set(clip.id, vid);
      }
      if (vid.readyState >= 2 && vid.videoWidth > 0 && !vid._decodeFailed) posterImg.style.display = 'none';
      vid._targetTime = targetTime;
      vid.muted = !!track.muted || (clip.volume === 0);
      vid.volume = Math.min(1, Math.max(0, clip.volume ?? 1));
      vid.playbackRate = clip.speed || 1;
      if (!vid._decodeFailed && vid.readyState >= 1 && Math.abs((vid.currentTime || 0) - targetTime) > (playing ? 0.25 : 0.04)) {
        try { vid.currentTime = targetTime; } catch { }
      }
      if (!vid._decodeFailed && playing && !clip.reverse && vid.paused) vid.play().catch(() => {});
      else if ((!playing || clip.reverse) && !vid.paused) vid.pause();
      if (!vid._decodeFailed) layer.appendChild(vid);
    }
    vp.appendChild(layer);
  }

  positionStageGizmo(gizmoTarget, vp);

  vig.style.background = maxVignette > 0
    ? `radial-gradient(circle, rgba(0,0,0,0) 45%, rgba(0,0,0,${(maxVignette * 0.85).toFixed(2)}) 100%)`
    : 'none';

  for (const [id, vid] of activeVideos) {
    if (!activeVidIds.has(id)) { vid.pause(); activeVideos.delete(id); }
  }

  const activeAudIds = new Set();
  for (const { clip } of audioClips) {
    if (!clip.path) continue;
    activeAudIds.add(clip.id);
    let aud = activeAudios.get(clip.id);
    if (!aud || aud.dataset.path !== clip.path) {
      aud = new Audio(mediaUrl(clip.path));
      aud.dataset.path = clip.path;
      activeAudios.set(clip.id, aud);
    }
    aud.volume = Math.min(1, Math.max(0, clip.volume ?? 1));
    aud.playbackRate = clip.speed || 1;
    const targetTime = Math.max(0, (clip.source_in || 0) + (playheadSec - clip.start) * (clip.speed || 1));
    if (Math.abs((aud.currentTime || 0) - targetTime) > (playing ? 0.25 : 0.06)) {
      try { aud.currentTime = targetTime; } catch { }
    }
    if (playing && aud.paused) aud.play().catch(() => {});
    else if (!playing && !aud.paused) aud.pause();
  }
  for (const [id, aud] of activeAudios) {
    if (!activeAudIds.has(id)) { aud.pause(); activeAudios.delete(id); }
  }
}

// Canvas Transform Gizmo (drag to move, corner handle to scale)
$('stageGizmo').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const hit = findClipById(selectedClipId);
  if (!hit) return;
  e.preventDefault();
  e.stopPropagation();
  const corner = e.target.dataset.corner || null;
  const vp = $('stageViewport');
  const [px, py] = hit.clip.pos || (hit.clip.kind === 'text' ? [0, -0.7] : [0, 0]);
  $('stageGizmo').setPointerCapture(e.pointerId);
  gizmoDrag = {
    mode: corner ? 'scale' : 'move',
    corner,
    id: hit.clip.id,
    kind: hit.clip.kind,
    startX: e.clientX,
    startY: e.clientY,
    vpW: vp.clientWidth || 640,
    vpH: vp.clientHeight || 360,
    origX: px,
    origY: py,
    origScale: hit.clip.scale ?? 1,
    curX: px,
    curY: py,
    curScale: hit.clip.scale ?? 1,
  };
});

window.addEventListener('pointermove', e => {
  if (!gizmoDrag) return;
  const dx = e.clientX - gizmoDrag.startX;
  const dy = e.clientY - gizmoDrag.startY;
  const layer = $(`stageViewport`).querySelector(`[data-clip-id="${gizmoDrag.id}"]`);
  if (gizmoDrag.mode === 'move') {
    gizmoDrag.curX = Number((gizmoDrag.origX + (dx * 2) / gizmoDrag.vpW).toFixed(3));
    gizmoDrag.curY = Number((gizmoDrag.origY - (dy * 2) / gizmoDrag.vpH).toFixed(3));
    if (layer) {
      if (gizmoDrag.kind === 'text') {
        layer.style.left = `${50 + gizmoDrag.curX * 50}%`;
        layer.style.top = `${50 - gizmoDrag.curY * 50}%`;
      } else {
        const hit = findClipById(gizmoDrag.id)?.clip;
        const fx = hit?.flip_h ? -1 : 1, fy = hit?.flip_v ? -1 : 1;
        layer.style.transform = `translate(${gizmoDrag.curX * 50}%, ${-gizmoDrag.curY * 50}%) scale(${gizmoDrag.origScale * fx}, ${gizmoDrag.origScale * fy}) rotate(${hit?.rot ?? 0}deg)`;
      }
    }
    const gizmo = $('stageGizmo');
    const bw = gizmo.offsetWidth, bh = gizmo.offsetHeight;
    gizmo.style.left = `${Math.round(gizmoDrag.vpW * (0.5 + gizmoDrag.curX * 0.5) - bw / 2)}px`;
    gizmo.style.top = `${Math.round(gizmoDrag.vpH * (0.5 - gizmoDrag.curY * 0.5) - bh / 2)}px`;
  } else {
    const signX = gizmoDrag.corner.includes('r') ? 1 : -1;
    const signY = gizmoDrag.corner.includes('b') ? 1 : -1;
    const delta = (dx * signX + dy * signY) / Math.max(120, gizmoDrag.vpW);
    gizmoDrag.curScale = Math.max(0.1, Math.min(4, Number((gizmoDrag.origScale + delta * 1.5).toFixed(3))));
    if (layer) {
      if (gizmoDrag.kind === 'text') {
        const hit = findClipById(gizmoDrag.id)?.clip;
        layer.style.transform = `translate(-50%, -50%) scale(${gizmoDrag.curScale}) rotate(${hit?.rot ?? 0}deg)`;
      } else {
        const hit = findClipById(gizmoDrag.id)?.clip;
        const fx = hit?.flip_h ? -1 : 1, fy = hit?.flip_v ? -1 : 1;
        layer.style.transform = `translate(${gizmoDrag.origX * 50}%, ${-gizmoDrag.origY * 50}%) scale(${gizmoDrag.curScale * fx}, ${gizmoDrag.curScale * fy}) rotate(${hit?.rot ?? 0}deg)`;
      }
    }
  }
});

window.addEventListener('pointerup', async () => {
  if (!gizmoDrag) return;
  const g = gizmoDrag;
  gizmoDrag = null;
  if (g.mode === 'move' && (Math.abs(g.curX - g.origX) > 0.005 || Math.abs(g.curY - g.origY) > 0.005)) {
    await runTool('edit_segments', { ids: [g.id], x: g.curX, y: g.curY }, 'Move on canvas');
  } else if (g.mode === 'scale' && Math.abs(g.curScale - g.origScale) > 0.01) {
    await runTool('edit_segments', { ids: [g.id], scale: g.curScale }, 'Scale on canvas');
  }
});

function clearStageMedia() {
  for (const v of activeVideos.values()) v.pause();
  activeVideos.clear();
  for (const a of activeAudios.values()) a.pause();
  activeAudios.clear();
  $('stageViewport').querySelectorAll('.stage-layer, .stage-text-layer, .stage-bg-blur').forEach(n => n.remove());
}

function togglePlay() {
  if (!overview || projectDuration() <= 0) return;
  playing = !playing;
  const btn = $('btnPlay');
  btn.classList.toggle('playing', playing);
  $('playIcon').innerHTML = playing
    ? '<path d="M6 5h4v14H6zm8 0h4v14h-4z"/>'
    : '<path d="M8 5v14l11-7z"/>';

  if (playing) {
    if (playheadSec >= projectDuration() - 0.02) playheadSec = 0;
    lastFrameTs = performance.now();
    rafId = requestAnimationFrame(tickPlayback);
  } else {
    cancelAnimationFrame(rafId);
    renderStage();
  }
}

function tickPlayback(ts) {
  if (!playing) return;
  const dt = Math.min(0.1, (ts - lastFrameTs) / 1000);
  lastFrameTs = ts;
  playheadSec += dt;
  const dur = projectDuration();
  if (playheadSec >= dur) {
    playheadSec = dur;
    togglePlay();
    updatePlayheadDom();
    return;
  }
  updatePlayheadDom();
  renderStage();
  rafId = requestAnimationFrame(tickPlayback);
}

function seekTo(sec) {
  playheadSec = Math.max(0, Math.min(sec, Math.max(projectDuration(), 60)));
  updatePlayheadDom();
  renderStage();
}

function stepFrames(delta) {
  if (playing) togglePlay();
  seekTo(Math.max(0, playheadSec + delta / projectFps()));
}

// ---------------- Right Inspector & CapCut Details Sub-Tabs ----------------
function setInspTab(tab) {
  inspTab = tab;
  document.querySelectorAll('.insp-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
  $('inspProject').style.display = tab === 'project' ? 'flex' : 'none';
  $('inspDetails').style.display = tab === 'details' ? 'flex' : 'none';
  $('inspChat').style.display = tab === 'editpilot' ? 'flex' : 'none';
  $('editPilotFab').style.display = tab === 'editpilot' ? 'none' : 'flex';
  if (tab === 'editpilot') setTimeout(() => $('chatInput').focus(), 20);
}

function openEditPilot() { setInspTab('editpilot'); }

function renderInspector() {
  renderProjectInspector();
  renderDetailsInspector();
}

function renderProjectInspector() {
  const el = $('inspProject');
  const mainTrack = overview?.tracks?.find(t => t.is_main);
  const mainMuted = !!mainTrack?.muted;
  el.innerHTML = `
    <div class="smart-card">
      <div class="smart-head">
        <span>Smart suggestions</span>
        <span class="ai-mini">AI</span>
      </div>
      <div class="smart-sub">Find out how your video can be improved with EditPilot.</div>
      <div><button class="cyan" onclick="analyzeWithAI()">${SVG.sparkle} Analyze</button></div>
    </div>
    <div class="section-title">Global edits</div>
    <div class="global-list">
      <div class="global-row" onclick="runTool('set_adjustments', { all_main: true, contrast: 0.14, saturation: 0.12, sharpen: 0.15, vignette: 0.15 }, 'Make colors better')">
        <div class="global-row-left">${SVG.palette}<span>Make colors better</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="runTool('set_adjustments', { all_main: true, contrast: 0.08, saturation: 0.05, brightness: 0.02, temperature: 0 }, 'Make colors consistent')">
        <div class="global-row-left">${SVG.sliders}<span>Make colors consistent</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="makeVolumeConsistent()">
        <div class="global-row-left">${SVG.vol}<span>Make volume consistent</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="makeVoiceClearer()">
        <div class="global-row-left">${SVG.mic}<span>Make voice clearer</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="runTool('set_transitions', { all_main: true, name: 'Cross Dissolve', duration: 0.6 }, 'Add smooth transitions')">
        <div class="global-row-left">${SVG.trans}<span>Add smooth transitions</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="runTool('add_motion', { all_main_photos: true, preset: 'alternate', strength: 0.15 }, 'Add Ken Burns motion')">
        <div class="global-row-left">${SVG.motion}<span>Add Ken Burns motion</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="toggleMainMute()">
        <div class="global-row-left">${SVG.mute}<span>Mute original video audio</span></div>
        <span class="toggle-box ${mainMuted ? 'on' : ''}">${mainMuted ? SVG.check : ''}</span>
      </div>
    </div>`;
}

async function makeVolumeConsistent() {
  const mainTrack = overview?.tracks?.find(t => t.is_main);
  const ids = (mainTrack?.segments || []).filter(s => s.kind === 'video').map(s => s.id);
  if (!ids.length) { toast('No video clips on main track'); return; }
  await runTool('edit_segments', { ids, volume: 1 }, 'Make volume consistent');
}

async function makeVoiceClearer() {
  const mainTrack = overview?.tracks?.find(t => t.is_main);
  const ids = (mainTrack?.segments || []).filter(s => s.kind === 'video').map(s => s.id);
  if (!ids.length) { toast('No video clips on main track'); return; }
  await runTool('edit_segments', { ids, noise_reduction: true, vocal_isolation: 'keep_vocals' }, 'Make voice clearer');
}

async function toggleMainMute() {
  const mainTrack = overview?.tracks?.find(t => t.is_main);
  if (!mainTrack) return;
  await runTool('set_track_mute', { track: 'video#0', muted: !mainTrack.muted }, 'Toggle main mute');
}

function analyzeWithAI() {
  openEditPilot();
  quickPrompt('Inspect my current timeline with get_project_overview and suggest 3 concrete improvements we can apply.');
}

function setDetailSubTab(tab) {
  detailSubTab = tab;
  renderDetailsInspector();
  if (tab === 'animation') {
    const hit = findClipById(selectedClipId);
    if (hit) {
      const cats = hit.clip.kind === 'text' ? ['text_in', 'text_out', 'text_loop'] : ['video_in', 'video_out', 'video_combo'];
      Promise.all(cats.map(c => catalogCache[c] ? null : loadCatalog(c))).then(() => renderDetailsInspector());
    }
  }
  if (tab === 'text' && !catalogCache['font']) {
    loadCatalog('font').then(() => renderDetailsInspector());
  }
}

function renderDetailsInspector() {
  const el = $('inspDetails');
  const hit = findClipById(selectedClipId);
  if (!hit) {
    if (!overview) {
      el.innerHTML = `<div style="color:var(--text-muted);padding:16px;text-align:center">Open a project and select a clip on the timeline to inspect its CapCut properties.</div>`;
      return;
    }
    const segCount = overview.tracks.reduce((a, t) => a + t.segments.length, 0);
    el.innerHTML = `
      <div class="prop-group">
        <div class="prop-group-title">Project Properties</div>
        <div class="prop-row"><label>Project</label><span>${esc(overview.project)}</span></div>
        <div class="prop-row"><label>Editor</label><span>${esc(overview.editor)}</span></div>
        <div class="prop-row"><label>Canvas</label><span>${esc(overview.canvas)}</span></div>
        <div class="prop-row"><label>Frame Rate</label><span>${overview.fps} fps</span></div>
        <div class="prop-row"><label>Duration</label><span>${fmtTimeShort(overview.duration_s)} (${overview.duration_s}s)</span></div>
        <div class="prop-row"><label>Tracks / Clips</label><span>${overview.tracks.length} tracks · ${segCount} clips</span></div>
      </div>`;
    return;
  }

  const { clip } = hit;
  const selCount = activeSelectionIds().length;
  const title = clip.text || clip.file || clip.name || clip.id;

  let tabs;
  if (clip.kind === 'text') tabs = [['text', 'Text'], ['animation', 'Animation']];
  else if (clip.kind === 'audio') tabs = [['basic', 'Audio'], ['speed', 'Speed']];
  else if (clip.kind === 'video') tabs = [['basic', 'Basic'], ['speed', 'Speed'], ['animation', 'Anim'], ['adjust', 'Adjust'], ['audio', 'Audio']];
  else tabs = [['basic', 'Basic'], ['animation', 'Anim'], ['adjust', 'Adjust']];

  if (!tabs.some(([k]) => k === detailSubTab)) detailSubTab = tabs[0][0];

  let html = `
    <div class="prop-group">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:6px">
        <span style="font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(title)}${selCount > 1 ? ` (+${selCount - 1})` : ''}</span>
        <span class="ai-mini">${esc(clip.kind.toUpperCase())}</span>
      </div>
      <div class="sub-tabs">
        ${tabs.map(([k, lbl]) => `<button class="sub-tab ${detailSubTab === k ? 'active' : ''}" onclick="setDetailSubTab('${k}')">${lbl}</button>`).join('')}
      </div>
    </div>`;

  if (detailSubTab === 'basic') html += renderBasicTab(clip);
  else if (detailSubTab === 'speed') html += renderSpeedTab(clip);
  else if (detailSubTab === 'animation') html += renderAnimTab(clip);
  else if (detailSubTab === 'adjust') html += renderAdjustTab(clip);
  else if (detailSubTab === 'audio') html += renderAudioTab(clip);
  else if (detailSubTab === 'text') html += renderTextTab(clip);

  el.innerHTML = html;
}

function renderBasicTab(clip) {
  const [px, py] = clip.pos || [0, 0];
  const [cl, cr, ct, cb] = clip.crop || [0, 0, 0, 0];
  if (clip.kind === 'audio') return renderAudioTab(clip);
  const blends = ['normal', 'multiply', 'screen', 'overlay', 'darken', 'lighten', 'color-dodge', 'soft-light'];
  return `
    <div class="prop-group">
      <div class="prop-group-title">Timing &amp; Actions</div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Start (s)</label><input type="number" step="0.1" value="${clip.start}" onchange="editClipProp('${clip.id}', { start: Number(this.value) })"></div>
        <div class="prop-field"><label>Duration (s)</label><input type="number" step="0.1" min="0.1" value="${clip.dur}" onchange="editClipProp('${clip.id}', { duration: Number(this.value) })"></div>
      </div>
      <div class="pill-row">
        <button class="mini-pill" onclick="splitSelectedAtPlayhead()">Split</button>
        <button class="mini-pill" onclick="duplicateSelected()">Duplicate</button>
        <button class="mini-pill" onclick="freezeSelected()">Freeze</button>
        <button class="mini-pill" onclick="replaceSelectedMedia()">Replace</button>
        <button class="mini-pill" onclick="copyClipAttributes()">Copy Attrs</button>
        ${copiedAttributes ? `<button class="mini-pill on" onclick="pasteClipAttributes()">Paste Attrs</button>` : ''}
      </div>
    </div>

    <div class="prop-group">
      <div class="prop-group-title">Transform &amp; Blend</div>
      <div class="prop-row"><label>Scale</label><div class="range-wrap"><input type="range" min="0.1" max="3" step="0.05" value="${clip.scale ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { scale: Number(this.value) })"><span class="range-val">${Math.round((clip.scale ?? 1) * 100)}%</span></div></div>
      <div class="prop-row"><label>Opacity</label><div class="range-wrap"><input type="range" min="0" max="1" step="0.05" value="${clip.opacity ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { opacity: Number(this.value) })"><span class="range-val">${Math.round((clip.opacity ?? 1) * 100)}%</span></div></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Pos X</label><input type="number" step="0.05" min="-2" max="2" value="${px}" onchange="editClipProp('${clip.id}', { x: Number(this.value) })"></div>
        <div class="prop-field"><label>Pos Y</label><input type="number" step="0.05" min="-2" max="2" value="${py}" onchange="editClipProp('${clip.id}', { y: Number(this.value) })"></div>
      </div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Rotation (°)</label><input type="number" step="1" value="${clip.rot ?? 0}" onchange="editClipProp('${clip.id}', { rotation: Number(this.value) })"></div>
        <div class="prop-field"><label>Blend Mode</label>
          <select onchange="editClipProp('${clip.id}', { blend_mode: this.value })">
            ${blends.map(b => `<option value="${b}" ${(clip.blend_mode || 'normal') === b ? 'selected' : ''}>${b}</option>`).join('')}
          </select>
        </div>
      </div>
      <div class="pill-row">
        <button class="mini-pill" onclick="editClipProp('${clip.id}', { rotation: ${(clip.rot || 0) - 90} })">↺ -90°</button>
        <button class="mini-pill" onclick="editClipProp('${clip.id}', { rotation: ${(clip.rot || 0) + 90} })">↻ +90°</button>
        <button class="mini-pill ${clip.flip_h ? 'on' : ''}" onclick="editClipProp('${clip.id}', { flip_horizontal: ${!clip.flip_h} })">Flip H</button>
        <button class="mini-pill ${clip.flip_v ? 'on' : ''}" onclick="editClipProp('${clip.id}', { flip_vertical: ${!clip.flip_v} })">Flip V</button>
      </div>
    </div>

    <div class="prop-group">
      <div class="prop-group-title">Crop &amp; Canvas Background</div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Crop Left</label><input type="number" step="0.02" min="0" max="0.8" value="${cl}" onchange="editClipProp('${clip.id}', { crop_left: Number(this.value) })"></div>
        <div class="prop-field"><label>Crop Right</label><input type="number" step="0.02" min="0" max="0.8" value="${cr}" onchange="editClipProp('${clip.id}', { crop_right: Number(this.value) })"></div>
        <div class="prop-field"><label>Crop Top</label><input type="number" step="0.02" min="0" max="0.8" value="${ct}" onchange="editClipProp('${clip.id}', { crop_top: Number(this.value) })"></div>
        <div class="prop-field"><label>Crop Bottom</label><input type="number" step="0.02" min="0" max="0.8" value="${cb}" onchange="editClipProp('${clip.id}', { crop_bottom: Number(this.value) })"></div>
      </div>
      <div class="prop-row"><label>Radius</label><div class="range-wrap"><input type="range" min="0" max="1" step="0.05" value="${clip.corner_radius ?? 0}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { corner_radius: Number(this.value) })"><span class="range-val">${Math.round((clip.corner_radius ?? 0) * 100)}%</span></div></div>
      <div class="prop-row"><label>BG Blur</label><div class="range-wrap"><input type="range" min="0" max="1" step="0.05" value="${clip.canvas_blur ?? 0}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { canvas_blur: Number(this.value) })"><span class="range-val">${Math.round((clip.canvas_blur ?? 0) * 100)}%</span></div></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>BG Color</label><input type="color" value="${esc(clip.canvas_color || '#000000')}" onchange="editClipProp('${clip.id}', { canvas_color: this.value })"></div>
        <div class="prop-field"><label>&nbsp;</label><button onclick="editClipProp('${clip.id}', { canvas_color: '', canvas_blur: 0, crop_left: 0, crop_right: 0, crop_top: 0, crop_bottom: 0 })">Reset BG/Crop</button></div>
      </div>
    </div>

    <div class="prop-group">
      <div class="prop-group-title">Keyframes (${clip.keyframes?.length || 0})</div>
      <div style="display:flex;gap:6px">
        <button class="cyan" style="flex:1" onclick="addKeyframeAtPlayhead('${clip.id}')">+ Keyframe</button>
        <button onclick="runTool('set_keyframes', { id: '${clip.id}', action: 'clear' }, 'Clear keyframes')">Clear</button>
      </div>
    </div>`;
}

function renderSpeedTab(clip) {
  const sp = clip.speed ?? 1;
  return `
    <div class="prop-group">
      <div class="prop-group-title">Playback Speed</div>
      <div class="prop-row"><label>Speed</label><div class="range-wrap"><input type="range" min="0.25" max="4" step="0.05" value="${sp}" oninput="this.nextElementSibling.textContent=Number(this.value).toFixed(2)+'x'" onchange="editClipProp('${clip.id}', { speed: Number(this.value) })"><span class="range-val">${sp.toFixed(2)}x</span></div></div>
      <div class="pill-row">
        ${[0.5, 0.75, 1, 1.5, 2, 3].map(v => `<button class="mini-pill ${Math.abs(sp - v) < 0.02 ? 'on' : ''}" onclick="editClipProp('${clip.id}', { speed: ${v} })">${v}x</button>`).join('')}
      </div>
      <div class="global-row" onclick="editClipProp('${clip.id}', { keep_pitch: ${!clip.keep_pitch} })">
        <div class="global-row-left"><span>Keep Voice Pitch</span></div>
        <span class="toggle-box ${clip.keep_pitch ? 'on' : ''}">${clip.keep_pitch ? SVG.check : ''}</span>
      </div>
      <div class="global-row" onclick="runTool('reverse_segment', { id: '${clip.id}', reversed: ${!clip.reverse} }, 'Reverse clip')">
        <div class="global-row-left"><span>Reverse Clip</span></div>
        <span class="toggle-box ${clip.reverse ? 'on' : ''}">${clip.reverse ? SVG.check : ''}</span>
      </div>
    </div>`;
}

function renderAnimTab(clip) {
  const isText = clip.kind === 'text';
  const kinds = isText
    ? [['in', 'text_in', 'In'], ['out', 'text_out', 'Out'], ['loop', 'text_loop', 'Loop']]
    : [['in', 'video_in', 'In'], ['out', 'video_out', 'Out'], ['group', 'video_combo', 'Combo']];
  const presets = ['none', 'zoom_in', 'zoom_out', 'pan_left', 'pan_right', 'pan_up', 'pan_down', 'fade_in_out'];

  let html = '';
  if (!isText) {
    html += `
    <div class="prop-group">
      <div class="prop-group-title">Ken Burns Motion</div>
      <select onchange="runTool('add_motion', { ids: ['${clip.id}'], preset: this.value, strength: 0.15 }, 'Motion')">
        ${presets.map(pr => `<option value="${pr}" ${(clip.motion || 'none') === pr ? 'selected' : ''}>${pr}</option>`).join('')}
      </select>
    </div>`;
  }

  for (const [kind, cat, label] of kinds) {
    const items = (catalogCache[cat] || []).filter(it => !it.note);
    html += `
    <div class="prop-group">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <span class="prop-group-title">${label} Animation</span>
        <button class="mini-pill" onclick="runTool('set_animations',{ids:['${clip.id}'],kind:'${kind}',name:'none'},'Remove anim')">None</button>
      </div>
      <div class="catalog-list" style="max-height:140px">
        ${items.length ? items.slice(0, 16).map(it =>
          `<button class="cat-card" onclick="runTool('set_animations',{ids:['${clip.id}'],kind:'${kind}',name:'${esc(it.name)}',duration:0.5},'Anim ${esc(it.name)}')"><div class="cname">${esc(it.name)}</div></button>`).join('')
          : `<div style="grid-column:1/-1;color:var(--text-muted);font-size:11px">No ${label} animations cached for this editor.</div>`}
      </div>
    </div>`;
  }
  return html;
}

function renderAdjustTab(clip) {
  const adj = clip.adjust || {};
  const sliders = [
    ['brightness', 'Brightness', -1, 1, 0.05],
    ['contrast', 'Contrast', -1, 1, 0.05],
    ['saturation', 'Saturation', -1, 1, 0.05],
    ['temperature', 'Temp', -1, 1, 0.05],
    ['hue', 'Hue (°)', -180, 180, 5],
    ['sharpen', 'Sharpen', 0, 1, 0.05],
    ['highlights', 'Highlights', -1, 1, 0.05],
    ['shadows', 'Shadows', -1, 1, 0.05],
    ['vignette', 'Vignette', 0, 1, 0.05],
    ['fade', 'Fade', 0, 1, 0.05],
  ];
  return `
    <div class="prop-group">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <span class="prop-group-title">Color Adjustment</span>
        <div style="display:flex;gap:4px">
          <button class="mini-pill" onclick="runTool('set_adjustments',{ids:['${clip.id}'],reset:true},'Reset color')">Reset</button>
          <button class="mini-pill on" onclick="applyAdjustToAll('${clip.id}')">Apply All</button>
        </div>
      </div>
      ${sliders.map(([k, lbl, min, max, step]) => {
        const val = adj[k] ?? 0;
        const fmt = k === 'hue' ? `${Math.round(val)}°` : Math.round(val * 100);
        return `<div class="prop-row"><label>${lbl}</label><div class="range-wrap"><input type="range" min="${min}" max="${max}" step="${step}" value="${val}" oninput="previewAdjustRange(this,'${k}')" onchange="runTool('set_adjustments',{ids:activeSelectionIds(),${k}:Number(this.value)},'Adjust ${lbl}')"><span class="range-val">${fmt}</span></div></div>`;
      }).join('')}
    </div>`;
}

function previewAdjustRange(el, k) {
  el.nextElementSibling.textContent = k === 'hue' ? `${Math.round(el.value)}°` : String(Math.round(el.value * 100));
}

async function applyAdjustToAll(clipId) {
  const hit = findClipById(clipId);
  const adj = hit?.clip?.adjust || {};
  await runTool('set_adjustments', { all_main: true, ...adj }, 'Apply color to all');
}

function renderAudioTab(clip) {
  return `
    <div class="prop-group">
      <div class="prop-group-title">Volume &amp; Fades</div>
      <div class="prop-row"><label>Volume</label><div class="range-wrap"><input type="range" min="0" max="2" step="0.05" value="${clip.volume ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { volume: Number(this.value) })"><span class="range-val">${Math.round((clip.volume ?? 1) * 100)}%</span></div></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Fade In (s)</label><input type="number" step="0.1" min="0" value="${clip.fade_in ?? 0}" onchange="runTool('set_audio_fade', { ids: ['${clip.id}'], fade_in: Number(this.value), fade_out: ${clip.fade_out ?? 0} }, 'Audio fade')"></div>
        <div class="prop-field"><label>Fade Out (s)</label><input type="number" step="0.1" min="0" value="${clip.fade_out ?? 0}" onchange="runTool('set_audio_fade', { ids: ['${clip.id}'], fade_in: ${clip.fade_in ?? 0}, fade_out: Number(this.value) }, 'Audio fade')"></div>
      </div>
      <div class="prop-row"><label>Pan L/R</label><div class="range-wrap"><input type="range" min="-1" max="1" step="0.05" value="${clip.audio_pan ?? 0}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)" onchange="editClipProp('${clip.id}', { audio_pan: Number(this.value) })"><span class="range-val">${Math.round((clip.audio_pan ?? 0) * 100)}</span></div></div>
    </div>
    <div class="prop-group">
      <div class="prop-group-title">Enhancement &amp; Tools</div>
      <div class="global-row" onclick="editClipProp('${clip.id}', { noise_reduction: ${!clip.noise_reduction} })">
        <div class="global-row-left"><span>Noise Reduction</span></div>
        <span class="toggle-box ${clip.noise_reduction ? 'on' : ''}">${clip.noise_reduction ? SVG.check : ''}</span>
      </div>
      <div class="prop-field">
        <label>Vocal Isolation</label>
        <select onchange="editClipProp('${clip.id}', { vocal_isolation: this.value })">
          <option value="none" ${(clip.vocal_isolation || 'none') === 'none' ? 'selected' : ''}>Off</option>
          <option value="keep_vocals" ${clip.vocal_isolation === 'keep_vocals' ? 'selected' : ''}>Keep Vocals</option>
          <option value="remove_vocals" ${clip.vocal_isolation === 'remove_vocals' ? 'selected' : ''}>Remove Vocals (Instrumental)</option>
        </select>
      </div>
      ${clip.kind === 'video' ? `<button class="cyan" onclick="extractAudioSelected()">Extract Audio to Track</button>` : ''}
    </div>`;
}

function renderTextTab(clip) {
  const [px, py] = clip.pos || [0, -0.7];
  const fonts = (catalogCache['font'] || []).filter(f => !f.note).map(f => f.name);
  return `
    <div class="prop-group">
      <div class="prop-group-title">Text &amp; Font</div>
      <div class="prop-field"><label>Content</label><textarea rows="2" onchange="editClipProp('${clip.id}', { text: this.value })">${esc(clip.text || '')}</textarea></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Font</label>
          <select onchange="editClipProp('${clip.id}', { font: this.value })">
            <option value="">Default</option>
            ${fonts.map(f => `<option value="${esc(f)}" ${clip.font === f ? 'selected' : ''}>${esc(f)}</option>`).join('')}
          </select>
        </div>
        <div class="prop-field"><label>Size</label><input type="number" step="1" min="2" max="60" value="${clip.font_size ?? 8}" onchange="editClipProp('${clip.id}', { font_size: Number(this.value) })"></div>
      </div>
      <div class="pill-row">
        <button class="mini-pill ${clip.bold ? 'on' : ''}" onclick="editClipProp('${clip.id}', { bold: ${!clip.bold} })">B</button>
        <button class="mini-pill ${clip.italic ? 'on' : ''}" onclick="editClipProp('${clip.id}', { italic: ${!clip.italic} })">I</button>
        <button class="mini-pill ${(clip.align || 'center') === 'left' ? 'on' : ''}" onclick="editClipProp('${clip.id}', { align: 'left' })">Left</button>
        <button class="mini-pill ${(clip.align || 'center') === 'center' ? 'on' : ''}" onclick="editClipProp('${clip.id}', { align: 'center' })">Center</button>
        <button class="mini-pill ${(clip.align || 'center') === 'right' ? 'on' : ''}" onclick="editClipProp('${clip.id}', { align: 'right' })">Right</button>
        <button class="mini-pill ${clip.shadow ? 'on' : ''}" onclick="editClipProp('${clip.id}', { shadow: ${!clip.shadow} })">Shadow</button>
      </div>
    </div>
    <div class="prop-group">
      <div class="prop-group-title">Colors &amp; Spacing</div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Text Color</label><input type="color" value="${esc(clip.color || '#FFFFFF')}" onchange="editClipProp('${clip.id}', { color: this.value })"></div>
        <div class="prop-field"><label>Stroke Color</label><input type="color" value="${esc(clip.stroke_color || '#000000')}" onchange="editClipProp('${clip.id}', { stroke_color: this.value })"></div>
        <div class="prop-field"><label>BG Color</label><input type="color" value="${esc(clip.background_color || '#000000')}" onchange="editClipProp('${clip.id}', { background_color: this.value })"></div>
        <div class="prop-field"><label>&nbsp;</label><button onclick="editClipProp('${clip.id}', { stroke_color: '', background_color: '' })">Clear Stroke/BG</button></div>
      </div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Pos X</label><input type="number" step="0.05" value="${px}" onchange="editClipProp('${clip.id}', { x: Number(this.value) })"></div>
        <div class="prop-field"><label>Pos Y</label><input type="number" step="0.05" value="${py}" onchange="editClipProp('${clip.id}', { y: Number(this.value) })"></div>
      </div>
    </div>`;
}

async function editClipProp(id, fields) {
  const ids = selectedClipIds.has(id) && selectedClipIds.size > 1 ? Array.from(selectedClipIds) : [id];
  await runTool('edit_segments', { ids, ...fields }, 'Edit clip');
}

function copyClipAttributes() {
  const hit = findClipById(selectedClipId);
  if (!hit) return;
  const c = hit.clip;
  copiedAttributes = {
    scale: c.scale ?? 1, x: c.pos?.[0] ?? 0, y: c.pos?.[1] ?? 0, rotation: c.rot ?? 0,
    opacity: c.opacity ?? 1, flip_horizontal: !!c.flip_h, flip_vertical: !!c.flip_v,
    blend_mode: c.blend_mode || 'normal', corner_radius: c.corner_radius || 0,
    adjust: c.adjust ? { ...c.adjust } : null,
  };
  toast('Copied clip attributes');
  renderDetailsInspector();
}

async function pasteClipAttributes() {
  if (!copiedAttributes) { toast('Copy attributes from a clip first'); return; }
  const ids = activeSelectionIds();
  if (!ids.length) { toast('Select a target clip'); return; }
  const { adjust, ...transform } = copiedAttributes;
  await runTool('edit_segments', { ids, ...transform }, 'Paste attributes');
  if (adjust) await runTool('set_adjustments', { ids, ...adjust }, 'Paste color adjustments');
}

async function addKeyframeAtPlayhead(id) {
  const hit = findClipById(id);
  if (!hit) return;
  await runTool('set_keyframes', {
    id, action: 'add', at: Number(playheadSec.toFixed(3)),
    scale: hit.clip.scale ?? 1, x: hit.clip.pos?.[0] ?? 0, y: hit.clip.pos?.[1] ?? 0,
    rotation: hit.clip.rot ?? 0, opacity: hit.clip.opacity ?? 1,
  }, 'Add keyframe');
}

// ---------------- Interactive Multi-Track Timeline ----------------
function setToolMode(m) {
  toolMode = m;
  $('btnModeSelect').classList.toggle('active', m === 'select');
  $('btnModeBlade').classList.toggle('active', m === 'blade');
  $('tlLanes').style.cursor = m === 'blade' ? 'crosshair' : 'default';
}
function toggleRipple() { rippleDelete = !rippleDelete; $('btnRipple').classList.toggle('active', rippleDelete); }
function toggleSnap() { snapEnabled = !snapEnabled; $('btnSnap').classList.toggle('active', snapEnabled); }
const MIN_ZOOM = 0.2, MAX_ZOOM = 240, ZOOM_RATIO = MAX_ZOOM / MIN_ZOOM;
function setZoomFromSlider(val) {
  setZoom(MIN_ZOOM * Math.pow(ZOOM_RATIO, Math.max(0, Math.min(100, val)) / 100));
}
function setZoom(v) {
  pxPerSec = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, v));
  const sliderVal = Math.round(100 * Math.log(pxPerSec / MIN_ZOOM) / Math.log(ZOOM_RATIO));
  if ($('zoomSlider')) $('zoomSlider').value = String(Math.max(0, Math.min(100, sliderVal)));
  renderTimeline();
}
function zoomTimeline(dir) { setZoom(dir > 0 ? pxPerSec * 1.28 : pxPerSec / 1.28); }
function fitTimeline() {
  const dur = Math.max(projectDuration(), 10);
  const avail = Math.max(300, $('tlScroll').clientWidth - 40);
  setZoom(Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, avail / dur)));
  $('tlScroll').scrollLeft = 0;
}

function trackHeight(t) { return t.is_main ? 56 : t.type === 'video' ? 46 : t.type === 'audio' ? 42 : 32; }
function trackShortBadge(t) { return t.is_main ? 'Main' : `${t.type === 'video' ? 'V' : t.type === 'audio' ? 'A' : t.type === 'text' ? 'T' : 'FX'}${t.index + 1}`; }
function trackApiId(t) { return `${t.type}#${t.index}`; }

function renderTimeline() {
  const hdrList = $('tlHeaderList');
  const ruler = $('tlRuler');
  const lanes = $('tlLanes');
  const canvas = $('tlCanvas');
  const scrollEl = $('tlScroll');

  if (!overview) {
    hdrList.innerHTML = ''; ruler.innerHTML = '';
    lanes.innerHTML = '<div style="padding:28px;color:var(--text-muted);text-align:center">No project loaded. Open a project from the Projects view or Menu.</div>';
    updatePlayheadDom();
    return;
  }

  const dur = Math.max(projectDuration(), 20);
  const totalSec = dur + Math.max(10, dur * 0.25);
  const widthPx = Math.max(scrollEl.clientWidth, Math.ceil(totalSec * pxPerSec));
  canvas.style.width = `${widthPx}px`;

  const STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
  const stepSec = STEPS.find(s => s * pxPerSec >= 70) || 600;
  const minorSec = stepSec / 5;
  let rHtml = '';
  for (let t = 0; t <= totalSec; t += minorSec) {
    const x = Math.round(t * pxPerSec);
    const isMajor = Math.abs(Math.round(t / stepSec) * stepSec - t) < 0.001;
    rHtml += `<div class="tl-ruler-tick ${isMajor ? 'major' : 'minor'}" style="left:${x}px"></div>`;
    if (isMajor) rHtml += `<span class="tl-ruler-label" style="left:${x}px">${fmtTimeShort(t)}</span>`;
  }
  for (let i = 0; i < markers.length; i++) {
    const x = Math.round(markers[i] * pxPerSec);
    rHtml += `<div class="tl-marker" style="left:${x}px" title="Marker ${fmtTimeShort(markers[i])}" onclick="event.stopPropagation(); seekTo(${markers[i]})"></div>`;
  }
  ruler.innerHTML = rHtml;

  const ordered = visualTracksOrdered();
  const hasOverlays = ordered.some(t => !t.is_main && t.type !== 'audio');
  let hHtml = '', lHtml = '';
  for (const t of ordered) {
    const h = trackHeight(t), tid = trackApiId(t), locked = lockedTracks.has(tid);
    const mainCls = t.is_main ? 'main-track' : '';
    const hasMute = t.type === 'video' || t.type === 'audio', hasEye = t.type !== 'audio';

    if (t.is_main) {
      const spaceH = hasOverlays ? 24 : 112;
      hHtml += `<div style="height:${spaceH}px"></div>`;
      lHtml += `<div class="tl-overlay-space ${hasOverlays ? 'compact' : ''}" id="tlOverlaySpace">+ Drop photo / video here for overlay track</div>`;
    }

    const coverHtml = t.is_main
      ? `<div class="tl-cover-box" onclick="setInspTab('project')" title="Cover &amp; Project settings">${SVG.cover}<span>Cover</span></div>`
      : `<span class="tl-hdr-badge">${trackShortBadge(t)}</span>`;

    hHtml += `<div class="tl-track-hdr ${mainCls}" style="height:${h}px">
      <div class="tl-hdr-btns">
        ${hasEye ? `<button class="tl-hdr-btn ${t.hidden ? 'warn' : ''}" onclick="toggleTrackHidden('${tid}', ${!t.hidden})" title="Toggle track visibility">${t.hidden ? SVG.eyeOff : SVG.eye}</button>` : ''}
        <button class="tl-hdr-btn ${locked ? 'on' : ''}" onclick="toggleTrackLock('${tid}')" title="Lock track">${locked ? SVG.lock : SVG.unlock}</button>
        ${hasMute ? `<button class="tl-hdr-btn ${t.muted ? 'warn' : ''}" onclick="toggleTrackMute('${tid}', ${!t.muted})" title="Mute track">${t.muted ? SVG.mute : SVG.vol}</button>` : ''}
      </div>
      ${coverHtml}
    </div>`;

    let segsHtml = '';
    for (const c of t.segments) {
      const left = Math.round(c.start * pxPerSec);
      const w = Math.max(8, Math.round(c.dur * pxPerSec));
      const sel = (c.id === selectedClipId || selectedClipIds.has(c.id)) ? 'selected' : '';
      const label = c.text || c.file || c.name || c.kind;
      const bodyStyle = (c.kind === 'video' || c.kind === 'photo') && c.path ? `style="background-image:url('${thumbUrl(c.path, 140)}')"` : '';
      const waveHtml = c.kind === 'audio' ? `<canvas class="tl-wave" data-path="${esc(c.path || '')}" data-id="${c.id}"></canvas>` : '';
      const transHtml = c.transition ? `<div class="tl-trans-badge" title="Transition: ${esc(c.transition)}">${SVG.trans}</div>` : '';
      const kfHtml = (c.keyframes || []).map(k => `<div class="tl-kf-dot" style="left:${Math.round(k.t * pxPerSec)}px" title="Keyframe ${k.t}s"></div>`).join('');
      const badges = [c.reverse ? 'REV' : '', c.freeze ? 'FRZ' : '', c.adjust ? 'ADJ' : '', c.volume === 0 ? 'MUTE' : ''].filter(Boolean).join(' ');

      segsHtml += `<div class="tl-clip kind-${esc(c.kind)} ${sel}" data-id="${c.id}" data-track="${tid}" style="left:${left}px;width:${w}px">
        <div class="tl-trim left" data-trim="left"></div>
        <div class="tl-clip-head"><span>${esc(label)}</span>${badges ? `<span class="tl-clip-badges">${badges}</span>` : ''}</div>
        <div class="tl-clip-body" ${bodyStyle}>${waveHtml}${kfHtml}</div>
        <div class="tl-trim right" data-trim="right"></div>
        ${transHtml}
      </div>`;
    }
    lHtml += `<div class="tl-lane ${mainCls} ${locked ? 'locked' : ''}" data-track="${tid}" data-type="${t.type}" style="height:${h}px">${segsHtml}</div>`;
  }

  hdrList.innerHTML = hHtml;
  lanes.innerHTML = lHtml;
  hdrList.scrollTop = scrollEl.scrollTop;
  updatePlayheadDom();
  drawAllWaveforms();
}

function updatePlayheadDom() { $('tlPlayhead').style.left = `${Math.round(playheadSec * pxPerSec)}px`; }
$('tlScroll').addEventListener('scroll', () => { $('tlHeaderList').scrollTop = $('tlScroll').scrollTop; });

async function toggleTrackMute(trackId, muted) { await runTool('set_track_mute', { track: trackId, muted }, `Mute ${trackId}`); }
async function toggleTrackHidden(trackId, hidden) { await runTool('set_track_hidden', { track: trackId, hidden }, `Hide ${trackId}`); }
function toggleTrackLock(trackId) { if (lockedTracks.has(trackId)) lockedTracks.delete(trackId); else lockedTracks.add(trackId); renderTimeline(); }

// ---------------- Audio Waveform Drawing ----------------
function drawAllWaveforms() {
  document.querySelectorAll('.tl-wave').forEach(canvas => {
    const path = canvas.dataset.path;
    const rect = canvas.getBoundingClientRect();
    canvas.width = Math.max(10, Math.round(rect.width));
    canvas.height = Math.max(10, Math.round(rect.height));
    const peaks = path ? waveformCache.get(path) : null;
    paintWaveform(canvas, peaks, path);
    if (path && !peaks && !waveformCache.has(path + ':pending')) {
      waveformCache.set(path + ':pending', true);
      decodeAudioPeaks(path);
    }
  });
}

function paintWaveform(canvas, peaks, seedStr) {
  const ctx = canvas.getContext('2d'), w = canvas.width, h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = 'rgba(96, 165, 250, 0.78)';
  let hash = 17;
  for (let i = 0; i < (seedStr || '').length; i++) hash = (hash * 31 + seedStr.charCodeAt(i)) | 0;
  for (let x = 0; x < w; x += 3) {
    const amp = peaks?.length ? peaks[Math.min(peaks.length - 1, Math.floor((x / w) * peaks.length))]
      : 0.25 + 0.65 * Math.abs(Math.sin((x + Math.abs(hash % 97)) * 0.17) * Math.cos(x * 0.05));
    const bh = Math.max(2, Math.round(amp * (h - 3)));
    ctx.fillRect(x, h - bh, 2, bh);
  }
}

async function decodeAudioPeaks(path) {
  try {
    if (!activeAudioCtx) activeAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const resp = await fetch(mediaUrl(path));
    if (!resp.ok) return;
    const audioBuf = await activeAudioCtx.decodeAudioData(await resp.arrayBuffer());
    const data = audioBuf.getChannelData(0), buckets = 240, step = Math.max(1, Math.floor(data.length / buckets));
    const peaks = new Float32Array(buckets);
    for (let i = 0; i < buckets; i++) {
      let max = 0, start = i * step;
      for (let j = 0; j < step && start + j < data.length; j += Math.max(1, Math.floor(step / 16)))
        max = Math.max(max, Math.abs(data[start + j]));
      peaks[i] = Math.min(1, max);
    }
    waveformCache.set(path, peaks);
    document.querySelectorAll(`.tl-wave[data-path="${CSS.escape(path)}"]`).forEach(c => paintWaveform(c, peaks, path));
  } catch { }
}

// ---------------- Timeline Drop Indicators & Magnetic Reorder ----------------
function snapPoints(excludeId) {
  const pts = [0, playheadSec];
  for (const t of overview?.tracks || [])
    for (const c of t.segments) if (c.id !== excludeId) pts.push(c.start, c.end);
  return pts;
}

function applySnap(sec, pts) {
  if (!snapEnabled) return { sec, snapped: null };
  const tol = 8 / pxPerSec;
  let best = sec, bestDist = tol, snapped = null;
  for (const p of pts) {
    const d = Math.abs(sec - p);
    if (d < bestDist) { bestDist = d; best = p; snapped = p; }
  }
  return { sec: Math.max(0, best), snapped };
}

function timeAtClientX(clientX) {
  const rect = $('tlScroll').getBoundingClientRect();
  return Math.max(0, (clientX - rect.left + $('tlScroll').scrollLeft) / pxPerSec);
}

function nearestMainBoundary(sec, excludeId = null) {
  const main = overview?.tracks?.find(t => t.is_main);
  if (!main || !main.segments.length) return { sec: 0, slotIdx: 0, siblings: [] };
  const siblings = main.segments.filter(s => s.id !== excludeId);
  const boundaries = [0];
  for (const c of siblings) boundaries.push(c.end);
  let bestSec = boundaries[boundaries.length - 1];
  let bestIdx = siblings.length;
  let bestDist = Infinity;
  for (let i = 0; i < boundaries.length; i++) {
    const d = Math.abs(sec - boundaries[i]);
    if (d < bestDist) { bestDist = d; bestSec = boundaries[i]; bestIdx = i; }
  }
  return { sec: bestSec, slotIdx: bestIdx, siblings };
}

function clearTimelineDropIndicators() {
  $('tlDropGhost').style.display = 'none';
  $('tlInsertBar').style.display = 'none';
  $('tlOverlaySpace')?.classList.remove('hot');
  document.querySelectorAll('.tl-lane.drop-target').forEach(l => l.classList.remove('drop-target'));
  currentDropPlan = null;
}

function computeTimelineDrop(clientX, clientY, items) {
  const tRaw = timeAtClientX(clientX);
  const hitEl = document.elementFromPoint(clientX, clientY);
  const overlayDrop = hitEl?.closest('#tlOverlaySpace');
  const lane = hitEl?.closest('.tl-lane');
  const isAudioOnly = items && items.length > 0 && items.every(x => x.kind === 'audio');
  const ghost = $('tlDropGhost');
  const insertBar = $('tlInsertBar');
  const canvasRect = $('tlCanvas').getBoundingClientRect();

  clearTimelineDropIndicators();

  if (isAudioOnly) {
    const snapped = applySnap(tRaw, snapPoints(null)).sec;
    const audioLane = (lane?.dataset.type === 'audio' ? lane : null) || document.querySelector('.tl-lane[data-type="audio"]');
    if (audioLane) {
      audioLane.classList.add('drop-target');
      const r = audioLane.getBoundingClientRect();
      ghost.style.top = `${Math.round(r.top - canvasRect.top + 3)}px`;
      ghost.style.height = `${Math.max(24, r.height - 6)}px`;
    } else {
      ghost.style.top = '110px';
      ghost.style.height = '36px';
    }
    const dur = items.reduce((a, x) => a + (x.duration_s || 5), 0);
    ghost.style.left = `${Math.round(snapped * pxPerSec)}px`;
    ghost.style.width = `${Math.max(40, Math.round(dur * pxPerSec))}px`;
    ghost.textContent = items.length > 1 ? `+ ${items.length} audio files` : `+ ${items[0].name}`;
    ghost.style.display = 'block';
    currentDropPlan = { mode: 'audio', sec: Number(snapped.toFixed(3)), target: 'audio' };
    return;
  }

  if (overlayDrop || (lane && lane.dataset.type === 'video' && lane.dataset.track !== 'video#0')) {
    const snapped = applySnap(tRaw, snapPoints(null)).sec;
    const targetEl = overlayDrop || lane;
    if (overlayDrop) overlayDrop.classList.add('hot');
    else lane.classList.add('drop-target');
    const r = targetEl.getBoundingClientRect();
    const dur = items ? items.reduce((a, x) => a + (x.duration_s || 3), 0) : 3;
    ghost.style.top = `${Math.round(r.top - canvasRect.top + 2)}px`;
    ghost.style.height = `${Math.max(24, r.height - 4)}px`;
    ghost.style.left = `${Math.round(snapped * pxPerSec)}px`;
    ghost.style.width = `${Math.max(36, Math.round(dur * pxPerSec))}px`;
    ghost.textContent = items ? (items.length > 1 ? `+ ${items.length} overlay clips` : `+ ${items[0].name}`) : '+ Drop as overlay';
    ghost.style.display = 'block';
    currentDropPlan = { mode: 'overlay', sec: Number(snapped.toFixed(3)), target: overlayDrop ? 'new_track' : 'overlay' };
    return;
  }

  // Main Track Magnetic Insertion
  const { sec: bSec } = nearestMainBoundary(tRaw);
  const mainLane = document.querySelector('.tl-lane.main-track');
  const dur = items ? items.reduce((a, x) => a + (x.duration_s || 3), 0) : 3;
  insertBar.style.left = `${Math.round(bSec * pxPerSec)}px`;
  insertBar.style.display = 'block';
  if (mainLane) {
    mainLane.classList.add('drop-target');
    const r = mainLane.getBoundingClientRect();
    ghost.style.top = `${Math.round(r.top - canvasRect.top + 3)}px`;
    ghost.style.height = `${Math.max(30, r.height - 6)}px`;
  } else {
    ghost.style.top = '42px';
    ghost.style.height = '48px';
  }
  ghost.style.left = `${Math.round(bSec * pxPerSec)}px`;
  ghost.style.width = `${Math.max(36, Math.round(dur * pxPerSec))}px`;
  ghost.textContent = items ? (items.length > 1 ? `+ ${items.length} clips` : `+ ${items[0].name}`) : '+ Insert here';
  ghost.style.display = 'block';
  currentDropPlan = { mode: 'main', sec: Number(bSec.toFixed(3)), target: 'main' };
}

$('tlScroll').addEventListener('dragover', e => {
  const hasBin = draggingBinItems || e.dataTransfer?.types?.includes('application/x-aive-media');
  const hasFiles = e.dataTransfer?.types?.includes('Files');
  if (!hasBin && !hasFiles) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'copy';
  computeTimelineDrop(e.clientX, e.clientY, draggingBinItems);
});

$('tlScroll').addEventListener('dragleave', e => {
  if (!e.relatedTarget || !e.relatedTarget.closest('#tlScroll')) {
    clearTimelineDropIndicators();
  }
});

$('tlScroll').addEventListener('drop', async e => {
  const raw = e.dataTransfer?.getData('application/x-aive-media');
  if (!raw) return; // External OS files handled by document drop handler with pendingExternalDrop
  e.preventDefault();
  e.stopPropagation();
  const plan = currentDropPlan || { mode: 'main', sec: 0, target: 'main' };
  clearTimelineDropIndicators();
  draggingBinItems = null;
  if (!overview) { toast('Open or create a project first'); return; }
  try {
    const parsed = JSON.parse(raw);
    const items = Array.isArray(parsed) ? parsed : [parsed];
    if (plan.mode === 'audio') {
      let cur = plan.sec;
      for (const it of items) {
        await runTool('add_audio', { path: it.path, start: cur }, `Add ${it.name || 'audio'}`);
        cur += (it.duration_s || 5);
      }
    } else {
      await runTool('add_media', {
        paths: items.map(x => x.path),
        target: plan.target,
        start: plan.sec,
      }, `Add ${items.length} clip(s)`);
    }
  } catch { }
});

// ---------------- Timeline Pointer Interactions ----------------
$('tlRuler').addEventListener('pointerdown', e => {
  if (e.button !== 0 || e.target.classList.contains('tl-marker')) return;
  if (playing) togglePlay();
  e.preventDefault();
  $('tlRuler').setPointerCapture(e.pointerId);
  dragState = { type: 'scrub', pointerId: e.pointerId };
  seekTo(timeAtClientX(e.clientX));
});

$('tlPlayheadHandle').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  if (playing) togglePlay();
  e.preventDefault();
  e.stopPropagation();
  $('tlPlayheadHandle').setPointerCapture(e.pointerId);
  dragState = { type: 'scrub', pointerId: e.pointerId };
});

$('tlLanes').addEventListener('contextmenu', e => {
  const clipEl = e.target.closest('.tl-clip');
  if (!clipEl) return;
  e.preventDefault();
  const id = clipEl.dataset.id;
  if (!selectedClipIds.has(id)) selectSingleClip(id);
  else selectedClipId = id;
  renderTimeline();
  renderDetailsInspector();
  const menu = $('clipCtxMenu');
  menu.style.left = `${Math.min(window.innerWidth - 205, e.clientX)}px`;
  menu.style.top = `${Math.min(window.innerHeight - 360, e.clientY)}px`;
  menu.classList.add('open');
});

async function ctxAction(act) {
  closeCtxMenu();
  if (act === 'split') await splitAtPlayhead();
  else if (act === 'duplicate') await duplicateSelected();
  else if (act === 'delete') await deleteSelected();
  else if (act === 'freeze') await freezeSelected();
  else if (act === 'reverse') await reverseSelected();
  else if (act === 'rotate') await rotateSelected90();
  else if (act === 'mirror') await mirrorSelected();
  else if (act === 'extract_audio') await extractAudioSelected();
  else if (act === 'replace') await replaceSelectedMedia();
  else if (act === 'copy_attrs') copyClipAttributes();
  else if (act === 'paste_attrs') await pasteClipAttributes();
  else if (act === 'move_earlier' || act === 'move_later') await shiftMainClip(act === 'move_earlier' ? -1 : 1);
}

$('tlLanes').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const clipEl = e.target.closest('.tl-clip');
  if (!clipEl) {
    if (selectedClipId || selectedClipIds.size) {
      selectSingleClip(null);
      renderTimeline();
      renderDetailsInspector();
    }
    return;
  }

  const id = clipEl.dataset.id, trackId = clipEl.dataset.track;
  if (lockedTracks.has(trackId)) { toast('Track is locked'); return; }

  if (toolMode === 'blade') {
    runTool('split_segment', { id, at: Number(timeAtClientX(e.clientX).toFixed(3)) }, 'Split clip');
    return;
  }

  if (e.ctrlKey) {
    if (selectedClipIds.has(id)) selectedClipIds.delete(id);
    else selectedClipIds.add(id);
    selectedClipId = selectedClipIds.has(id) ? id : (Array.from(selectedClipIds)[0] || null);
  } else {
    selectSingleClip(id);
  }

  if (inspTab === 'project') setInspTab('details');
  else renderDetailsInspector();

  const hit = findClipById(id);
  if (!hit) return;
  const trimDir = e.target.dataset.trim || null;
  e.preventDefault();
  clipEl.setPointerCapture(e.pointerId);
  dragState = {
    type: trimDir ? 'trim' : 'move', trimDir, pointerId: e.pointerId, el: clipEl, id,
    origTrack: trackId, origIsMain: hit.track.is_main, origType: hit.track.type,
    origStart: hit.clip.start, origDur: hit.clip.dur,
    startX: e.clientX, curStart: hit.clip.start, curDur: hit.clip.dur, targetTrack: trackId,
    reorderIdx: null, snapPts: snapPoints(id),
  };
  renderTimeline();
});

window.addEventListener('pointermove', e => {
  if (!dragState) return;
  if (dragState.type === 'scrub') { seekTo(timeAtClientX(e.clientX)); return; }
  const dt = (e.clientX - dragState.startX) / pxPerSec;
  const snapLine = $('tlSnapLine');
  const insertBar = $('tlInsertBar');
  const el = document.querySelector(`.tl-clip[data-id="${dragState.id}"]`);
  if (!el) return;

  if (dragState.type === 'move') {
    const rawStart = Math.max(0, dragState.origStart + dt);
    const overEl = document.elementFromPoint(e.clientX, e.clientY);
    const overlayDrop = overEl?.closest('#tlOverlaySpace');
    const lane = overEl?.closest('.tl-lane');
    document.querySelectorAll('.tl-lane.drop-target').forEach(l => l.classList.remove('drop-target'));
    $('tlOverlaySpace')?.classList.remove('hot');

    if (dragState.origIsMain && (overlayDrop || (lane && lane.dataset.type === 'video' && lane.dataset.track !== 'video#0'))) {
      dragState.targetTrack = overlayDrop ? 'video#new' : lane.dataset.track;
      dragState.reorderIdx = null;
      insertBar.style.display = 'none';
      if (overlayDrop) overlayDrop.classList.add('hot');
      else lane.classList.add('drop-target');
      const sStart = applySnap(rawStart, dragState.snapPts);
      dragState.curStart = Number(sStart.sec.toFixed(3));
      el.style.left = `${Math.round(sStart.sec * pxPerSec)}px`;
      snapLine.style.display = sStart.snapped != null ? 'block' : 'none';
      if (sStart.snapped != null) snapLine.style.left = `${Math.round(sStart.snapped * pxPerSec)}px`;
    } else if (dragState.origIsMain) {
      // Magnetic main-track reorder slot calculation
      dragState.targetTrack = dragState.origTrack;
      const cursorSec = timeAtClientX(e.clientX);
      const main = overview?.tracks?.find(t => t.is_main);
      const siblings = (main?.segments || []).filter(s => s.id !== dragState.id);
      let slot = siblings.length;
      for (let i = 0; i < siblings.length; i++) {
        const mid = (siblings[i].start + siblings[i].end) / 2;
        if (cursorSec < mid) { slot = i; break; }
      }
      const barSec = slot === 0 ? 0 : siblings[slot - 1].end;
      dragState.reorderIdx = slot;
      dragState.curStart = Number(rawStart.toFixed(3));
      el.style.left = `${Math.round(rawStart * pxPerSec)}px`;
      insertBar.style.left = `${Math.round(barSec * pxPerSec)}px`;
      insertBar.style.display = 'block';
      snapLine.style.display = 'none';
    } else {
      insertBar.style.display = 'none';
      const sStart = applySnap(rawStart, dragState.snapPts), sEnd = applySnap(rawStart + dragState.origDur, dragState.snapPts);
      let finalStart = rawStart, snapAt = null;
      if (sStart.snapped != null) { finalStart = sStart.sec; snapAt = sStart.snapped; }
      else if (sEnd.snapped != null) { finalStart = Math.max(0, sEnd.sec - dragState.origDur); snapAt = sEnd.snapped; }
      dragState.curStart = Number(finalStart.toFixed(3));
      el.style.left = `${Math.round(finalStart * pxPerSec)}px`;
      if (lane && lane.dataset.type === dragState.origType && !lockedTracks.has(lane.dataset.track)) {
        dragState.targetTrack = lane.dataset.track;
        if (dragState.targetTrack !== dragState.origTrack) lane.classList.add('drop-target');
      }
      snapLine.style.display = snapAt != null ? 'block' : 'none';
      if (snapAt != null) snapLine.style.left = `${Math.round(snapAt * pxPerSec)}px`;
    }
  } else if (dragState.type === 'trim') {
    if (dragState.trimDir === 'right') {
      const s = applySnap(Math.max(dragState.origStart + 0.1, dragState.origStart + dragState.origDur + dt), dragState.snapPts);
      const dur = Math.max(0.1, s.sec - dragState.origStart);
      dragState.curDur = Number(dur.toFixed(3));
      el.style.width = `${Math.max(8, Math.round(dur * pxPerSec))}px`;
      snapLine.style.display = s.snapped != null ? 'block' : 'none';
      if (s.snapped != null) snapLine.style.left = `${Math.round(s.snapped * pxPerSec)}px`;
    } else {
      const origEnd = dragState.origStart + dragState.origDur;
      const s = applySnap(Math.max(0, Math.min(origEnd - 0.1, dragState.origStart + dt)), dragState.snapPts);
      const newStart = Math.min(origEnd - 0.1, s.sec), dur = Math.max(0.1, origEnd - newStart);
      dragState.curStart = Number(newStart.toFixed(3));
      dragState.curDur = Number(dur.toFixed(3));
      el.style.left = `${Math.round(newStart * pxPerSec)}px`;
      el.style.width = `${Math.max(8, Math.round(dur * pxPerSec))}px`;
      snapLine.style.display = s.snapped != null ? 'block' : 'none';
      if (s.snapped != null) snapLine.style.left = `${Math.round(s.snapped * pxPerSec)}px`;
    }
  }
});

window.addEventListener('pointerup', async () => {
  if (!dragState) return;
  const ds = dragState;
  dragState = null;
  $('tlSnapLine').style.display = 'none';
  $('tlInsertBar').style.display = 'none';
  $('tlOverlaySpace')?.classList.remove('hot');
  document.querySelectorAll('.tl-lane.drop-target').forEach(l => l.classList.remove('drop-target'));

  if (ds.type === 'move') {
    if (ds.origIsMain && ds.targetTrack === ds.origTrack && ds.reorderIdx != null) {
      const main = overview?.tracks?.find(t => t.is_main);
      const ids = (main?.segments || []).map(s => s.id);
      const oldIdx = ids.indexOf(ds.id);
      if (oldIdx >= 0 && ds.reorderIdx !== oldIdx) {
        const rest = ids.filter(x => x !== ds.id);
        rest.splice(ds.reorderIdx, 0, ds.id);
        await runTool('reorder_main_track', { order: rest }, 'Reorder clips');
      } else {
        renderTimeline();
      }
    } else if (Math.abs(ds.curStart - ds.origStart) > 0.01 || ds.targetTrack !== ds.origTrack) {
      const args = { id: ds.id, start: ds.curStart };
      if (ds.targetTrack !== ds.origTrack) args.to_track = ds.targetTrack;
      await runTool('move_segment', args, 'Move clip');
    }
  } else if (ds.type === 'trim') {
    if (Math.abs(ds.curStart - ds.origStart) > 0.01 || Math.abs(ds.curDur - ds.origDur) > 0.01) {
      const args = { ids: [ds.id], duration: ds.curDur };
      if (ds.trimDir === 'left') args.start = ds.curStart;
      await runTool('edit_segments', args, 'Trim clip');
    }
  }
});

// ---------------- Timeline & Clip Editing Actions ----------------
function clipUnderPlayhead() {
  if (!overview) return null;
  if (selectedClipId) {
    const hit = findClipById(selectedClipId);
    if (hit && playheadSec > hit.clip.start + 0.05 && playheadSec < hit.clip.end - 0.05) return hit.clip;
  }
  for (const t of overview.tracks)
    for (const c of t.segments) if (playheadSec > c.start + 0.05 && playheadSec < c.end - 0.05) return c;
  return null;
}

async function splitAtPlayhead() {
  const c = clipUnderPlayhead();
  if (!c) { toast('Move playhead inside a clip to split'); return; }
  await runTool('split_segment', { id: c.id, at: Number(playheadSec.toFixed(3)) }, 'Split clip');
}
async function splitSelectedAtPlayhead() { await splitAtPlayhead(); }

async function trimToPlayhead(side) {
  const hit = findClipById(selectedClipId);
  if (!hit) { toast('Select a clip first'); return; }
  const c = hit.clip;
  if (playheadSec <= c.start + 0.05 || playheadSec >= c.end - 0.05) { toast('Playhead must be inside the selected clip'); return; }
  if (side === 'left') await runTool('edit_segments', { ids: [c.id], start: Number(playheadSec.toFixed(3)), duration: Number((c.end - playheadSec).toFixed(3)) }, 'Trim start');
  else await runTool('edit_segments', { ids: [c.id], duration: Number((playheadSec - c.start).toFixed(3)) }, 'Trim end');
}

async function deleteSelected() {
  const ids = activeSelectionIds();
  if (!ids.length) { toast('Select a clip to delete'); return; }
  selectSingleClip(null);
  await runTool('delete_segments', { ids, ripple: rippleDelete }, 'Delete clip');
}

async function duplicateSelected() {
  if (!selectedClipId) { toast('Select a clip to duplicate'); return; }
  await runTool('duplicate_segment', { id: selectedClipId }, 'Duplicate clip');
}

async function freezeSelected() {
  const c = findClipById(selectedClipId)?.clip || clipUnderPlayhead();
  if (!c) { toast('Select a clip or move playhead inside one'); return; }
  await runTool('freeze_frame', { id: c.id, at: Number(playheadSec.toFixed(3)), duration: 3.0 }, 'Freeze frame');
}

async function reverseSelected() {
  if (!selectedClipId) { toast('Select a clip first'); return; }
  const hit = findClipById(selectedClipId);
  await runTool('reverse_segment', { id: selectedClipId, reversed: !hit?.clip?.reverse }, 'Reverse clip');
}

async function rotateSelected90() {
  const ids = activeSelectionIds();
  if (!ids.length) { toast('Select a clip first'); return; }
  const hit = findClipById(ids[0]);
  await runTool('edit_segments', { ids, rotation: ((hit?.clip?.rot || 0) + 90) % 360 }, 'Rotate +90°');
}

async function mirrorSelected() {
  const ids = activeSelectionIds();
  if (!ids.length) { toast('Select a clip first'); return; }
  const hit = findClipById(ids[0]);
  await runTool('edit_segments', { ids, flip_horizontal: !hit?.clip?.flip_h }, 'Mirror');
}

async function extractAudioSelected() {
  if (!selectedClipId) { toast('Select a video clip first'); return; }
  await runTool('extract_audio', { id: selectedClipId }, 'Extract audio');
}

async function replaceSelectedMedia() {
  if (!selectedClipId) { toast('Select a clip first'); return; }
  const path = await api.pick_single_media();
  if (!path) return;
  await runTool('replace_media', { id: selectedClipId, path }, 'Replace media');
}

async function shiftMainClip(dir) {
  const main = overview?.tracks?.find(t => t.is_main);
  if (!main || !selectedClipId) return;
  const order = main.segments.map(s => s.id);
  const idx = order.indexOf(selectedClipId);
  const next = idx + dir;
  if (idx < 0 || next < 0 || next >= order.length) { toast('Clip is not movable further on main track'); return; }
  [order[idx], order[next]] = [order[next], order[idx]];
  await runTool('reorder_main_track', { order }, 'Reorder clip');
}

async function closeMainGaps() { await runTool('close_gaps', { track: 'video#0' }, 'Close gaps'); }

function addMarkerAtPlayhead() {
  const t = Number(playheadSec.toFixed(2));
  if (!markers.includes(t)) markers.push(t);
  renderTimeline();
  toast(`Added marker at ${fmtTimeShort(t)}`);
}

// Horizontal Splitter Resize
(() => {
  const sp = $('hSplitter'), dock = $('timelineDock');
  let startY = 0, startH = 310, active = false;
  sp.addEventListener('pointerdown', e => { active = true; startY = e.clientY; startH = dock.getBoundingClientRect().height; sp.setPointerCapture(e.pointerId); });
  window.addEventListener('pointermove', e => {
    if (!active) return;
    dock.style.height = `${Math.max(185, Math.min(window.innerHeight - 260, startH - (e.clientY - startY)))}px`;
    layoutStage();
  });
  window.addEventListener('pointerup', () => { active = false; });
})();

// ---------------- EditPilot AI Chat ----------------
function renderMarkdown(md) {
  const raw = String(md ?? '').replace(/\r\n/g, '\n');
  const blocks = [];
  const src = raw.replace(/```[^\n]*\n([\s\S]*?)```/g, (_, code) => {
    const idx = blocks.length;
    blocks.push(`<pre class="md-code" dir="ltr"><code>${esc(code.replace(/\n$/, ''))}</code></pre>`);
    return `\u0000B${idx}\u0000`;
  });

  const fmtInline = s => {
    const codes = [];
    let out = esc(s).replace(/`([^`\n]+)`/g, (_, c) => {
      const idx = codes.length;
      codes.push(`<code class="md-inline-code" dir="ltr">${c}</code>`);
      return `\u0001C${idx}\u0001`;
    });
    out = out.replace(/\[([^\]]+)\]\((https:\/\/[^)\s]+)\)|(https:\/\/[^\s<)]+)/g, (_, label, mdUrl, bareUrl) => {
      const url = mdUrl || bareUrl;
      return `<a href="#" data-url="${url}" onclick="event.preventDefault(); api.open_url(this.dataset.url)">${label || url}</a>`;
    });
    out = out.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    out = out.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    out = out.replace(/\u0001C(\d+)\u0001/g, (_, i) => codes[Number(i)] || '');
    return out;
  };

  const splitCells = line => {
    let t = line.trim();
    if (t.startsWith('|')) t = t.slice(1);
    if (t.endsWith('|')) t = t.slice(0, -1);
    return t.split('|').map(c => c.trim());
  };
  const isSepRow = line => /^\s*\|?(\s*:?-{3,}:?\s*\|)+\s*:?-{3,}:?\s*\|?\s*$/.test(line)
    || /^\s*\|(\s*:?-{3,}:?\s*\|)+\s*$/.test(line);

  const lines = src.split('\n');
  const html = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const bm = line.trim().match(/^\u0000B(\d+)\u0000$/);
    if (bm) { html.push(blocks[Number(bm[1])] || ''); continue; }
    if (!line.trim()) continue;

    if (line.includes('|') && i + 1 < lines.length && isSepRow(lines[i + 1])) {
      const headers = splitCells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].includes('|') && lines[i].trim()) {
        rows.push(splitCells(lines[i]));
        i++;
      }
      i--;
      const ths = headers.map(h => `<th>${fmtInline(h)}</th>`).join('');
      const trs = rows.map(r => `<tr>${headers.map((_, ci) => `<td>${fmtInline(r[ci] ?? '')}</td>`).join('')}</tr>`).join('');
      html.push(`<div class="md-table-wrap"><table class="md-table" dir="auto"><thead><tr>${ths}</tr></thead><tbody>${trs}</tbody></table></div>`);
      continue;
    }

    const hm = line.match(/^\s*#{1,4}\s+(.+)$/);
    if (hm) { html.push(`<div class="md-h" dir="auto">${fmtInline(hm[1])}</div>`); continue; }

    if (/^\s*[-*•]\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*[-*•]\s+/.test(lines[i])) {
        items.push(`<li dir="auto">${fmtInline(lines[i].replace(/^\s*[-*•]\s+/, ''))}</li>`);
        i++;
      }
      i--;
      html.push(`<ul class="md-list">${items.join('')}</ul>`);
      continue;
    }

    if (/^\s*\d+[.)]\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i])) {
        items.push(`<li dir="auto">${fmtInline(lines[i].replace(/^\s*\d+[.)]\s+/, ''))}</li>`);
        i++;
      }
      i--;
      html.push(`<ol class="md-list">${items.join('')}</ol>`);
      continue;
    }

    const para = [];
    while (i < lines.length && lines[i].trim()
      && !/^\s*#{1,4}\s+/.test(lines[i])
      && !/^\s*[-*•]\s+/.test(lines[i])
      && !/^\s*\d+[.)]\s+/.test(lines[i])
      && !/^\u0000B(\d+)\u0000$/.test(lines[i].trim())
      && !(lines[i].includes('|') && i + 1 < lines.length && isSepRow(lines[i + 1]))) {
      para.push(fmtInline(lines[i]));
      i++;
    }
    i--;
    html.push(`<p class="md-p" dir="auto">${para.join('<br>')}</p>`);
  }
  return html.join('');
}

function addChat(cls, html) {
  $('chatWelcome')?.remove();
  const d = document.createElement('div');
  d.className = cls;
  if (cls.includes('user') || cls.includes('bot')) d.setAttribute('dir', 'auto');
  d.innerHTML = html;
  const log = $('chatLog');
  if (typingEl && typingEl.parentNode) log.insertBefore(d, typingEl);
  else log.appendChild(d);
  log.scrollTop = log.scrollHeight;
  return d;
}

function setBusy(b) {
  busy = b;
  $('btnSend').disabled = b;
  $('btnStop').style.display = b ? '' : 'none';
  if (b) {
    typingEl = document.createElement('div');
    typingEl.className = 'typing';
    typingEl.textContent = 'EditPilot is working...';
    $('chatLog').appendChild(typingEl);
    $('chatLog').scrollTop = $('chatLog').scrollHeight;
  } else if (typingEl) { typingEl.remove(); typingEl = null; }
}

async function sendChat() {
  const text = $('chatInput').value.trim();
  if (!text || busy) return;
  if (await pollEditor()) { pendingSendText = text; $('runTitle').textContent = `${edInfo().display} is Open`; showModal('mRun'); return; }
  await doSendChat(text);
}

async function runChoice(c) {
  hideModal('mRun');
  const t = pendingSendText; pendingSendText = null;
  if (c === 'cancel' || !t) return;
  if (c === 'close') await closeEditor();
  await doSendChat(t);
}

async function doSendChat(text) {
  $('chatInput').value = '';
  autoGrowChat();
  addChat('chat-msg user', esc(text));
  setBusy(true);
  const r = await api.send(text);
  if (!r.ok) { setBusy(false); if (r.error === 'nokey') showSettings(); else addChat('chat-msg err', esc(r.error)); }
}

function quickPrompt(t) { openEditPilot(); $('chatInput').value = t; sendChat(); }
async function newChat() { await api.new_chat(); $('chatLog').innerHTML = '<div class="chat-msg sys">Started a new EditPilot conversation.</div>'; }
async function attachFolder() { const f = await api.pick_folder(); if (f) insertChatText(`"${f}"`); }
async function attachFiles() { const fs = await api.pick_files(); if (fs?.length) insertChatText(fs.map(f => `"${f}"`).join(' ')); }
function insertChatText(s) { const i = $('chatInput'); i.value = (i.value ? i.value.trimEnd() + ' ' : '') + s + ' '; i.focus(); autoGrowChat(); }
function autoGrowChat() { const i = $('chatInput'); i.style.height = 'auto'; i.style.height = `${Math.min(i.scrollHeight, 110)}px`; }
$('chatInput').addEventListener('input', autoGrowChat);
$('chatInput').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChat(); } });

// ---------------- Projects, Settings, Export Modals ----------------
async function switchEditor(editorId) {
  try {
    const list = await api.set_editor(editorId);
    appState.editor = editorId;
    currentProjectPath = null;
    window._projectList = list;
    catalogCache = {};
    renderTopStatus();
    applyOverview(null);
    renderBin();
    pollEditor();
  } catch (e) { toast(e.message || String(e)); }
}

async function loadProjects() {
  try { window._projectList = await api.projects(); if (binTab === 'projects') renderBin(); }
  catch (e) { toast(e.message || String(e)); }
}

async function openProject(path) {
  if (busy) return;
  try {
    const ov = await api.open_project(path);
    currentProjectPath = path;
    localMediaPool = [];
    selectedBinPaths.clear();
    catalogCache = {};
    selectSingleClip(null);
    playheadSec = 0;
    needsTimelineFit = true;
    applyOverview(ov);
    loadProjects();
    if (binTab === 'projects') setBinTab('media');
    toast(`Opened "${ov.project}"`);
  } catch (e) { toast(e.message || String(e)); }
}

async function openFile() {
  try {
    const ov = await api.open_file();
    if (ov) { localMediaPool = []; selectedBinPaths.clear(); catalogCache = {}; selectSingleClip(null); playheadSec = 0; needsTimelineFit = true; applyOverview(ov); loadProjects(); if (binTab === 'projects') setBinTab('media'); }
  } catch (e) { toast(e.message || String(e)); }
}

function showNewProject() { $('newName').value = ''; showModal('mNew'); $('newName').focus(); }
async function createProject() {
  const n = $('newName').value.trim(); if (!n) return;
  if (await pollEditor()) { toast(`Close ${edInfo().display} before creating a project`); hideModal('mNew'); return; }
  hideModal('mNew');
  try {
    const ov = await api.new_project(n, $('newRatio').value);
    localMediaPool = []; selectedBinPaths.clear(); catalogCache = {}; selectSingleClip(null); playheadSec = 0; needsTimelineFit = true;
    applyOverview(ov); await loadProjects();
    if (binTab === 'projects') setBinTab('media');
    toast(`Created "${ov.project}"`);
  } catch (e) { toast(e.message || String(e)); }
}

async function exportProject() {
  try {
    const r = await api.export_project();
    $('exportMsg').textContent = r.exportOnly ? `${r.editor}: import files written (${r.hint}).` : r.message;
    $('exportFiles').innerHTML = (r.files || []).map(f =>
      `<div class="export-file-item"><span>${esc(f.name)}</span><small title="${esc(f.path)}">${esc(f.path)}</small></div>`).join('');
    showModal('mExport');
  } catch (e) { toast(e.message || String(e)); }
}

function pickProvider(id) {
  activeProvider = id;
  const p = provInfo(id);
  if (!p) return;
  $('provSel').value = id;
  $('provNote').textContent = p.note;
  $('keyLabel').innerHTML = `API Key — get one from <a onclick="api.open_url('${p.keyUrl}')">${esc(new URL(p.keyUrl).host)}</a>`;
  $('provKey').value = '';
  $('provKey').placeholder = p.hasKey ? 'Saved (leave blank to keep)' : 'Paste API key';
  const sel = $('provModel');
  sel.innerHTML = '<option value="">(Automatic - best available)</option>';
  if (p.model) { sel.add(new Option(p.model, p.model)); sel.value = p.model; }
}

function showSettings() {
  if (!appState) return;
  $('provSel').innerHTML = appState.providers.map(p =>
    `<option value="${p.id}">${esc(p.display)}${p.free ? ' - Free' : ' - Paid'}${p.hasKey ? ' (Saved)' : ''}</option>`).join('');
  pickProvider(appState.provider || 'gemini');
  showModal('mSettings');
}

async function loadModels() {
  try {
    const list = await api.list_models(activeProvider, $('provKey').value.trim());
    const sel = $('provModel'), keep = sel.value;
    sel.innerHTML = '<option value="">(Automatic - best available)</option>' + list.map(m => `<option>${esc(m)}</option>`).join('');
    sel.value = list.includes(keep) ? keep : (list[0] || '');
    if (!list.length) toast('No compatible models found');
  } catch (e) { toast(e.message || String(e)); }
}

async function saveSettings() {
  try {
    appState = await api.save_settings({ provider: activeProvider, key: $('provKey').value.trim(), model: $('provModel').value });
    hideModal('mSettings');
    renderTopStatus();
    toast(appState.hasKey ? `Saved ${provInfo(activeProvider)?.display}` : 'API key missing');
  } catch (e) { toast(e.message || String(e)); }
}

// ---------------- Host Push Events & External Explorer Drop ----------------
async function handleExternalTimelineDrop(paths, dropMeta) {
  const probed = await api.probe_media(paths);
  if (!probed || !probed.length) { toast('No supported media files found'); return; }
  const map = new Map(localMediaPool.map(x => [x.path.toLowerCase(), x]));
  for (const item of probed) {
    const k = item.path.toLowerCase();
    if (!map.has(k)) map.set(k, { ...item, in_timeline: false });
  }
  localMediaPool = Array.from(map.values());
  const visual = probed.filter(x => x.kind !== 'audio');
  const audio = probed.filter(x => x.kind === 'audio');
  if (visual.length) {
    await runTool('add_media', {
      paths: visual.map(x => x.path),
      target: dropMeta.target === 'overlay' || dropMeta.target === 'new_track' ? dropMeta.target : 'main',
      start: dropMeta.sec ?? 0,
    }, `Drop ${visual.length} media file(s)`);
  }
  let audStart = dropMeta.sec ?? 0;
  for (const a of audio) {
    await runTool('add_audio', { path: a.path, start: audStart }, `Drop ${a.name}`);
    audStart += (a.duration_s || 5);
  }
  if (!visual.length && !audio.length) renderBin();
}

function addToMediaPool(item) {
  if (!item?.path) return;
  const k = item.path.toLowerCase();
  const idx = localMediaPool.findIndex(x => (x.path || '').toLowerCase() === k);
  if (idx >= 0) localMediaPool[idx] = { ...localMediaPool[idx], ...item };
  else localMediaPool.push({ ...item, in_timeline: false });
  renderBin();
}

function showYouTubeModal() {
  $('ytUrl').value = '';
  $('ytMode').value = binTab === 'audio' ? 'audio' : 'audio';
  $('ytStatus').textContent = '';
  $('btnYtDownload').disabled = false;
  $('btnYtCancel').disabled = false;
  showModal('mYouTube');
  setTimeout(() => $('ytUrl').focus(), 40);
}

async function downloadYouTubeNow() {
  const url = $('ytUrl').value.trim();
  const mode = $('ytMode').value || 'audio';
  const addTimeline = $('ytAddToTimeline').checked;
  if (!url) { $('ytUrl').focus(); return; }
  $('btnYtDownload').disabled = true;
  $('btnYtCancel').disabled = true;
  $('ytStatus').textContent = 'Downloading from YouTube... (first run also fetches yt-dlp)';
  try {
    const res = await api.download_youtube(url, mode);
    addToMediaPool(res);
    if (addTimeline && overview) {
      $('ytStatus').textContent = 'Adding to timeline...';
      if (res.kind === 'audio') {
        await runTool('add_audio', { path: res.path, fit_to_video: true, fade_out: 2 }, `Add ${res.title || 'audio'}`);
      } else {
        await runTool('add_media', { paths: [res.path], target: 'main' }, `Add ${res.title || 'video'}`);
      }
    }
    hideModal('mYouTube');
    toast(`Downloaded "${res.title || res.name}"`);
  } catch (e) {
    $('ytStatus').textContent = e.message || String(e);
  } finally {
    $('btnYtDownload').disabled = false;
    $('btnYtCancel').disabled = false;
  }
}

function renderUpdateButton() {
  const b = $('updBtn');
  if (!b) return;
  if (updateInfo?.hasUpdate) {
    b.className = 'has-update';
    b.innerHTML = `${SVG.download}<span>Update v${esc(updateInfo.latest)}</span>`;
    b.title = `Update available: v${updateInfo.latest} (current v${updateInfo.current || appState?.version || ''})`;
  } else {
    b.className = 'ghost';
    const ver = updateInfo?.current || appState?.version || '';
    b.innerHTML = `${SVG.download}<span>${ver ? 'v' + esc(ver) : 'Updates'}</span>`;
    b.title = 'Check GitHub Releases for updates';
  }
}

function renderUpdateModal() {
  const sum = $('updSummary');
  const notes = $('updNotes');
  const inst = $('btnInstallUpd');
  if (!updateInfo) {
    sum.innerHTML = `Current version: <b>v${esc(appState?.version || '')}</b>`;
    notes.innerHTML = '<div style="color:var(--text-muted)">Click Check Again to fetch the latest GitHub Release.</div>';
    inst.style.display = 'none';
    return;
  }
  const pub = updateInfo.published ? ` · ${updateInfo.published.slice(0, 10)}` : '';
  if (updateInfo.hasUpdate) {
    sum.innerHTML = `<span style="color:var(--accent);font-weight:600">New release v${esc(updateInfo.latest)} available</span> (installed: v${esc(updateInfo.current)})${pub}`;
  } else {
    sum.innerHTML = `You are up to date on <b>v${esc(updateInfo.current)}</b> (latest on GitHub: v${esc(updateInfo.latest)})${pub}`;
  }
  notes.innerHTML = renderMarkdown(updateInfo.notes || 'No release notes provided.');
  if (updateInfo.setupUrl) {
    const mb = updateInfo.setupSize ? ` (${(updateInfo.setupSize / (1024 * 1024)).toFixed(1)} MB)` : '';
    inst.style.display = '';
    inst.textContent = (updateInfo.hasUpdate ? 'Download & Install' : 'Reinstall') + mb;
    inst.className = updateInfo.hasUpdate ? 'cyan' : '';
  } else {
    inst.style.display = 'none';
  }
}

async function openUpdateModal() {
  $('updProgWrap').style.display = 'none';
  $('updProgFill').style.width = '0%';
  showModal('mUpdate');
  renderUpdateModal();
  await checkUpdateNow(true);
}

function openReleasesPage() {
  api.open_url(updateInfo?.url || 'https://github.com/dor2500/ai-video-editor/releases');
}

async function checkUpdateNow(quiet = false) {
  const btn = $('btnCheckUpd');
  btn.disabled = true;
  if (!quiet) $('updSummary').textContent = 'Checking GitHub Releases...';
  try {
    updateInfo = await api.check_update();
    renderUpdateButton();
    renderUpdateModal();
  } catch (e) {
    if (!quiet || !updateInfo) $('updSummary').textContent = `Could not reach GitHub Releases: ${e.message || e}`;
  } finally {
    btn.disabled = false;
  }
}

async function installUpdateNow() {
  if (!updateInfo?.setupUrl) { openReleasesPage(); return; }
  $('btnInstallUpd').disabled = true;
  $('btnCheckUpd').disabled = true;
  $('updProgWrap').style.display = 'flex';
  $('updProgFill').style.width = '2%';
  $('updProgText').textContent = `Downloading ${updateInfo.setupName || 'installer'}...`;
  try {
    await api.install_update(updateInfo.setupUrl, updateInfo.setupName || '');
    $('updProgFill').style.width = '100%';
    $('updProgText').textContent = 'Launching installer...';
  } catch (e) {
    $('updProgText').textContent = `Download failed: ${e.message || e}`;
    $('btnInstallUpd').disabled = false;
    $('btnCheckUpd').disabled = false;
  }
}

function onEvent(kind, data) {
  if (kind === 'assistant') addChat('chat-msg bot', renderMarkdown(data));
  else if (kind === 'tool') {
    const d = addChat('tool-log', `Tool: ${esc(data.name)}<pre>${esc(JSON.stringify(data.input, null, 2))}</pre>`);
    d.onclick = () => d.classList.toggle('open');
  }
  else if (kind === 'changed') { refreshOverview(); if (data) addChat('chat-msg sys', esc(data)); }
  else if (kind === 'error') addChat('chat-msg err', esc(data));
  else if (kind === 'info') {
    if ($('mYouTube')?.style.display === 'flex' && typeof data === 'string') $('ytStatus').textContent = data;
    addChat('chat-msg sys', esc(data));
  }
  else if (kind === 'media_downloaded') { addToMediaPool(data); }
  else if (kind === 'provider') {
    if (appState) { appState.provider = data.id; const p = provInfo(data.id); if (p) p.hasKey = true; renderTopStatus(); }
  }
  else if (kind === 'dropped') {
    const meta = pendingExternalDrop;
    pendingExternalDrop = null;
    if (data?.length) {
      if (meta?.zone === 'timeline' && overview) handleExternalTimelineDrop(data, meta);
      else if (meta?.zone === 'chat' || inspTab === 'editpilot') insertChatText(data.map(f => `"${f}"`).join(' '));
      else probeAndAddToBin(data);
    }
  }
  else if (kind === 'update') { updateInfo = data; renderUpdateButton(); if ($('mUpdate')?.style.display === 'flex') renderUpdateModal(); }
  else if (kind === 'update_progress') {
    if ($('updProgWrap')) {
      $('updProgWrap').style.display = 'flex';
      $('updProgFill').style.width = `${Math.max(0, Math.min(100, data?.pct || 0))}%`;
      $('updProgText').textContent = data?.totalMb > 0
        ? `Downloading... ${data.pct}% (${data.downloadedMb} / ${data.totalMb} MB)`
        : `Downloading... (${data?.downloadedMb || 0} MB)`;
    }
  }
  else if (kind === 'project') { currentProjectPath = data.path; localMediaPool = []; selectedBinPaths.clear(); catalogCache = {}; selectSingleClip(null); playheadSec = 0; needsTimelineFit = true; refreshOverview(); loadProjects(); }
  else if (kind === 'done') { setBusy(false); refreshOverview(); }
}

document.addEventListener('dragover', e => {
  if (e.dataTransfer?.types?.includes('Files')) { e.preventDefault(); document.body.classList.add('dragging'); }
});
document.addEventListener('dragleave', e => {
  if (!e.relatedTarget) { document.body.classList.remove('dragging'); clearTimelineDropIndicators(); }
});
document.addEventListener('drop', e => {
  if (!e.dataTransfer?.files?.length) return;
  e.preventDefault();
  document.body.classList.remove('dragging');
  const inTimeline = !!e.target.closest('#tlScroll, #timelineDock');
  const inChat = !!e.target.closest('#inspChat');
  if (inTimeline) {
    const plan = currentDropPlan || { mode: 'main', sec: 0, target: 'main' };
    pendingExternalDrop = { zone: 'timeline', ...plan };
  } else if (inChat) {
    pendingExternalDrop = { zone: 'chat' };
  } else {
    pendingExternalDrop = { zone: 'bin' };
  }
  clearTimelineDropIndicators();
  try { window.chrome.webview.postMessageWithAdditionalObjects({ method: 'dropped' }, e.dataTransfer.files); }
  catch { pendingExternalDrop = null; toast('Drop local files or folders from Windows Explorer'); }
});

['mSettings', 'mNew', 'mRun', 'mExport', 'mUpdate', 'mYouTube'].forEach(id =>
  $(id).addEventListener('click', e => { if (e.target.id === id) hideModal(id); }));

// ---------------- Keyboard Shortcuts ----------------
document.addEventListener('keydown', e => {
  const tag = document.activeElement?.tagName;
  const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
  if (e.ctrlKey && e.key.toLowerCase() === 'z' && !e.shiftKey && !typing) { e.preventDefault(); doUndo(); return; }
  if (((e.ctrlKey && e.key.toLowerCase() === 'y') || (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'z')) && !typing) { e.preventDefault(); doRedo(); return; }
  if (e.ctrlKey && e.key.toLowerCase() === 'b') { e.preventDefault(); splitAtPlayhead(); return; }
  if (e.ctrlKey && e.key.toLowerCase() === 'd' && !typing) { e.preventDefault(); duplicateSelected(); return; }
  if (e.ctrlKey && (e.key === '=' || e.key === '+')) { e.preventDefault(); zoomTimeline(1); return; }
  if (e.ctrlKey && e.key === '-') { e.preventDefault(); zoomTimeline(-1); return; }
  if (typing) return;
  if (e.code === 'Space') { e.preventDefault(); togglePlay(); }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); stepFrames(-1); }
  else if (e.key === 'ArrowRight') { e.preventDefault(); stepFrames(1); }
  else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelected(); }
  else if (e.key === '[') trimToPlayhead('left');
  else if (e.key === ']') trimToPlayhead('right');
  else if (e.key.toLowerCase() === 'f') freezeSelected();
  else if (e.key.toLowerCase() === 'r') reverseSelected();
  else if (e.key.toLowerCase() === 'm') addMarkerAtPlayhead();
  else if (e.key.toLowerCase() === 'v') setToolMode('select');
  else if (e.key.toLowerCase() === 'c') setToolMode('blade');
});

// ---------------- Boot ----------------
(async () => {
  try {
    appState = await api.init();
    renderTopStatus();
    await loadProjects();
    if (appState.project) {
      currentProjectPath = appState.projectPath;
      needsTimelineFit = true;
      await refreshOverview();
    } else {
      applyOverview(null);
      setBinTab('projects');
    }
    renderInspector();
    pollEditor();
    setInterval(pollEditor, 5000);
  } catch (e) { toast(e.message || String(e)); }
})();
