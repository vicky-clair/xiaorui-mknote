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
        // Avoid `.esm-` qualifier filenames that trigger WinAppSDK PRI249 warnings in Assets/
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
