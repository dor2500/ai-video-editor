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
let updUrl = '';
let busy = false;
let pendingSendText = null;
let typingEl = null;
let activeProvider = 'gemini';

let binTab = 'media';
let binFilter = 'all';
let binSearch = '';
let localMediaPool = [];
let catalogCache = {};

let inspTab = 'project';
let selectedClipId = null;
let toolMode = 'select'; // select | blade
let rippleDelete = true;
let snapEnabled = true;
let pxPerSec = 36;
let playheadSec = 0;
let playing = false;
let rafId = 0;
let lastFrameTs = 0;
let lockedTracks = new Set();
let markers = [];

let dragState = null;
let activeAudioCtx = null;
const waveformCache = new Map();
const activeVideos = new Map();
const activeAudios = new Map();

function esc(s) {
  return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

function showModal(id) { $(id).style.display = 'flex'; }
function hideModal(id) { $(id).style.display = 'none'; }

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
document.addEventListener('click', e => {
  if (!e.target.closest('.menu-wrap')) closeMenu();
});

function renderTopStatus() {
  if (!appState) return;
  const pi = provInfo(appState.provider);
  const name = pi ? pi.display.replace(' (free models)', '').replace(/ \(.*\)/, '') : appState.provider;
  $('provpill').textContent = `AI: ${name}`;
  $('provpill').className = 'pill ' + (appState.hasKey ? 'ok' : 'warn');
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
  } catch (e) {
    toast(e.message || String(e));
  }
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

  // Merge project media_pool into localMediaPool without losing uncommitted imported items.
  const map = new Map();
  for (const item of localMediaPool) map.set(item.path.toLowerCase(), { ...item, in_timeline: false });
  for (const item of (ov.media_pool || [])) {
    const key = (item.path || '').toLowerCase();
    if (!key) continue;
    map.set(key, { ...item });
  }
  localMediaPool = Array.from(map.values());

  if (playheadSec > projectDuration() && projectDuration() > 0) playheadSec = projectDuration();
  if (selectedClipId && !findClipById(selectedClipId)) selectedClipId = null;

  updateAutoSaveStamp();
  renderBin();
  renderTimeline();
  renderStage();
  renderInspector();
}

function visualTracksOrdered() {
  if (!overview?.tracks) return [];
  const videos = [];
  const overlays = [];
  const audios = [];
  for (const t of overview.tracks) {
    if (t.is_main) videos.push(t);
    else if (t.type === 'audio') audios.push(t);
    else overlays.push(t);
  }
  // Overlays sit above the main video track; audio tracks sit below.
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

// ---------------- Left Media & Asset Bin ----------------
function setBinTab(tab) {
  binTab = tab;
  document.querySelectorAll('.bin-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
  renderBin();
  if (['effects', 'transitions', 'filters'].includes(tab)) loadCatalogTab(tab);
}

async function loadCatalogTab(tab) {
  const cat = tab === 'effects' ? 'effect' : tab === 'transitions' ? 'transition' : 'filter';
  try {
    const items = await api.search_catalog(cat, binSearch);
    catalogCache[cat] = Array.isArray(items) ? items : [];
    renderBin();
  } catch { }
}

function renderBin() {
  const body = $('binBody');
  if (binTab === 'projects') {
    renderProjectsBin(body);
    return;
  }
  if (binTab === 'text') {
    renderTextBin(body);
    return;
  }
  if (['effects', 'transitions', 'filters'].includes(binTab)) {
    renderCatalogBin(body, binTab);
    return;
  }

  const isAudioTab = binTab === 'audio';
  const subItems = isAudioTab
    ? [{ id: 'audio', label: 'All Audio' }, { id: 'timeline', label: 'In Timeline' }]
    : [{ id: 'all', label: 'All' }, { id: 'video', label: 'Video' }, { id: 'photo', label: 'Photos' }, { id: 'audio', label: 'Audio' }, { id: 'timeline', label: 'In Timeline' }];

  if (isAudioTab && binFilter !== 'timeline') binFilter = 'audio';
  if (!isAudioTab && binFilter === 'audio' && binTab === 'media') { /* keep */ }

  let list = localMediaPool.filter(item => {
    if (binFilter === 'timeline' && !item.in_timeline) return false;
    if (binFilter === 'video' && item.kind !== 'video') return false;
    if (binFilter === 'photo' && item.kind !== 'photo') return false;
    if (binFilter === 'audio' && item.kind !== 'audio') return false;
    if (binSearch && !item.name.toLowerCase().includes(binSearch.toLowerCase())) return false;
    return true;
  });

  const subNavHtml = `
    <div class="bin-subnav">
      <div class="subnav-label">Import</div>
      <button class="subnav-btn" onclick="importMediaFiles()"><span>Files</span><span>+</span></button>
      <button class="subnav-btn" onclick="importMediaFolder()"><span>Folder</span><span>+</span></button>
      <div class="subnav-label">Library</div>
      ${subItems.map(s => `<button class="subnav-btn ${binFilter === s.id ? 'active' : ''}" onclick="setBinFilter('${s.id}')"><span>${s.label}</span></button>`).join('')}
      <div class="subnav-label">Generate</div>
      <button class="subnav-btn" onclick="openEditPilot()"><span>EditPilot</span><span class="ai-mini">AI</span></button>
    </div>`;

  const cardsHtml = list.length ? list.map(item => {
    const durBadge = item.kind !== 'photo' && item.duration_s ? `<span class="badge-dur">${fmtTimeShort(item.duration_s)}</span>` : '';
    const addedBadge = item.in_timeline ? `<span class="badge-added">Added</span>` : '';
    const thumbHtml = item.kind === 'audio'
      ? `<div class="media-thumb audio-thumb"><div class="audio-bars">${[12, 22, 16, 25, 14, 20, 18, 11].map(h => `<span style="height:${h}px"></span>`).join('')}</div>${addedBadge}${durBadge}<button class="quick-add" onclick="event.stopPropagation(); quickAddMedia('${esc(item.path)}','${item.kind}')" title="Add to timeline">+</button></div>`
      : `<div class="media-thumb"><img src="${thumbUrl(item.path, 220)}" loading="lazy" alt="">${addedBadge}${durBadge}<button class="quick-add" onclick="event.stopPropagation(); quickAddMedia('${esc(item.path)}','${item.kind}')" title="Add to timeline">+</button></div>`;
    return `<div class="media-card" draggable="true" ondragstart="onMediaDragStart(event, '${esc(item.path)}', '${item.kind}')" ondblclick="quickAddMedia('${esc(item.path)}','${item.kind}')" title="${esc(item.path)}">
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
        <div class="search-box">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/></svg>
          <input value="${esc(binSearch)}" placeholder="Search media" oninput="binSearch=this.value; renderBin()">
        </div>
      </div>
      <div class="bin-section-head"><span>${subItems.find(s => s.id === binFilter)?.label || 'All'}</span><span>${list.length} items</span></div>
      <div class="media-grid">${cardsHtml}</div>
    </div>`;
}

function setBinFilter(f) { binFilter = f; renderBin(); }

function renderTextBin(body) {
  const presets = [
    { name: 'Default Title', desc: 'Clean centered white heading', args: { text: 'New Title', font_size: 14, color: '#FFFFFF', bold: true, x: 0, y: 0.55 } },
    { name: 'Lower Third', desc: 'Bottom left caption', args: { text: 'Subtitle / Name', font_size: 9, color: '#FFFFFF', bold: false, x: -0.45, y: -0.7, background_color: '#000000' } },
    { name: 'Bold Highlight', desc: 'High-contrast yellow subtitle', args: { text: 'Highlight Text', font_size: 11, color: '#FFD700', bold: true, x: 0, y: -0.7, stroke_color: '#000000' } },
    { name: 'Cyan Accent', desc: 'CapCut teal headline', args: { text: 'Chapter Title', font_size: 13, color: '#00D2C8', bold: true, x: 0, y: 0.6 } },
  ];
  body.innerHTML = `
    <div class="bin-main">
      <div class="bin-section-head"><span>Text Presets (click to insert at playhead)</span></div>
      <div class="catalog-list">
        ${presets.map((p, i) => `<button class="cat-card" onclick="addTextPreset(${i})"><div class="cname">${esc(p.name)}</div><div class="cdesc">${esc(p.desc)}</div></button>`).join('')}
      </div>
    </div>`;
  window._textPresets = presets;
}

async function addTextPreset(idx) {
  const p = window._textPresets[idx];
  if (!p) return;
  await runTool('add_text', { ...p.args, start: playheadSec, duration: 3.0 }, `Add text: ${p.name}`);
}

function renderCatalogBin(body, tab) {
  const cat = tab === 'effects' ? 'effect' : tab === 'transitions' ? 'transition' : 'filter';
  const items = catalogCache[cat] || [];
  const filtered = items.filter(it => !binSearch || (it.name || '').toLowerCase().includes(binSearch.toLowerCase()));
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
        ${filtered.length ? filtered.map(it => {
          if (it.note) return `<div style="grid-column:1/-1;color:var(--text-muted);padding:10px">${esc(it.note)}</div>`;
          const desc = it.description || (it.used_before ? 'Used before in CapCut' : (it.vip ? 'Pro item' : 'Ready'));
          return `<button class="cat-card" onclick="applyCatalogItem('${cat}','${esc(it.name)}')"><div class="cname">${esc(it.name)}</div><div class="cdesc">${esc(desc)}</div></button>`;
        }).join('') : `<div style="grid-column:1/-1;color:var(--text-muted);padding:16px;text-align:center">No ${tab} found in catalog.</div>`}
      </div>
    </div>`;
}

async function applyCatalogItem(cat, name) {
  if (!overview) { toast('Open a project first'); return; }
  if (cat === 'transition') {
    if (selectedClipId) await runTool('set_transitions', { ids: [selectedClipId], name, duration: 0.6 }, `Transition ${name}`);
    else await runTool('set_transitions', { all_main: true, name, duration: 0.6 }, `Transitions ${name}`);
  } else {
    await runTool('add_filter_or_effect', { category: cat, name, start: playheadSec, duration: 3.0, intensity: 1.0 }, `Add ${cat} ${name}`);
  }
}

function renderProjectsBin(body) {
  const edOpts = (appState?.editors || []).map(e =>
    `<option value="${e.id}" ${e.id === appState.editor ? 'selected' : ''}>${esc(e.display)}${e.installed ? ' ✓' : ''}${e.exportOnly ? ' (Import)' : ''}</option>`).join('');
  const ed = edInfo();
  const hint = ed ? (DIRECT_EDITORS.includes(ed.id) ? `Direct editing of ${ed.display} project files.` : `Writes import files (${ed.hint}).`) : '';
  const listHtml = (window._projectList || []).map(p =>
    `<div class="pitem ${currentProjectPath === p.path ? 'on' : ''}" onclick="openProject('${esc(p.path)}')" title="${esc(p.path)}">
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
        <button onclick="loadProjects()" title="Refresh project list">↻</button>
      </div>
      <div class="plist">${listHtml}</div>
    </div>`;
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

function onMediaDragStart(ev, path, kind) {
  ev.dataTransfer.setData('application/x-aive-media', JSON.stringify({ path, kind }));
  ev.dataTransfer.effectAllowed = 'copy';
}

// ---------------- Preview Stage ----------------
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

function renderStage() {
  layoutStage();
  const vp = $('stageViewport');
  const empty = $('stageEmpty');
  const fps = projectFps();
  $('tcCur').textContent = fmtSmpte(playheadSec, fps);
  $('tcTot').textContent = fmtSmpte(projectDuration(), fps);

  if (!overview) {
    empty.style.display = 'flex';
    clearStageMedia();
    return;
  }
  empty.style.display = 'none';

  // Collect active visual layers bottom-to-top: video#0 first, then other visual/text tracks.
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
  // Ensure video#0 (main) is base layer and overlays render above it.
  visualClips.sort((a, b) => (a.track.is_main ? -1 : b.track.is_main ? 1 : 0));

  // Remove old dynamic layer nodes (keep stageEmpty and safeGrid).
  vp.querySelectorAll('.stage-layer, .stage-text-layer').forEach(n => n.remove());

  const activeVidIds = new Set();
  const stageW = vp.clientWidth || 640;

  for (const { track, clip } of visualClips) {
    if (clip.visible === false || clip.opacity === 0) continue;
    if (clip.kind === 'text') {
      const div = document.createElement('div');
      div.className = 'stage-text-layer';
      const [px, py] = clip.pos || [0, -0.7];
      const scale = clip.scale ?? 1;
      const rot = clip.rot ?? 0;
      div.style.left = `${50 + px * 50}%`;
      div.style.top = `${50 - py * 50}%`;
      div.style.transform = `translate(-50%, -50%) scale(${scale}) rotate(${rot}deg)`;
      div.style.opacity = String(clip.opacity ?? 1);
      div.style.color = clip.color || '#FFFFFF';
      div.style.fontWeight = clip.bold ? '700' : '500';
      div.style.fontSize = `${Math.max(12, Math.round((clip.font_size || 8) * stageW / 260))}px`;
      div.textContent = clip.text || '';
      vp.appendChild(div);
      continue;
    }

    const layer = document.createElement('div');
    layer.className = 'stage-layer';
    const [px, py] = clip.pos || [0, 0];
    const sc = clip.scale ?? 1;
    const rot = clip.rot ?? 0;
    const fx = clip.flip_h ? -1 : 1;
    const fy = clip.flip_v ? -1 : 1;
    layer.style.opacity = String(clip.opacity ?? 1);
    layer.style.transform = `translate(${px * 50}%, ${-py * 50}%) scale(${sc * fx}, ${sc * fy}) rotate(${rot}deg)`;

    if (clip.kind === 'photo' && clip.path) {
      const img = document.createElement('img');
      img.src = mediaUrl(clip.path);
      layer.appendChild(img);
    } else if (clip.kind === 'video' && clip.path) {
      activeVidIds.add(clip.id);
      let vid = activeVideos.get(clip.id);
      if (!vid || vid.dataset.path !== clip.path) {
        vid = document.createElement('video');
        vid.dataset.path = clip.path;
        vid.src = mediaUrl(clip.path);
        vid.playsInline = true;
        vid.preload = 'auto';
        activeVideos.set(clip.id, vid);
      }
      vid.muted = !!track.muted || (clip.volume === 0);
      vid.volume = Math.min(1, Math.max(0, clip.volume ?? 1));
      vid.playbackRate = clip.speed || 1;
      const targetTime = Math.max(0, (clip.source_in || 0) + (playheadSec - clip.start) * (clip.speed || 1));
      if (Math.abs((vid.currentTime || 0) - targetTime) > (playing ? 0.25 : 0.04)) {
        try { vid.currentTime = targetTime; } catch { }
      }
      if (playing && vid.paused) vid.play().catch(() => {});
      else if (!playing && !vid.paused) vid.pause();
      layer.appendChild(vid);
    }
    vp.appendChild(layer);
  }

  for (const [id, vid] of activeVideos) {
    if (!activeVidIds.has(id)) { vid.pause(); activeVideos.delete(id); }
  }

  // Sync audio tracks.
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

function clearStageMedia() {
  for (const v of activeVideos.values()) v.pause();
  activeVideos.clear();
  for (const a of activeAudios.values()) a.pause();
  activeAudios.clear();
  $('stageViewport').querySelectorAll('.stage-layer, .stage-text-layer').forEach(n => n.remove());
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

// ---------------- Right Inspector & EditPilot ----------------
function setInspTab(tab) {
  inspTab = tab;
  document.querySelectorAll('.insp-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
  $('inspProject').style.display = tab === 'project' ? 'flex' : 'none';
  $('inspDetails').style.display = tab === 'details' ? 'flex' : 'none';
  $('inspChat').style.display = tab === 'editpilot' ? 'flex' : 'none';
  $('editPilotFab').style.display = tab === 'editpilot' ? 'none' : 'flex';
  if (tab === 'editpilot') setTimeout(() => $('chatInput').focus(), 20);
}

function openEditPilot() {
  setInspTab('editpilot');
}

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
      <div class="smart-sub">Find out how your timeline pacing, transitions, audio, and framing can be improved.</div>
      <div>
        <button class="cyan" onclick="analyzeWithAI()">✦ Analyze</button>
      </div>
    </div>
    <div class="section-title">Global edits</div>
    <div class="global-list">
      <div class="global-row" onclick="runTool('set_transitions', { all_main: true, name: 'Cross Dissolve', duration: 0.6 }, 'Cross Dissolve all')">
        <div class="global-row-left"><span>✦</span><span>Add Cross Dissolve transitions</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="runTool('add_motion', { all_main_photos: true, preset: 'alternate', strength: 0.15 }, 'Ken Burns motion')">
        <div class="global-row-left"><span>⤢</span><span>Add Ken Burns motion to photos</span></div>
        <span style="color:var(--accent);font-size:11px">Apply</span>
      </div>
      <div class="global-row" onclick="toggleMainMute()">
        <div class="global-row-left"><span>🔇</span><span>Mute original video audio</span></div>
        <span class="toggle-box ${mainMuted ? 'on' : ''}">${mainMuted ? '✓' : ''}</span>
      </div>
      <div class="global-row" onclick="closeMainGaps()">
        <div class="global-row-left"><span>⇥</span><span>Close main-track gaps</span></div>
        <span style="color:var(--accent);font-size:11px">Run</span>
      </div>
      <div class="global-row" onclick="runTool('set_transitions', { all_main: true, name: 'none' }, 'Clear transitions')">
        <div class="global-row-left"><span>✕</span><span>Remove all transitions</span></div>
        <span style="color:var(--text-muted);font-size:11px">Clear</span>
      </div>
    </div>`;
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

function renderDetailsInspector() {
  const el = $('inspDetails');
  const hit = findClipById(selectedClipId);
  if (!hit) {
    if (!overview) {
      el.innerHTML = `<div style="color:var(--text-muted);padding:16px;text-align:center">Open a project and click a clip on the timeline to inspect its properties.</div>`;
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
  const [px, py] = clip.pos || [0, 0];
  const isVisual = clip.kind === 'video' || clip.kind === 'photo' || clip.kind === 'text';
  const hasAudio = clip.kind === 'video' || clip.kind === 'audio';
  const title = clip.text || clip.file || clip.name || clip.id;

  let html = `
    <div class="prop-group">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:6px">
        <span style="font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(title)}</span>
        <span class="ai-mini">${esc(clip.kind.toUpperCase())}</span>
      </div>
      <div style="display:flex;gap:6px">
        <button style="flex:1" onclick="splitSelectedAtPlayhead()">Split at Playhead</button>
        <button style="flex:1;color:#fca5a5" onclick="deleteSelected()">Delete</button>
      </div>
    </div>

    <div class="prop-group">
      <div class="prop-group-title">Timing</div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Start (s)</label><input type="number" step="0.1" value="${clip.start}" onchange="editClipProp('${clip.id}', { start: Number(this.value) })"></div>
        <div class="prop-field"><label>Duration (s)</label><input type="number" step="0.1" min="0.1" value="${clip.dur}" onchange="editClipProp('${clip.id}', { duration: Number(this.value) })"></div>
      </div>
      ${clip.kind !== 'text' ? `<div class="prop-row"><label>Speed</label><div class="range-wrap"><input type="range" min="0.25" max="4" step="0.05" value="${clip.speed ?? 1}" oninput="this.nextElementSibling.textContent=Number(this.value).toFixed(2)+'x'" onchange="editClipProp('${clip.id}', { speed: Number(this.value) })"><span class="range-val">${(clip.speed ?? 1).toFixed(2)}x</span></div></div>` : ''}
    </div>`;

  if (isVisual) {
    html += `
    <div class="prop-group">
      <div class="prop-group-title">Transform</div>
      <div class="prop-row"><label>Scale</label><div class="range-wrap"><input type="range" min="0.1" max="3" step="0.05" value="${clip.scale ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { scale: Number(this.value) })"><span class="range-val">${Math.round((clip.scale ?? 1) * 100)}%</span></div></div>
      <div class="prop-row"><label>Opacity</label><div class="range-wrap"><input type="range" min="0" max="1" step="0.05" value="${clip.opacity ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { opacity: Number(this.value) })"><span class="range-val">${Math.round((clip.opacity ?? 1) * 100)}%</span></div></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Pos X</label><input type="number" step="0.05" min="-2" max="2" value="${px}" onchange="editClipProp('${clip.id}', { x: Number(this.value) })"></div>
        <div class="prop-field"><label>Pos Y</label><input type="number" step="0.05" min="-2" max="2" value="${py}" onchange="editClipProp('${clip.id}', { y: Number(this.value) })"></div>
      </div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Rotation (°)</label><input type="number" step="1" value="${clip.rot ?? 0}" onchange="editClipProp('${clip.id}', { rotation: Number(this.value) })"></div>
        <div class="prop-field"><label>Flip</label>
          <div style="display:flex;gap:4px">
            <button style="flex:1" class="${clip.flip_h ? 'cyan' : ''}" onclick="editClipProp('${clip.id}', { flip_horizontal: ${!clip.flip_h} })">H</button>
            <button style="flex:1" class="${clip.flip_v ? 'cyan' : ''}" onclick="editClipProp('${clip.id}', { flip_vertical: ${!clip.flip_v} })">V</button>
          </div>
        </div>
      </div>
    </div>`;
  }

  if (hasAudio) {
    html += `
    <div class="prop-group">
      <div class="prop-group-title">Audio</div>
      <div class="prop-row"><label>Volume</label><div class="range-wrap"><input type="range" min="0" max="2" step="0.05" value="${clip.volume ?? 1}" oninput="this.nextElementSibling.textContent=Math.round(this.value*100)+'%'" onchange="editClipProp('${clip.id}', { volume: Number(this.value) })"><span class="range-val">${Math.round((clip.volume ?? 1) * 100)}%</span></div></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Fade In (s)</label><input type="number" step="0.1" min="0" value="${clip.fade_in ?? 0}" onchange="runTool('set_audio_fade', { ids: ['${clip.id}'], fade_in: Number(this.value), fade_out: ${clip.fade_out ?? 0} }, 'Audio fade')"></div>
        <div class="prop-field"><label>Fade Out (s)</label><input type="number" step="0.1" min="0" value="${clip.fade_out ?? 0}" onchange="runTool('set_audio_fade', { ids: ['${clip.id}'], fade_in: ${clip.fade_in ?? 0}, fade_out: Number(this.value) }, 'Audio fade')"></div>
      </div>
    </div>`;
  }

  if (clip.kind === 'photo' || clip.kind === 'video') {
    const presets = ['none', 'zoom_in', 'zoom_out', 'pan_left', 'pan_right', 'pan_up', 'pan_down', 'fade_in_out'];
    html += `
    <div class="prop-group">
      <div class="prop-group-title">Motion (Ken Burns)</div>
      <select onchange="runTool('add_motion', { ids: ['${clip.id}'], preset: this.value, strength: 0.15 }, 'Motion')">
        ${presets.map(pr => `<option value="${pr}" ${(clip.motion || 'none') === pr ? 'selected' : ''}>${pr}</option>`).join('')}
      </select>
    </div>`;
  }

  if (clip.kind === 'text') {
    html += `
    <div class="prop-group">
      <div class="prop-group-title">Text Style</div>
      <div class="prop-field"><label>Content</label><input value="${esc(clip.text || '')}" onchange="editClipProp('${clip.id}', { text: this.value })"></div>
      <div class="prop-grid-2">
        <div class="prop-field"><label>Size</label><input type="number" step="1" min="2" max="60" value="${clip.font_size ?? 8}" onchange="editClipProp('${clip.id}', { font_size: Number(this.value) })"></div>
        <div class="prop-field"><label>Color</label><input type="color" value="${esc(clip.color || '#FFFFFF')}" onchange="editClipProp('${clip.id}', { color: this.value })"></div>
      </div>
      <button class="${clip.bold ? 'cyan' : ''}" onclick="editClipProp('${clip.id}', { bold: ${!clip.bold} })">Bold ${clip.bold ? 'On' : 'Off'}</button>
    </div>`;
  }

  el.innerHTML = html;
}

async function editClipProp(id, fields) {
  await runTool('edit_segments', { ids: [id], ...fields }, 'Edit clip');
}

// ---------------- Interactive Multi-Track Timeline ----------------
function setToolMode(m) {
  toolMode = m;
  $('btnModeSelect').classList.toggle('active', m === 'select');
  $('btnModeBlade').classList.toggle('active', m === 'blade');
  $('tlLanes').style.cursor = m === 'blade' ? 'crosshair' : 'default';
}

function toggleRipple() {
  rippleDelete = !rippleDelete;
  $('btnRipple').classList.toggle('active', rippleDelete);
}

function toggleSnap() {
  snapEnabled = !snapEnabled;
  $('btnSnap').classList.toggle('active', snapEnabled);
}

function setZoom(v) {
  pxPerSec = Math.max(4, Math.min(240, v));
  $('zoomSlider').value = String(pxPerSec);
  renderTimeline();
}

function zoomTimeline(dir) {
  setZoom(dir > 0 ? pxPerSec * 1.28 : pxPerSec / 1.28);
}

function fitTimeline() {
  const dur = Math.max(projectDuration(), 5);
  const avail = Math.max(300, $('tlScroll').clientWidth - 40);
  setZoom(Math.max(4, Math.min(240, avail / dur)));
  $('tlScroll').scrollLeft = 0;
}

function trackHeight(t) {
  if (t.is_main) return 56;
  if (t.type === 'video') return 46;
  if (t.type === 'audio') return 42;
  return 32;
}

function trackShortBadge(t) {
  if (t.is_main) return 'Main';
  const p = t.type === 'video' ? 'V' : t.type === 'audio' ? 'A' : t.type === 'text' ? 'T' : 'FX';
  return `${p}${t.index + 1}`;
}

function trackApiId(t) { return `${t.type}#${t.index}`; }

function renderTimeline() {
  const hdrList = $('tlHeaderList');
  const ruler = $('tlRuler');
  const lanes = $('tlLanes');
  const canvas = $('tlCanvas');
  const scrollEl = $('tlScroll');

  if (!overview) {
    hdrList.innerHTML = '';
    ruler.innerHTML = '';
    lanes.innerHTML = '<div style="padding:28px;color:var(--text-muted);text-align:center">No project loaded. Open a project from the Projects tab or Menu.</div>';
    updatePlayheadDom();
    return;
  }

  const dur = Math.max(projectDuration(), 20);
  const totalSec = dur + Math.max(10, dur * 0.25);
  const widthPx = Math.max(scrollEl.clientWidth, Math.ceil(totalSec * pxPerSec));
  canvas.style.width = `${widthPx}px`;

  // Time Ruler
  const stepSec = pxPerSec >= 120 ? 1 : pxPerSec >= 55 ? 2 : pxPerSec >= 22 ? 5 : pxPerSec >= 10 ? 10 : 30;
  const subDiv = 5;
  const minorSec = stepSec / subDiv;
  let rHtml = '';
  for (let t = 0; t <= totalSec; t += minorSec) {
    const x = Math.round(t * pxPerSec);
    const isMajor = Math.abs(Math.round(t / stepSec) * stepSec - t) < 0.001;
    rHtml += `<div class="tl-ruler-tick ${isMajor ? 'major' : 'minor'}" style="left:${x}px"></div>`;
    if (isMajor) rHtml += `<span class="tl-ruler-label" style="left:${x}px">${fmtTimeShort(t)}</span>`;
  }
  for (let i = 0; i < markers.length; i++) {
    const x = Math.round(markers[i] * pxPerSec);
    rHtml += `<div class="tl-marker" style="left:${x}px" title="Marker ${fmtTimeShort(markers[i])} (click to jump)" onclick="event.stopPropagation(); seekTo(${markers[i]})"></div>`;
  }
  ruler.innerHTML = rHtml;

  // Track Headers & Lanes
  const ordered = visualTracksOrdered();
  let hHtml = '';
  let lHtml = '';
  for (const t of ordered) {
    const h = trackHeight(t);
    const tid = trackApiId(t);
    const locked = lockedTracks.has(tid);
    const mainCls = t.is_main ? 'main-track' : '';
    const hasMute = t.type === 'video' || t.type === 'audio';
    const hasEye = t.type !== 'audio';
    hHtml += `<div class="tl-track-hdr ${mainCls}" style="height:${h}px">
      <div class="tl-hdr-btns">
        ${hasEye ? `<button class="tl-hdr-btn ${t.hidden ? 'warn' : ''}" onclick="toggleTrackHidden('${tid}', ${!t.hidden})" title="Toggle track visibility">${t.hidden ? '⊘' : '👁'}</button>` : ''}
        <button class="tl-hdr-btn ${locked ? 'on' : ''}" onclick="toggleTrackLock('${tid}')" title="Lock track">${locked ? '🔒' : '🔓'}</button>
        ${hasMute ? `<button class="tl-hdr-btn ${t.muted ? 'warn' : ''}" onclick="toggleTrackMute('${tid}', ${!t.muted})" title="Mute track">${t.muted ? '🔇' : '🔊'}</button>` : ''}
      </div>
      <span class="tl-hdr-badge ${t.is_main ? 'main' : ''}">${trackShortBadge(t)}</span>
    </div>`;

    let segsHtml = '';
    for (const c of t.segments) {
      const left = Math.round(c.start * pxPerSec);
      const w = Math.max(8, Math.round(c.dur * pxPerSec));
      const sel = c.id === selectedClipId ? 'selected' : '';
      const label = c.text || c.file || c.name || c.kind;
      const bodyStyle = (c.kind === 'video' || c.kind === 'photo') && c.path
        ? `style="background-image:url('${thumbUrl(c.path, 140)}')"`
        : '';
      const waveHtml = c.kind === 'audio' ? `<canvas class="tl-wave" data-path="${esc(c.path || '')}" data-id="${c.id}"></canvas>` : '';
      const transHtml = c.transition ? `<div class="tl-trans-badge" title="Transition: ${esc(c.transition)}">⋈</div>` : '';

      segsHtml += `<div class="tl-clip kind-${esc(c.kind)} ${sel}" data-id="${c.id}" data-track="${tid}" style="left:${left}px;width:${w}px">
        <div class="tl-trim left" data-trim="left"></div>
        <div class="tl-clip-head"><span>${esc(label)}</span></div>
        <div class="tl-clip-body" ${bodyStyle}>${waveHtml}</div>
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

function updatePlayheadDom() {
  const x = Math.round(playheadSec * pxPerSec);
  $('tlPlayhead').style.left = `${x}px`;
}

$('tlScroll').addEventListener('scroll', () => {
  $('tlHeaderList').scrollTop = $('tlScroll').scrollTop;
});

async function toggleTrackMute(trackId, muted) {
  await runTool('set_track_mute', { track: trackId, muted }, `Mute ${trackId}`);
}
async function toggleTrackHidden(trackId, hidden) {
  await runTool('set_track_hidden', { track: trackId, hidden }, `Hide ${trackId}`);
}
function toggleTrackLock(trackId) {
  if (lockedTracks.has(trackId)) lockedTracks.delete(trackId);
  else lockedTracks.add(trackId);
  renderTimeline();
}

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
  const ctx = canvas.getContext('2d');
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = 'rgba(96, 165, 250, 0.78)';
  const barW = 2;
  const gap = 1;
  let hash = 17;
  for (let i = 0; i < (seedStr || '').length; i++) hash = (hash * 31 + seedStr.charCodeAt(i)) | 0;

  for (let x = 0; x < w; x += barW + gap) {
    let amp;
    if (peaks && peaks.length) {
      const idx = Math.floor((x / w) * peaks.length);
      amp = peaks[Math.min(peaks.length - 1, idx)];
    } else {
      amp = 0.25 + 0.65 * Math.abs(Math.sin((x + Math.abs(hash % 97)) * 0.17) * Math.cos(x * 0.05));
    }
    const bh = Math.max(2, Math.round(amp * (h - 3)));
    ctx.fillRect(x, h - bh, barW, bh);
  }
}

async function decodeAudioPeaks(path) {
  try {
    if (!activeAudioCtx) activeAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const resp = await fetch(mediaUrl(path));
    if (!resp.ok) return;
    const buf = await resp.arrayBuffer();
    const audioBuf = await activeAudioCtx.decodeAudioData(buf);
    const data = audioBuf.getChannelData(0);
    const buckets = 240;
    const step = Math.max(1, Math.floor(data.length / buckets));
    const peaks = new Float32Array(buckets);
    for (let i = 0; i < buckets; i++) {
      let max = 0;
      const start = i * step;
      for (let j = 0; j < step && start + j < data.length; j += Math.max(1, Math.floor(step / 16))) {
        const v = Math.abs(data[start + j]);
        if (v > max) max = v;
      }
      peaks[i] = Math.min(1, max);
    }
    waveformCache.set(path, peaks);
    document.querySelectorAll(`.tl-wave[data-path="${CSS.escape(path)}"]`).forEach(c => paintWaveform(c, peaks, path));
  } catch { }
}

// ---------------- Timeline Pointer Interactions (Scrub, Drag, Trim, Snap, Blade) ----------------
function snapPoints(excludeId) {
  const pts = [0, playheadSec];
  for (const t of overview?.tracks || []) {
    for (const c of t.segments) {
      if (c.id === excludeId) continue;
      pts.push(c.start, c.end);
    }
  }
  return pts;
}

function applySnap(sec, pts) {
  if (!snapEnabled) return { sec, snapped: null };
  const tol = 8 / pxPerSec;
  let best = sec;
  let bestDist = tol;
  let snapped = null;
  for (const p of pts) {
    const d = Math.abs(sec - p);
    if (d < bestDist) {
      bestDist = d;
      best = p;
      snapped = p;
    }
  }
  return { sec: Math.max(0, best), snapped };
}

function timeAtClientX(clientX) {
  const rect = $('tlScroll').getBoundingClientRect();
  return Math.max(0, (clientX - rect.left + $('tlScroll').scrollLeft) / pxPerSec);
}

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

$('tlLanes').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const clipEl = e.target.closest('.tl-clip');
  if (!clipEl) {
    if (selectedClipId) {
      selectedClipId = null;
      renderTimeline();
      renderDetailsInspector();
    }
    return;
  }

  const id = clipEl.dataset.id;
  const trackId = clipEl.dataset.track;
  if (lockedTracks.has(trackId)) { toast('Track is locked'); return; }

  if (toolMode === 'blade') {
    const t = timeAtClientX(e.clientX);
    runTool('split_segment', { id, at: Number(t.toFixed(3)) }, 'Split clip');
    return;
  }

  selectedClipId = id;
  if (inspTab === 'project') setInspTab('details');
  else renderDetailsInspector();

  const hit = findClipById(id);
  if (!hit) return;
  const trimDir = e.target.dataset.trim || null;
  const pts = snapPoints(id);

  e.preventDefault();
  clipEl.setPointerCapture(e.pointerId);
  dragState = {
    type: trimDir ? 'trim' : 'move',
    trimDir,
    pointerId: e.pointerId,
    el: clipEl,
    id,
    origTrack: trackId,
    origType: hit.track.type,
    origStart: hit.clip.start,
    origDur: hit.clip.dur,
    startX: e.clientX,
    curStart: hit.clip.start,
    curDur: hit.clip.dur,
    targetTrack: trackId,
    snapPts: pts,
  };
  renderTimeline();
});

window.addEventListener('pointermove', e => {
  if (!dragState) return;
  if (dragState.type === 'scrub') {
    seekTo(timeAtClientX(e.clientX));
    return;
  }

  const dx = e.clientX - dragState.startX;
  const dt = dx / pxPerSec;
  const snapLine = $('tlSnapLine');
  const el = document.querySelector(`.tl-clip[data-id="${dragState.id}"]`);
  if (!el) return;

  if (dragState.type === 'move') {
    const rawStart = Math.max(0, dragState.origStart + dt);
    const sStart = applySnap(rawStart, dragState.snapPts);
    const sEnd = applySnap(rawStart + dragState.origDur, dragState.snapPts);
    let finalStart = rawStart;
    let snapAt = null;
    if (sStart.snapped != null) { finalStart = sStart.sec; snapAt = sStart.snapped; }
    else if (sEnd.snapped != null) { finalStart = Math.max(0, sEnd.sec - dragState.origDur); snapAt = sEnd.snapped; }
    dragState.curStart = Number(finalStart.toFixed(3));
    el.style.left = `${Math.round(finalStart * pxPerSec)}px`;

    // Vertical track hover check
    const lane = document.elementFromPoint(e.clientX, e.clientY)?.closest('.tl-lane');
    document.querySelectorAll('.tl-lane.drop-target').forEach(l => l.classList.remove('drop-target'));
    if (lane && lane.dataset.type === dragState.origType && !lockedTracks.has(lane.dataset.track)) {
      dragState.targetTrack = lane.dataset.track;
      if (dragState.targetTrack !== dragState.origTrack) lane.classList.add('drop-target');
    }

    if (snapAt != null) {
      snapLine.style.left = `${Math.round(snapAt * pxPerSec)}px`;
      snapLine.style.display = 'block';
    } else snapLine.style.display = 'none';
  } else if (dragState.type === 'trim') {
    if (dragState.trimDir === 'right') {
      const rawEnd = Math.max(dragState.origStart + 0.1, dragState.origStart + dragState.origDur + dt);
      const s = applySnap(rawEnd, dragState.snapPts);
      const dur = Math.max(0.1, s.sec - dragState.origStart);
      dragState.curDur = Number(dur.toFixed(3));
      el.style.width = `${Math.max(8, Math.round(dur * pxPerSec))}px`;
      snapLine.style.display = s.snapped != null ? 'block' : 'none';
      if (s.snapped != null) snapLine.style.left = `${Math.round(s.snapped * pxPerSec)}px`;
    } else {
      const origEnd = dragState.origStart + dragState.origDur;
      const rawStart = Math.max(0, Math.min(origEnd - 0.1, dragState.origStart + dt));
      const s = applySnap(rawStart, dragState.snapPts);
      const newStart = Math.min(origEnd - 0.1, s.sec);
      const dur = Math.max(0.1, origEnd - newStart);
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
  document.querySelectorAll('.tl-lane.drop-target').forEach(l => l.classList.remove('drop-target'));

  if (ds.type === 'move') {
    const movedTime = Math.abs(ds.curStart - ds.origStart) > 0.01;
    const movedTrack = ds.targetTrack !== ds.origTrack;
    if (movedTime || movedTrack) {
      const args = { id: ds.id, start: ds.curStart };
      if (movedTrack) args.to_track = ds.targetTrack;
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

// Media bin drag-and-drop onto timeline
$('tlLanes').addEventListener('dragover', e => {
  if (e.dataTransfer?.types?.includes('application/x-aive-media')) {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  }
});
$('tlLanes').addEventListener('drop', async e => {
  const raw = e.dataTransfer?.getData('application/x-aive-media');
  if (!raw) return;
  e.preventDefault();
  try {
    const { path, kind } = JSON.parse(raw);
    const t = timeAtClientX(e.clientX);
    const lane = e.target.closest('.tl-lane');
    const target = lane && lane.dataset.track !== 'video#0' ? 'new_track' : 'main';
    await quickAddMedia(path, kind, Number(t.toFixed(2)), target);
  } catch { }
});

// Timeline Actions
function clipUnderPlayhead() {
  if (!overview) return null;
  if (selectedClipId) {
    const hit = findClipById(selectedClipId);
    if (hit && playheadSec > hit.clip.start + 0.05 && playheadSec < hit.clip.end - 0.05) return hit.clip;
  }
  for (const t of overview.tracks) {
    for (const c of t.segments) {
      if (playheadSec > c.start + 0.05 && playheadSec < c.end - 0.05) return c;
    }
  }
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
  if (playheadSec <= c.start + 0.05 || playheadSec >= c.end - 0.05) {
    toast('Playhead must be inside the selected clip');
    return;
  }
  if (side === 'left') {
    const newDur = Number((c.end - playheadSec).toFixed(3));
    await runTool('edit_segments', { ids: [c.id], start: Number(playheadSec.toFixed(3)), duration: newDur }, 'Trim start');
  } else {
    const newDur = Number((playheadSec - c.start).toFixed(3));
    await runTool('edit_segments', { ids: [c.id], duration: newDur }, 'Trim end');
  }
}

async function deleteSelected() {
  if (!selectedClipId) { toast('Select a clip to delete'); return; }
  const id = selectedClipId;
  selectedClipId = null;
  await runTool('delete_segments', { ids: [id], ripple: rippleDelete }, 'Delete clip');
}

async function closeMainGaps() {
  await runTool('close_gaps', { track: 'video#0' }, 'Close gaps');
}

function addMarkerAtPlayhead() {
  const t = Number(playheadSec.toFixed(2));
  if (!markers.includes(t)) markers.push(t);
  renderTimeline();
  toast(`Added marker at ${fmtTimeShort(t)}`);
}

// Horizontal Splitter Resize
(() => {
  const sp = $('hSplitter');
  const dock = $('timelineDock');
  let startY = 0, startH = 310, active = false;
  sp.addEventListener('pointerdown', e => {
    active = true;
    startY = e.clientY;
    startH = dock.getBoundingClientRect().height;
    sp.setPointerCapture(e.pointerId);
  });
  window.addEventListener('pointermove', e => {
    if (!active) return;
    const nextH = Math.max(185, Math.min(window.innerHeight - 260, startH - (e.clientY - startY)));
    dock.style.height = `${nextH}px`;
    layoutStage();
  });
  window.addEventListener('pointerup', () => { active = false; });
})();

// ---------------- EditPilot AI Chat ----------------
function addChat(cls, html) {
  $('chatWelcome')?.remove();
  const d = document.createElement('div');
  d.className = cls;
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
  } else if (typingEl) {
    typingEl.remove();
    typingEl = null;
  }
}

async function sendChat() {
  const text = $('chatInput').value.trim();
  if (!text || busy) return;
  if (await pollEditor()) {
    pendingSendText = text;
    $('runTitle').textContent = `${edInfo().display} is Open`;
    showModal('mRun');
    return;
  }
  await doSendChat(text);
}

async function runChoice(c) {
  hideModal('mRun');
  const t = pendingSendText;
  pendingSendText = null;
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
  if (!r.ok) {
    setBusy(false);
    if (r.error === 'nokey') showSettings();
    else addChat('chat-msg err', esc(r.error));
  }
}

function quickPrompt(t) {
  openEditPilot();
  $('chatInput').value = t;
  sendChat();
}

async function newChat() {
  await api.new_chat();
  $('chatLog').innerHTML = '<div class="chat-msg sys">Started a new EditPilot conversation.</div>';
}

async function attachFolder() {
  const f = await api.pick_folder();
  if (f) insertChatText(`"${f}"`);
}
async function attachFiles() {
  const fs = await api.pick_files();
  if (fs?.length) insertChatText(fs.map(f => `"${f}"`).join(' '));
}
function insertChatText(s) {
  const i = $('chatInput');
  i.value = (i.value ? i.value.trimEnd() + ' ' : '') + s + ' ';
  i.focus();
  autoGrowChat();
}

function autoGrowChat() {
  const i = $('chatInput');
  i.style.height = 'auto';
  i.style.height = `${Math.min(i.scrollHeight, 110)}px`;
}
$('chatInput').addEventListener('input', autoGrowChat);
$('chatInput').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChat(); }
});

// ---------------- Projects, Settings, Export Modals ----------------
async function switchEditor(editorId) {
  try {
    const list = await api.set_editor(editorId);
    appState.editor = editorId;
    currentProjectPath = null;
    window._projectList = list;
    renderTopStatus();
    applyOverview(null);
    renderBin();
    pollEditor();
  } catch (e) { toast(e.message || String(e)); }
}

async function loadProjects() {
  try {
    window._projectList = await api.projects();
    if (binTab === 'projects') renderBin();
  } catch (e) { toast(e.message || String(e)); }
}

async function openProject(path) {
  if (busy) return;
  try {
    const ov = await api.open_project(path);
    currentProjectPath = path;
    localMediaPool = [];
    selectedClipId = null;
    playheadSec = 0;
    applyOverview(ov);
    loadProjects();
    if (binTab === 'projects') setBinTab('media');
    toast(`Opened "${ov.project}"`);
  } catch (e) { toast(e.message || String(e)); }
}

async function openFile() {
  try {
    const ov = await api.open_file();
    if (ov) {
      localMediaPool = [];
      selectedClipId = null;
      playheadSec = 0;
      applyOverview(ov);
      loadProjects();
      if (binTab === 'projects') setBinTab('media');
    }
  } catch (e) { toast(e.message || String(e)); }
}

function showNewProject() { $('newName').value = ''; showModal('mNew'); $('newName').focus(); }
async function createProject() {
  const n = $('newName').value.trim();
  if (!n) return;
  if (await pollEditor()) { toast(`Close ${edInfo().display} before creating a project`); hideModal('mNew'); return; }
  hideModal('mNew');
  try {
    const ov = await api.new_project(n, $('newRatio').value);
    localMediaPool = [];
    selectedClipId = null;
    playheadSec = 0;
    applyOverview(ov);
    await loadProjects();
    if (binTab === 'projects') setBinTab('media');
    toast(`Created "${ov.project}"`);
  } catch (e) { toast(e.message || String(e)); }
}

async function exportProject() {
  try {
    const r = await api.export_project();
    $('exportMsg').textContent = r.exportOnly
      ? `${r.editor}: import files written (${r.hint}).`
      : r.message;
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
    `<option value="${p.id}">${esc(p.display)}${p.free ? ' - Free' : ' - Paid'}${p.hasKey ? ' ✓' : ''}</option>`).join('');
  pickProvider(appState.provider || 'gemini');
  showModal('mSettings');
}

async function loadModels() {
  try {
    const list = await api.list_models(activeProvider, $('provKey').value.trim());
    const sel = $('provModel');
    const keep = sel.value;
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

// ---------------- Host Push Events & Drag/Drop ----------------
function onEvent(kind, data) {
  if (kind === 'assistant') addChat('chat-msg bot', esc(data));
  else if (kind === 'tool') {
    const d = addChat('tool-log', `⚙ ${esc(data.name)}<pre>${esc(JSON.stringify(data.input, null, 2))}</pre>`);
    d.onclick = () => d.classList.toggle('open');
  }
  else if (kind === 'changed') {
    refreshOverview();
    if (data) addChat('chat-msg sys', esc(data));
  }
  else if (kind === 'error') addChat('chat-msg err', esc(data));
  else if (kind === 'info') addChat('chat-msg sys', esc(data));
  else if (kind === 'provider') {
    if (appState) {
      appState.provider = data.id;
      const p = provInfo(data.id);
      if (p) p.hasKey = true;
      renderTopStatus();
    }
  }
  else if (kind === 'dropped') {
    if (data?.length) {
      if (inspTab === 'editpilot') insertChatText(data.map(f => `"${f}"`).join(' '));
      else probeAndAddToBin(data);
    }
  }
  else if (kind === 'update') {
    updUrl = data.url;
    const b = $('updBtn');
    b.textContent = `⬆ v${data.version} available`;
    b.style.display = '';
  }
  else if (kind === 'project') {
    currentProjectPath = data.path;
    localMediaPool = [];
    selectedClipId = null;
    playheadSec = 0;
    refreshOverview();
    loadProjects();
  }
  else if (kind === 'done') {
    setBusy(false);
    refreshOverview();
  }
}

document.addEventListener('dragover', e => {
  if (e.dataTransfer?.types?.includes('Files')) {
    e.preventDefault();
    document.body.classList.add('dragging');
  }
});
document.addEventListener('dragleave', e => { if (!e.relatedTarget) document.body.classList.remove('dragging'); });
document.addEventListener('drop', e => {
  if (!e.dataTransfer?.files?.length) return;
  e.preventDefault();
  document.body.classList.remove('dragging');
  try { window.chrome.webview.postMessageWithAdditionalObjects({ method: 'dropped' }, e.dataTransfer.files); }
  catch { toast('Drop local files or folders from Windows Explorer'); }
});

['mSettings', 'mNew', 'mRun', 'mExport'].forEach(id =>
  $(id).addEventListener('click', e => { if (e.target.id === id) hideModal(id); }));

// ---------------- Keyboard Shortcuts ----------------
document.addEventListener('keydown', e => {
  const tag = document.activeElement?.tagName;
  const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
  if (e.ctrlKey && e.key.toLowerCase() === 'z' && !e.shiftKey && !typing) { e.preventDefault(); doUndo(); return; }
  if (((e.ctrlKey && e.key.toLowerCase() === 'y') || (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'z')) && !typing) { e.preventDefault(); doRedo(); return; }
  if (e.ctrlKey && e.key.toLowerCase() === 'b') { e.preventDefault(); splitAtPlayhead(); return; }
  if (e.ctrlKey && (e.key === '=' || e.key === '+')) { e.preventDefault(); zoomTimeline(1); return; }
  if (e.ctrlKey && e.key === '-') { e.preventDefault(); zoomTimeline(-1); return; }
  if (typing) return;
  if (e.code === 'Space') { e.preventDefault(); togglePlay(); }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); stepFrames(-1); }
  else if (e.key === 'ArrowRight') { e.preventDefault(); stepFrames(1); }
  else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelected(); }
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
      await refreshOverview();
    } else {
      applyOverview(null);
      setBinTab('projects');
    }
    renderInspector();
    pollEditor();
    setInterval(pollEditor, 5000);
  } catch (e) {
    toast(e.message || String(e));
  }
})();
