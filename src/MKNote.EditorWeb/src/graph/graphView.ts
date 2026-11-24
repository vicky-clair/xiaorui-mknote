import { hostBridge } from '../bridge/hostBridge';

interface GNode {
  id: string;
  label: string;
  kind: string;
  degree: number;
  x: number;
  y: number;
  vx: number;
  vy: number;
}
interface GEdge { source: string; target: string; kind: string; }

/**
 * 知识图谱力导向 Canvas 视图控制器：支持节点拖拽、画布平移缩放、实时筛选与双击跳转目标笔记。
 */
export class KnowledgeGraphView {
  private readonly canvas = document.getElementById('graph-canvas') as HTMLCanvasElement;
  private readonly ctx = this.canvas.getContext('2d')!;
  private nodes: GNode[] = [];
  private edges: GEdge[] = [];
  private filterText = '';
  private offsetX = 0;
  private offsetY = 0;
  private scale = 1;
  private draggingNode: GNode | null = null;
  private panning = false;
  private lastX = 0;
  private lastY = 0;
  private hovered: GNode | null = null;
  private rafId = 0;

  constructor() {
    this.wireEvents();
  }

  load(data: { nodes: any[]; edges: any[] }): void {
    const rect = this.canvas.getBoundingClientRect();
    this.canvas.width = Math.max(rect.width, 600);
    this.canvas.height = Math.max(rect.height, 400);
    this.offsetX = this.canvas.width / 2;
    this.offsetY = this.canvas.height / 2;
    this.scale = 1;

    this.nodes = (data.nodes || []).map((n, i) => {
      const angle = (i / Math.max(1, data.nodes.length)) * Math.PI * 2;
      const r = 90 + (i % 5) * 35;
      return {
        id: n.id ?? n.Id,
        label: n.label ?? n.Label,
        kind: n.kind ?? n.Kind,
        degree: n.degree ?? n.Degree ?? 0,
        x: Math.cos(angle) * r,
        y: Math.sin(angle) * r,
        vx: 0,
        vy: 0
      };
    });

    this.edges = (data.edges || []).map((e) => ({
      source: e.source ?? e.Source,
      target: e.target ?? e.Target,
      kind: e.kind ?? e.Kind
    }));

    cancelAnimationFrame(this.rafId);
    this.tick();
  }

  stop(): void {
    cancelAnimationFrame(this.rafId);
  }

  private tick = (): void => {
    const nodeMap = new Map(this.nodes.map((n) => [n.id, n]));

    // Force-directed physics step
    for (let i = 0; i < this.nodes.length; i++) {
      const a = this.nodes[i];
      a.vx += -a.x * 0.0025;
      a.vy += -a.y * 0.0025;
      for (let j = i + 1; j < this.nodes.length; j++) {
        const b = this.nodes[j];
        const dx = b.x - a.x || 0.01;
        const dy = b.y - a.y || 0.01;
        const distSq = Math.max(dx * dx + dy * dy, 220);
        const f = 1800 / distSq;
        const dist = Math.sqrt(distSq);
        a.vx -= (dx / dist) * f;
        a.vy -= (dy / dist) * f;
        b.vx += (dx / dist) * f;
        b.vy += (dy / dist) * f;
      }
    }

    for (const e of this.edges) {
      const a = nodeMap.get(e.source);
      const b = nodeMap.get(e.target);
      if (!a || !b) continue;
      const dx = b.x - a.x;
      const dy = b.y - a.y;
      const dist = Math.sqrt(dx * dx + dy * dy) || 1;
      const force = (dist - 120) * 0.018;
      a.vx += (dx / dist) * force;
      a.vy += (dy / dist) * force;
      b.vx -= (dx / dist) * force;
      b.vy -= (dy / dist) * force;
    }

    for (const n of this.nodes) {
      if (n === this.draggingNode) continue;
      n.vx *= 0.85;
      n.vy *= 0.85;
      n.x += n.vx;
      n.y += n.vy;
    }

    this.draw(nodeMap);
    this.rafId = requestAnimationFrame(this.tick);
  };

  private draw(nodeMap: Map<string, GNode>): void {
    const { ctx, canvas } = this;
    const style = getComputedStyle(document.body);
    const fg = style.getPropertyValue('--doc-fg').trim() || '#222';
    const accent = style.getPropertyValue('--doc-accent').trim() || '#2b88d8';
    const linkCol = style.getPropertyValue('--doc-link').trim() || '#7c3aed';

    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.save();
    ctx.translate(this.offsetX, this.offsetY);
    ctx.scale(this.scale, this.scale);

    // Edges
    for (const e of this.edges) {
      const a = nodeMap.get(e.source);
      const b = nodeMap.get(e.target);
      if (!a || !b) continue;
      const highlighted = this.hovered && (a === this.hovered || b === this.hovered);
      ctx.strokeStyle = highlighted ? linkCol : 'rgba(130, 140, 155, 0.38)';
      ctx.lineWidth = highlighted ? 2 : 1;
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.stroke();
    }

    // Nodes
    const q = this.filterText.toLowerCase();
    for (const n of this.nodes) {
      const matches = !q || n.label.toLowerCase().includes(q);
      const radius = 6 + Math.min(n.degree * 2.2, 12);
      ctx.globalAlpha = matches ? 1 : 0.2;
      ctx.fillStyle = n.kind === 'unresolved' ? '#94a3b8' : (n === this.hovered ? linkCol : accent);
      ctx.beginPath();
      ctx.arc(n.x, n.y, radius, 0, Math.PI * 2);
      ctx.fill();

      ctx.font = '12px "Microsoft YaHei UI", sans-serif';
      ctx.fillStyle = fg;
      ctx.textAlign = 'center';
      ctx.fillText(n.label, n.x, n.y + radius + 14);
    }

    ctx.restore();
  }

  private hitTest(clientX: number, clientY: number): GNode | null {
    const rect = this.canvas.getBoundingClientRect();
    const wx = (clientX - rect.left - this.offsetX) / this.scale;
    const wy = (clientY - rect.top - this.offsetY) / this.scale;
    for (const n of this.nodes) {
      const r = 10 + Math.min(n.degree * 2, 12);
      if ((n.x - wx) ** 2 + (n.y - wy) ** 2 <= r * r) return n;
    }
    return null;
  }

  private wireEvents(): void {
    this.canvas.addEventListener('mousedown', (e) => {
      const hit = this.hitTest(e.clientX, e.clientY);
      this.lastX = e.clientX;
      this.lastY = e.clientY;
      if (hit) this.draggingNode = hit;
      else this.panning = true;
    });

    window.addEventListener('mousemove', (e) => {
      if (this.draggingNode) {
        const rect = this.canvas.getBoundingClientRect();
        this.draggingNode.x = (e.clientX - rect.left - this.offsetX) / this.scale;
        this.draggingNode.y = (e.clientY - rect.top - this.offsetY) / this.scale;
      } else if (this.panning) {
        this.offsetX += e.clientX - this.lastX;
        this.offsetY += e.clientY - this.lastY;
        this.lastX = e.clientX;
        this.lastY = e.clientY;
      } else {
        this.hovered = this.hitTest(e.clientX, e.clientY);
      }
    });

    window.addEventListener('mouseup', () => {
      this.draggingNode = null;
      this.panning = false;
    });

    this.canvas.addEventListener('dblclick', (e) => {
      const hit = this.hitTest(e.clientX, e.clientY);
      if (hit && hit.kind === 'note') {
        hostBridge.postEvent('graph.openNote', { noteId: hit.id });
      }
    });

    this.canvas.addEventListener('wheel', (e) => {
      e.preventDefault();
      this.scale = Math.max(0.3, Math.min(3, this.scale * (e.deltaY < 0 ? 1.1 : 0.9)));
    });

    (document.getElementById('graph-filter') as HTMLInputElement)?.addEventListener('input', (e) => {
      this.filterText = (e.target as HTMLInputElement).value;
    });

    document.getElementById('graph-reset')?.addEventListener('click', () => {
      this.offsetX = this.canvas.width / 2;
      this.offsetY = this.canvas.height / 2;
      this.scale = 1;
    });

    document.getElementById('graph-close')?.addEventListener('click', () => {
      this.stop();
      hostBridge.postEvent('graph.close');
    });
  }
}
