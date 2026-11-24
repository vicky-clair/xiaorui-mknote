/**
 * 微软 Word 风格字数统计结果结构（支持中日韩字符、西文单词、字符数、段落数、行数及当前选区统计）。
 */
export interface WordCountStatsPayload {
  wordStyleTotal: number;
  cjkChars: number;
  latinWords: number;
  charsNoSpaces: number;
  charsWithSpaces: number;
  paragraphs: number;
  lines: number;
  selectionWords: number;
  selectionChars: number;
}

function isCjkWordChar(code: number): boolean {
  return (
    (code >= 0x4e00 && code <= 0x9fff) ||
    (code >= 0x3400 && code <= 0x4dbf) ||
    (code >= 0xf900 && code <= 0xfaff) ||
    (code >= 0x3040 && code <= 0x309f) ||
    (code >= 0x30a0 && code <= 0x30ff) ||
    (code >= 0xac00 && code <= 0xd7af)
  );
}

function isLatinWordChar(code: number): boolean {
  return (
    (code >= 65 && code <= 90) ||
    (code >= 97 && code <= 122) ||
    (code >= 48 && code <= 57) ||
    code === 95 ||
    (code >= 0x00c0 && code <= 0x024f) ||
    (code >= 0x0400 && code <= 0x04ff)
  );
}

function countCore(raw: string): {
  wordStyleTotal: number;
  cjkChars: number;
  latinWords: number;
  charsNoSpaces: number;
  charsWithSpaces: number;
  paragraphs: number;
  lines: number;
} {
  if (!raw) {
    return { wordStyleTotal: 0, cjkChars: 0, latinWords: 0, charsNoSpaces: 0, charsWithSpaces: 0, paragraphs: 0, lines: 0 };
  }

  const normalized = raw.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  let cjkChars = 0;
  let latinWords = 0;
  let charsNoSpaces = 0;
  let charsWithSpaces = 0;
  let inLatinWord = false;

  for (let i = 0; i < normalized.length; i++) {
    const ch = normalized[i];
    const code = normalized.charCodeAt(i);
    if (ch === '\n') {
      inLatinWord = false;
      continue;
    }

    charsWithSpaces++;
    if (!/\s/.test(ch)) charsNoSpaces++;

    if (isCjkWordChar(code)) {
      cjkChars++;
      inLatinWord = false;
    } else if (isLatinWordChar(code)) {
      if (!inLatinWord) {
        latinWords++;
        inLatinWord = true;
      }
    } else if ((ch === "'" || ch === '’' || ch === '-') && inLatinWord) {
      // keep contraction/hyphenated word intact
    } else {
      inLatinWord = false;
    }
  }

  const linesArr = normalized.split('\n');
  const lines = normalized.trim().length === 0 ? 0 : linesArr.length;
  const paragraphs = linesArr.filter((l) => l.trim().length > 0).length;

  return {
    wordStyleTotal: cjkChars + latinWords,
    cjkChars,
    latinWords,
    charsNoSpaces,
    charsWithSpaces,
    paragraphs,
    lines
  };
}

export function computeWordCountStats(text: string, selectionText = ''): WordCountStatsPayload {
  const cleaned = text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n?/, '');
  const full = countCore(cleaned);
  const sel = selectionText ? countCore(selectionText) : null;
  return {
    ...full,
    selectionWords: sel ? sel.wordStyleTotal : 0,
    selectionChars: sel ? sel.charsNoSpaces : 0
  };
}
