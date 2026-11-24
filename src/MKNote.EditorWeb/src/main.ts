import './styles/theme.css';
import { hostBridge } from './bridge/hostBridge';
import { RichTextEditorController } from './rich/richEditor';
import { MarkdownEditorController, MdViewMode, VimConfig, MarkdownCustomShortcut } from './markdown/markdownEditor';
import { KnowledgeGraphView } from './graph/graphView';

const vimStatusBar = document.getElementById('vim-status-bar')!;
const vimModePill = document.getElementById('vim-mode-pill')!;
const vimKeyBuf = document.getElementById('vim-key-buf')!;
const vimHintText = document.getElementById('vim-hint-text')!;
const globalBgLayer = document.getElementById('global-bg-layer')!;

let vimConfig: VimConfig = { enabled: false, jkEscape: true, relativeLineNumbers: false };

function updateVimStatusBar(mode: string): void {
  vimStatusBar.classList.toggle('hidden', !vimConfig.enabled || currentFormat === 'graph' || currentFormat === 'empty');
  const norm = (mode || 'normal').toLowerCase();
  vimModePill.textContent = norm.toUpperCase();
  vimModePill.className = 'vim-pill ' + (
    norm.includes('insert') ? 'vim-insert'
    : norm.includes('visual') ? 'vim-visual'
    : norm.includes('replace') ? 'vim-replace'
    : 'vim-normal'
  );
  hostBridge.postEvent('vim.modeChanged', { mode: norm });
}

const vimCallbacks = {
  onModeChanged: updateVimStatusBar,
  onKeyBufferChanged: (keys: string) => {
    vimKeyBuf.textContent = keys;
  },
  onStatusFeedback: (msg: string) => {
    vimHintText.textContent = msg;
  }
};

const richCtrl = new RichTextEditorController(vimCallbacks);
const mdCtrl = new MarkdownEditorController(vimCallbacks);
const graphView = new KnowledgeGraphView();

let currentFormat: 'rich_html' | 'markdown' | 'graph' | 'empty' = 'markdown';
let currentMdMode: MdViewMode = 'live';

const richToolbar = document.getElementById('rich-toolbar')!;
const richMoreMenu = document.getElementById('rich-more-menu')!;
const richFindBar = document.getElementById('rich-find-bar')!;
const mdToolbar = document.getElementById('md-toolbar')!;
const mdMoreMenu = document.getElementById('md-more-menu')!;
const mdProps = document.getElementById('md-properties')!;
const richContainer = document.getElementById('rich-container')!;
const mdContainer = document.getElementById('md-container')!;
const graphContainer = document.getElementById('graph-container')!;

function applyBackground(bgHex: string, bgStyle: string, isLightBg: boolean): void {
  document.documentElement.style.setProperty('--doc-bg', bgHex || (isLightBg ? '#FFFFFF' : '#1E1E1E'));
  document.body.classList.toggle('theme-dark', !isLightBg);
  document.body.classList.toggle('theme-light', isLightBg);

  document.body.classList.remove('bg-solid', 'bg-lined', 'bg-grid', 'bg-dot', 'bg-paper');
  document.body.classList.add(`bg-${(bgStyle || 'solid').toLowerCase()}`);
}

function applyGlobalBackgroundImage(url: string | null | undefined, fit: string | undefined, opacity: number | undefined): void {
  if (!url) {
    globalBgLayer.classList.add('hidden');
    globalBgLayer.style.backgroundImage = 'none';
    return;
  }
  globalBgLayer.classList.remove('hidden');
  globalBgLayer.style.backgroundImage = `url("${url}")`;
  globalBgLayer.style.opacity = String(Math.max(0.05, Math.min(1, opacity ?? 0.25)));

  switch ((fit || 'cover').toLowerCase()) {
    case 'stretch':
      globalBgLayer.style.backgroundSize = '100% 100%';
      globalBgLayer.style.backgroundRepeat = 'no-repeat';
      globalBgLayer.style.backgroundPosition = 'center center';
      break;
    case 'contain':
      globalBgLayer.style.backgroundSize = 'contain';
      globalBgLayer.style.backgroundRepeat = 'no-repeat';
      globalBgLayer.style.backgroundPosition = 'center center';
      break;
    case 'center':
      globalBgLayer.style.backgroundSize = 'auto';
      globalBgLayer.style.backgroundRepeat = 'no-repeat';
      globalBgLayer.style.backgroundPosition = 'center center';
      break;
    case 'repeat':
      globalBgLayer.style.backgroundSize = 'auto';
      globalBgLayer.style.backgroundRepeat = 'repeat';
      globalBgLayer.style.backgroundPosition = 'top left';
      break;
    default: // cover
      globalBgLayer.style.backgroundSize = 'cover';
      globalBgLayer.style.backgroundRepeat = 'no-repeat';
      globalBgLayer.style.backgroundPosition = 'center center';
      break;
  }
}

function applyLocale(dict: Record<string, string>): void {
  if (!dict) return;
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    const key = el.getAttribute('data-i18n');
    if (key && dict[key]) el.textContent = dict[key];
  });
  document.querySelectorAll('[data-i18n-title]').forEach((el) => {
    const key = el.getAttribute('data-i18n-title');
    if (key && dict[key]) (el as HTMLElement).title = dict[key];
  });
  document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => {
    const key = el.getAttribute('data-i18n-placeholder');
    if (key && dict[key]) (el as HTMLInputElement).placeholder = dict[key];
  });
}

function applyVimConfig(cfg: VimConfig): void {
  vimConfig = { ...cfg };
  mdCtrl.setVimConfig(vimConfig);
  richCtrl.setVimConfig(vimConfig.enabled, vimConfig.jkEscape, vimConfig.customMappings);
  vimStatusBar.classList.toggle('hidden', !vimConfig.enabled || currentFormat === 'graph' || currentFormat === 'empty');
  if (vimConfig.enabled) updateVimStatusBar('normal');
}

function showSurface(mode: 'rich_html' | 'markdown' | 'graph' | 'empty'): void {
  currentFormat = mode;
  graphView.stop();

  richToolbar.classList.toggle('hidden', mode !== 'rich_html');
  richContainer.classList.toggle('hidden', mode !== 'rich_html');
  if (mode !== 'rich_html') {
    richMoreMenu.classList.add('hidden');
    richFindBar.classList.add('hidden');
    document.getElementById('btn-rich-more')?.classList.remove('active');
  }

  mdToolbar.classList.toggle('hidden', mode !== 'markdown');
  mdContainer.classList.toggle('hidden', mode !== 'markdown');
  if (mode !== 'markdown') {
    mdMoreMenu.classList.add('hidden');
    mdProps.classList.add('hidden');
    document.getElementById('btn-md-more')?.classList.remove('active');
  }

  graphContainer.classList.toggle('hidden', mode !== 'graph');
  vimStatusBar.classList.toggle('hidden', !vimConfig.enabled || mode === 'graph' || mode === 'empty');
}

hostBridge.onMessage((msg) => {
  const p = msg.payload || {};
  switch (msg.method) {
    case 'editor.clear':
      showSurface('empty');
      break;

    case 'editor.setLocale':
      applyLocale(p.dict || p);
      break;

    case 'editor.setBackgroundImage':
      applyGlobalBackgroundImage(p.url, p.fit, p.opacity);
      break;

    case 'editor.setMarkdownShortcuts':
      if (Array.isArray(p.shortcuts)) {
        mdCtrl.setMarkdownShortcuts(p.shortcuts as MarkdownCustomShortcut[]);
      }
      break;

    case 'editor.loadDocument': {
      const fmt = p.format === 'markdown' ? 'markdown' : 'rich_html';
      showSurface(fmt);
      applyBackground(p.bgHex, p.bgStyle, p.isLightBg ?? true);
      if (p.fontFamily) document.documentElement.style.setProperty('--doc-font', p.fontFamily);
      if (p.fontSize) document.documentElement.style.setProperty('--doc-font-size', `${p.fontSize}px`);
      if (p.vimConfig) applyVimConfig(p.vimConfig);
      if (Array.isArray(p.mdShortcuts)) mdCtrl.setMarkdownShortcuts(p.mdShortcuts);
      if (p.globalBgImage !== undefined) {
        applyGlobalBackgroundImage(p.globalBgImage?.url, p.globalBgImage?.fit, p.globalBgImage?.opacity);
      }

      if (fmt === 'markdown') {
        const reqMode = (p.viewMode as string) || 'live';
        currentMdMode = (reqMode === 'split' || reqMode === 'reading') ? reqMode : 'live';
        if (Array.isArray(p.knownTitles)) mdCtrl.setKnownNoteTitles(p.knownTitles);
        mdCtrl.load(p.noteId, p.content || '', !!p.readOnly, currentMdMode);
      } else {
        richCtrl.load(p.noteId, p.content || '', !!p.readOnly);
      }
      break;
    }

    case 'editor.setVimConfig':
      applyVimConfig({
        enabled: !!p.enabled,
        jkEscape: p.jkEscape ?? true,
        relativeLineNumbers: !!p.relativeLineNumbers,
        customMappings: Array.isArray(p.customMappings) ? p.customMappings : []
      });
      break;

    case 'editor.setBackground':
      applyBackground(p.bgHex, p.bgStyle, p.isLightBg ?? true);
      break;

    case 'editor.setViewMode':
      if (currentFormat === 'markdown') {
        currentMdMode = (p.mode === 'split' || p.mode === 'reading') ? p.mode : 'live';
        mdCtrl.setViewMode(currentMdMode);
      }
      break;

    case 'editor.execCommand':
      if (currentFormat === 'rich_html') {
        richCtrl.execCommand(p.command, p.args);
      } else if (currentFormat === 'markdown') {
        if (p.command === 'insertDate') mdCtrl.execMdAction('timestamp');
        else mdCtrl.execMdAction(p.command);
      }
      break;

    case 'editor.insertMedia':
      if (currentFormat === 'markdown') mdCtrl.insertMedia(p.url, p.fileName, !!p.isImage);
      else richCtrl.insertMedia(p.url, p.fileName, !!p.isImage);
      break;

    case 'editor.scrollToHeading':
      if (currentFormat === 'markdown' && p.line) mdCtrl.scrollToLine(p.line);
      break;

    case 'editor.updateKnownTitles':
      if (Array.isArray(p.titles)) mdCtrl.setKnownNoteTitles(p.titles);
      break;

    case 'graph.loadData':
      showSurface('graph');
      applyBackground(p.bgHex, 'solid', p.isLightBg ?? true);
      graphView.load(p.graph);
      break;
  }
});

// Fixed protected shortcuts inside editor
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && currentFormat === 'graph') {
    e.preventDefault();
    graphView.stop();
    hostBridge.postEvent('graph.close');
    return;
  }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'e' && currentFormat === 'markdown') {
    e.preventDefault();
    currentMdMode = currentMdMode === 'reading' ? 'live' : 'reading';
    mdCtrl.setViewMode(currentMdMode);
  } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault();
    hostBridge.postEvent('doc.forceSaveRequested');
  } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'h' && currentFormat === 'rich_html') {
    e.preventDefault();
    richCtrl.execCommand('toggleFindBar');
  }
});

document.addEventListener('click', (e) => {
  const btn = (e.target as HTMLElement).closest('button[data-host-action]');
  if (!btn) return;
  const action = btn.getAttribute('data-host-action');
  if (action) hostBridge.postEvent(action);
});

hostBridge.postEvent('editor.ready');
