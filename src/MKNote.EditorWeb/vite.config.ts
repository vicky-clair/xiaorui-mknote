/**
 * MKNote.EditorWeb 前端离线打包配置（输出至 WinUI 3 宿主的 Assets/EditorWeb 目录）。
 */
import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
  base: './',
  build: {
    outDir: path.resolve(__dirname, '../MKNote.App/Assets/EditorWeb'),
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 3000,
    rollupOptions: {
      output: {
        // 避免文件名包含 `.esm-` 限定符触发 Windows App SDK PRI249 资源打包警告
        sanitizeFileName(name) {
          return name.replace(/\.esm/gi, '_esm').replace(/[^a-zA-Z0-9._-]/g, '_');
        },
        manualChunks: {
          tiptap: ['@tiptap/core', '@tiptap/starter-kit'],
          codemirror: ['@codemirror/state', '@codemirror/view', '@codemirror/lang-markdown'],
          mermaid: ['mermaid'],
          katex: ['katex']
        }
      }
    }
  }
});
