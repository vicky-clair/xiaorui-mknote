import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import Underline from '@tiptap/extension-underline';
import TextStyle from '@tiptap/extension-text-style';
import Color from '@tiptap/extension-color';
import Highlight from '@tiptap/extension-highlight';
import Table from '@tiptap/extension-table';
import TableRow from '@tiptap/extension-table-row';
import TableHeader from '@tiptap/extension-table-header';
import TableCell from '@tiptap/extension-table-cell';
import TaskList from '@tiptap/extension-task-list';
import TaskItem from '@tiptap/extension-task-item';
import Image from '@tiptap/extension-image';
import Link from '@tiptap/extension-link';
import Placeholder from '@tiptap/extension-placeholder';
import TextAlign from '@tiptap/extension-text-align';
import { hostBridge } from '../bridge/hostBridge';
import { computeWordCountStats } from '../utils/wordCount';
import type { VimStatusCallbacks } from '../markdown/markdownEditor';

export class RichTextEditorController {
  private editor: Editor | null = null;
  private noteId = '';
  private vimEnabled = false;
  private vimJkEscape = true;
  private vimInsertEscapeSeq = 'jk';
  private vimNormalRemaps = new Map<string, string>();
  private vimMode: 'normal' | 'insert' | 'visual' = 'normal';
  private pendingOp = '';
  private yankBuffer = '';
  private lastSearchQuery = '';
  private lastKey = '';
  private lastKeyTime = 0;
  private debounceTimer: number | null = null;
  private statsTimer: number | null = null;
  private suppressEmit = false;

  private readonly hostEl = document.getElementById('rich-editor')!;
  private readonly toolbarEl = document.getElementById('rich-toolbar')!;
  private readonly moreMenuEl = document.getElementById('rich-more-menu')!;
  private readonly moreToggleBtn = document.getElementById('btn-rich-more')!;
  private readonly findBarEl = document.getElementById('rich-find-bar')!;
  private readonly findInput = document.getElementById('rich-find-input') as HTMLInputElement;
  private readonly replaceInput = document.getElementById('rich-replace-input') as HTMLInputElement;
  private readonly findStatus = document.getElementById('rich-find-status')!;
  private readonly cmdInput = document.getElementById('vim-cmdline-input') as HTMLInputElement;
  private imgActionBar: HTMLDivElement | null = null;
  private activeImgPos: number | null = null;

  constructor(private readonly vimCallbacks: VimStatusCallbacks) {
    this.wireToolbar();
    this.wireFindReplaceBar();
    this.wirePasteHandler();
    this.wireRichVimLayer();
    this.wireVimCmdLine();
    this.createImageActionBar();
  }

  setVimConfig(enabled: boolean, jkEscape: boolean, customMappings?: { id?: string; mode: string; lhs: string; rhs: string }[]): void {
    this.vimEnabled = enabled;
    this.vimJkEscape = jkEscape;
    this.vimInsertEscapeSeq = 'jk';
    this.vimNormalRemaps.clear();

    for (const m of customMappings || []) {
      if (m.mode === 'insert' && m.rhs === '<Esc>' && m.lhs.length === 2) {
        this.vimInsertEscapeSeq = m.lhs;
      } else if (m.mode === 'normal' && m.lhs && m.rhs) {
        this.vimNormalRemaps.set(m.lhs, m.rhs);
      }
    }

    this.pendingOp = '';
    this.vimMode = enabled ? 'normal' : 'insert';
    this.syncRichVimVisual();
  }

  private syncRichVimVisual(): void {
    const pm = this.hostEl.querySelector('.ProseMirror');
    pm?.classList.toggle('vim-normal-mode', this.vimEnabled && this.vimMode === 'normal');
    if (this.vimEnabled) {
      this.vimCallbacks.onModeChanged(this.vimMode);
      this.vimCallbacks.onKeyBufferChanged(this.pendingOp);
    }
  }

  private ensureEditor(): Editor {
    if (this.editor) return this.editor;
    this.editor = new Editor({
      element: this.hostEl,
      extensions: [
        StarterKit,
        Underline,
        TextStyle,
        Color,
        Highlight.configure({ multicolor: true }),
        Table.configure({ resizable: true }),
        TableRow,
        TableHeader,
        TableCell,
        TaskList,
        TaskItem.configure({ nested: true }),
        Image.configure({ inline: true, allowBase64: true }),
        Link.configure({ openOnClick: false }),
        TextAlign.configure({ types: ['heading', 'paragraph'] }),
        Placeholder.configure({ placeholder: '开始输入笔记内容...' })
      ],
      content: '',
      onUpdate: ({ editor }) => {
        if (this.suppressEmit) return;
        this.hideImageActionBar();
        const plain = editor.getText({ blockSeparator: '\n' });
        this.scheduleEmit(editor.getHTML(), plain);
        this.scheduleStatsEmit(editor);
      },
      onSelectionUpdate: ({ editor }) => {
        this.checkSelectedImage(editor);
        this.syncToolbarActiveStates(editor);
        this.scheduleStatsEmit(editor);
      }
    });
    return this.editor;
  }

  private scheduleStatsEmit(editor: Editor): void {
    if (this.statsTimer !== null) window.clearTimeout(this.statsTimer);
    this.statsTimer = window.setTimeout(() => {
      const fullText = editor.getText({ blockSeparator: '\n' });
      const { from, to } = editor.state.selection;
      const selText = to > from ? editor.state.doc.textBetween(from, to, '\n') : '';
      const stats = computeWordCountStats(fullText, selText);
      hostBridge.postEvent('doc.statsChanged', { noteId: this.noteId, ...stats });
    }, 120);
  }

  private syncToolbarActiveStates(editor: Editor): void {
    const toggle = (cmd: string, active: boolean) => {
      this.toolbarEl.querySelector(`button[data-cmd="${cmd}"]`)?.classList.toggle('active', active);
    };
    toggle('bold', editor.isActive('bold'));
    toggle('italic', editor.isActive('italic'));
    toggle('underline', editor.isActive('underline'));
    toggle('strike', editor.isActive('strike'));
    toggle('bulletList', editor.isActive('bulletList'));
    toggle('orderedList', editor.isActive('orderedList'));
    toggle('taskList', editor.isActive('taskList'));
    toggle('blockquote', editor.isActive('blockquote'));
    toggle('codeBlock', editor.isActive('codeBlock'));

    const headingSel = document.getElementById('rich-heading') as HTMLSelectElement | null;
    if (headingSel) {
      if (editor.isActive('heading', { level: 1 })) headingSel.value = '1';
      else if (editor.isActive('heading', { level: 2 })) headingSel.value = '2';
      else if (editor.isActive('heading', { level: 3 })) headingSel.value = '3';
      else headingSel.value = 'p';
    }
  }

  private createImageActionBar(): void {
    const bar = document.createElement('div');
    bar.className = 'rich-img-actions hidden';
    bar.innerHTML = `
      <button type="button" data-img-act="left" data-i18n="ed_img_left">← 光标置左</button>
      <button type="button" data-img-act="right" data-i18n="ed_img_right">光标置右 →</button>
      <button type="button" class="danger" data-img-act="delete" data-i18n="ed_img_delete">🗑 删除图片</button>
    `;
    bar.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      const btn = (e.target as HTMLElement).closest('button');
      if (!btn || !this.editor || this.activeImgPos === null) return;
      const act = btn.getAttribute('data-img-act');
      const pos = this.activeImgPos;
      if (act === 'left') {
        this.editor.chain().focus().setTextSelection(pos).run();
        this.hideImageActionBar();
      } else if (act === 'right') {
        this.editor.chain().focus().setTextSelection(Math.min(pos + 1, this.editor.state.doc.content.size)).run();
        this.hideImageActionBar();
      } else if (act === 'delete') {
        this.editor.chain().focus().deleteRange({ from: pos, to: pos + 1 }).run();
        this.hideImageActionBar();
      }
    });
    document.body.appendChild(bar);
    this.imgActionBar = bar;

    this.hostEl.addEventListener('click', (e) => {
      const img = (e.target as HTMLElement).closest('img');
      if (!img || !this.editor) {
        this.hideImageActionBar();
        return;
      }
      const pos = this.editor.view.posAtDOM(img, 0);
      if (pos >= 0) {
        this.activeImgPos = pos;
        this.showImageActionBarNear(img);
      }
    });
  }

  private checkSelectedImage(editor: Editor): void {
    const sel: any = editor.state.selection;
    if (sel?.node?.type?.name === 'image') {
      this.activeImgPos = sel.from;
      const dom = editor.view.nodeDOM(sel.from) as HTMLElement | null;
      if (dom) this.showImageActionBarNear(dom);
      return;
    }
    this.hideImageActionBar();
  }

  private showImageActionBarNear(el: HTMLElement): void {
    if (!this.imgActionBar) return;
    const rect = el.getBoundingClientRect();
    this.imgActionBar.style.left = `${Math.max(12, rect.left)}px`;
    this.imgActionBar.style.top = `${Math.max(48, rect.top - 36)}px`;
    this.imgActionBar.classList.remove('hidden');
  }

  private hideImageActionBar(): void {
    this.activeImgPos = null;
    this.imgActionBar?.classList.add('hidden');
  }

  private wireVimCmdLine(): void {
    this.cmdInput?.addEventListener('keydown', (e) => {
      if (!this.vimEnabled || this.hostEl.closest('#rich-container')?.classList.contains('hidden')) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        this.closeCmdLine();
        return;
      }
      if (e.key === 'Enter') {
        e.preventDefault();
        const raw = this.cmdInput.value.trim();
        this.closeCmdLine();
        this.executeRichExOrSearch(raw);
      }
    });
  }

  private openCmdLine(prefix: ':' | '/'): void {
    this.cmdInput.value = prefix;
    this.cmdInput.classList.remove('hidden');
    this.cmdInput.focus();
    this.cmdInput.setSelectionRange(1, 1);
  }

  private closeCmdLine(): void {
    this.cmdInput.value = '';
    this.cmdInput.classList.add('hidden');
    this.editor?.commands.focus();
  }

  private executeRichExOrSearch(cmd: string): void {
    if (!cmd) return;
    if (cmd.startsWith(':')) {
      const ex = cmd.slice(1).trim().toLowerCase();
      if (ex === 'w' || ex === 'write' || ex === 'wq') {
        hostBridge.postEvent('doc.forceSaveRequested');
        this.vimCallbacks.onStatusFeedback(`:${ex} 已保存本地版本快照`);
      } else if (ex === 'noh' || ex === 'nohlsearch') {
        this.lastSearchQuery = '';
        window.getSelection()?.collapseToEnd();
        this.vimCallbacks.onStatusFeedback(':noh 已清除搜索高亮');
      } else {
        this.vimCallbacks.onStatusFeedback(`未知命令 ${cmd}（支持 :w / :noh / /搜索）`);
      }
    } else if (cmd.startsWith('/')) {
      const q = cmd.slice(1);
      if (q) {
        this.lastSearchQuery = q;
        const found = (window as any).find?.(q, false, false, true);
        this.vimCallbacks.onStatusFeedback(found ? `/${q} 已定位匹配项 (按 n 下一个)` : `/${q} 未找到匹配项`);
      }
    }
  }

  private wireRichVimLayer(): void {
    this.hostEl.addEventListener('keydown', (e) => {
      if (!this.vimEnabled || !this.editor) return;

      if (this.vimMode === 'insert') {
        if (e.isComposing) return;
        if (e.key === 'Escape') {
          e.preventDefault();
          this.vimMode = 'normal';
          this.pendingOp = '';
          this.syncRichVimVisual();
          return;
        }
        const esc1 = this.vimInsertEscapeSeq[0];
        const esc2 = this.vimInsertEscapeSeq[1];
        if (this.vimJkEscape && e.key === esc2 && this.lastKey === esc1 && Date.now() - this.lastKeyTime < 260) {
          e.preventDefault();
          const { from } = this.editor.state.selection;
          if (from > 1) this.editor.chain().focus().deleteRange({ from: from - 1, to: from }).run();
          this.vimMode = 'normal';
          this.pendingOp = '';
          this.lastKey = '';
          this.syncRichVimVisual();
          return;
        }
        this.lastKey = e.key;
        this.lastKeyTime = Date.now();
        return;
      }

      // NORMAL / VISUAL mode in Rich Text Editor
      if (e.ctrlKey || e.metaKey || e.altKey) {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'r') {
          e.preventDefault();
          this.editor.chain().focus().redo().run();
          this.vimCallbacks.onStatusFeedback('Ctrl+R 重做');
        }
        return;
      }

      if (e.key === 'Delete' || e.key === 'Backspace') return;

      const sel = window.getSelection();
      const action = this.vimMode === 'visual' ? 'extend' : 'move';
      const moveSel = (dir: 'forward' | 'backward', gran: 'character' | 'line' | 'word' | 'lineboundary') => {
        (sel as any)?.modify?.(action, dir, gran);
      };

      if (e.key === 'Escape') {
        e.preventDefault();
        this.vimMode = 'normal';
        this.pendingOp = '';
        this.syncRichVimVisual();
        return;
      }

      // Handle multi-key sequences: dd, dw, cc, ciw, yy, gg, f{char}
      if (this.pendingOp) {
        e.preventDefault();
        if (this.pendingOp === 'f') {
          const targetChar = e.key;
          this.pendingOp = '';
          this.vimCallbacks.onKeyBufferChanged('');
          const { $from } = this.editor.state.selection;
          const textAfter = $from.parent.textContent.slice($from.parentOffset + 1);
          const idx = textAfter.indexOf(targetChar);
          if (idx >= 0) {
            const targetPos = $from.pos + 1 + idx;
            this.editor.chain().focus().setTextSelection(targetPos).run();
          }
          return;
        }

        if (this.pendingOp === 'ci' && e.key === 'w') {
          this.pendingOp = '';
          this.vimCallbacks.onKeyBufferChanged('');
          const { $from } = this.editor.state.selection;
          const lineText = $from.parent.textContent;
          let s = $from.parentOffset;
          let t = $from.parentOffset;
          while (s > 0 && /[\w\u4e00-\u9fff]/.test(lineText[s - 1])) s--;
          while (t < lineText.length && /[\w\u4e00-\u9fff]/.test(lineText[t])) t++;
          const blockStart = $from.start($from.depth);
          if (t > s) {
            this.editor.chain().focus().deleteRange({ from: blockStart + s, to: blockStart + t }).run();
          }
          this.vimMode = 'insert';
          this.syncRichVimVisual();
          this.vimCallbacks.onStatusFeedback('ciw 已修改当前词');
          return;
        }

        const combo = this.pendingOp + e.key;
        if (combo === 'ci') {
          this.pendingOp = 'ci';
          this.vimCallbacks.onKeyBufferChanged('ci');
          return;
        }

        this.pendingOp = '';
        this.vimCallbacks.onKeyBufferChanged('');

        if (combo === 'dd') {
          const { $from } = this.editor.state.selection;
          const depth = Math.max(1, $from.depth);
          if ($from.depth >= 1) {
            const start = $from.before(depth);
            const end = $from.after(depth);
            this.yankBuffer = $from.parent.textContent;
            if (this.editor.state.doc.childCount <= 1 && depth === 1) {
              this.editor.chain().focus().deleteRange({ from: start + 1, to: Math.max(start + 1, end - 1) }).run();
            } else {
              this.editor.chain().focus().deleteRange({ from: start, to: end }).run();
            }
            this.vimCallbacks.onStatusFeedback('dd 已删除当前行');
          }
          return;
        }
        if (combo === 'dw') {
          const { from } = this.editor.state.selection;
          moveSel('forward', 'word');
          const newTo = this.editor.state.selection.to;
          if (newTo > from) {
            this.editor.chain().focus().deleteRange({ from, to: newTo }).run();
            this.vimCallbacks.onStatusFeedback('dw 已删除单词');
          }
          return;
        }
        if (combo === 'cc') {
          const { $from } = this.editor.state.selection;
          if ($from.depth >= 1) {
            const start = $from.start($from.depth);
            const end = $from.end($from.depth);
            this.yankBuffer = $from.parent.textContent;
            this.editor.chain().focus().deleteRange({ from: start, to: end }).run();
            this.vimMode = 'insert';
            this.syncRichVimVisual();
            this.vimCallbacks.onStatusFeedback('cc 已清空当前行并进入输入模式');
          }
          return;
        }
        if (combo === 'yy') {
          const { $from } = this.editor.state.selection;
          this.yankBuffer = $from.parent.textContent;
          this.vimCallbacks.onStatusFeedback('yy 已复制当前行');
          return;
        }
        if (combo === 'gg') {
          this.editor.commands.focus('start');
          this.vimCallbacks.onStatusFeedback('gg 跳转至文档顶部');
          return;
        }
        return;
      }

      const mappedKey = this.vimNormalRemaps.has(e.key) ? this.vimNormalRemaps.get(e.key)! : e.key;

      switch (mappedKey) {
        case '^': e.preventDefault(); moveSel('backward', 'lineboundary'); break;
        case ':': e.preventDefault(); this.openCmdLine(':'); break;
        case '/': e.preventDefault(); this.openCmdLine('/'); break;
        case 'n':
          e.preventDefault();
          if (this.lastSearchQuery) (window as any).find?.(this.lastSearchQuery, false, false, true);
          break;
        case 'N':
          e.preventDefault();
          if (this.lastSearchQuery) (window as any).find?.(this.lastSearchQuery, false, true, true);
          break;
        case 'v':
        case 'V':
          e.preventDefault();
          this.vimMode = this.vimMode === 'visual' ? 'normal' : 'visual';
          this.syncRichVimVisual();
          break;
        case 'i': e.preventDefault(); this.vimMode = 'insert'; this.syncRichVimVisual(); break;
        case 'I': e.preventDefault(); moveSel('backward', 'lineboundary'); this.vimMode = 'insert'; this.syncRichVimVisual(); break;
        case 'a': e.preventDefault(); moveSel('forward', 'character'); this.vimMode = 'insert'; this.syncRichVimVisual(); break;
        case 'A': e.preventDefault(); moveSel('forward', 'lineboundary'); this.vimMode = 'insert'; this.syncRichVimVisual(); break;
        case 'o':
          e.preventDefault();
          moveSel('forward', 'lineboundary');
          this.editor.chain().focus().splitBlock().run();
          this.vimMode = 'insert';
          this.syncRichVimVisual();
          break;
        case 'O':
          e.preventDefault();
          moveSel('backward', 'lineboundary');
          this.editor.chain().focus().splitBlock().run();
          moveSel('backward', 'line');
          this.vimMode = 'insert';
          this.syncRichVimVisual();
          break;
        case 'h': e.preventDefault(); moveSel('backward', 'character'); break;
        case 'l': e.preventDefault(); moveSel('forward', 'character'); break;
        case 'j': e.preventDefault(); moveSel('forward', 'line'); break;
        case 'k': e.preventDefault(); moveSel('backward', 'line'); break;
        case 'w':
        case 'e': e.preventDefault(); moveSel('forward', 'word'); break;
        case 'b': e.preventDefault(); moveSel('backward', 'word'); break;
        case '0': e.preventDefault(); moveSel('backward', 'lineboundary'); break;
        case '$': e.preventDefault(); moveSel('forward', 'lineboundary'); break;
        case 'D': {
          e.preventDefault();
          const { $from } = this.editor.state.selection;
          const end = $from.end($from.depth);
          if (end > $from.pos) {
            this.yankBuffer = this.editor.state.doc.textBetween($from.pos, end);
            this.editor.chain().focus().deleteRange({ from: $from.pos, to: end }).run();
            this.vimCallbacks.onStatusFeedback('D 已删至行尾');
          }
          break;
        }
        case 'C': {
          e.preventDefault();
          const { $from } = this.editor.state.selection;
          const end = $from.end($from.depth);
          if (end > $from.pos) {
            this.yankBuffer = this.editor.state.doc.textBetween($from.pos, end);
            this.editor.chain().focus().deleteRange({ from: $from.pos, to: end }).run();
          }
          this.vimMode = 'insert';
          this.syncRichVimVisual();
          this.vimCallbacks.onStatusFeedback('C 已删至行尾并进入输入模式');
          break;
        }
        case 'S': {
          e.preventDefault();
          const { $from } = this.editor.state.selection;
          const start = $from.start($from.depth);
          const end = $from.end($from.depth);
          this.yankBuffer = $from.parent.textContent;
          this.editor.chain().focus().deleteRange({ from: start, to: end }).run();
          this.vimMode = 'insert';
          this.syncRichVimVisual();
          break;
        }
        case 'd':
        case 'c':
        case 'y':
        case 'g':
        case 'f':
          e.preventDefault();
          if (this.vimMode === 'visual' && (mappedKey === 'd' || mappedKey === 'c')) {
            this.editor.chain().focus().deleteSelection().run();
            this.vimMode = mappedKey === 'c' ? 'insert' : 'normal';
            this.syncRichVimVisual();
          } else {
            this.pendingOp = mappedKey;
            this.vimCallbacks.onKeyBufferChanged(this.pendingOp);
          }
          break;
        case 'X': {
          e.preventDefault();
          const { from } = this.editor.state.selection;
          if (from > 1) this.editor.chain().focus().deleteRange({ from: from - 1, to: from }).run();
          break;
        }
        case 'p':
        case 'P':
          e.preventDefault();
          if (this.yankBuffer) {
            this.editor.chain().focus().insertContent(`<p>${this.escapeHtml(this.yankBuffer)}</p>`).run();
            this.vimCallbacks.onStatusFeedback(`${mappedKey} 已粘贴`);
          }
          break;
        case 'u':
          e.preventDefault();
          this.editor.chain().focus().undo().run();
          this.vimCallbacks.onStatusFeedback('u 撤销');
          break;
        case 'x': {
          e.preventDefault();
          const { from, to } = this.editor.state.selection;
          if (to > from) {
            this.editor.chain().focus().deleteRange({ from, to }).run();
          } else {
            this.editor.chain().focus().deleteRange({ from, to: Math.min(from + 1, this.editor.state.doc.content.size) }).run();
          }
          break;
        }
        case 'G':
          e.preventDefault();
          this.editor.commands.focus('end');
          this.vimCallbacks.onStatusFeedback('G 跳转至文档底部');
          break;
        default:
          if (e.key.length === 1) e.preventDefault();
          break;
      }
    }, true);
  }

  load(noteId: string, html: string, readOnly: boolean): void {
    this.noteId = noteId;
    this.hideImageActionBar();
    this.findBarEl.classList.add('hidden');

    const ed = this.ensureEditor();
    this.suppressEmit = true;
    ed.setEditable(!readOnly);
    ed.commands.setContent(this.normalizeTodoHtml(html || ''), false);
    if (ed.state.doc.content.size > 1) {
      ed.commands.setTextSelection(1);
    }
    this.suppressEmit = false;
    this.syncRichVimVisual();
    this.scheduleStatsEmit(ed);
  }

  /**
   * 规范化外部导入或粘贴的 HTML 待办复选框，将其统一转换为 TipTap 2 可交互渲染的
   * <ul data-type="taskList"><li data-type="taskItem" data-checked="true|false"> 结构，
   * 兼容原生 <input type="checkbox">、role="checkbox" 以及 <br> 分隔的多行待办。
   */
  private normalizeTodoHtml(rawHtml: string): string {
    if (!rawHtml) return '';
    if (!/checkbox|data-checked|aria-checked|-checked\s*:/i.test(rawHtml)) return rawHtml;

    const root = document.createElement('div');
    root.innerHTML = rawHtml;

    const isCheckedControl = (el: Element): boolean => {
      if (el instanceof HTMLInputElement && el.type.toLowerCase() === 'checkbox') {
        if (el.checked) return true;
        const attr = el.getAttribute('checked');
        if (attr !== null && !/^(false|0)$/i.test(attr.trim())) return true;
      }
      const aria = el.getAttribute('aria-checked');
      if (aria && /^true$/i.test(aria.trim())) return true;
      const dataChecked = el.getAttribute('data-checked');
      if (dataChecked && /^true$/i.test(dataChecked.trim())) return true;
      return false;
    };

    const findLeadingCheckbox = (container: Element): { control: Element; removeNode: Element; checked: boolean } | null => {
      for (const child of Array.from(container.childNodes)) {
        if (child.nodeType === Node.TEXT_NODE) {
          if ((child.nodeValue || '').trim().length > 0) return null;
          continue;
        }
        if (child.nodeType !== Node.ELEMENT_NODE) continue;
        const el = child as Element;
        if (el.matches('input[type="checkbox"], [role="checkbox"]')) {
          return { control: el, removeNode: el, checked: isCheckedControl(el) };
        }
        if (el.tagName === 'LABEL' || el.tagName === 'SPAN') {
          const nested = el.querySelector('input[type="checkbox"], [role="checkbox"]');
          if (nested && (el.textContent || '').trim() === '') {
            return { control: nested, removeNode: el, checked: isCheckedControl(nested) };
          }
          if (nested) {
            return { control: nested, removeNode: nested, checked: isCheckedControl(nested) };
          }
        }
        return null;
      }
      return null;
    };

    // 1. 处理标准 <ul>/<ol> 中带复选框或 checked 属性/样式的 <li>
    root.querySelectorAll('li').forEach((li) => {
      if (li.getAttribute('data-type') === 'taskItem') {
        li.parentElement?.setAttribute('data-type', 'taskList');
        return;
      }
      const lead = findLeadingCheckbox(li);
      const styleAttr = li.getAttribute('style') || '';
      const styleCheckedMatch = /-checked\s*:\s*(true|false)/i.exec(styleAttr);
      const attrChecked = li.getAttribute('data-checked') ?? li.getAttribute('aria-checked');
      if (!lead && !styleCheckedMatch && attrChecked === null) return;

      let checked = false;
      if (lead) {
        checked = lead.checked;
        lead.removeNode.remove();
      } else if (styleCheckedMatch) {
        checked = styleCheckedMatch[1].toLowerCase() === 'true';
      } else if (attrChecked !== null) {
        checked = /^true$/i.test(attrChecked.trim());
      }

      li.setAttribute('data-type', 'taskItem');
      li.setAttribute('data-checked', checked ? 'true' : 'false');
      if (!li.querySelector(':scope > p')) {
        const p = document.createElement('p');
        while (li.firstChild) p.appendChild(li.firstChild);
        li.appendChild(p);
      }
      if (li.parentElement && (li.parentElement.tagName === 'UL' || li.parentElement.tagName === 'OL')) {
        li.parentElement.setAttribute('data-type', 'taskList');
      }
    });

    // 2. 处理 <div> / <p> 中的块级待办（含内部用 <br> 分行的连续待办）
    const convertContainerBlocks = (parent: Element): void => {
      const children = Array.from(parent.children);
      let activeTaskList: HTMLUListElement | null = null;

      for (const child of children) {
        if (child.getAttribute('data-type') === 'taskList') {
          activeTaskList = child as HTMLUListElement;
          continue;
        }
        if (child.tagName === 'DIV' || child.tagName === 'P') {
          const directCheckboxes = child.querySelectorAll('input[type="checkbox"], [role="checkbox"]');
          if (directCheckboxes.length > 0 && child.querySelector('br')) {
            // 将 <br> 分隔的待办拆分为独立片段逐行转换
            const lines: Node[][] = [[]];
            for (const n of Array.from(child.childNodes)) {
              if (n.nodeType === Node.ELEMENT_NODE && (n as Element).tagName === 'BR') {
                lines.push([]);
              } else {
                lines[lines.length - 1].push(n);
              }
            }
            const frag = document.createDocumentFragment();
            let lineList: HTMLUListElement | null = null;
            for (const lineNodes of lines) {
              const tmp = document.createElement('div');
              lineNodes.forEach((n) => tmp.appendChild(n));
              const lead = findLeadingCheckbox(tmp);
              if (lead) {
                lead.removeNode.remove();
                if (!lineList) {
                  lineList = document.createElement('ul');
                  lineList.setAttribute('data-type', 'taskList');
                  frag.appendChild(lineList);
                }
                const li = document.createElement('li');
                li.setAttribute('data-type', 'taskItem');
                li.setAttribute('data-checked', lead.checked ? 'true' : 'false');
                const p = document.createElement('p');
                while (tmp.firstChild) p.appendChild(tmp.firstChild);
                li.appendChild(p);
                lineList.appendChild(li);
              } else if (tmp.innerHTML.trim().length > 0) {
                lineList = null;
                const p = document.createElement('p');
                while (tmp.firstChild) p.appendChild(tmp.firstChild);
                frag.appendChild(p);
              }
            }
            child.replaceWith(frag);
            activeTaskList = null;
            continue;
          }

          const lead = findLeadingCheckbox(child);
          if (lead) {
            lead.removeNode.remove();
            if (!activeTaskList) {
              activeTaskList = document.createElement('ul');
              activeTaskList.setAttribute('data-type', 'taskList');
              parent.insertBefore(activeTaskList, child);
            }
            const li = document.createElement('li');
            li.setAttribute('data-type', 'taskItem');
            li.setAttribute('data-checked', lead.checked ? 'true' : 'false');
            const p = document.createElement('p');
            while (child.firstChild) p.appendChild(child.firstChild);
            li.appendChild(p);
            activeTaskList.appendChild(li);
            child.remove();
            continue;
          }

          activeTaskList = null;
          convertContainerBlocks(child);
        } else {
          activeTaskList = null;
        }
      }
    };
    convertContainerBlocks(root);

    // 3. 兜底：保留剩余行内复选框为可视 ☑ / ☐ 图标，防止被编辑器丢弃
    root.querySelectorAll('input[type="checkbox"]').forEach((inp) => {
      if (inp.closest('li[data-type="taskItem"] > label')) return;
      const checked = isCheckedControl(inp);
      inp.replaceWith(document.createTextNode(checked ? '☑ ' : '☐ '));
    });

    return root.innerHTML;
  }

  insertMedia(url: string, fileName: string, isImage: boolean): void {
    const ed = this.ensureEditor();
    if (isImage) {
      ed.chain().focus().setImage({ src: url, alt: fileName }).insertContent(' ').run();
    } else {
      ed.chain().focus().insertContent(`<a href="${url}" target="_blank">📎 ${this.escapeHtml(fileName)}</a> `).run();
    }
  }

  execCommand(cmd: string, args?: any): void {
    const ed = this.ensureEditor();
    switch (cmd) {
      case 'undo': ed.chain().focus().undo().run(); break;
      case 'redo': ed.chain().focus().redo().run(); break;
      case 'bold': ed.chain().focus().toggleBold().run(); break;
      case 'italic': ed.chain().focus().toggleItalic().run(); break;
      case 'underline': ed.chain().focus().toggleUnderline().run(); break;
      case 'strike': ed.chain().focus().toggleStrike().run(); break;
      case 'bulletList': ed.chain().focus().toggleBulletList().run(); break;
      case 'orderedList': ed.chain().focus().toggleOrderedList().run(); break;
      case 'taskList': ed.chain().focus().toggleTaskList().run(); break;
      case 'blockquote': ed.chain().focus().toggleBlockquote().run(); break;
      case 'codeBlock': ed.chain().focus().toggleCodeBlock().run(); break;
      case 'table': ed.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(); break;
      case 'addRow': ed.chain().focus().addRowAfter().run(); break;
      case 'addCol': ed.chain().focus().addColumnAfter().run(); break;
      case 'delTable': ed.chain().focus().deleteTable().run(); break;
      case 'alignLeft': ed.chain().focus().setTextAlign('left').run(); break;
      case 'alignCenter': ed.chain().focus().setTextAlign('center').run(); break;
      case 'alignRight': ed.chain().focus().setTextAlign('right').run(); break;
      case 'toggleFindBar':
        this.findBarEl.classList.toggle('hidden');
        if (!this.findBarEl.classList.contains('hidden')) {
          this.findInput.focus();
          this.findInput.select();
        }
        break;
      case 'insertDate': {
        const now = new Date();
        const s = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')} ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
        ed.chain().focus().insertContent(s).run();
        break;
      }
      // 高效行级文本处理工具（排序 / 去重 / 去行尾空格 / 大小写转换）
      case 'sortLines': {
        const lines = ed.getText({ blockSeparator: '\n' }).split('\n').sort((a, b) => a.localeCompare(b, 'zh-CN'));
        ed.commands.setContent(lines.map((l) => `<p>${this.escapeHtml(l)}</p>`).join(''), true);
        break;
      }
      case 'dedupLines': {
        const seen = new Set<string>();
        const lines = ed.getText({ blockSeparator: '\n' }).split('\n').filter((l) => {
          const k = l.trim();
          if (!k) return true;
          if (seen.has(k)) return false;
          seen.add(k);
          return true;
        });
        ed.commands.setContent(lines.map((l) => `<p>${this.escapeHtml(l)}</p>`).join(''), true);
        break;
      }
      case 'trimWhitespace': {
        const lines = ed.getText({ blockSeparator: '\n' }).split('\n').map((l) => l.replace(/[ \t]+$/g, ''));
        ed.commands.setContent(lines.map((l) => `<p>${this.escapeHtml(l)}</p>`).join(''), true);
        break;
      }
      case 'toggleCase': {
        const { from, to } = ed.state.selection;
        if (to > from) {
          const selected = ed.state.doc.textBetween(from, to, '\n');
          const transformed = selected === selected.toUpperCase() ? selected.toLowerCase() : selected.toUpperCase();
          ed.chain().focus().insertContentAt({ from, to }, transformed).run();
        }
        break;
      }
      case 'highlight':
        if (!args?.color) ed.chain().focus().unsetHighlight().run();
        else ed.chain().focus().setHighlight({ color: args.color }).run();
        break;
    }
  }

  private wireToolbar(): void {
    const handleClick = (e: Event) => {
      const target = (e.target as HTMLElement).closest('button');
      if (!target) return;
      if (target.id === 'btn-rich-more') {
        const isHidden = this.moreMenuEl.classList.toggle('hidden');
        this.moreToggleBtn.classList.toggle('active', !isHidden);
        return;
      }
      const cmd = target.getAttribute('data-cmd');
      if (cmd) {
        this.execCommand(cmd);
        return;
      }
      const hl = target.getAttribute('data-hl');
      if (hl !== null) {
        this.execCommand('highlight', { color: hl });
      }
    };

    this.toolbarEl.addEventListener('click', handleClick);
    this.moreMenuEl.addEventListener('click', handleClick);

    const headingSelect = document.getElementById('rich-heading') as HTMLSelectElement;
    headingSelect?.addEventListener('change', () => {
      const ed = this.ensureEditor();
      if (headingSelect.value === 'p') ed.chain().focus().setParagraph().run();
      else ed.chain().focus().toggleHeading({ level: Number(headingSelect.value) as 1 | 2 | 3 }).run();
    });

    const textColor = document.getElementById('rich-text-color') as HTMLInputElement;
    textColor?.addEventListener('input', () => {
      this.ensureEditor().chain().focus().setColor(textColor.value).run();
    });
  }

  private wireFindReplaceBar(): void {
    document.getElementById('btn-rich-find-close')?.addEventListener('click', () => {
      this.findBarEl.classList.add('hidden');
      this.editor?.commands.focus();
    });

    this.findInput?.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && this.findInput.value) {
        e.preventDefault();
        const ok = (window as any).find?.(this.findInput.value, false, false, true);
        this.findStatus.textContent = ok ? '✓' : '0';
      } else if (e.key === 'Escape') {
        this.findBarEl.classList.add('hidden');
        this.editor?.commands.focus();
      }
    });

    document.getElementById('btn-rich-replace-all')?.addEventListener('click', () => {
      if (!this.editor) return;
      const needle = this.findInput.value;
      if (!needle) return;
      const replacement = this.replaceInput.value;
      const html = this.editor.getHTML();
      // Replace strictly inside text nodes (not inside HTML tags!)
      const container = document.createElement('div');
      container.innerHTML = html;
      let count = 0;
      const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
      let node: Text | null;
      while ((node = walker.nextNode() as Text | null)) {
        if (node.nodeValue && node.nodeValue.includes(needle)) {
          const parts = node.nodeValue.split(needle);
          count += parts.length - 1;
          node.nodeValue = parts.join(replacement);
        }
      }
      if (count > 0) {
        this.editor.commands.setContent(container.innerHTML, true);
      }
      this.findStatus.textContent = `${count}`;
    });
  }

  private wirePasteHandler(): void {
    this.hostEl.addEventListener('paste', (e) => {
      const cd = e.clipboardData;
      if (!cd) return;

      // 1. Pasted images go strictly into encrypted SQLCipher DB
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

      // 2. 粘贴含复选框的富文本 HTML 时，自动转换为可交互待办清单
      const html = cd.getData('text/html');
      const plain = cd.getData('text/plain');
      if (html && /checkbox|data-checked|aria-checked|-checked\s*:/i.test(html) && this.editor) {
        e.preventDefault();
        this.editor.chain().focus().insertContent(this.normalizeTodoHtml(html)).run();
        return;
      }

      // 3. Windows 纯文本粘贴：保留完整换行
      if (!html && plain && this.editor) {
        e.preventDefault();
        const normalized = plain.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        if (normalized.includes('\n')) {
          const paras = normalized.split('\n').map((l) => `<p>${this.escapeHtml(l) || '<br>'}</p>`).join('');
          this.editor.chain().focus().insertContent(paras).run();
        } else {
          this.editor.chain().focus().insertContent(this.escapeHtml(normalized)).run();
        }
      }
    });
  }

  private escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  private scheduleEmit(html: string, plainText: string): void {
    if (this.debounceTimer !== null) window.clearTimeout(this.debounceTimer);
    this.debounceTimer = window.setTimeout(() => {
      hostBridge.postEvent('doc.contentChanged', {
        noteId: this.noteId,
        content: html,
        plainText
      });
    }, 240);
  }
}
