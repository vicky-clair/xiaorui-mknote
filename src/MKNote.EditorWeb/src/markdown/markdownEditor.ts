import { EditorState, RangeSetBuilder, Compartment } from '@codemirror/state';
import { EditorView, Decoration, DecorationSet, ViewPlugin, ViewUpdate, WidgetType, keymap, lineNumbers } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, undo, redo } from '@codemirror/commands';
import { markdown, markdownLanguage } from '@codemirror/lang-markdown';
import { languages } from '@codemirror/language-data';
import { autocompletion, CompletionContext } from '@codemirror/autocomplete';
import { searchKeymap } from '@codemirror/search';
import { vim, Vim, getCM } from '@replit/codemirror-vim';
import { marked } from 'marked';
import katex from 'katex';
import mermaid from 'mermaid';
import { hostBridge } from '../bridge/hostBridge';
import { computeWordCountStats } from '../utils/wordCount';

export type MdViewMode = 'live' | 'split' | 'reading';

export interface VimCustomMapping {
  id?: string;
  mode: string;
  lhs: string;
  rhs: string;
}

export interface MarkdownCustomShortcut {
  id: string;
  action: string;
  shortcut: string;
}

export interface VimConfig {
  enabled: boolean;
  jkEscape: boolean;
  relativeLineNumbers: boolean;
  customMappings?: VimCustomMapping[];
}

export interface VimStatusCallbacks {
  onModeChanged: (mode: string) => void;
  onKeyBufferChanged: (keys: string) => void;
  onStatusFeedback: (msg: string) => void;
}

const RESERVED_SHORTCUTS = new Set([
  'CTRL+C', 'CTRL+V', 'CTRL+X', 'CTRL+Z', 'CTRL+Y', 'CTRL+A', 'CTRL+S', 'CTRL+F',
  'CTRL+K', 'CTRL+E', 'CTRL+J', 'CTRL+P', 'CTRL+R', 'CTRL+SHIFT+A', 'CTRL+SHIFT+V',
  'ALT+F4', 'ESC'
]);

function normalizeShortcutEvent(e: KeyboardEvent): string {
  const parts: string[] = [];
  if (e.ctrlKey || e.metaKey) parts.push('CTRL');
  if (e.altKey) parts.push('ALT');
  if (e.shiftKey) parts.push('SHIFT');
  const k = e.key.length === 1 ? e.key.toUpperCase() : e.key.toUpperCase();
  if (!['CONTROL', 'SHIFT', 'ALT', 'META'].includes(k)) parts.push(k);
  return parts.join('+');
}

function normalizeShortcutString(raw: string): string {
  const parts = (raw || '').split('+').map((p) => p.trim().toUpperCase()).filter(Boolean);
  const hasCtrl = parts.includes('CTRL') || parts.includes('CONTROL') || parts.includes('MOD');
  const hasAlt = parts.includes('ALT');
  const hasShift = parts.includes('SHIFT');
  const main = parts.filter((p) => !['CTRL', 'CONTROL', 'MOD', 'ALT', 'SHIFT'].includes(p)).pop() || '';
  const out: string[] = [];
  if (hasCtrl) out.push('CTRL');
  if (hasAlt) out.push('ALT');
  if (hasShift) out.push('SHIFT');
  if (main) out.push(main);
  return out.join('+');
}

class CheckboxWidget extends WidgetType {
  constructor(private readonly checked: boolean, private readonly pos: number) {
    super();
  }
  toDOM(view: EditorView): HTMLElement {
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = this.checked;
    cb.style.cursor = 'pointer';
    cb.style.marginRight = '6px';
    cb.addEventListener('change', () => {
      const replacement = cb.checked ? '[x]' : '[ ]';
      view.dispatch({ changes: { from: this.pos, to: this.pos + 3, insert: replacement } });
    });
    return cb;
  }
}

class WikiLinkWidget extends WidgetType {
  constructor(private readonly target: string, private readonly display: string) {
    super();
  }
  toDOM(): HTMLElement {
    const a = document.createElement('span');
    a.className = 'cm-md-wikilink';
    a.textContent = this.display;
    a.title = `点击跳转笔记：${this.target}`;
    a.addEventListener('mousedown', (e) => {
      e.preventDefault();
      hostBridge.postEvent('doc.openWikiLink', {
        targetTitle: this.target,
        openInNewTab: e.ctrlKey || e.metaKey
      });
    });
    return a;
  }
}

class InlineMathWidget extends WidgetType {
  constructor(private readonly expr: string) {
    super();
  }
  toDOM(): HTMLElement {
    const span = document.createElement('span');
    try {
      span.innerHTML = katex.renderToString(this.expr, { displayMode: false, throwOnError: false });
    } catch {
      span.textContent = `$${this.expr}$`;
    }
    return span;
  }
}

class InlineImageWidget extends WidgetType {
  constructor(
    private readonly alt: string,
    private readonly url: string,
    private readonly fromPos: number,
    private readonly toPos: number,
    private readonly isPreviewBelow: boolean
  ) {
    super();
  }

  eq(other: InlineImageWidget): boolean {
    return this.url === other.url && this.fromPos === other.fromPos && this.toPos === other.toPos && this.isPreviewBelow === other.isPreviewBelow;
  }

  toDOM(view: EditorView): HTMLElement {
    const wrap = document.createElement('span');
    wrap.className = 'cm-img-inline-wrap';
    wrap.contentEditable = 'false';
    if (this.isPreviewBelow) {
      wrap.style.display = 'block';
      wrap.style.marginTop = '6px';
    }

    const img = document.createElement('img');
    img.src = this.url;
    img.alt = this.alt;
    img.title = '点击图片左半边将光标置于左侧，右半边置于右侧（Backspace 可直接删除）';

    img.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      const rect = img.getBoundingClientRect();
      const targetPos = (e.clientX - rect.left) < rect.width / 2 ? this.fromPos : this.toPos;
      const safePos = Math.max(0, Math.min(targetPos, view.state.doc.length));
      view.dispatch({ selection: { anchor: safePos } });
      view.focus();
    });

    const overlay = document.createElement('span');
    overlay.className = 'cm-img-overlay-btns';

    const editBtn = document.createElement('button');
    editBtn.type = 'button';
    editBtn.textContent = '✎ 光标';
    editBtn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      const safePos = Math.max(0, Math.min(this.toPos, view.state.doc.length));
      view.dispatch({ selection: { anchor: safePos } });
      view.focus();
    });

    const delBtn = document.createElement('button');
    delBtn.type = 'button';
    delBtn.className = 'del';
    delBtn.textContent = '🗑 删除';
    delBtn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      const safeFrom = Math.max(0, Math.min(this.fromPos, view.state.doc.length));
      const safeTo = Math.max(safeFrom, Math.min(this.toPos, view.state.doc.length));
      view.dispatch({
        changes: { from: safeFrom, to: safeTo, insert: '' },
        selection: { anchor: safeFrom }
      });
      view.focus();
    });

    overlay.appendChild(editBtn);
    overlay.appendChild(delBtn);
    wrap.appendChild(img);
    wrap.appendChild(overlay);
    return wrap;
  }

  ignoreEvent(): boolean {
    return true;
  }
}

function createLivePreviewPlugin() {
  return ViewPlugin.fromClass(
    class {
      decorations: DecorationSet;
      constructor(view: EditorView) {
        this.decorations = this.build(view);
      }
      update(update: ViewUpdate) {
        if (update.docChanged || update.selectionSet || update.viewportChanged) {
          this.decorations = this.build(update.view);
        }
      }
      build(view: EditorView): DecorationSet {
        const builder = new RangeSetBuilder<Decoration>();
        const cursorLine = view.state.doc.lineAt(view.state.selection.main.head).number;
        const decos: { from: number; to: number; deco: Decoration }[] = [];

        for (const { from, to } of view.visibleRanges) {
          let pos = from;
          while (pos <= to) {
            const line = view.state.doc.lineAt(pos);
            const text = line.text;
            const isActiveLine = line.number === cursorLine;

            // Headings
            const hm = /^(#{1,3})\s+/.exec(text);
            if (hm) {
              const lvl = hm[1].length;
              decos.push({ from: line.from, to: line.from, deco: Decoration.line({ class: `cm-md-h${lvl}` }) });
              if (!isActiveLine) {
                decos.push({ from: line.from, to: line.from + hm[0].length, deco: Decoration.replace({}) });
              }
            }

            // Callout line styling
            if (/^>\s*\[!\w+\]/.test(text)) {
              decos.push({ from: line.from, to: line.from, deco: Decoration.line({ class: 'cm-md-callout-line' }) });
            }

            // Task checkboxes
            const tm = /^(\s*[-*]\s+)(\[[ xX]\])/.exec(text);
            if (tm && !isActiveLine) {
              const boxStart = line.from + tm[1].length;
              const checked = tm[2].toLowerCase() === '[x]';
              decos.push({
                from: boxStart,
                to: boxStart + 3,
                deco: Decoration.replace({ widget: new CheckboxWidget(checked, boxStart) })
              });
            }

            // Inline images
            const imgRe = /!\[([^\]]*)\]\(([^)\s]+)\)/g;
            let im: RegExpExecArray | null;
            while ((im = imgRe.exec(text)) !== null) {
              const s = line.from + im.index;
              const e = s + im[0].length;
              if (!isActiveLine) {
                decos.push({ from: s, to: e, deco: Decoration.replace({ widget: new InlineImageWidget(im[1], im[2], s, e, false) }) });
              } else {
                decos.push({ from: e, to: e, deco: Decoration.widget({ widget: new InlineImageWidget(im[1], im[2], s, e, true), side: 1 }) });
              }
            }

            // Inline KaTeX math
            if (!isActiveLine) {
              const mathRe = /(?<!\$)\$([^\n$]+?)\$(?!\$)/g;
              let mm: RegExpExecArray | null;
              while ((mm = mathRe.exec(text)) !== null) {
                const s = line.from + mm.index;
                const e = s + mm[0].length;
                decos.push({ from: s, to: e, deco: Decoration.replace({ widget: new InlineMathWidget(mm[1].trim()) }) });
              }
            }

            // Wikilinks
            const wikiRe = /\[\[([^\]\|#]+)(?:#([^\]\|]+))?(?:\|([^\]]+))?\]\]/g;
            let wm: RegExpExecArray | null;
            while ((wm = wikiRe.exec(text)) !== null) {
              const s = line.from + wm.index;
              const e = s + wm[0].length;
              if (!isActiveLine) {
                decos.push({ from: s, to: e, deco: Decoration.replace({ widget: new WikiLinkWidget(wm[1].trim(), (wm[3] || wm[1]).trim()) }) });
              } else {
                decos.push({ from: s, to: e, deco: Decoration.mark({ class: 'cm-md-wikilink' }) });
              }
            }

            // Highlight ==text==
            const hlRe = /==([^=\n]+)==/g;
            let hlm: RegExpExecArray | null;
            while ((hlm = hlRe.exec(text)) !== null) {
              const s = line.from + hlm.index;
              const e = s + hlm[0].length;
              decos.push({ from: s, to: e, deco: Decoration.mark({ class: 'cm-md-highlight' }) });
              if (!isActiveLine) {
                decos.push({ from: s, to: s + 2, deco: Decoration.replace({}) });
                decos.push({ from: e - 2, to: e, deco: Decoration.replace({}) });
              }
            }

            // Bold **text**
            const boldRe = /\*\*([^*\n]+)\*\*/g;
            let bm: RegExpExecArray | null;
            while ((bm = boldRe.exec(text)) !== null) {
              const s = line.from + bm.index;
              const e = s + bm[0].length;
              decos.push({ from: s, to: e, deco: Decoration.mark({ class: 'cm-md-bold' }) });
              if (!isActiveLine) {
                decos.push({ from: s, to: s + 2, deco: Decoration.replace({}) });
                decos.push({ from: e - 2, to: e, deco: Decoration.replace({}) });
              }
            }

            // Inline code
            const codeRe = /`([^`\n]+)`/g;
            let cm: RegExpExecArray | null;
            while ((cm = codeRe.exec(text)) !== null) {
              const s = line.from + cm.index;
              const e = s + cm[0].length;
              decos.push({ from: s, to: e, deco: Decoration.mark({ class: 'cm-md-code' }) });
            }

            // Tags #tag/subtag
            const tagRe = /(?:^|\s)(#[\p{L}\p{N}_][\p{L}\p{N}_/\-]*)/gu;
            let tgm: RegExpExecArray | null;
            while ((tgm = tagRe.exec(text)) !== null) {
              const offset = tgm[0].indexOf('#');
              const s = line.from + tgm.index + offset;
              const e = s + tgm[1].length;
              decos.push({ from: s, to: e, deco: Decoration.mark({ class: 'cm-md-tag' }) });
            }

            pos = line.to + 1;
          }
        }

        decos.sort((a, b) => a.from - b.from || a.to - b.to);
        for (const d of decos) {
          try { builder.add(d.from, d.to, d.deco); } catch { }
        }
        return builder.finish();
      }
    },
    { decorations: (v) => v.decorations }
  );
}

/**
 * 双向链接 Markdown 编辑器控制器（基于 CodeMirror 6）：
 * 支持行内实时预览、左右分屏预览、沉浸阅读模式、全局 Vim 引擎、
 * KaTeX 数学公式、Mermaid 流程图、[[双链]] 自动补全及自定义快捷键。
 */
export class MarkdownEditorController {
  private view: EditorView | null = null;
  private liveCompartment = new Compartment();
  private readOnlyCompartment = new Compartment();
  private vimCompartment = new Compartment();
  private gutterCompartment = new Compartment();

  private noteId = '';
  private mode: MdViewMode = 'live';
  private vimConfig: VimConfig = { enabled: false, jkEscape: true, relativeLineNumbers: false, customMappings: [] };
  private appliedCustomMaps: { lhs: string; mode: string }[] = [];
  private mdShortcutMap = new Map<string, string>();
  private knownTitles: string[] = [];
  private keyBuffer = '';
  private lastCursorLine = -1;
  private debounceTimer: number | null = null;
  private splitRenderTimer: number | null = null;
  private statsTimer: number | null = null;
  private suppressEmit = false;

  private readonly mdContainer = document.getElementById('md-container')!;
  private readonly cmHost = document.getElementById('md-cm-host')!;
  private readonly readingScrollEl = document.getElementById('md-reading-view')!;
  private readonly readingInnerEl = document.getElementById('md-reading-inner')!;
  private readonly toolbarEl = document.getElementById('md-toolbar')!;
  private readonly moreMenuEl = document.getElementById('md-more-menu')!;
  private readonly moreToggleBtn = document.getElementById('btn-md-more')!;
  private readonly propsPanel = document.getElementById('md-properties')!;
  private readonly propsBody = document.getElementById('md-properties-body')!;

  constructor(private readonly vimCallbacks: VimStatusCallbacks) {
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' });
    this.registerVimExCommands();
    this.loadDefaultMdShortcuts();
    this.wireToolbar();
    this.wireReadingInteractions();
    this.wireScrollSync();
    this.wireMarkdownShortcuts();
  }

  private loadDefaultMdShortcuts(): void {
    this.setMarkdownShortcuts([
      { id: 'md_h1', action: 'h1', shortcut: 'Ctrl+1' },
      { id: 'md_h2', action: 'h2', shortcut: 'Ctrl+2' },
      { id: 'md_h3', action: 'h3', shortcut: 'Ctrl+3' },
      { id: 'md_bold', action: 'bold', shortcut: 'Ctrl+B' },
      { id: 'md_italic', action: 'italic', shortcut: 'Ctrl+I' },
      { id: 'md_strike', action: 'strike', shortcut: 'Ctrl+Shift+X' },
      { id: 'md_highlight', action: 'highlight', shortcut: 'Ctrl+Shift+H' },
      { id: 'md_code', action: 'code', shortcut: 'Ctrl+`' },
      { id: 'md_codeblock', action: 'codeblock', shortcut: 'Ctrl+Shift+C' },
      { id: 'md_quote', action: 'quote', shortcut: 'Ctrl+Shift+Q' },
      { id: 'md_bullet', action: 'bullet', shortcut: 'Ctrl+Shift+U' },
      { id: 'md_ordered', action: 'ordered', shortcut: 'Ctrl+Shift+O' },
      { id: 'md_todo', action: 'todo', shortcut: 'Ctrl+L' },
      { id: 'md_wikilink', action: 'wikilink', shortcut: 'Ctrl+Shift+L' },
      { id: 'md_link', action: 'link', shortcut: 'Ctrl+Alt+K' },
      { id: 'md_table', action: 'table', shortcut: 'Ctrl+Alt+T' },
      { id: 'md_callout', action: 'callout', shortcut: 'Ctrl+Alt+C' },
      { id: 'md_mermaid', action: 'mermaid', shortcut: 'Ctrl+Alt+G' },
      { id: 'md_hr', action: 'hr', shortcut: 'Ctrl+Alt+H' }
    ]);
  }

  setMarkdownShortcuts(items: MarkdownCustomShortcut[]): void {
    this.mdShortcutMap.clear();
    for (const item of items || []) {
      if (!item.action || !item.shortcut) continue;
      const norm = normalizeShortcutString(item.shortcut);
      if (!norm || RESERVED_SHORTCUTS.has(norm)) continue;
      this.mdShortcutMap.set(norm, item.action);
    }
  }

  private wireMarkdownShortcuts(): void {
    this.cmHost.addEventListener('keydown', (e) => {
      if (!this.view || (!e.ctrlKey && !e.metaKey && !e.altKey)) return;
      const combo = normalizeShortcutEvent(e);
      if (RESERVED_SHORTCUTS.has(combo)) return; // Never shadow fixed app hotkeys
      const action = this.mdShortcutMap.get(combo);
      if (action) {
        e.preventDefault();
        e.stopPropagation();
        this.execMdAction(action);
      }
    }, true);
  }

  private registerVimExCommands(): void {
    Vim.defineEx('write', 'w', () => {
      hostBridge.postEvent('doc.forceSaveRequested');
      this.vimCallbacks.onStatusFeedback(':w 已写入并创建本地版本快照');
    });
    Vim.defineEx('wq', 'wq', () => {
      hostBridge.postEvent('doc.forceSaveRequested');
      this.vimCallbacks.onStatusFeedback(':wq 已保存本地快照');
    });
    Vim.defineEx('nohlsearch', 'noh', () => {
      this.vimCallbacks.onStatusFeedback(':noh 已清除搜索高亮');
    });
  }

  setKnownNoteTitles(titles: string[]): void {
    this.knownTitles = titles;
  }

  setVimConfig(cfg: VimConfig): void {
    this.vimConfig = { ...cfg };
    this.applyCustomVimKeymaps(cfg);

    if (this.view) {
      this.view.dispatch({
        effects: [
          this.vimCompartment.reconfigure(cfg.enabled ? vim({ status: true }) : []),
          this.gutterCompartment.reconfigure(this.buildGutterExtension())
        ]
      });
      this.attachVimModeListener();
      if (cfg.enabled) this.view.focus();
    }
    if (cfg.enabled) {
      this.keyBuffer = '';
      this.vimCallbacks.onModeChanged('normal');
      this.vimCallbacks.onKeyBufferChanged('');
      const customCount = cfg.customMappings?.length ?? 0;
      this.vimCallbacks.onStatusFeedback(
        customCount > 0
          ? `Vim 模式已激活（含 ${customCount} 条自定义映射）· 支持 :w / :noh / 搜索 / Visual`
          : 'Vim 模式已激活 · 支持 gj/gk 换行移动 / :w / :noh / dd / yy / ciw / Visual'
      );
    }
  }

  private applyCustomVimKeymaps(cfg: VimConfig): void {
    for (const prev of this.appliedCustomMaps) {
      try { Vim.unmap(prev.lhs, prev.mode); } catch { }
    }
    this.appliedCustomMaps = [];
    try { Vim.unmap('jk', 'insert'); } catch { }
    try { Vim.unmap('j', 'normal'); } catch { }
    try { Vim.unmap('k', 'normal'); } catch { }

    if (!cfg.enabled) return;

    const customList = cfg.customMappings || [];
    const customNormalLhs = new Set(customList.filter((m) => m.mode === 'normal').map((m) => m.lhs));

    // Smooth visual-line j/k navigation for wrapped Chinese/English paragraphs
    if (!customNormalLhs.has('j')) {
      Vim.map('j', 'gj', 'normal');
      this.appliedCustomMaps.push({ lhs: 'j', mode: 'normal' });
    }
    if (!customNormalLhs.has('k')) {
      Vim.map('k', 'gk', 'normal');
      this.appliedCustomMaps.push({ lhs: 'k', mode: 'normal' });
    }

    const hasCustomEsc = customList.some((m) => m.id === 'vim_esc_jk' || (m.mode === 'insert' && m.rhs === '<Esc>'));
    if (cfg.jkEscape && !hasCustomEsc) {
      Vim.map('jk', '<Esc>', 'insert');
      this.appliedCustomMaps.push({ lhs: 'jk', mode: 'insert' });
    }

    for (const m of customList) {
      if (!m.lhs || !m.rhs || !m.mode) continue;
      if (m.mode !== 'normal' && m.mode !== 'insert' && m.mode !== 'visual') continue;
      if (RESERVED_SHORTCUTS.has(normalizeShortcutString(m.lhs))) continue;
      try {
        Vim.map(m.lhs, m.rhs, m.mode);
        this.appliedCustomMaps.push({ lhs: m.lhs, mode: m.mode });
      } catch { }
    }
  }

  private buildGutterExtension() {
    if (!this.vimConfig.enabled || !this.vimConfig.relativeLineNumbers) return [];
    return lineNumbers({
      formatNumber: (lineNo, state) => {
        const cur = state.doc.lineAt(state.selection.main.head).number;
        return lineNo === cur ? String(lineNo) : String(Math.abs(lineNo - cur));
      }
    });
  }

  private readonly vimModeHandler = (ev: { mode: string; subMode?: string }) => {
    const label = ev?.subMode ? `${ev.mode} ${ev.subMode}` : (ev?.mode || 'normal');
    this.keyBuffer = '';
    this.vimCallbacks.onModeChanged(label);
    this.vimCallbacks.onKeyBufferChanged('');
  };

  private readonly vimKeypressHandler = (key: string) => {
    if (!key) return;
    if (key === '<Esc>') this.keyBuffer = '';
    else this.keyBuffer = (this.keyBuffer + key).slice(-12);
    this.vimCallbacks.onKeyBufferChanged(this.keyBuffer);
  };

  private readonly vimCommandDoneHandler = () => {
    this.keyBuffer = '';
    this.vimCallbacks.onKeyBufferChanged('');
  };

  private attachVimModeListener(): void {
    if (!this.view || !this.vimConfig.enabled) return;
    const cm = getCM(this.view);
    if (!cm) return;
    cm.off('vim-mode-change', this.vimModeHandler);
    cm.on('vim-mode-change', this.vimModeHandler);
    cm.off('vim-keypress', this.vimKeypressHandler);
    cm.on('vim-keypress', this.vimKeypressHandler);
    cm.off('vim-command-done', this.vimCommandDoneHandler);
    cm.on('vim-command-done', this.vimCommandDoneHandler);
  }

  load(noteId: string, content: string, readOnly: boolean, mode: MdViewMode = 'live'): void {
    this.noteId = noteId;
    this.mode = (mode === 'split' || mode === 'reading') ? mode : 'live';

    const wikiCompletion = (ctx: CompletionContext) => {
      const m = ctx.matchBefore(/\[\[[^\]\n]*/);
      if (!m) return null;
      const q = m.text.slice(2).toLowerCase();
      return {
        from: m.from + 2,
        options: this.knownTitles
          .filter((t) => !q || t.toLowerCase().includes(q))
          .slice(0, 25)
          .map((t) => ({ label: t, apply: `${t}]]`, type: 'text' }))
      };
    };

    const slashCompletion = (ctx: CompletionContext) => {
      const m = ctx.matchBefore(/\/[a-zA-Z\u4e00-\u9fa5]*/);
      if (!m || (m.from > 0 && !/\s/.test(ctx.state.sliceDoc(m.from - 1, m.from)))) return null;
      return {
        from: m.from,
        options: [
          { label: '/h1 一级标题', apply: '# ' },
          { label: '/h2 二级标题', apply: '## ' },
          { label: '/h3 三级标题', apply: '### ' },
          { label: '/code 代码块', apply: '```\n\n```' },
          { label: '/todo 待办复选框', apply: '- [ ] ' },
          { label: '/callout 折叠提示块', apply: '> [!tip] 提示\n> ' },
          { label: '/highlight 荧光高亮', apply: '==高亮文本==' },
          { label: '/link 双向链接', apply: '[[]]' },
          { label: '/table 表格', apply: '| 列1 | 列2 |\n| :--- | :--- |\n|  |  |' },
          { label: '/math 数学公式', apply: '$$\nE = mc^2\n$$' },
          { label: '/mermaid 流程图', apply: '```mermaid\nflowchart LR\n  A[节点A] --> B[节点B]\n```' },
          { label: '/date 插入今天日期', apply: new Date().toISOString().slice(0, 10) }
        ]
      };
    };

    this.applyCustomVimKeymaps(this.vimConfig);

    const state = EditorState.create({
      doc: content || '',
      extensions: [
        this.vimCompartment.of(this.vimConfig.enabled ? vim({ status: true }) : []),
        this.gutterCompartment.of(this.buildGutterExtension()),
        history(),
        keymap.of([...defaultKeymap, ...historyKeymap, ...searchKeymap]),
        markdown({ base: markdownLanguage, codeLanguages: languages }),
        autocompletion({ override: [wikiCompletion, slashCompletion] }),
        EditorView.lineWrapping,
        this.liveCompartment.of(this.mode === 'live' ? createLivePreviewPlugin() : []),
        this.readOnlyCompartment.of(EditorState.readOnly.of(readOnly)),
        EditorView.updateListener.of((u) => {
          if (u.docChanged && !this.suppressEmit) {
            const docStr = u.state.doc.toString();
            this.renderPropertiesPanel(docStr);
            this.scheduleEmit(docStr);
            this.scheduleStatsEmit(u.state);
            if (this.mode === 'split') {
              this.scheduleSplitRender(docStr);
            }
          } else if (u.selectionSet) {
            this.scheduleStatsEmit(u.state);
          }

          if (u.selectionSet && this.vimConfig.enabled && this.vimConfig.relativeLineNumbers) {
            const curLine = u.state.doc.lineAt(u.state.selection.main.head).number;
            if (curLine !== this.lastCursorLine) {
              this.lastCursorLine = curLine;
              queueMicrotask(() => {
                this.view?.dispatch({ effects: this.gutterCompartment.reconfigure(this.buildGutterExtension()) });
              });
            }
          }
        })
      ]
    });

    if (this.view) {
      this.suppressEmit = true;
      this.view.setState(state);
      this.suppressEmit = false;
    } else {
      this.view = new EditorView({ state, parent: this.cmHost });
      this.wirePasteHandler();
    }

    this.attachVimModeListener();
    this.renderPropertiesPanel(content || '');
    this.setViewMode(this.mode);
    this.scheduleStatsEmit(state);
  }

  private scheduleStatsEmit(state: EditorState): void {
    if (this.statsTimer !== null) window.clearTimeout(this.statsTimer);
    this.statsTimer = window.setTimeout(() => {
      const fullText = state.doc.toString();
      const sel = state.selection.main;
      const selText = sel.to > sel.from ? state.sliceDoc(sel.from, sel.to) : '';
      const stats = computeWordCountStats(fullText, selText);
      hostBridge.postEvent('doc.statsChanged', { noteId: this.noteId, ...stats });
    }, 120);
  }

  setViewMode(mode: MdViewMode): void {
    const safeMode: MdViewMode = (mode === 'split' || mode === 'reading') ? mode : 'live';
    this.mode = safeMode;
    this.toolbarEl.querySelectorAll('.pill').forEach((btn) => {
      btn.classList.toggle('active', btn.getAttribute('data-md-mode') === safeMode);
    });

    this.mdContainer.classList.toggle('split-layout', safeMode === 'split');

    if (safeMode === 'reading') {
      this.cmHost.classList.add('hidden');
      this.readingScrollEl.classList.remove('hidden');
      this.readingScrollEl.scrollTop = 0;
      void this.renderReadingView(this.getValue());
    } else if (safeMode === 'split') {
      this.cmHost.classList.remove('hidden');
      this.readingScrollEl.classList.remove('hidden');
      this.view?.dispatch({ effects: this.liveCompartment.reconfigure([]) });
      void this.renderReadingView(this.getValue());
    } else {
      this.readingScrollEl.classList.add('hidden');
      this.cmHost.classList.remove('hidden');
      this.view?.dispatch({
        effects: this.liveCompartment.reconfigure(createLivePreviewPlugin())
      });
    }
  }

  getValue(): string {
    return this.view ? this.view.state.doc.toString() : '';
  }

  insertText(snippet: string): void {
    if (!this.view) return;
    if (this.mode === 'reading') this.setViewMode('live');
    const sel = this.view.state.selection.main;
    this.view.dispatch({
      changes: { from: sel.from, to: sel.to, insert: snippet },
      selection: { anchor: sel.from + snippet.length }
    });
    this.view.focus();
  }

  wrapSelection(prefix: string, suffix: string, placeholder: string): void {
    if (!this.view) return;
    if (this.mode === 'reading') this.setViewMode('live');
    const sel = this.view.state.selection.main;
    const selected = this.view.state.sliceDoc(sel.from, sel.to);
    if (selected.length > 0) {
      if (selected.startsWith(prefix) && selected.endsWith(suffix) && selected.length >= prefix.length + suffix.length) {
        const unwrapped = selected.slice(prefix.length, selected.length - suffix.length);
        this.view.dispatch({
          changes: { from: sel.from, to: sel.to, insert: unwrapped },
          selection: { anchor: sel.from, head: sel.from + unwrapped.length }
        });
      } else {
        const wrapped = `${prefix}${selected}${suffix}`;
        this.view.dispatch({
          changes: { from: sel.from, to: sel.to, insert: wrapped },
          selection: { anchor: sel.from + prefix.length, head: sel.from + prefix.length + selected.length }
        });
      }
    } else {
      const inserted = `${prefix}${placeholder}${suffix}`;
      this.view.dispatch({
        changes: { from: sel.from, to: sel.to, insert: inserted },
        selection: { anchor: sel.from + prefix.length, head: sel.from + prefix.length + placeholder.length }
      });
    }
    this.view.focus();
  }

  setHeadingLevel(level: 1 | 2 | 3): void {
    if (!this.view) return;
    if (this.mode === 'reading') this.setViewMode('live');
    const line = this.view.state.doc.lineAt(this.view.state.selection.main.head);
    const targetPrefix = '#'.repeat(level) + ' ';
    const existing = /^(#{1,6})\s+/.exec(line.text);
    if (existing) {
      if (existing[0] === targetPrefix) {
        this.view.dispatch({ changes: { from: line.from, to: line.from + existing[0].length, insert: '' } });
      } else {
        this.view.dispatch({ changes: { from: line.from, to: line.from + existing[0].length, insert: targetPrefix } });
      }
    } else {
      this.view.dispatch({ changes: { from: line.from, to: line.from, insert: targetPrefix } });
    }
    this.view.focus();
  }

  prependLinePrefix(prefix: string): void {
    if (!this.view) return;
    if (this.mode === 'reading') this.setViewMode('live');
    const line = this.view.state.doc.lineAt(this.view.state.selection.main.head);
    if (line.text.startsWith(prefix)) {
      this.view.dispatch({ changes: { from: line.from, to: line.from + prefix.length, insert: '' } });
    } else {
      this.view.dispatch({ changes: { from: line.from, to: line.from, insert: prefix } });
    }
    this.view.focus();
  }

  execMdAction(action: string): void {
    if (!this.view) return;
    switch (action) {
      case 'h1': this.setHeadingLevel(1); break;
      case 'h2': this.setHeadingLevel(2); break;
      case 'h3': this.setHeadingLevel(3); break;
      case 'bold': this.wrapSelection('**', '**', '粗体'); break;
      case 'italic': this.wrapSelection('*', '*', '斜体'); break;
      case 'strike': this.wrapSelection('~~', '~~', '删除线'); break;
      case 'highlight': this.wrapSelection('==', '==', '高亮'); break;
      case 'code': this.wrapSelection('`', '`', 'code'); break;
      case 'codeblock': this.wrapSelection('\n```text\n', '\n```\n', '// code'); break;
      case 'quote': this.prependLinePrefix('> '); break;
      case 'bullet': this.prependLinePrefix('- '); break;
      case 'ordered': this.prependLinePrefix('1. '); break;
      case 'todo': this.prependLinePrefix('- [ ] '); break;
      case 'wikilink': this.wrapSelection('[[', ']]', '笔记标题'); break;
      case 'link': this.wrapSelection('[', '](https://)', '链接标题'); break;
      case 'table': this.insertText('\n| 列1 | 列2 |\n| :--- | :--- |\n|  |  |\n'); break;
      case 'callout': this.insertText('\n> [!tip] 提示\n> 内容\n'); break;
      case 'math': this.insertText('\n$$\nE = mc^2\n$$\n'); break;
      case 'mermaid': this.insertText('\n```mermaid\nflowchart LR\n  A[开始] --> B[结束]\n```\n'); break;
      case 'hr': this.insertText('\n---\n'); break;
      case 'timestamp': {
        const now = new Date();
        const s = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')} ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
        this.insertText(s);
        break;
      }
      case 'sortLines': this.transformLines((lines) => [...lines].sort((a, b) => a.localeCompare(b, 'zh-CN'))); break;
      case 'dedupLines': this.transformLines((lines) => {
        const seen = new Set<string>();
        return lines.filter((l) => {
          const k = l.trim();
          if (!k) return true;
          if (seen.has(k)) return false;
          seen.add(k);
          return true;
        });
      }); break;
      case 'trimWhitespace': this.transformLines((lines) => lines.map((l) => l.replace(/[ \t]+$/g, ''))); break;
    }
  }

  private transformLines(fn: (lines: string[]) => string[]): void {
    if (!this.view) return;
    if (this.mode === 'reading') this.setViewMode('live');
    const sel = this.view.state.selection.main;
    const hasSel = sel.to > sel.from;
    const from = hasSel ? this.view.state.doc.lineAt(sel.from).from : 0;
    const to = hasSel ? this.view.state.doc.lineAt(sel.to).to : this.view.state.doc.length;
    const text = this.view.state.sliceDoc(from, to);
    const out = fn(text.split('\n')).join('\n');
    this.view.dispatch({ changes: { from, to, insert: out } });
    this.view.focus();
  }

  execMdCommand(cmd: string): void {
    if (!this.view) return;
    if (cmd === 'undo') { undo(this.view); this.view.focus(); }
    else if (cmd === 'redo') { redo(this.view); this.view.focus(); }
  }

  insertMedia(url: string, fileName: string, isImage: boolean): void {
    this.insertText(isImage ? `![${fileName}](${url})\n` : `[📎 ${fileName}](${url})\n`);
  }

  scrollToLine(lineNumber: number): void {
    if (!this.view) return;
    const clamped = Math.max(1, Math.min(lineNumber, this.view.state.doc.lines));
    const line = this.view.state.doc.line(clamped);
    this.view.dispatch({
      selection: { anchor: line.from },
      effects: EditorView.scrollIntoView(line.from, { y: 'center' })
    });
    this.view.focus();
  }

  private scheduleSplitRender(md: string): void {
    if (this.splitRenderTimer !== null) window.clearTimeout(this.splitRenderTimer);
    this.splitRenderTimer = window.setTimeout(() => {
      void this.renderReadingView(md);
    }, 140);
  }

  private wireScrollSync(): void {
    this.cmHost.addEventListener('scroll', () => {
      if (this.mode !== 'split') return;
      const scroller = this.cmHost.querySelector('.cm-scroller') as HTMLElement | null;
      if (!scroller) return;
      const maxSrc = scroller.scrollHeight - scroller.clientHeight;
      if (maxSrc <= 0) return;
      const ratio = scroller.scrollTop / maxSrc;
      const maxDst = this.readingScrollEl.scrollHeight - this.readingScrollEl.clientHeight;
      this.readingScrollEl.scrollTop = ratio * Math.max(0, maxDst);
    }, true);
  }

  private renderPropertiesPanel(md: string): void {
    const m = /^---\r?\n([\s\S]*?)\r?\n---/.exec(md);
    if (!m) {
      this.propsPanel.classList.add('hidden');
      return;
    }
    this.propsPanel.classList.remove('hidden');
    const lines = m[1].split('\n').filter((l) => l.includes(':'));
    this.propsBody.innerHTML = lines
      .map((l) => {
        const idx = l.indexOf(':');
        const k = l.slice(0, idx).trim();
        const v = l.slice(idx + 1).trim().replace(/^["']|["']$/g, '');
        return `<span class="prop-row"><span class="prop-key">${k}:</span><span class="prop-val">${v || '—'}</span></span>`;
      })
      .join('');
  }

  private async renderReadingView(md: string): Promise<void> {
    let body = md.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n?/, '');

    const mathBlocks: string[] = [];
    body = body.replace(/\$\$([\s\S]+?)\$\$/g, (_, expr) => {
      const idx = mathBlocks.length;
      try { mathBlocks.push(katex.renderToString(expr.trim(), { displayMode: true, throwOnError: false })); }
      catch { mathBlocks.push(`<pre>${expr}</pre>`); }
      return `@@MATHBLOCK_${idx}@@`;
    });

    body = body.replace(/(?<!\$)\$([^\n$]+?)\$(?!\$)/g, (_, expr) => {
      try { return katex.renderToString(expr.trim(), { displayMode: false, throwOnError: false }); }
      catch { return expr; }
    });

    const mermaidBlocks: string[] = [];
    body = body.replace(/```mermaid\r?\n([\s\S]*?)```/g, (_, code) => {
      const idx = mermaidBlocks.length;
      mermaidBlocks.push(code.trim());
      return `@@MERMAID_${idx}@@`;
    });

    body = body.replace(/\[\[([^\]\|#]+)(?:#([^\]\|]+))?(?:\|([^\]]+))?\]\]/g, (_, target, _head, alias) => {
      const label = (alias || target).trim();
      return `<a class="wikilink" data-target="${target.trim()}">${label}</a>`;
    });

    body = body.replace(/==([^=\n]+)==/g, '<mark>$1</mark>');

    let html = await marked.parse(body, { gfm: true, breaks: true });

    html = html.replace(/<blockquote>\s*<p>\[!(note|tip|info|warning|danger|quote)\]\s*([^\n<]*)/gi,
      (_, type, title) => `<div class="callout callout-${type.toLowerCase()}"><div class="callout-title">${title || type.toUpperCase()}</div><p>`);

    mathBlocks.forEach((rendered, i) => {
      html = html.replace(`@@MATHBLOCK_${i}@@`, rendered);
    });

    mermaidBlocks.forEach((_, i) => {
      html = html.replace(`@@MERMAID_${i}@@`, `<div class="mermaid-block" data-mermaid-idx="${i}"></div>`);
    });

    this.readingInnerEl.innerHTML = html;

    for (let i = 0; i < mermaidBlocks.length; i++) {
      const holder = this.readingInnerEl.querySelector(`[data-mermaid-idx="${i}"]`);
      if (!holder) continue;
      try {
        const { svg } = await mermaid.render(`mmd_${Date.now()}_${i}`, mermaidBlocks[i]);
        holder.innerHTML = svg;
      } catch {
        holder.innerHTML = `<pre>${mermaidBlocks[i]}</pre>`;
      }
    }
  }

  private wireToolbar(): void {
    const handleClick = (e: Event) => {
      const btn = (e.target as HTMLElement).closest('button');
      if (!btn) return;
      if (btn.id === 'btn-md-more') {
        const isHidden = this.moreMenuEl.classList.toggle('hidden');
        this.moreToggleBtn.classList.toggle('active', !isHidden);
        return;
      }
      const mode = btn.getAttribute('data-md-mode') as MdViewMode | null;
      if (mode) {
        this.setViewMode(mode);
        return;
      }
      const cmd = btn.getAttribute('data-md-cmd');
      if (cmd) {
        this.execMdCommand(cmd);
        return;
      }
      const action = btn.getAttribute('data-md-action');
      if (action) {
        this.execMdAction(action);
      }
    };

    this.toolbarEl.addEventListener('click', handleClick);
    this.moreMenuEl.addEventListener('click', handleClick);

    const togglePropsBtn = document.getElementById('btn-toggle-props');
    togglePropsBtn?.addEventListener('click', () => {
      const hidden = this.propsBody.classList.toggle('hidden');
      togglePropsBtn.textContent = hidden ? '▼' : '▲';
    });
  }

  private wireReadingInteractions(): void {
    this.readingInnerEl.addEventListener('click', (e) => {
      const link = (e.target as HTMLElement).closest('a.wikilink');
      if (link) {
        e.preventDefault();
        const target = link.getAttribute('data-target');
        if (target) {
          hostBridge.postEvent('doc.openWikiLink', {
            targetTitle: target,
            openInNewTab: e.ctrlKey || e.metaKey
          });
        }
      }
    });
  }

  /**
   * Smart Windows Clipboard paste handler for Markdown:
   * - Images -> encrypted directly into SQLCipher DB
   * - Text -> preserves exact Markdown or plain text cleanly (normalizing CRLF -> LF)
   */
  private wirePasteHandler(): void {
    this.cmHost.addEventListener('paste', (e) => {
      const cd = e.clipboardData;
      if (!cd) return;

      for (const item of cd.items) {
        if (item.type.startsWith('image/')) {
          e.preventDefault();
          const file = item.getAsFile();
          if (!file) continue;
          const reader = new FileReader();
          reader.onload = () => {
            const base64 = String(reader.result).split(',')[1] || '';
            hostBridge.postEvent('doc.savePastedAsset', {
              noteId: this.noteId,
              fileName: `paste_${Date.now()}.png`,
              mimeType: file.type,
              base64Data: base64
            });
          };
          reader.readAsDataURL(file);
          return;
        }
      }

      const plain = cd.getData('text/plain');
      if (plain && this.view) {
        e.preventDefault();
        const normalized = plain.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        const sel = this.view.state.selection.main;
        this.view.dispatch({
          changes: { from: sel.from, to: sel.to, insert: normalized },
          selection: { anchor: sel.from + normalized.length }
        });
      }
    }, true);
  }

  private scheduleEmit(markdownText: string): void {
    if (this.debounceTimer !== null) window.clearTimeout(this.debounceTimer);
    this.debounceTimer = window.setTimeout(() => {
      hostBridge.postEvent('doc.contentChanged', {
        noteId: this.noteId,
        content: markdownText,
        plainText: markdownText
      });
    }, 240);
  }
}
