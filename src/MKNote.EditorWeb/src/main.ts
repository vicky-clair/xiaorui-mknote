/**
 * MKNote Next 离线双内核编辑器入口：
 * 统一调度富文本编辑器（TipTap 2）、Markdown 编辑器（CodeMirror 6 + Vim）、知识图谱视图、
 * 全局加密背景图层、系统字体与字号联动、多语言切换及与 WinUI 3 宿主的 JSON 消息通信。
 */
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

const richFontSelect = document.getElementById('rich-font-family') as HTMLSelectElement | null;
const richSizeSelect = document.getElementById('rich-font-size') as HTMLSelectElement | null;
const mdFontSelect = document.getElementById('md-font-family') as HTMLSelectElement | null;
const mdSizeSelect = document.getElementById('md-font-size') as HTMLSelectElement | null;

let currentDefaultFont = 'Microsoft YaHei UI';
let currentDefaultSize = 15;

function extractPrimaryFont(cssChain: string | undefined): string {
  if (!cssChain) return 'Microsoft YaHei UI';
  const primary = cssChain.split(',')[0].trim().replace(/^["']|["']$/g, '');
  return primary || 'Microsoft YaHei UI';
}

function buildSafeCssFontChain(primary: string): string {
  const clean = extractPrimaryFont(primary);
  return `"${clean}", "Microsoft YaHei UI", "Segoe UI", "Yu Gothic UI", "Meiryo UI", sans-serif`;
}

function ensureOption(sel: HTMLSelectElement | null, value: string, label?: string): void {
  if (!sel || !value) return;
  if (!Array.from(sel.options).some((o) => o.value === value)) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label ?? value;
    sel.appendChild(opt);
  }
}

function populateSystemFonts(fonts: string[]): void {
  if (!Array.isArray(fonts) || fonts.length === 0) return;
  for (const sel of [richFontSelect, mdFontSelect]) {
    if (!sel) continue;
    sel.innerHTML = '';
    for (const f of fonts) {
      const opt = document.createElement('option');
      opt.value = f;
      opt.textContent = f;
      sel.appendChild(opt);
    }
  }
  syncFontToolbarControls();
}

function syncFontToolbarControls(): void {
  ensureOption(richFontSelect, currentDefaultFont);
  ensureOption(mdFontSelect, currentDefaultFont);
  if (richFontSelect) richFontSelect.value = currentDefaultFont;
  if (mdFontSelect) mdFontSelect.value = currentDefaultFont;

  const sizeVal = String(currentDefaultSize);
  ensureOption(richSizeSelect, sizeVal, `${sizeVal}px`);
  ensureOption(mdSizeSelect, sizeVal, `${sizeVal}px`);
  if (richSizeSelect) richSizeSelect.value = sizeVal;
  if (mdSizeSelect) mdSizeSelect.value = sizeVal;
  richCtrl.setDefaultFontState(currentDefaultFont, currentDefaultSize);
}

function applyEditorFontSettings(primaryFont?: string, fontSize?: number, notifyHost = false): void {
  if (primaryFont) {
    currentDefaultFont = extractPrimaryFont(primaryFont);
    document.documentElement.style.setProperty('--doc-font', buildSafeCssFontChain(currentDefaultFont));
  }
  if (fontSize && Number.isFinite(fontSize) && fontSize >= 10) {
    currentDefaultSize = Math.round(fontSize);
    document.documentElement.style.setProperty('--doc-font-size', `${currentDefaultSize}px`);
  }
  syncFontToolbarControls();

  if (notifyHost) {
    hostBridge.postEvent('editor.fontSettingsChanged', {
      fontFamily: currentDefaultFont,
      fontSize: currentDefaultSize
    });
  }
}

const richCtrl = new RichTextEditorController(vimCallbacks, (fontFamily, fontSize) => {
  applyEditorFontSettings(fontFamily, fontSize, true);
});
const mdCtrl = new MarkdownEditorController(vimCallbacks);
const graphView = new KnowledgeGraphView();

mdFontSelect?.addEventListener('change', () => {
  if (mdFontSelect.value) applyEditorFontSettings(mdFontSelect.value, undefined, true);
});
mdSizeSelect?.addEventListener('change', () => {
  const s = parseInt(mdSizeSelect.value, 10);
  if (Number.isFinite(s) && s >= 10) applyEditorFontSettings(undefined, s, true);
});

let currentFormat: 'rich_html' | 'markdown' | 'graph' | 'empty' = 'markdown';
let currentMdMode: MdViewMode = 'live';
let savedMdModeBeforePrint: MdViewMode | null = null;

const printDocTitle = document.getElementById('print-doc-title');
const printDocMeta = document.getElementById('print-doc-meta');

const richToolbar = document.getElementById('rich-toolbar')!;
const richMoreMenu = document.getElementById('rich-more-menu')!;
const richFindBar = document.getElementById('rich-find-bar')!;
const mdToolbar = document.getElementById('md-toolbar')!;
const mdMoreMenu = document.getElementById('md-more-menu')!;
const mdProps = document.getElementById('md-properties')!;
const richContainer = document.getElementById('rich-container')!;
const mdContainer = document.getElementById('md-container')!;
const graphContainer = document.getElementById('graph-container')!;

/** 应用当前文档背景色、纸张纹理（纯色/横线/方格/点阵/纸感）与浅/深色主题类。 */
function applyBackground(bgHex: string, bgStyle: string, isLightBg: boolean): void {
  const resolved = bgHex || (isLightBg ? '#FFFFFF' : '#121418');
  document.documentElement.style.setProperty('--doc-bg', resolved);
  document.body.style.setProperty('--doc-bg', resolved);
  document.body.style.backgroundColor = resolved;
  document.body.classList.toggle('theme-dark', !isLightBg);
  document.body.classList.toggle('theme-light', isLightBg);

  document.body.classList.remove('bg-solid', 'bg-lined', 'bg-grid', 'bg-dot', 'bg-paper');
  document.body.classList.add(`bg-${(bgStyle || 'solid').toLowerCase()}`);
}

/** 渲染全局加密背景图层，支持填充/拉伸/适应/居中/平铺 5 种显示方式与透明度调节。 */
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

/** 热更新编辑器界面三语文案（文本、Tooltip 提示与输入框占位符）。 */
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

/** 切换当前展示画布（富文本 / Markdown / 知识图谱 / 空白态）并自动收起浮层面板。 */
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

    case 'editor.setSystemFonts':
      if (Array.isArray(p.fonts)) populateSystemFonts(p.fonts);
      applyEditorFontSettings(p.currentFont, p.currentSize, false);
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
      if (p.fontFamily || p.fontSize) {
        applyEditorFontSettings(p.fontFamily, p.fontSize, false);
      }
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

    case 'editor.setPrintMode': {
      // 导出 PDF 前准备：写入打印标题头；若为 Markdown 非阅读态则临时切至完整渲染阅读视图（含 KaTeX / Mermaid）
      const active = !!p.active;
      if (active) {
        if (printDocTitle) printDocTitle.textContent = typeof p.title === 'string' ? p.title : '';
        if (printDocMeta) printDocMeta.textContent = typeof p.meta === 'string' ? p.meta : '';
        if (currentFormat === 'markdown' && currentMdMode !== 'reading') {
          savedMdModeBeforePrint = currentMdMode;
          mdCtrl.setViewMode('reading');
        }
        // 等待两帧重绘及 Mermaid/图片排版就绪后通知宿主调用 PrintToPdfAsync
        requestAnimationFrame(() => requestAnimationFrame(() => {
          hostBridge.postEvent('editor.printReady', {});
        }));
      } else {
        if (currentFormat === 'markdown' && savedMdModeBeforePrint !== null) {
          mdCtrl.setViewMode(savedMdModeBeforePrint);
          savedMdModeBeforePrint = null;
        }
      }
      break;
    }

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

// 监听编辑器内受保护的固定全局快捷键（Esc 退出图谱、Ctrl+E 切换阅读模式、Ctrl+S 强制保存、Ctrl+H 查找替换）
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
