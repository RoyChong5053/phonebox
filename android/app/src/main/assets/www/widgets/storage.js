import { API, PANEL_HEADERS, chart, color, cssVar, D, fmtMem, h, rateStr, t, toast } from '../main.js';
import { W } from '../registry.js';

// Computer widgets that watch storage: the SSD throughput and the temp folder.
const HISTORY = 60;
const push = (a, v) => { a.push(v || 0); if (a.length > HISTORY) a.shift(); };

// ---------------------------------------------------------------- disk I/O
W.diskiostat = {
  needs: ['state'],
  create(box, { o }) {
    const filter = String(o.device || '').trim().toLowerCase();
    const head = h('div', 's-head', h('span', 's-label', o.title || t('w.diskIo')), h('span', 's-name', ''));
    const body = h('div', 'io-body');
    const canvas = h('canvas', 'io-chart');
    box.append(head, body, canvas);
    box.style.setProperty('--c', cssVar('--net'));
    const read = [], write = [];
    let lastKey = '';

    const row = (d) => h('div', 'io-row',
      h('span', 'io-name', d.name),
      h('span', 'io-rate', h('b', null, 'R '), rateStr(d.readBps).join(' ')),
      h('span', 'io-rate', h('b', null, 'W '), rateStr(d.writeBps).join(' ')),
      h('span', 'io-busy', h('span', 'io-track', h('i')), h('em', null, Math.round(d.busyPct) + '%')));

    return {
      update() {
        const all = D.state?.pc?.diskIo;
        if (!all) { body.replaceChildren(h('div', 'empty', t('w.loading'))); canvas.style.visibility = 'hidden'; return; }
        if (!all.length) { body.replaceChildren(h('div', 'empty', t('w.noDiskIo'))); canvas.style.visibility = 'hidden'; return; }
        const list = all.filter((d) => !filter || d.name.toLowerCase().includes(filter));
        head.lastChild.textContent = list.length ? list.map((d) => d.name).join(' · ') : '';
        if (!list.length) { body.replaceChildren(h('div', 'empty', t('w.noSuchDevice'))); canvas.style.visibility = 'hidden'; return; }
        canvas.style.visibility = 'visible';

        const rb = list.reduce((n, d) => n + d.readBps, 0);
        const wb = list.reduce((n, d) => n + d.writeBps, 0);
        push(read, rb);
        push(write, wb);
        const key = JSON.stringify([Math.round(rb), Math.round(wb), list.map((d) => [d.name, Math.round(d.busyPct)]), box.clientHeight]);
        if (key === lastKey) return;
        lastKey = key;

        const max = Math.max(1, ...read, ...write);
        chart(canvas, [
          { data: read, color: color('--net'), max },
          { data: write, color: color('#f59e0b'), max },
        ], { grid: true });
        body.replaceChildren(...list.slice(0, 4).map((d) => {
          const e = row(d);
          e.querySelector('.io-busy i').style.width = Math.min(100, d.busyPct) + '%';
          return e;
        }));
      },
    };
  },
};

// ---------------------------------------------------------------- temp folder
W.tmpdir = {
  needs: ['state'],
  create(box, { o }) {
    const minAge = Math.max(1, Math.min(10080, Number(o.minAge) || 60));
    const showTop = o.top !== false;
    const head = h('div', 's-head', h('span', 's-label', o.title || t('w.tmp')), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const track = h('div', 'track', h('i'));
    const meta = h('div', 'tmp-meta');
    const top = h('div', 'tmp-top');
    const btn = h('button', 'tmp-clean', t('w.clean'));
    box.append(head, main, track, meta, top, btn);
    box.style.setProperty('--c', cssVar('--ram'));

    let info = null;

    const render = (next) => {
      info = next || D.state?.pc?.tmp || info;
      if (!info) { main.replaceChildren(h('b', null, '—')); meta.textContent = t('w.loading'); btn.disabled = true; return; }
      const pct = info.totalMb > 0 ? Math.min(100, (info.usedMb / info.totalMb) * 100) : 0;
      head.lastChild.textContent = info.fs === 'tmpfs' ? 'RAM' : info.fs;
      main.replaceChildren(h('b', null, fmtMem(info.usedMb)), h('small', null, ' / ' + fmtMem(info.totalMb)));
      track.firstChild.style.width = pct + '%';
      track.firstChild.style.background = pct > 85 ? 'var(--err, #f87171)' : '';
      meta.replaceChildren(
        h('span', null, `${Math.round(pct)}%`),
        h('span', null, t('w.tmpEntries', { n: info.entries })),
        info.oldestSec > 0 ? h('span', null, t('w.tmpOldest', { age: age(info.oldestSec) })) : null);
      btn.disabled = !info.entries;
      if (!showTop) { top.replaceChildren(); return; }
      const rows = (info.top || []).filter((e) => e.sizeMb > 0).slice(0, 3);
      top.replaceChildren(...rows.map((e) => h('div', 'tmp-e' + (e.protected ? ' keep' : ''),
        h('span', 'tmp-en', e.name), h('span', 'tmp-es', fmtMem(e.sizeMb)))));
    };

    const clean = async () => {
      if (!info) return;
      btn.disabled = true;
      try {
        const dry = await post('/tmp/clean?dry=1&minAge=' + minAge);
        if (!dry.deleted) { toast(t('w.tmpNothing'), 'ok'); return; }
        const ask = t('w.tmpConfirm', { n: minAge, size: fmtMem(dry.freedMb), path: info.path });
        if (!confirm(ask)) return;
        const r = await post('/tmp/clean?minAge=' + minAge);
        toast(t('w.tmpCleaned', { size: fmtMem(r.freedMb), n: r.deleted }), 'ok');
        render(r.tmp);
      } catch (_) {
        toast(t('panel.pcNoAnswer'), 'err');
      } finally {
        btn.disabled = !info?.entries;
      }
    };
    btn.addEventListener('click', clean);
    render(null);

    return { update() { render(null); } };
  },
};

/** A POST that may take longer than the panel's 2.5 s api() deadline: a clean pass walks the folder. */
async function post(path) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), 20_000);
  try {
    const r = await fetch(API + path, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.json();
  } finally { clearTimeout(timer); }
}

function age(sec) {
  if (sec < 3600) return Math.max(1, Math.round(sec / 60)) + 'm';
  if (sec < 86400) return Math.round(sec / 3600) + 'h';
  return Math.round(sec / 86400) + 'd';
}
