import { D, ago, bridge, en, h, r1, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- compute units
// Machines monitored elsewhere (the LG-V35's Termux collects them over SSH) and pushed into the agent:
//
//   POST /api/push/compute?ttl=30
//   { "v": 1, "nodes": [{ "id": "loq", "name": "Lenovo LOQ", "online": true, "ping": 3,
//       "cpu": 42.1, "cores": 16, "ram_used": 8234, "ram_total": 16384,
//       "bat": 87, "bat_state": "Charging", "temp": 71,
//       "gpu": 58, "gpu_temp": 77, "gpu_mem_used": 1200, "gpu_mem_total": 8188 }] }
//
// RAM is in MB. A field that the machine cannot report (CPU on an Android/Termux node) is null, not zero.

const pctOf = (v) => (v == null || !Number.isFinite(+v) ? null : Math.max(0, Math.min(100, +v)));
const level = (p) => (p == null ? '' : p >= 90 ? ' hot' : p >= 70 ? ' warm' : '');
// The machine a node is about: a computer (💻) or the panel's own phone (📱); a field that is null is "not this".
const devIcon = (n) => (n && (n.local || n.kind === 'android')) ? '📱' : '💻';

function battery(n) {
  let pct = n.bat, chg = n.bat_state === 'Charging' || n.bat_state === 'Full' || n.ac === true;
  // This phone cannot read its own battery through Termux; the app can (same source as the panel's status line).
  if (pct == null && n.local && bridge) {
    try {
      const b = JSON.parse(bridge.battery());
      if (b && b.level >= 0) { pct = b.level; chg = !!b.plugged; }
    } catch (_) {}
  }
  if (typeof pct === 'string' && pct) return { text: pct, pct: null };
  if (pct == null) return { text: null, pct: null };
  return { text: (chg ? '⚡' : '') + Math.round(pct) + '%', pct };
}

function meter(cls, p, text) {
  const bar = h('i', 'cu-bar', h('em'));
  bar.querySelector('em').style.width = p != null ? p + '%' : '0';
  const cell = h('span', 'cu-cell ' + cls, h('b', null, text), bar);
  if (p != null) cell.className += level(p);
  return cell;
}

W.compute = {
  needs: ['push', 'tick'],
  create(box, { o }) {
    const key = o.key || 'compute';
    const only = String(o.nodes || '').split(',').map((s) => s.trim()).filter(Boolean);
    const compact = o.compact === true;
    const title = h('div', 'w-title', o.title ? o.title : en('Compute units'));
    const meta = h('span', 'w-sub');
    const rows = h('div', 'cu-rows');
    // the device glyph of the instance: it follows the machine the first row shows (computer 💻, phone 📱)
    const devIco = h('span', 'cu-ico', '💻');
    const head = h('div', 'cu-head', devIco, title, meta);
    box.append(head, rows);
    if (compact) box.classList.add('compact');
    let lastAt = null, lastNodes = '';

    const row = (n) => {
      const cpu = pctOf(n.cpu);
      const ramPct = n.ram_used != null && n.ram_total ? (n.ram_used / n.ram_total) * 100 : null;
      const ramText = n.ram_total ? `${r1(n.ram_used / 1024)}/${Math.round(n.ram_total / 1024)}GB` : null;
      const cpuText = cpu == null ? '—' : Math.round(cpu) + '%';
      const temp = n.temp == null ? null : Math.round(n.temp) + '°';
      const bat = battery(n);
      const el = h('div', 'cu-row' + (n.online ? '' : ' off'),
        h('span', 'cu-dot'),
        h('span', 'cu-ico', devIcon(n)),
        h('span', 'cu-name', h('b', null, n.name || n.id || '?'),
          n.cores ? h('small', null, ' ' + n.cores + 'c') : null),
        h('span', 'cu-ping', n.online ? (n.ping == null ? '' : Math.round(n.ping) + 'ms') : 'offline'),
        meter('cu-cpu', cpu, cpuText),
        compact ? null : meter('cu-ram', ramPct, ramText || '—'),
        compact ? null : h('span', 'cu-temp' + (n.temp >= 85 ? ' hot' : ''), temp || ''),
        h('span', 'cu-bat' + (bat.pct != null && bat.pct <= 15 ? ' low' : ''), bat.text || ''));
      if (n.gpu != null) el.title = `GPU ${Math.round(n.gpu)}%${n.gpu_temp != null ? ' · ' + Math.round(n.gpu_temp) + '°C' : ''}${n.gpu_mem_total ? ' · ' + r1(n.gpu_mem_used / 1024) + '/' + Math.round(n.gpu_mem_total / 1024) + ' GB VRAM' : ''}`;
      return el;
    };

    const render = () => {
      const item = D.push[key];
      const data = item && item.data && typeof item.data === 'object' && !Array.isArray(item.data) ? item.data : null;
      let nodes = data && Array.isArray(data.nodes) ? data.nodes : [];
      if (only.length) nodes = nodes.filter((n) => only.includes(n.id) || only.includes(n.name));
      const at = item ? item.at : 0;
      if (!nodes.length) {
        lastAt = at; lastNodes = '';
        meta.textContent = '';
        rows.replaceChildren(h('div', 'cu-empty', item ? en('No nodes in the payload') : t('w.noDataYet'),
          h('code', null, `POST /api/push/${key}`)));
        return;
      }
      const online = nodes.filter((n) => n.online).length;
      meta.textContent = `${online}/${nodes.length} · ${ago(at)}`;
      const sig = JSON.stringify(nodes) + box.clientHeight;
      if (at === lastAt && sig === lastNodes) return;
      lastAt = at; lastNodes = sig;
      if (devIco && nodes[0]) devIco.textContent = devIcon(nodes[0]);
      rows.replaceChildren(...nodes.map(row));
    };

    return { update: render };
  },
};
