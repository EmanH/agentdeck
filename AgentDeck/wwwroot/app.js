'use strict';

// AgentDeck UI. C# owns projects and the terminal processes (ConPTY); this page owns the layout:
// per-project tabs, each tab a tree of split panes, each pane an xterm.js terminal.

const bridge = window.chrome.webview;
const send = message => bridge.postMessage(message);
const $ = selector => document.querySelector(selector);

const AGENTS = ['claude', 'codex', 'grok', 'shell'];
const AGENT_NAMES = { claude: 'Claude', codex: 'Codex', grok: 'Grok', shell: 'Shell' };

// Windows Terminal's default "Campbell" scheme.
const THEME = {
  background: '#0c0c0c', foreground: '#cccccc', cursor: '#ffffff', cursorAccent: '#0c0c0c',
  selectionBackground: '#ffffff40',
  black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00',
  blue: '#0037da', magenta: '#881798', cyan: '#3a96dd', white: '#cccccc',
  brightBlack: '#767676', brightRed: '#e74856', brightGreen: '#16c60c', brightYellow: '#f9f1a5',
  brightBlue: '#3b78ff', brightMagenta: '#b4009e', brightCyan: '#61d6d6', brightWhite: '#f2f2f2',
};

// Cascadia first; then colour emoji (limited by unicode-range in app.css); then monochrome symbols.
const TERMINAL_FONT = '"Cascadia Mono", "AgentDeck Emoji", "Segoe UI Symbol", Consolas, monospace';

const state = { projects: [], selected: null, sessions: new Map(), palette: [] };
const terms = new Map();       // session id -> pane record
const workspaces = new Map();  // project id -> { tabs: [], activeTab }
let tabSeq = 0;
let fontSize = loadNumber('fontSize', 14);

function loadNumber(key, fallback) {
  try { return Number(localStorage.getItem(key)) || fallback; } catch { return fallback; }
}

function workspace(projectId) {
  if (!workspaces.has(projectId)) workspaces.set(projectId, { tabs: [], activeTab: null });
  return workspaces.get(projectId);
}

const selectedProject = () => state.projects.find(p => p.id === state.selected) || null;
const activeTab = projectId => {
  const ws = workspace(projectId);
  return ws.tabs.find(t => t.id === ws.activeTab) || ws.tabs[0] || null;
};

function agentIcon(agent, className = 'logo') {
  return agent === 'shell' || !agent
    ? `<span class="shell-glyph">&gt;_</span>`
    : `<img class="${className}" src="${agent}.svg" alt="">`;
}

const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// ---------------------------------------------------------------- terminals

function createPane(sid, projectId, agent) {
  const el = document.createElement('div');
  el.className = 'pane';
  const host = document.createElement('div');
  host.className = 'xterm-host';
  el.append(host);

  const term = new Terminal({
    fontFamily: TERMINAL_FONT,
    rescaleOverlappingGlyphs: true, // keep wide emoji glyphs inside their cells
    fontSize,
    lineHeight: 1.1,
    cursorBlink: true,
    cursorStyle: 'bar',
    scrollback: 20000,
    allowProposedApi: true,
    theme: THEME,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.loadAddon(new WebLinksAddon.WebLinksAddon((_, uri) => send({ t: 'openUrl', uri })));

  const pane = { sid, projectId, agent, term, fit, el, host, opened: false, sent: '' };
  terms.set(sid, pane);

  term.onData(data => send({ t: 'input', id: sid, data }));
  term.onTitleChange(title => send({ t: 'title', id: sid, title }));
  term.attachCustomKeyEventHandler(e => onTerminalKey(e, pane));

  el.addEventListener('mousedown', () => setActivePane(pane, false));
  el.addEventListener('contextmenu', e => {
    e.preventDefault();
    if (term.hasSelection()) {
      navigator.clipboard.writeText(term.getSelection());
      term.clearSelection();
    } else {
      navigator.clipboard.readText().then(text => text && term.paste(text)).catch(() => {});
    }
  });
  new ResizeObserver(() => fitPane(pane)).observe(el);
  return pane;
}

function openIfNeeded(pane) {
  if (pane.opened || !pane.el.isConnected || pane.el.offsetWidth === 0) return pane.opened;
  pane.term.open(pane.host);
  try {
    const webgl = new WebglAddon.WebglAddon();
    webgl.onContextLoss(() => webgl.dispose());
    pane.term.loadAddon(webgl);
  } catch { /* DOM renderer fallback */ }
  // Focus can be restored by the browser (e.g. when the window is re-activated) to a pane the user
  // didn't pick; only let it move the active pane within the tab already on screen, never switch tabs.
  pane.term.textarea?.addEventListener('focus', () => {
    const hit = findPane(pane.sid);
    if (hit && !hit.tab.el.hidden && hit.projectId === state.selected) setActivePane(pane, false);
  });
  pane.opened = true;
  return true;
}

function fitPane(pane) {
  if (!openIfNeeded(pane) || pane.el.offsetWidth === 0 || pane.el.offsetHeight === 0) return;
  try { pane.fit.fit(); } catch { return; }
  const size = `${pane.term.cols}x${pane.term.rows}`;
  if (size !== pane.sent) {
    pane.sent = size;
    send({ t: 'resize', id: pane.sid, cols: pane.term.cols, rows: pane.term.rows });
  }
}

function copySelection(pane) {
  if (!pane.term.hasSelection()) return false;
  navigator.clipboard.writeText(pane.term.getSelection());
  pane.term.clearSelection();
  return true;
}

function onTerminalKey(e, pane) {
  if (e.type !== 'keydown') return true;
  const key = e.key.toLowerCase();
  if (e.ctrlKey && !e.altKey && key === 'c' && (e.shiftKey || pane.term.hasSelection())) {
    copySelection(pane);
    e.preventDefault();
    return false;
  }
  // Let the browser deliver a paste event, which xterm turns into (bracketed) input.
  if (e.ctrlKey && !e.altKey && key === 'v') return false;
  if (handleShortcut(e)) return false;
  return true;
}

// ---------------------------------------------------------------- layout tree
// Tab: { id, root, activeSid, el }. Node: { sid } (leaf) or { dir: 'row'|'col', a, b, ratio }.

function leaves(node) {
  return node.sid != null ? [node.sid] : [...leaves(node.a), ...leaves(node.b)];
}

function findPane(sid) {
  for (const [projectId, ws] of workspaces) {
    for (const tab of ws.tabs) {
      const hit = findNode(tab.root, sid, null);
      if (hit) return { projectId, ws, tab, ...hit };
    }
  }
  return null;
}

function findNode(node, sid, parent) {
  if (node.sid === sid) return { node, parent };
  if (node.sid != null) return null;
  return findNode(node.a, sid, node) || findNode(node.b, sid, node);
}

function addTab(projectId, sid) {
  const ws = workspace(projectId);
  const el = document.createElement('div');
  el.className = 'tab-view';
  $('#stage').append(el);
  const tab = { id: ++tabSeq, root: { sid }, activeSid: sid, el };
  ws.tabs.push(tab);
  ws.activeTab = tab.id;
  buildTab(tab);
  return tab;
}

function splitPane(targetSid, newSid, dir) {
  const hit = findPane(targetSid);
  if (!hit) return null;
  const { node, tab } = hit;
  delete node.sid;
  Object.assign(node, { dir, a: { sid: targetSid }, b: { sid: newSid }, ratio: 0.5 });
  tab.activeSid = newSid;
  buildTab(tab);
  return tab;
}

function removePane(sid) {
  const hit = findPane(sid);
  if (!hit) return;
  const { ws, tab, node, parent } = hit;
  if (!parent) {
    ws.tabs.splice(ws.tabs.indexOf(tab), 1);
    tab.el.remove();
    if (ws.activeTab === tab.id) ws.activeTab = ws.tabs.at(-1)?.id ?? null;
  } else {
    const sibling = parent.a === node ? parent.b : parent.a;
    for (const k of Object.keys(parent)) delete parent[k];
    Object.assign(parent, sibling);
    if (tab.activeSid === sid) tab.activeSid = leaves(tab.root)[0];
    buildTab(tab);
  }
}

function buildTab(tab) {
  tab.el.replaceChildren(buildNode(tab.root));
  tab.el.classList.toggle('multi', tab.root.sid == null);
  const only = tab.el.firstChild;
  only.style.flex = '1 1 0';
}

function buildNode(node) {
  if (node.sid != null) return terms.get(node.sid).el;
  const el = document.createElement('div');
  el.className = `split ${node.dir}`;
  const a = buildNode(node.a);
  const b = buildNode(node.b);
  const gutter = document.createElement('div');
  gutter.className = 'gutter';
  const apply = () => {
    a.style.flex = `${node.ratio} 1 0`;
    b.style.flex = `${1 - node.ratio} 1 0`;
  };
  apply();
  gutter.addEventListener('mousedown', down => {
    down.preventDefault();
    const rect = el.getBoundingClientRect();
    const move = e => {
      const r = node.dir === 'row' ? (e.clientX - rect.left) / rect.width : (e.clientY - rect.top) / rect.height;
      node.ratio = Math.min(0.9, Math.max(0.1, r));
      apply();
    };
    const up = () => { document.removeEventListener('mousemove', move); document.removeEventListener('mouseup', up); };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', up);
  });
  el.append(a, gutter, b);
  return el;
}

// ---------------------------------------------------------------- focus & activation

function setActivePane(pane, focus = true) {
  const hit = findPane(pane.sid);
  if (!hit) return;
  const changed = hit.tab.activeSid !== pane.sid || hit.ws.activeTab !== hit.tab.id;
  hit.tab.activeSid = pane.sid;
  hit.ws.activeTab = hit.tab.id;
  send({ t: 'focus', id: pane.sid });
  if (changed) render();
  if (focus) requestAnimationFrame(() => pane.term.focus());
}

function focusActive() {
  const project = selectedProject();
  const tab = project && activeTab(project.id);
  const pane = tab && terms.get(tab.activeSid);
  if (pane) requestAnimationFrame(() => { fitPane(pane); pane.term.focus(); });
}

function activateSession(sid) {
  const pane = terms.get(sid);
  if (!pane) return;
  document.activeElement?.blur(); // drop focus from the old pane so it can't reclaim the tab
  state.selected = pane.projectId;
  const hit = findPane(sid);
  if (!hit) return;
  hit.ws.activeTab = hit.tab.id;
  hit.tab.activeSid = sid;
  render();
  send({ t: 'focus', id: sid });
  focusActive();
}

function moveFocus(direction) {
  const project = selectedProject();
  const tab = project && activeTab(project.id);
  if (!tab) return;
  const current = terms.get(tab.activeSid).el.getBoundingClientRect();
  const cx = current.left + current.width / 2, cy = current.top + current.height / 2;
  let best = null, bestScore = Infinity;
  for (const sid of leaves(tab.root)) {
    if (sid === tab.activeSid) continue;
    const r = terms.get(sid).el.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height / 2;
    const ok = { left: r.right <= current.left + 1, right: r.left >= current.right - 1,
                 up: r.bottom <= current.top + 1, down: r.top >= current.bottom - 1 }[direction];
    if (!ok) continue;
    const score = Math.hypot(x - cx, y - cy);
    if (score < bestScore) { bestScore = score; best = sid; }
  }
  if (best != null) setActivePane(terms.get(best));
}

function cycleTab(step) {
  const project = selectedProject();
  if (!project) return;
  const ws = workspace(project.id);
  if (ws.tabs.length < 2) return;
  const i = ws.tabs.findIndex(t => t.id === ws.activeTab);
  const tab = ws.tabs[(i + step + ws.tabs.length) % ws.tabs.length];
  ws.activeTab = tab.id;
  render();
  send({ t: 'focus', id: tab.activeSid });
  focusActive();
}

// ---------------------------------------------------------------- actions

function launch(agent, placement = 'tab') {
  const project = selectedProject();
  if (!project) return;
  const tab = activeTab(project.id);
  send({ t: 'newSession', projectId: project.id, agent, placement: tab ? placement : 'tab', relativeTo: tab?.activeSid ?? null });
}

function closeActivePane() {
  const project = selectedProject();
  const tab = project && activeTab(project.id);
  if (tab) send({ t: 'close', id: tab.activeSid });
}

function closeTab(tab) {
  for (const sid of leaves(tab.root)) send({ t: 'close', id: sid });
}

function setFontSize(size) {
  fontSize = Math.min(32, Math.max(8, size));
  try { localStorage.setItem('fontSize', fontSize); } catch { }
  for (const pane of terms.values()) {
    pane.term.options.fontSize = fontSize;
    fitPane(pane);
  }
}

function handleShortcut(e) {
  const key = e.key.toLowerCase();
  const code = e.code;
  let handled = true;
  if (e.ctrlKey && e.shiftKey && !e.altKey && key === 't') launch('shell');
  else if (e.ctrlKey && e.shiftKey && !e.altKey && key === 'w') closeActivePane();
  else if (e.ctrlKey && e.shiftKey && code === 'Digit1') launch('claude');
  else if (e.ctrlKey && e.shiftKey && code === 'Digit2') launch('codex');
  else if (e.ctrlKey && e.shiftKey && code === 'Digit3') launch('grok');
  else if (e.altKey && e.shiftKey && (code === 'Equal' || code === 'NumpadAdd')) launch('shell', 'right');
  else if (e.altKey && e.shiftKey && (code === 'Minus' || code === 'NumpadSubtract')) launch('shell', 'down');
  else if (e.altKey && !e.ctrlKey && !e.shiftKey && key.startsWith('arrow')) moveFocus(key.slice(5));
  else if (e.ctrlKey && key === 'tab') cycleTab(e.shiftKey ? -1 : 1);
  else if (e.ctrlKey && !e.shiftKey && !e.altKey && (key === '=' || key === '+')) setFontSize(fontSize + 1);
  else if (e.ctrlKey && !e.shiftKey && !e.altKey && key === '-') setFontSize(fontSize - 1);
  else if (e.ctrlKey && !e.shiftKey && !e.altKey && key === '0') setFontSize(14);
  else handled = false;
  if (handled) e.preventDefault();
  return handled;
}

document.addEventListener('keydown', e => {
  if (!$('#overlay').hidden) {
    if (e.key === 'Escape') closeDialog();
    return;
  }
  if (e.key === 'Escape') hideMenu();
  if (!e.target.closest?.('.xterm')) handleShortcut(e);
});

// ---------------------------------------------------------------- rendering

function render() {
  const project = selectedProject();
  document.documentElement.style.setProperty('--accent', project?.color || '#3b82f6');
  renderSidebar();
  renderTabs(project);

  // Show only the selected project's active tab.
  const shown = project ? activeTab(project.id) : null;
  for (const ws of workspaces.values())
    for (const tab of ws.tabs) tab.el.hidden = tab !== shown;
  for (const tab of shown ? [shown] : []) {
    for (const sid of leaves(tab.root)) terms.get(sid)?.el.classList.toggle('active', sid === tab.activeSid);
  }

  $('#stage').hidden = !shown;
  renderEmpty(project, !shown);
  for (const b of document.querySelectorAll('.launch, #new-tab, #split-right, #split-down')) b.disabled = !project;
  if (shown) requestAnimationFrame(() => leaves(shown.root).forEach(sid => fitPane(terms.get(sid))));
}

function renderSidebar() {
  const nav = $('#projects');
  nav.replaceChildren(...state.projects.map((p, index) => {
    const projectSessions = [...state.sessions.values()].filter(s => s.projectId === p.id);
    const count = projectSessions.length;
    const done = p.id !== state.selected && projectSessions.some(s => s.done);
    const item = document.createElement('div');
    item.className = 'project' + (p.id === state.selected ? ' selected' : '');
    item.style.setProperty('--c', p.color);
    item.draggable = true;
    item.title = p.path;
    item.innerHTML = `${p.icon && iconUrl(p.icon) ? `<span class="tile">${iconImg(p.icon, 18)}</span>` : '<span class="swatch"></span>'}
      <div class="meta"><div class="name">${escapeHtml(p.name)}</div><div class="path">${escapeHtml(p.path)}</div></div>
      ${done ? '<span class="star" title="A terminal finished">✦</span>' : ''}
      ${count ? `<span class="count">${count}</span>` : ''}`;
    item.addEventListener('click', () => selectProject(p.id));
    item.addEventListener('contextmenu', e => { e.preventDefault(); projectMenu(p, e.clientX, e.clientY); });
    item.addEventListener('dragstart', e => e.dataTransfer.setData('text/plain', p.id));
    item.addEventListener('dragover', e => { e.preventDefault(); item.classList.add('drop-before'); });
    item.addEventListener('dragleave', () => item.classList.remove('drop-before'));
    item.addEventListener('drop', e => {
      e.preventDefault();
      item.classList.remove('drop-before');
      const id = e.dataTransfer.getData('text/plain');
      if (id && id !== p.id) {
        const from = state.projects.findIndex(x => x.id === id);
        send({ t: 'moveProject', id, index: from < index ? index - 1 : index });
      }
    });
    return item;
  }));
}

function renderTabs(project) {
  const tabsEl = $('#tabs');
  if (!project) { tabsEl.replaceChildren(); return; }
  const ws = workspace(project.id);
  const current = activeTab(project.id);
  tabsEl.replaceChildren(...ws.tabs.map(tab => {
    const session = state.sessions.get(tab.activeSid);
    const pane = terms.get(tab.activeSid);
    const agent = session?.agent || pane?.agent || 'shell';
    const paneCount = leaves(tab.root).length;
    const done = leaves(tab.root).some(sid => state.sessions.get(sid)?.done);
    const el = document.createElement('div');
    el.className = 'tab' + (tab === current ? ' active' : '');
    el.innerHTML = `${agentIcon(agent)}<span class="label">${escapeHtml(session?.label || AGENT_NAMES[agent])}</span>
      ${done ? '<span class="star" title="Finished">✦</span>' : ''}
      ${paneCount > 1 ? `<span class="panes">${paneCount}</span>` : ''}<button class="x" type="button" title="Close tab">✕</button>`;
    el.addEventListener('mousedown', e => {
      if (e.button === 1) { e.preventDefault(); closeTab(tab); return; }
      if (e.button !== 0 || e.target.closest('.x')) return;
      ws.activeTab = tab.id;
      render();
      send({ t: 'focus', id: tab.activeSid });
      focusActive();
    });
    el.querySelector('.x').addEventListener('click', () => closeTab(tab));
    return el;
  }));
}

function renderEmpty(project, visible) {
  const empty = $('#empty');
  empty.hidden = !visible;
  if (!visible) return;
  if (!project) {
    empty.innerHTML = `<div class="empty-card">
      <h1>Welcome to AgentDeck</h1>
      <div class="where">Add a project folder to start terminals and coding agents in it.</div>
      <button class="primary" id="empty-add" type="button">+ Add project</button></div>`;
    $('#empty-add').addEventListener('click', () => send({ t: 'addProject' }));
    return;
  }
  empty.innerHTML = `<div class="empty-card">
    <h1>${iconImg(project.icon, 34, 'icon hero') || '<span class="dot"></span>'}${escapeHtml(project.name)}</h1>
    <div class="where">${escapeHtml(project.path)}</div>
    <div class="big-launchers">${AGENTS.map(a => `<button class="big-launch" data-agent="${a}" type="button">${agentIcon(a)}${AGENT_NAMES[a]}</button>`).join('')}</div>
    ${projectWorkflows().length ? `<div class="empty-workflows"><div class="label">Workflows</div><div class="chips">${projectWorkflows().map(w =>
      `<button class="chip" type="button" data-run="${w.id}" style="--wc:${w.color}" title="${escapeHtml(w.instructions)}">${iconImg(w.icon, 18) || agentIcon(w.agent)}${escapeHtml(w.name)}${agentIcon(w.agent, 'logo mini')}</button>`).join('')}</div></div>` : ''}
    <div class="hint"><kbd>Ctrl+Shift+1</kbd> Claude · <kbd>2</kbd> Codex · <kbd>3</kbd> Grok · <kbd>Ctrl+Shift+T</kbd> shell</div></div>`;
  for (const b of empty.querySelectorAll('.big-launch')) b.addEventListener('click', () => launch(b.dataset.agent));
  for (const b of empty.querySelectorAll('.chip')) b.addEventListener('click', () => runWorkflow(b.dataset.run));
}

function selectProject(id) {
  if (state.selected === id) return;
  state.selected = id;
  send({ t: 'selectProject', id });
  render();
  focusActive();
}

// ---------------------------------------------------------------- menus & dialogs

function showMenu(x, y, items) {
  const menu = $('#menu');
  menu.replaceChildren(...items.map(item => {
    if (item === '-') return document.createElement('hr');
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = item.label;
    if (item.danger) b.className = 'danger';
    b.addEventListener('click', () => { hideMenu(); item.action(); });
    return b;
  }));
  menu.hidden = false;
  const r = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, innerWidth - r.width - 8)}px`;
  menu.style.top = `${Math.min(y, innerHeight - r.height - 8)}px`;
}

function hideMenu() { $('#menu').hidden = true; }
document.addEventListener('mousedown', e => { if (!e.target.closest('#menu')) hideMenu(); });

function projectMenu(project, x, y) {
  showMenu(x, y, [
    { label: 'Edit name & color…', action: () => showProjectDialog({ mode: 'edit', ...project }) },
    { label: 'Open folder', action: () => send({ t: 'openFolder', id: project.id }) },
    '-',
    { label: 'Remove project', danger: true, action: () => confirmRemove(project) },
  ]);
}

function showProjectDialog({ mode, id, name, path, color, icon }) {
  const dialog = $('#dialog');
  dialog.className = 'wide';
  transcriptsOpen = workflowsOpen = false;
  let picked = color || state.palette[0];
  let pickedIcon = icon || uniqueIcon(state.projects.map(p => p.icon));
  dialog.innerHTML = `
    <h2>${mode === 'add' ? 'Add project' : 'Edit project'}</h2>
    <label for="project-name">Name</label>
    <input type="text" id="project-name" value="${escapeHtml(name || '')}" spellcheck="false">
    <div class="folder">${escapeHtml(path || '')}</div>
    <label>Icon</label>
    <div class="icon-field" id="project-icon"></div>
    <label>Color</label>
    <div class="swatches">${state.palette.map(c => `<button type="button" data-color="${c}" style="background:${c}"></button>`).join('')}</div>
    <div class="actions"><button type="button" id="cancel">Cancel</button><button type="submit" class="primary">${mode === 'add' ? 'Add project' : 'Save'}</button></div>`;
  const pick = c => {
    picked = c;
    dialog.style.setProperty('--pick', c);
    for (const b of dialog.querySelectorAll('.swatches button')) b.classList.toggle('picked', b.dataset.color === c);
  };
  for (const b of dialog.querySelectorAll('.swatches button')) b.addEventListener('click', () => pick(b.dataset.color));
  pick(picked);
  mountIconPicker($('#project-icon'), pickedIcon, i => { pickedIcon = i; });
  dialog.onsubmit = e => {
    e.preventDefault();
    const newName = $('#project-name').value.trim() || name || 'Project';
    send({ t: 'saveProject', id: mode === 'add' ? undefined : id, name: newName, path, color: picked, icon: pickedIcon });
    closeDialog();
  };
  $('#cancel').addEventListener('click', closeDialog);
  $('#overlay').hidden = false;
  const input = $('#project-name');
  input.focus();
  input.select();
}

function confirmRemove(project) {
  const count = [...state.sessions.values()].filter(s => s.projectId === project.id).length;
  const dialog = $('#dialog');
  dialog.className = '';
  transcriptsOpen = workflowsOpen = false;
  dialog.style.removeProperty('--pick');
  dialog.innerHTML = `<h2>Remove ${escapeHtml(project.name)}?</h2>
    <p>The folder stays on disk; it's only removed from AgentDeck.${count ? ` Its ${count} open terminal${count > 1 ? 's' : ''} will be closed.` : ''}</p>
    <div class="actions"><button type="button" id="cancel">Cancel</button><button type="submit" class="danger">Remove</button></div>`;
  dialog.onsubmit = e => {
    e.preventDefault();
    send({ t: 'removeProject', id: project.id });
    closeDialog();
  };
  $('#cancel').addEventListener('click', closeDialog);
  $('#overlay').hidden = false;
  dialog.querySelector('.danger').focus();
}

function closeDialog() {
  $('#overlay').hidden = true;
  $('#dialog').className = '';
  transcriptsOpen = false;
  workflowsOpen = false;
  focusActive();
}

// ---------------------------------------------------------------- recent dictations

let transcripts = [];
let transcriptsOpen = false;

function activePane() {
  const project = selectedProject();
  const tab = project && activeTab(project.id);
  return tab ? terms.get(tab.activeSid) : null;
}

function relativeTime(iso) {
  const seconds = (Date.now() - new Date(iso)) / 1000;
  if (seconds < 60) return 'Just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
}

function showTranscripts() {
  workflowsOpen = false;
  transcriptsOpen = true;
  renderTranscripts();
  $('#overlay').hidden = false;
  $('#dialog').querySelector('.primary').focus();
}

function renderTranscripts() {
  const dialog = $('#dialog');
  dialog.className = 'wide';
  dialog.style.removeProperty('--pick');
  const pane = activePane();
  dialog.innerHTML = `<h2>Recent dictations</h2>
    ${transcripts.length
      ? `<div class="transcripts">${transcripts.map((x, i) => `
          <div class="transcript">
            <div class="when">${relativeTime(x.at)}</div>
            <div class="text">${escapeHtml(x.text)}</div>
            <div class="row">
              <button type="button" data-copy="${i}">Copy</button>
              <button type="button" data-insert="${i}" ${pane ? '' : 'disabled'} title="${pane ? '' : 'Open a terminal first'}">Insert into terminal</button>
            </div>
          </div>`).join('')}</div>`
      : '<p>No dictations yet. Your last 10 transcripts will appear here.</p>'}
    <div class="actions"><button type="submit" class="primary">Done</button></div>`;
  dialog.onsubmit = e => { e.preventDefault(); closeDialog(); };
  for (const b of dialog.querySelectorAll('[data-copy]')) {
    b.addEventListener('click', () => {
      navigator.clipboard.writeText(transcripts[b.dataset.copy].text);
      b.textContent = 'Copied ✓';
      setTimeout(() => { b.textContent = 'Copy'; }, 1200);
    });
  }
  for (const b of dialog.querySelectorAll('[data-insert]')) {
    b.addEventListener('click', () => {
      const text = transcripts[b.dataset.insert].text;
      const target = activePane();
      closeDialog();
      if (target) { target.term.paste(text); target.term.focus(); }
    });
  }
}

// ---------------------------------------------------------------- icons
// Fluent Emoji (flat) library shared with the Stream Deck renderer: { size, categories, icons, keywords }.
// Icons render as <img> data URLs so each SVG's internal ids stay isolated.

let iconLib = { size: 32, categories: {}, icons: {}, keywords: {} };
const iconUrls = new Map();
let iconIndex = null; // [{ name, words }] for search

const iconsLoaded = fetch('icons/fluent.json').then(r => r.json()).then(lib => { iconLib = lib; }).catch(() => {});

function iconUrl(name) {
  if (!iconUrls.has(name)) {
    const body = iconLib.icons[name];
    if (!body) return '';
    const s = iconLib.size;
    iconUrls.set(name, 'data:image/svg+xml;charset=utf-8,' +
      encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${s} ${s}">${body}</svg>`));
  }
  return iconUrls.get(name);
}

function iconImg(name, size = 20, cls = 'icon') {
  const url = name && iconUrl(name);
  return url ? `<img class="${cls}" src="${url}" width="${size}" height="${size}" alt="">` : '';
}

/** First featured icon nobody is using yet, so every project/workflow gets its own. */
function uniqueIcon(taken) {
  const used = new Set(taken);
  const featured = iconLib.categories.Featured || [];
  return featured.find(n => !used.has(n)) || featured[Math.floor(Math.random() * featured.length)] || '';
}

function searchIcons(query) {
  if (!iconIndex) {
    const categoryOf = {};
    for (const [cat, names] of Object.entries(iconLib.categories))
      for (const n of names) categoryOf[n] ??= cat;
    iconIndex = Object.keys(iconLib.icons).map(name => ({
      name,
      nameWords: name.split('-'),
      words: [...name.split('-'), ...(iconLib.keywords[name] || '').split(' '), ...(categoryOf[name] || '').toLowerCase().split(/\W+/)],
    }));
  }
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  const results = [];
  for (const entry of iconIndex) {
    let score = 0;
    const ok = terms.every(t => {
      if (entry.nameWords.some(w => w.startsWith(t))) { score += 2; return true; } // name matches rank first
      if (entry.words.some(w => w.startsWith(t))) { score += 1; return true; }
      return false;
    });
    if (ok) results.push({ name: entry.name, score });
  }
  return results.sort((a, b) => b.score - a.score || a.name.length - b.name.length).map(r => r.name);
}

/** Icon field for dialogs: a button showing the current icon that opens a searchable, categorised picker. */
function mountIconPicker(root, initial, onChange) {
  let current = initial;
  let category = 'Featured';
  root.innerHTML = `
    <button type="button" class="icon-current" title="Change icon">${iconImg(current, 30)}<span>Change icon</span></button>
    <div class="icon-panel" hidden>
      <input type="text" class="icon-search" spellcheck="false"
        placeholder="Search ${Object.keys(iconLib.icons).length.toLocaleString()} icons: try fast, bug, money, launch…">
      <div class="icon-cats">${Object.keys(iconLib.categories).map(c => `<button type="button" data-cat="${escapeHtml(c)}">${escapeHtml(c)}</button>`).join('')}</div>
      <div class="icon-grid"></div>
    </div>`;
  const panel = root.querySelector('.icon-panel');
  const grid = root.querySelector('.icon-grid');
  const search = root.querySelector('.icon-search');

  const show = names => {
    grid.innerHTML = names.slice(0, 280).map(n =>
      `<button type="button" data-icon="${n}" title="${n.replace(/-/g, ' ')}" class="${n === current ? 'picked' : ''}">${iconImg(n, 28)}</button>`).join('')
      || '<div class="icon-none">No icons match.</div>';
  };
  const showCategory = c => {
    category = c;
    for (const b of root.querySelectorAll('.icon-cats button')) b.classList.toggle('picked', b.dataset.cat === c);
    show(iconLib.categories[c] || []);
  };

  root.querySelector('.icon-current').addEventListener('click', () => {
    panel.hidden = !panel.hidden;
    if (!panel.hidden) { showCategory(category); search.focus(); }
  });
  for (const b of root.querySelectorAll('.icon-cats button'))
    b.addEventListener('click', () => { search.value = ''; showCategory(b.dataset.cat); });
  search.addEventListener('input', () => (search.value.trim() ? show(searchIcons(search.value)) : showCategory(category)));
  search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); }); // don't submit the dialog
  grid.addEventListener('click', e => {
    const b = e.target.closest('[data-icon]');
    if (!b) return;
    current = b.dataset.icon;
    root.querySelector('.icon-current').innerHTML = `${iconImg(current, 30)}<span>Change icon</span>`;
    panel.hidden = true;
    onChange(current);
  });
}

// ---------------------------------------------------------------- workflows
// A workflow = name + agent + instructions. Running one opens that agent in the selected project with
// the instructions already submitted (C# passes them as the agent's initial prompt).

let workflows = [];
let workflowsOpen = false;
let agentOptions = {};       // agent -> { models: [{ id, label, efforts, defaultEffort }], efforts, defaultModel }
let onAgentOptions = null;   // editor hook to refresh its pickers when fresh options arrive
const WORKFLOW_AGENTS = ['claude', 'codex', 'grok'];

/** Workflows belong to a project; only the selected project's are offered. */
const projectWorkflows = () => workflows.filter(w => w.projectId === state.selected);

function runWorkflow(id) {
  if (!selectedProject()) return;
  closeDialog();
  send({ t: 'runWorkflow', id });
}

function showWorkflows() {
  transcriptsOpen = false;
  workflowsOpen = true;
  renderWorkflows();
  $('#overlay').hidden = false;
}

function renderWorkflows() {
  const dialog = $('#dialog');
  dialog.className = 'wide';
  dialog.style.removeProperty('--pick');
  const project = selectedProject();
  dialog.innerHTML = `
    <div class="dialog-head"><h2>Workflows${project ? ` <span class="for">${iconImg(project.icon, 18)}${escapeHtml(project.name)}</span>` : ''}</h2>
      <button type="button" id="wf-new" ${project ? '' : 'disabled'}>+ New workflow</button></div>
    ${!project ? '<p>Select a project first: workflows belong to a project and run in its folder.</p>' : projectWorkflows().length
      ? `<div class="workflow-list">${projectWorkflows().map(w => `
          <div class="workflow" style="--wc:${w.color}">
            <span class="tile big">${iconImg(w.icon, 26) || agentIcon(w.agent)}</span>
            <div class="title">${escapeHtml(w.name)}
              <span class="meta">${agentIcon(w.agent, 'logo mini')}${AGENT_NAMES[w.agent]}${w.model ? ` · ${escapeHtml(w.model)}` : ''}${w.effort ? ` · ${escapeHtml(w.effort)} thinking` : ''}</span>
            </div>
            <div class="buttons">
              <button type="button" class="run" data-run="${w.id}" ${project ? '' : 'disabled'}
                title="${project ? `Run in ${escapeHtml(project.name)}` : 'Select a project first'}">Run</button>
              <button type="button" data-edit="${w.id}">Edit</button>
              <button type="button" data-delete="${w.id}">Delete</button>
            </div>
            <div class="preview">${escapeHtml(w.instructions)}</div>
          </div>`).join('')}</div>`
      : '<p>No workflows in this project yet. A workflow is a set of instructions plus the agent to run them in; running one opens that agent in the selected project and starts on your instructions.</p>'}
    <div class="actions"><button type="submit" class="primary">Done</button></div>`;
  dialog.onsubmit = e => { e.preventDefault(); closeDialog(); };
  $('#wf-new').addEventListener('click', () => showWorkflowEditor(null, true));
  for (const b of dialog.querySelectorAll('[data-run]')) b.addEventListener('click', () => runWorkflow(b.dataset.run));
  for (const b of dialog.querySelectorAll('[data-edit]'))
    b.addEventListener('click', () => showWorkflowEditor(workflows.find(w => w.id === b.dataset.edit), true));
  for (const b of dialog.querySelectorAll('[data-delete]')) {
    b.addEventListener('click', () => {
      if (!b.classList.contains('confirm')) {
        b.classList.add('confirm');
        b.textContent = 'Confirm';
        setTimeout(() => { b.classList.remove('confirm'); b.textContent = 'Delete'; }, 3000);
        return;
      }
      send({ t: 'deleteWorkflow', id: b.dataset.delete });
    });
  }
}

function showWorkflowEditor(workflow, backToList = false) {
  workflowsOpen = false;
  const dialog = $('#dialog');
  dialog.className = 'wide';
  let agent = workflow?.agent || 'claude';
  let picked = workflow?.color || '#a855f7';
  let icon = workflow?.icon || uniqueIcon(workflows.map(w => w.icon));
  let effort = workflow?.effort || '';
  dialog.innerHTML = `
    <h2>${workflow ? 'Edit workflow' : 'New workflow'}</h2>
    <div class="wf-top">
      <div class="icon-field" id="wf-icon"></div>
      <div class="wf-name-col">
        <label for="wf-name">Name</label>
        <input type="text" id="wf-name" value="${escapeHtml(workflow?.name || '')}" placeholder="e.g. Review open PRs" spellcheck="false">
      </div>
    </div>
    <label>Agent</label>
    <div class="agent-picker">${WORKFLOW_AGENTS.map(a => `<button type="button" data-agent="${a}">${agentIcon(a)}${AGENT_NAMES[a]}</button>`).join('')}</div>
    <div class="wf-model-row">
      <div>
        <label for="wf-model">Model <button type="button" class="refresh" id="wf-refresh" title="Ask the agents again what they support">↻</button></label>
        <input type="text" id="wf-model" list="wf-models" value="${escapeHtml(workflow?.model || '')}" spellcheck="false" autocomplete="off">
        <datalist id="wf-models"></datalist>
      </div>
      <div>
        <label>Thinking</label>
        <div class="effort-picker" id="wf-effort"></div>
      </div>
    </div>
    <label for="wf-instructions">Instructions</label>
    <textarea id="wf-instructions" spellcheck="true" placeholder="What should the agent do? This is sent as the first message.">${escapeHtml(workflow?.instructions || '')}</textarea>
    <label>Color</label>
    <div class="swatches">${state.palette.map(c => `<button type="button" data-color="${c}" style="background:${c}"></button>`).join('')}</div>
    <div class="actions"><button type="button" id="cancel">Cancel</button><button type="submit" class="primary">${workflow ? 'Save' : 'Create workflow'}</button></div>`;

  mountIconPicker($('#wf-icon'), icon, i => { icon = i; });
  const modelInput = $('#wf-model');

  // Models and thinking levels come from the agents at runtime (see AgentCatalog.cs).
  const refreshOptions = () => {
    const options = agentOptions[agent];
    $('#wf-models').innerHTML = (options?.models || []).map(m => `<option value="${escapeHtml(m.id)}">${escapeHtml(m.label)}</option>`).join('');
    modelInput.placeholder = options?.defaultModel ? `Default (${options.defaultModel})` : `Default (${AGENT_NAMES[agent]}'s own setting)`;
    const model = options?.models.find(m => m.id === modelInput.value.trim());
    const levels = model?.efforts || options?.efforts || [];
    if (effort && levels.length && !levels.includes(effort)) effort = ''; // not supported by this agent/model
    const def = model?.defaultEffort ? `Default (${model.defaultEffort})` : 'Default';
    $('#wf-effort').innerHTML = ['', ...levels].map(l =>
      `<button type="button" data-effort="${l}" class="${l === effort ? 'picked' : ''}">${l ? escapeHtml(l) : def}</button>`).join('')
      + (options ? '' : '<span class="muted">Checking…</span>');
  };
  $('#wf-effort').addEventListener('click', e => {
    const b = e.target.closest('[data-effort]');
    if (!b) return;
    effort = b.dataset.effort;
    refreshOptions();
  });
  modelInput.addEventListener('input', refreshOptions);
  $('#wf-refresh').addEventListener('click', () => { send({ t: 'refreshAgentOptions' }); $('#wf-refresh').classList.add('spin'); });
  onAgentOptions = () => { $('#wf-refresh')?.classList.remove('spin'); if (dialog.contains(modelInput)) refreshOptions(); };
  send({ t: 'refreshAgentOptions' }); // pick up CLI updates since startup

  const pickAgent = a => {
    if (a !== agent) modelInput.value = ''; // models don't carry across agents
    agent = a;
    for (const b of dialog.querySelectorAll('.agent-picker button')) b.classList.toggle('picked', b.dataset.agent === a);
    refreshOptions();
  };
  const pickColor = c => {
    picked = c;
    dialog.style.setProperty('--pick', c);
    for (const b of dialog.querySelectorAll('.swatches button')) b.classList.toggle('picked', b.dataset.color === c);
  };
  for (const b of dialog.querySelectorAll('.agent-picker button')) b.addEventListener('click', () => pickAgent(b.dataset.agent));
  for (const b of dialog.querySelectorAll('.swatches button')) b.addEventListener('click', () => pickColor(b.dataset.color));
  pickAgent(agent);
  pickColor(picked);

  const done = () => (backToList ? showWorkflows() : closeDialog());
  dialog.onsubmit = e => {
    e.preventDefault();
    const name = $('#wf-name').value.trim();
    const instructions = $('#wf-instructions').value.trim();
    if (!name) { $('#wf-name').focus(); return; }
    if (!instructions) { $('#wf-instructions').focus(); return; }
    send({ t: 'saveWorkflow', id: workflow?.id, projectId: workflow?.projectId ?? state.selected, name, agent, instructions, color: picked, icon, model: modelInput.value.trim(), effort });
    done();
  };
  $('#wf-instructions').addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); dialog.requestSubmit(); } // Ctrl+Enter saves
  });
  $('#cancel').addEventListener('click', done);
  $('#overlay').hidden = false;
  $('#wf-name').focus();
}

function workflowMenu(anchor) {
  const r = anchor.getBoundingClientRect();
  showMenu(r.left, r.bottom + 4, [
    ...projectWorkflows().map(w => ({ label: `${w.name}  ·  ${AGENT_NAMES[w.agent]}`, action: () => runWorkflow(w.id) })),
    ...(projectWorkflows().length ? ['-'] : []),
    { label: projectWorkflows().length ? 'Manage workflows…' : 'Create a workflow…', action: projectWorkflows().length ? showWorkflows : () => showWorkflowEditor(null) },
  ]);
}

// ---------------------------------------------------------------- messages from C#

bridge.addEventListener('message', ({ data: m }) => {
  switch (m.t) {
    case 'state': {
      const projectChanged = state.selected !== m.selected;
      state.projects = m.projects;
      state.selected = m.selected;
      state.palette = m.palette;
      state.sessions = new Map(m.sessions.map(s => [s.id, s]));
      render();
      if (projectChanged) focusActive(); // e.g. picked from the Stream Deck: focus that project's terminal
      if (projectChanged && workflowsOpen) renderWorkflows(); // workflows are per project
      break;
    }
    case 'created': {
      const pane = createPane(m.id, m.projectId, m.agent);
      const target = m.relativeTo != null ? findPane(m.relativeTo) : null;
      if (m.placement !== 'tab' && target && target.projectId === m.projectId)
        splitPane(m.relativeTo, m.id, m.placement === 'right' ? 'row' : 'col');
      else
        addTab(m.projectId, m.id);
      if (m.projectId === state.selected) {
        render();
        setActivePane(pane);
      }
      break;
    }
    case 'output':
      terms.get(m.id)?.term.write(m.data);
      break;
    case 'exited': {
      const pane = terms.get(m.id);
      if (!pane) break;
      removePane(m.id);
      terms.delete(m.id);
      pane.term.dispose();
      render();
      focusActive();
      break;
    }
    case 'activate':
      activateSession(m.id);
      break;
    case 'projectDialog':
      showProjectDialog(m);
      break;
    case 'pasteInto': {
      // Dictation aimed at this terminal: paste like Ctrl+V would (bracketed paste when the app wants
      // it), then Enter as separate input a moment later. Works while the window is in the background.
      const pane = terms.get(m.id);
      if (!pane) break;
      pane.term.paste(m.text);
      if (m.submit) setTimeout(() => send({ t: 'input', id: m.id, data: '\r' }), 200);
      break;
    }
    case 'workflows':
      workflows = m.items;
      if (workflowsOpen) renderWorkflows();
      render();
      break;
    case 'agentOptions':
      agentOptions = m.agents || {};
      onAgentOptions?.();
      break;
    case 'workflowEditor':
      showWorkflowEditor(null);
      break;
    case 'transcripts':
      transcripts = m.items;
      if (transcriptsOpen) renderTranscripts();
      break;
  }
});

// ---------------------------------------------------------------- wiring

$('#add-project').addEventListener('click', () => send({ t: 'addProject' }));
$('#recent').addEventListener('click', showTranscripts);
$('#workflows').addEventListener('click', showWorkflows);
$('#workflow-menu').addEventListener('click', e => workflowMenu(e.currentTarget));
$('#new-tab').addEventListener('click', () => launch('shell'));
$('#split-right').addEventListener('click', () => launch('shell', 'right'));
$('#split-down').addEventListener('click', () => launch('shell', 'down'));
for (const b of document.querySelectorAll('.launch')) b.addEventListener('click', () => launch(b.dataset.agent));
window.addEventListener('focus', focusActive);

render();

// The WebGL renderer caches each glyph the first time it's drawn, so make sure the fonts are loaded
// before any terminal exists (otherwise early emoji get cached from the wrong font).
Promise.race([
  Promise.all([
    document.fonts.load(`14px "Cascadia Mono"`, 'Aa'),
    document.fonts.load(`14px "AgentDeck Emoji"`, '⚠✅🚀'),
    iconsLoaded,
  ]),
  new Promise(resolve => setTimeout(resolve, 1500)),
]).finally(() => send({ t: 'ready' }));
iconsLoaded.then(render); // in case the library finished after the startup timeout
