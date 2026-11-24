/**
 * WebView2 前后端双向消息信封结构，用于编辑器内核与 WinUI 3 宿主窗口之间的事件与命令通信。
 */
export interface HostEnvelope {
  id?: string;
  type: 'request' | 'response' | 'event';
  method: string;
  payload?: any;
}

type MessageHandler = (msg: HostEnvelope) => void;

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(data: unknown): void;
        addEventListener(type: 'message', listener: (ev: { data: unknown }) => void): void;
      };
    };
  }
}

class HostBridge {
  private listeners = new Set<MessageHandler>();

  constructor() {
    if (window.chrome?.webview) {
      window.chrome.webview.addEventListener('message', (ev) => {
        try {
          const data = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
          this.listeners.forEach((fn) => fn(data as HostEnvelope));
        } catch (err) {
          console.error('[Bridge] parse error:', err);
        }
      });
    }
  }

  onMessage(fn: MessageHandler): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  postEvent(method: string, payload?: any): void {
    const env: HostEnvelope = { type: 'event', method, payload };
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(env);
    } else {
      console.debug('[Bridge->Host]', method, payload);
    }
  }
}

export const hostBridge = new HostBridge();
