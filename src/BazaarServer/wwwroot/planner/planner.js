/* 大巴扎 · 对手与种子编排台
 * 接口契约见 docs/PLANNER_API_CONTRACT.md；所有用户/Mod 文本一律用 textContent 写入，不拼 HTML。
 */
(() => {
  'use strict';

  // ════════════════ 常量 ════════════════
  const API_BASE = window.location.pathname.replace(/\/planner(\/.*)?$/i, '').replace(/\/$/, '');
  const TOKEN_KEY = `bazaar_planner_session:${API_BASE || '/'}`;
  const TIERS = ['Bronze', 'Silver', 'Gold', 'Diamond', 'Legendary'];
  const TIER_ZH = { Bronze: '青铜', Silver: '白银', Gold: '黄金', Diamond: '钻石', Legendary: '传奇' };
  const ENCH_ZH = {
    Fiery: '炽焰', Toxic: '剧毒', Shielded: '护盾', Heavy: '沉重', Icy: '冰霜', Turbo: '极速', Restorative: '治愈',
    Deadly: '致命', Golden: '黄金', Radiant: '辉光', Obsidian: '黑曜', Shiny: '闪耀', Mossy: '青苔', Mystical: '秘法',
    Ethereal: '虚灵', Tiny: '微型',
  };
  const SIZE_W = { Small: 1, Medium: 2, Large: 3 };
  const KIND_ZH = { event: '奇遇', merchant: '商人', monster: '野怪', pedestal: '祭坛' };
  const POWER_ZH = { Weak: '偏弱', Normal: '普通', Strong: '强', MetaBoss: '超强' };
  const SOURCE_ZH = { custom: '手搓', imported: '导入', sandbox: '沙盒', faced: '收藏', run: '来自对局' };
  const DEFAULT_HEROES = [
    { id: 'Vanessa', nameZh: '瓦内莎' }, { id: 'Pygmalien', nameZh: '皮格马利翁' }, { id: 'Dooley', nameZh: '杜利' },
    { id: 'Mak', nameZh: '马克' }, { id: 'Jules', nameZh: '朱尔斯' }, { id: 'Stelle', nameZh: '斯黛拉' },
    { id: 'Karnok', nameZh: '卡诺克' }, { id: 'Hero8', nameZh: '双龙' },
  ];
  const DEFAULT_EDIT_RULES = {
    quantityAttributes: ['DamageAmount', 'ShieldApplyAmount', 'HealAmount', 'BurnApplyAmount', 'PoisonApplyAmount', 'RegenApplyAmount', 'CritChance'],
    attributeLabelsZh: { DamageAmount: '伤害', ShieldApplyAmount: '护盾', HealAmount: '治疗', BurnApplyAmount: '灼烧', PoisonApplyAmount: '剧毒', RegenApplyAmount: '回复', CritChance: '暴击率%' },
    quantityMax: 1000000, healthMin: 1, healthMax: 1000000, levelMax: 99, prestigeMax: 100,
    dayBaseSlots: { 1: 4, 2: 6, 3: 8, 4: 10 },
    socketEffects: [],
    editableToggles: [
      { key: 'Tiny', labelZh: '微型（缩小 1 格）', onValue: 1, scope: 'nonSmallItem' },
      { key: 'Lifesteal', labelZh: '吸血', onValue: 100, scope: 'item' },
      { key: 'Flying', labelZh: '开局飞行', onValue: 1, scope: 'item' },
      { key: 'DestroyImmunity', labelZh: '不可摧毁', onValue: 1, scope: 'item' },
    ],
    editableTags: [
      { id: 'Aquatic', nameZh: '水生' }, { id: 'Weapon', nameZh: '武器' }, { id: 'Friend', nameZh: '伙伴' },
      { id: 'Relic', nameZh: '遗物' }, { id: 'Tool', nameZh: '工具' }, { id: 'Food', nameZh: '食物' },
      { id: 'Tech', nameZh: '科技' }, { id: 'Property', nameZh: '地产' }, { id: 'Toy', nameZh: '玩具' },
      { id: 'Dragon', nameZh: '龙' }, { id: 'Core', nameZh: '核心' }, { id: 'Apparel', nameZh: '服饰' },
      { id: 'Vehicle', nameZh: '载具' }, { id: 'Potion', nameZh: '药水' }, { id: 'Loot', nameZh: '战利品' },
      { id: 'Ray', nameZh: '射线' }, { id: 'Dinosaur', nameZh: '恐龙' },
    ],
    maxExtraTags: 6,
  };
  const DEFAULT_LIMITS = { maxLineups: 50, maxLineupDays: 30, maxSeedTextLength: 48, maxDisplayName: 24, maxLineupName: 40, maxDescription: 200 };
  const VISIBLE_DAYS_DEFAULT = 15;
  const SEED_FORMAT_HINT = '种子码是 S 开头的 8 位，比如 S3KZ9QW1。点「🎲 随机」生成一个，或者粘贴别人给你的码。';

  /** 种子只认固定格式的码：去空格、转大写，O 当 0、I/L 当 1；格式不对返回 null。和服务器的规则一模一样。 */
  function normalizeSeedCode(raw) {
    let s = String(raw || '').normalize('NFKC').replace(/\s+/g, '').toUpperCase();
    if (s.length !== 8 || s[0] !== 'S') return null;
    s = 'S' + s.slice(1).replace(/O/g, '0').replace(/[IL]/g, '1');
    if (!/^S[0-3][0-9A-HJKMNP-TV-Z]{6}$/.test(s)) return null; // 首位最大 3（不能超出 32 位）
    return s;
  }

  // ════════════════ 状态 ════════════════
  const state = {
    token: readToken(),
    ctx: null,
    heroes: DEFAULT_HEROES,
    heroZh: {},
    editRules: DEFAULT_EDIT_RULES,
    limits: DEFAULT_LIMITS,
    modEnv: null,                 // 编排环境：勾选的社区 Mod ID（不含全服默认）
    lineups: [],
    editor: null,                 // 当前编辑的方案
    cardCache: new Map(),         // templateId -> catalog 卡
    eventsCache: new Map(),       // `${hero}|${env}` -> events
    finder: { jobId: null, timer: null, conds: [], hits: [], scanned: 0, running: false, lastReq: null, nextSeed: null },
    importPreview: null,
    importCode: '',
    lastExport: null,
    unauthorizedShown: false,
    ctxLoadedAt: 0,
    calendarHighlight: null,
  };

  // ════════════════ 通用工具 ════════════════
  const $ = (id) => document.getElementById(id);

  /** 安全建节点：文本子节点一律走 TextNode。 */
  function h(tag, attrs, ...children) {
    const node = document.createElement(tag);
    if (attrs) {
      for (const [k, v] of Object.entries(attrs)) {
        if (v === undefined || v === null || v === false) continue;
        if (k === 'class') node.className = v;
        else if (k === 'text') node.textContent = v;
        else if (k === 'style' && typeof v === 'object') Object.assign(node.style, v);
        else if (k === 'dataset') Object.assign(node.dataset, v);
        else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
        else if (v === true) node.setAttribute(k, '');
        else node.setAttribute(k, String(v));
      }
    }
    appendChildren(node, children);
    return node;
  }
  function appendChildren(node, children) {
    for (const c of children) {
      if (c === null || c === undefined || c === false) continue;
      if (Array.isArray(c)) appendChildren(node, c);
      else if (c instanceof Node) node.appendChild(c);
      else node.appendChild(document.createTextNode(String(c)));
    }
  }
  function clear(node) { while (node && node.firstChild) node.removeChild(node.firstChild); return node; }
  function deepClone(o) { return o === undefined ? undefined : JSON.parse(JSON.stringify(o)); }
  function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }
  function heroName(id) { return state.heroZh[id] || id || '未知英雄'; }
  function tierZh(t) { return TIER_ZH[t] || t || '青铜'; }
  function fmtDays(days) {
    if (!days || !days.length) return '无';
    const sorted = [...days].sort((a, b) => a - b);
    return sorted.map((d) => `第${d}天`).join('、');
  }
  function fmtTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function debounce(fn, ms) {
    let t = null;
    const wrapped = (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
    wrapped.cancel = () => clearTimeout(t);
    return wrapped;
  }
  function readToken() { try { return sessionStorage.getItem(TOKEN_KEY) || ''; } catch { return ''; } }
  function writeToken(tok) {
    state.token = tok || '';
    try {
      if (tok) sessionStorage.setItem(TOKEN_KEY, tok);
      else sessionStorage.removeItem(TOKEN_KEY);
    } catch { /* 隐私模式下 sessionStorage 不可用时只存内存 */ }
  }

  function toast(msg, type = 'ok', ms = 4200) {
    const box = $('toastContainer');
    const el = h('div', { class: `toast ${type === 'ok' ? '' : type}`, text: msg });
    box.appendChild(el);
    setTimeout(() => el.remove(), ms);
  }

  async function copyText(text, okMsg = '已复制') {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
      } else {
        const ta = h('textarea', { style: { position: 'fixed', left: '-9999px', top: '0' } });
        ta.value = text;
        document.body.appendChild(ta);
        ta.select();
        const ok = document.execCommand('copy');
        ta.remove();
        if (!ok) throw new Error('copy failed');
      }
      toast(okMsg);
    } catch {
      toast('浏览器不允许自动复制，请手动选中后复制', 'warn');
    }
  }

  // ════════════════ 接口 ════════════════
  class ApiError extends Error {
    constructor(message, status, code, details) {
      super(message);
      this.status = status;
      this.code = code || '';
      this.details = details || null;
    }
  }

  async function api(path, opts = {}) {
    const { method = 'GET', json, query } = opts;
    let url = `${API_BASE}/api/planner/${path}`;
    if (query) {
      const qs = new URLSearchParams();
      for (const [k, v] of Object.entries(query)) {
        if (v === undefined || v === null || v === '' || (Array.isArray(v) && !v.length)) continue;
        qs.set(k, Array.isArray(v) ? v.join(',') : String(v));
      }
      const s = qs.toString();
      if (s) url += `?${s}`;
    }
    const headers = {};
    if (state.token) headers['X-Planner-Session'] = state.token;
    const init = { method, headers };
    if (json !== undefined) {
      headers['Content-Type'] = 'application/json; charset=utf-8';
      init.body = JSON.stringify(json);
    }
    let resp;
    try {
      resp = await fetch(url, init);
    } catch {
      throw new ApiError('连不上服务器：请检查网络，或确认服务器正在运行', 0, 'NETWORK');
    }
    let data = null;
    const ct = resp.headers.get('content-type') || '';
    if (ct.includes('json')) {
      try { data = await resp.json(); } catch { data = null; }
    } else {
      const t = await resp.text().catch(() => '');
      data = t ? { error: t.slice(0, 200) } : null;
    }
    if (!resp.ok) {
      const msg = (data && data.error) || `请求失败（${resp.status}）`;
      const code = (data && data.code) || '';
      const isAuthCall = path.startsWith('web-ticket') || path.startsWith('auth/');
      if (resp.status === 401 && !isAuthCall) handleUnauthorized();
      throw new ApiError(msg, resp.status, code, data && data.details);
    }
    return data || {};
  }

  function showError(e, prefix = '') {
    const msg = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    toast(prefix ? `${prefix}：${msg}` : msg, 'err', 6000);
  }

  // ════════════════ 弹窗 ════════════════
  function openModal({ title, body, footer = [], size = '', onClose } = {}) {
    const root = $('modalRoot');
    const bodyNode = h('div', { class: 'modal-body' });
    if (body) appendChildren(bodyNode, [body]);
    const footNode = h('div', { class: 'modal-foot' });
    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      document.removeEventListener('keydown', onKey);
      backdrop.remove();
      if (onClose) onClose();
    };
    for (const b of footer) {
      footNode.appendChild(h('button', {
        type: 'button',
        class: `btn ${b.kind || 'btn-subtle'}`,
        disabled: b.disabled,
        onclick: async (ev) => {
          if (b.onClick) {
            const r = await b.onClick(ev);
            if (r === false) return;
          }
          if (b.close !== false) close();
        },
      }, b.label));
    }
    const modal = h('div', { class: `modal ${size}`, role: 'dialog', 'aria-modal': 'true' },
      h('div', { class: 'modal-head' },
        h('h3', { text: title || '' }),
        h('button', { type: 'button', class: 'modal-close', title: '关闭（Esc）', onclick: close }, '×')),
      bodyNode,
      footer.length ? footNode : null);
    const backdrop = h('div', { class: 'modal-backdrop', onmousedown: (ev) => { if (ev.target === backdrop) close(); } }, modal);
    const onKey = (ev) => {
      if (ev.key === 'Escape' && root.lastElementChild === backdrop) { ev.preventDefault(); close(); }
    };
    document.addEventListener('keydown', onKey);
    root.appendChild(backdrop);
    return { close, body: bodyNode, foot: footNode, modal };
  }

  function confirmDialog(title, message, okLabel = '确定', okKind = 'btn-gold', cancelLabel = '取消') {
    return new Promise((resolve) => {
      let answered = false;
      const body = h('div', null, ...(Array.isArray(message) ? message : [h('p', { text: message })]));
      openModal({
        title,
        body,
        size: 'narrow',
        footer: [
          { label: cancelLabel, kind: 'btn-subtle', onClick: () => { answered = true; resolve(false); } },
          { label: okLabel, kind: okKind, onClick: () => { answered = true; resolve(true); } },
        ],
        onClose: () => { if (!answered) resolve(false); },
      });
    });
  }

  // ════════════════ 登录 ════════════════
  function showAuth(message, kind = 'warn') {
    $('app').classList.add('hidden');
    $('authScreen').classList.remove('hidden');
    const st = $('authStatus');
    if (message) {
      st.className = `auth-status ${kind === 'err' ? 'err-box' : kind === 'ok' ? 'ok-box' : 'warn-box'}`;
      st.textContent = message;
    } else {
      st.className = 'auth-status hidden';
    }
  }

  function showApp() {
    $('authScreen').classList.add('hidden');
    $('app').classList.remove('hidden');
  }

  function handleUnauthorized() {
    writeToken('');
    if (state.unauthorizedShown) return;
    state.unauthorizedShown = true;
    if (state.editor && state.editor.dirty) {
      showAuth('登录已过期（服务器可能重启过）。没保存的方案修改还留在这个页面：设置过口令的话，直接在下面重新登录就能接着编辑；没设口令只能回游戏重新打开编排台，这页没保存的修改会丢失。', 'warn');
    } else {
      showAuth('登录已过期（服务器可能重启过），请从游戏里重新点一次「去网页详细编辑」。', 'warn');
    }
  }

  async function exchangeTicket(ticket) {
    try {
      const r = await api('web-ticket/exchange', { method: 'POST', json: { ticket } });
      writeToken(r.token);
      return true;
    } catch {
      return false;
    }
  }

  async function onLoginSubmit(ev) {
    ev.preventDefault();
    const username = $('loginUsername').value.trim();
    const password = $('loginPassword').value;
    if (!username || !password) return;
    const btn = $('btnLogin');
    btn.disabled = true;
    btn.textContent = '登录中...';
    try {
      const r = await api('auth/login', { method: 'POST', json: { username, password } });
      writeToken(r.token);
      $('loginPassword').value = '';
      state.unauthorizedShown = false;
      await enterApp();
    } catch (e) {
      if (e.code === 'PASSWORD_REQUIRED_FOR_WEB_LOGIN') {
        showAuth('这个账号还没有设置口令，只能从游戏里打开编排台。', 'warn');
      } else if (e.code === 'BAD_CREDENTIALS') {
        showAuth('账号名或口令不对。', 'err');
      } else {
        showAuth(e.message, 'err');
      }
    } finally {
      btn.disabled = false;
      btn.textContent = '登录';
    }
  }

  async function logout() {
    if (state.editor && state.editor.dirty) {
      const ok = await confirmDialog('退出登录', '当前方案有未保存的修改，退出后会丢失。确定退出？', '退出', 'btn-danger');
      if (!ok) return;
    }
    writeToken('');
    stopFinderPolling();
    resetAccountState();
    showAuth('已退出。', 'ok');
  }

  /** 退出 / 换账号时清掉上一个账号留在页面里的所有东西，防止串号。 */
  function resetAccountState() {
    stopFinderPolling();
    state.ctx = null;
    state.accountId = null;
    state.modEnv = null;
    state.lineups = [];
    state.editor = null;
    state.cardCache.clear();
    state.eventsCache.clear();
    describeCache.clear();
    state.finder = { jobId: null, timer: null, conds: [], hits: [], scanned: 0, running: false, lastReq: null, nextSeed: null };
    state.importPreview = null;
    state.importCode = '';
    state.lastExport = null;
    state.calendarHighlight = null;
    state.seedInputDirty = false;
    ['lineupList', 'lineupEditor', 'calendarOut', 'finderResults', 'exportResult', 'importPreview', 'mineList', 'seedStatus', 'condList'].forEach((id) => { const n = $(id); if (n) clear(n); });
    $('exportResult').classList.add('hidden');
    $('finderProgress').classList.add('hidden');
    $('btnFindStart').classList.remove('hidden');
    $('btnFindStop').classList.add('hidden');
    $('findHero').disabled = false;
    $('findUseRules').disabled = false;
    $('importInput').value = '';
    $('seedInput').value = '';
    switchTab('lineups');
  }

  // ════════════════ 上下文 ════════════════
  async function loadContext() {
    const ctx = await api('me/context');
    if (state.accountId && ctx.accountId && state.accountId !== ctx.accountId) {
      // 登录过期后换了另一个账号登录：清掉上一个账号的方案、草稿和搜种结果
      resetAccountState();
    }
    state.accountId = ctx.accountId || state.accountId;
    state.ctx = ctx;
    state.ctxLoadedAt = Date.now();
    state.heroes = (ctx.heroes && ctx.heroes.length) ? ctx.heroes : DEFAULT_HEROES;
    state.heroZh = {};
    for (const hItem of state.heroes) state.heroZh[hItem.id] = hItem.nameZh;
    state.editRules = Object.assign({}, DEFAULT_EDIT_RULES, ctx.boardEditRules || {});
    state.limits = Object.assign({}, DEFAULT_LIMITS, ctx.limits || {});
    state.lineups = ctx.lineups || [];
    if (state.modEnv === null) {
      const known = new Set((ctx.mods || []).filter((m) => !m.isServerDefault).map((m) => m.modId));
      state.modEnv = ((ctx.rules && ctx.rules.selectedModIds) || []).filter((id) => known.has(id));
    }
    return ctx;
  }

  async function refreshContextQuietly(force = false) {
    if (!state.token) return;
    if (!force && Date.now() - state.ctxLoadedAt < 5000) return;
    try {
      await loadContext();
      renderGameStrip();
      renderLineupList();
      renderExportSources();
      syncSeedInputFromRules();
    } catch { /* 静默刷新失败不打扰用户 */ }
  }

  function rules() { return (state.ctx && state.ctx.rules) || {}; }
  function modsById() {
    const map = new Map();
    for (const m of (state.ctx && state.ctx.mods) || []) map.set(m.modId, m);
    return map;
  }
  function serverDefaultMods() { return ((state.ctx && state.ctx.mods) || []).filter((m) => m.isServerDefault); }
  function modName(id) { const m = modsById().get(id); return m ? m.name : id; }
  function selectedLineupSummary() {
    const r = rules();
    if (r.ghostOpponentMode !== 'FixedLineup' || !r.selectedGhostLineupId) return null;
    return state.lineups.find((l) => l.lineupId === r.selectedGhostLineupId) || null;
  }

  function renderGameStrip() {
    const r = rules();
    const seed = state.ctx && state.ctx.currentSeedText;
    const gsSeed = $('gsSeed');
    gsSeed.textContent = seed || '未设置（随机）';
    gsSeed.classList.toggle('off', !seed);
    gsSeed.classList.toggle('mono', !!seed);

    const gsOpp = $('gsOpp');
    const reset = $('gsOppReset');
    if (r.ghostOpponentMode === 'FixedLineup' && r.selectedGhostLineupId) {
      const l = selectedLineupSummary();
      gsOpp.textContent = l ? `方案「${l.name}」（${l.dayNumbers.length} 天固定）` : '所选方案已不存在（按全随机）';
      gsOpp.classList.toggle('off', !l);
      reset.classList.remove('hidden');
    } else {
      gsOpp.textContent = '全随机';
      gsOpp.classList.add('off');
      reset.classList.add('hidden');
    }

    const userMods = (r.selectedModIds || []).map(modName);
    const defaults = serverDefaultMods().map((m) => m.name);
    const gsMods = $('gsMods');
    const parts = [];
    if (userMods.length) parts.push(userMods.join('、'));
    if (defaults.length) parts.push(`全服默认 ${defaults.length} 个`);
    gsMods.textContent = parts.length ? parts.join(' + ') : '原版';
    gsMods.title = [...userMods, ...defaults.map((d) => `${d}（全服默认）`)].join('\n');
    gsMods.classList.toggle('off', !parts.length);

    const run = state.ctx && state.ctx.activeRun;
    const gsRun = $('gsRun');
    if (run && !run.isFinished) {
      gsRun.textContent = `进行中：${run.heroZh || heroName(run.hero)} 第 ${run.day} 天 · 改动从下一局开始生效`;
      gsRun.classList.remove('hidden');
    } else {
      gsRun.classList.add('hidden');
    }

    $('accountPill').textContent = (state.ctx && state.ctx.username) || '—';
    renderModEnvLabel();
  }

  function renderModEnvLabel() {
    const env = state.modEnv || [];
    const label = $('modEnvLabel');
    if (!env.length) {
      label.textContent = serverDefaultMods().length ? `原版 + 全服默认` : '原版';
    } else {
      label.textContent = env.map(modName).join('、');
    }
  }

  async function applyRulesPatch(patch, okMsg, onError) {
    try {
      const r = await api('apply-rules', { method: 'POST', json: patch });
      if (state.ctx) {
        state.ctx.rules = r.rules || state.ctx.rules;
        state.ctx.currentSeedText = r.currentSeedText === undefined ? state.ctx.currentSeedText : r.currentSeedText;
      }
      await refreshContextQuietly(true);
      if (okMsg) toast(okMsg);
      return r;
    } catch (e) {
      if (onError) onError(e); else showError(e, '保存设置失败');
      return null;
    }
  }

  async function resetOpponentsToRandom() {
    const r = await applyRulesPatch({ ghostOpponentMode: 'Random' }, '下一局起对手改回全随机');
    if (r) { renderGameStrip(); renderLineupList(); if (state.editor) renderEditor(); }
  }

  // ════════════════ 页签 ════════════════
  function switchTab(name) {
    document.querySelectorAll('.tab-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
    document.querySelectorAll('.tab-panel').forEach((p) => p.classList.toggle('active', p.id === `tab-${name}`));
    if (name === 'share') { renderExportSources(); loadMine(); }
    if (name === 'seeds') { ensureFinderInit(); }
  }

  // ════════════════ 编排用 Mod 环境 ════════════════
  function openModEnvModal() {
    const mods = (state.ctx && state.ctx.mods) || [];
    const chosen = new Set(state.modEnv || []);
    const list = h('div', { class: 'pick-list' });
    if (!mods.length) {
      list.appendChild(h('div', { class: 'loading-line', text: '服务器上没有可选的 Mod，当前就是原版环境。' }));
    }
    for (const m of mods) {
      const cb = h('input', { type: 'checkbox', checked: m.isServerDefault || chosen.has(m.modId), disabled: m.isServerDefault });
      cb.addEventListener('change', () => { if (cb.checked) chosen.add(m.modId); else chosen.delete(m.modId); });
      list.appendChild(h('label', { class: `pick-row ${m.isServerDefault ? '' : ''}` },
        cb,
        h('div', { class: 'pr-main' },
          h('div', { class: 'pr-name', text: `${m.name} v${m.version || '?'}` }),
          h('div', { class: 'pr-sub', text: [m.author ? `作者 ${m.author}` : '', m.isGameplay ? '玩法 Mod' : '外观 Mod', m.modId].filter(Boolean).join(' · ') })),
        m.isServerDefault ? h('span', { class: 'badge badge-blue', text: '全服默认，始终开启' }) : null));
    }
    openModal({
      title: '编排时可以使用哪些 Mod',
      body: [
        h('p', { class: 'muted', text: '这里只决定编排方案、搜镜像、看种子日历时能用哪些 Mod 的卡牌和事件，不会改你的游戏设置。方案保存时会记下用到的 Mod；把方案设为下一局对手时，如果你的游戏设置里没开这些 Mod，会提示你一起开启。' }),
        list,
      ],
      footer: [
        { label: '取消', kind: 'btn-subtle' },
        {
          label: '确定', kind: 'btn-gold', onClick: () => {
            const next = [...chosen].filter((id) => !(modsById().get(id) || {}).isServerDefault);
            const changed = JSON.stringify([...next].sort()) !== JSON.stringify([...(state.modEnv || [])].sort());
            state.modEnv = next;
            renderModEnvLabel();
            if (changed) {
              state.eventsCache.clear();
              if (state.editor) revalidateAllDays();
              toast('编排环境已切换');
            }
          },
        },
      ],
    });
  }

  // ════════════════ Tab 1：方案列表 ════════════════
  function renderLineupList() {
    const box = $('lineupList');
    clear(box);
    $('lineupCount').textContent = `${state.lineups.length}/${state.limits.maxLineups}`;
    if (!state.lineups.length) {
      box.appendChild(h('div', { class: 'loading-line', text: '还没有方案。点上面的「+ 新建」，或者「⤓ 从对局生成」把打过的一局存下来。' }));
      return;
    }
    const editingId = state.editor && state.editor.lineupId;
    for (const l of state.lineups) {
      const badges = [];
      if (l.isSelected) badges.push(h('span', { class: 'badge badge-green', text: '下一局使用中' }));
      if (l.missingMods && l.missingMods.length) badges.push(h('span', { class: 'badge badge-red', text: '缺 Mod' }));
      const item = h('div', {
        class: `lineup-item ${editingId === l.lineupId ? 'active' : ''}`,
        onclick: () => openLineup(l.lineupId),
        title: l.description || '',
      },
        h('div', { class: 'li-head' }, h('span', { class: 'li-name', text: l.name || '未命名方案' }), ...badges),
        h('div', { class: 'li-meta' },
          h('span', { text: `${l.dayNumbers.length} 天有对手` }),
          h('span', { text: '·' }),
          h('span', { text: SOURCE_ZH[l.sourceType] || '手搓' }),
          l.seedText ? h('span', { class: 'mono', text: `· ${l.seedText}` }) : null,
          l.updatedAtUtc ? h('span', { text: `· ${fmtTime(l.updatedAtUtc)}` }) : null),
        h('div', { class: 'li-days' }, l.dayNumbers.slice(0, 15).map((d) => h('span', { class: 'li-day', text: d })),
          l.dayNumbers.length > 15 ? h('span', { class: 'li-day', text: '…' }) : null));
      box.appendChild(item);
    }
  }

  async function guardDirty() {
    if (!state.editor || !state.editor.dirty) return true;
    return confirmDialog('有修改还没保存', `方案「${state.editor.name || '未命名'}」有未保存的修改，要放弃这些修改吗？`, '放弃修改', 'btn-danger', '继续编辑');
  }

  let openSeq = 0;
  async function openLineup(lineupId) {
    if (state.editor && state.editor.lineupId === lineupId) return;
    if (!(await guardDirty())) return;
    const mySeq = ++openSeq;
    const ed = $('lineupEditor');
    clear(ed).appendChild(h('div', { class: 'loading-line', text: '正在读取方案...' }));
    try {
      const r = await api(`lineups/${encodeURIComponent(lineupId)}`);
      if (mySeq !== openSeq) return; // 期间又点了别的方案
      state.editor = editorFromFull(r.lineup);
      renderEditor();
      renderLineupList();
      ensureCards(Object.values(state.editor.days));
    } catch (e) {
      if (mySeq !== openSeq) return;
      showError(e, '读取方案失败');
      state.editor = null;
      renderEditor();
      renderLineupList();
    }
  }

  function editorFromFull(l) {
    const days = {};
    for (const [k, v] of Object.entries(l.days || {})) {
      const d = parseInt(k, 10);
      if (d >= 1 && d <= state.limits.maxLineupDays && v) days[d] = normBoard(v, d);
    }
    const firstDay = Object.keys(days).map(Number).sort((a, b) => a - b)[0] || 1;
    return {
      lineupId: l.lineupId,
      name: l.name || '',
      description: l.description || '',
      days,
      requiredModIds: l.requiredModIds || [],
      missingMods: l.missingMods || [],
      sourceType: l.sourceType || 'custom',
      seedText: l.seedText || null,
      seedHero: l.seedHero || null,
      autoDays: new Set(),          // 这次「从对局生成」时自动补的天（只在本页提示用，改过那天就去掉标记）
      dirty: false,
      selectedDay: firstDay,
      selectedItem: -1,
      selectedSkill: -1,
      showAllDays: firstDay > VISIBLE_DAYS_DEFAULT || Object.keys(days).some((d) => Number(d) > VISIBLE_DAYS_DEFAULT),
      validation: {},
      editVersion: {},
    };
  }

  /** 补齐数组字段，避免服务端 / 旧数据缺字段时界面报错。 */
  function normBoard(b, day) {
    b.day = day;
    b.items = Array.isArray(b.items) ? b.items : [];
    b.skills = Array.isArray(b.skills) ? b.skills : [];
    b.socketEffects = Array.isArray(b.socketEffects) ? b.socketEffects : [];
    for (const it of b.items) it.slot = clamp(parseInt(it.slot, 10) || 0, 0, 9);
    for (const se of b.socketEffects) se.slot = clamp(parseInt(se.slot, 10) || 0, 0, 9);
    b.socketEffects.sort((a, c) => a.slot - c.slot);
    return b;
  }

  /** 给阵容里的卡补齐卡牌资料（有哪些品阶、能上哪些附魔、各品阶基础数值），拿到后刷新当前面板。 */
  const cardFetchInFlight = new Set();
  async function ensureCards(boards) {
    const need = new Set();
    for (const b of boards) {
      if (!b) continue;
      for (const x of [...(b.items || []), ...(b.skills || [])]) {
        if (x.templateId && !state.cardCache.has(x.templateId) && !cardFetchInFlight.has(x.templateId)) need.add(x.templateId);
      }
    }
    if (!need.size) return;
    const ids = [...need];
    ids.forEach((id) => cardFetchInFlight.add(id));
    try {
      for (let i = 0; i < ids.length; i += 100) {
        const chunk = ids.slice(i, i + 100);
        const r = await api('catalog/cards', { query: { ids: chunk, limit: chunk.length, selectedModIds: state.modEnv || [] } });
        cacheCards(r.items);
        for (const id of chunk) if (!state.cardCache.has(id)) state.cardCache.set(id, null); // 服务器不认识的卡：不再重复请求
      }
    } catch { /* 拿不到卡牌资料就用默认（全部品阶 / 全部附魔），保存时服务器还会再检查 */ }
    finally { ids.forEach((id) => cardFetchInFlight.delete(id)); }
    if (state.editor && state.editor.days[state.editor.selectedDay]) renderDayPanel();
  }

  async function newLineup() {
    if (state.lineups.length >= state.limits.maxLineups) {
      toast(`方案最多 ${state.limits.maxLineups} 套，请先删掉不用的`, 'warn');
      return;
    }
    if (!(await guardDirty())) return;
    openSeq++;
    state.editor = {
      lineupId: null, name: '新方案', description: '', days: {}, requiredModIds: [], missingMods: [],
      sourceType: 'custom', seedText: null, seedHero: null, autoDays: new Set(),
      dirty: true, selectedDay: 1, selectedItem: -1, selectedSkill: -1, showAllDays: false, validation: {}, editVersion: {},
    };
    renderEditor();
    renderLineupList();
    const ni = document.querySelector('#lineupEditor .name-input');
    if (ni) { ni.focus(); ni.select(); }
  }

  function markDirty() {
    if (!state.editor) return;
    state.editor.dirty = true;
    const dot = document.querySelector('#lineupEditor .dirty-dot');
    if (dot) { dot.classList.add('dirty'); dot.title = '有未保存的修改'; }
    const sb = $('btnSaveLineup');
    if (sb) updateSaveButton();
  }

  function dayHasError(d) {
    const v = state.editor && state.editor.validation[d];
    return !!(v && v.ok === false && v.errors && v.errors.length);
  }
  function anyDayHasError() {
    return Object.keys(state.editor.days).some((d) => dayHasError(Number(d)));
  }
  function anyDayChecking() {
    return Object.keys(state.editor.days).some((d) => {
      const v = state.editor.validation[Number(d)];
      return v && v.checking;
    });
  }
  function updateSaveButton() {
    const sb = $('btnSaveLineup');
    if (!sb || !state.editor) return;
    const bad = anyDayHasError();
    sb.disabled = bad || !state.editor.name.trim();
    sb.title = bad ? '有天的对手存在问题（红色感叹号），修好后才能保存' : (!state.editor.name.trim() ? '先给方案起个名字' : '保存方案');
  }

  // ── 编辑器主体 ──
  function renderEditor() {
    const ed = $('lineupEditor');
    clear(ed);
    const e = state.editor;
    if (!e) {
      ed.appendChild(h('div', { class: 'empty-state' },
        h('div', { class: 'es-icon', text: '🗓️' }),
        h('h3', { text: '选择左边的方案，或新建一个' }),
        h('p', { class: 'muted', text: '方案里每一天都可以安排一个固定对手：从镜像池里挑真实玩家的阵容，或者自己 10 格手搓。' }),
        h('div', { class: 'btn-row' }, h('button', { type: 'button', class: 'btn btn-gold', onclick: newLineup }, '+ 新建方案'))));
      return;
    }

    const summary = e.lineupId ? state.lineups.find((l) => l.lineupId === e.lineupId) : null;
    const isSelected = !!(summary && summary.isSelected);

    const nameInput = h('input', { type: 'text', class: 'name-input', maxlength: state.limits.maxLineupName, placeholder: '给方案起个名字', value: e.name });
    nameInput.addEventListener('input', () => { e.name = nameInput.value; markDirty(); updateSaveButton(); });

    const head = h('div', { class: 'editor-head' },
      h('span', { class: `dirty-dot ${e.dirty ? 'dirty' : ''}`, title: e.dirty ? '有未保存的修改' : '已保存' }),
      nameInput,
      h('button', { type: 'button', class: 'btn btn-gold btn-sm', id: 'btnSaveLineup', onclick: saveLineup }, '保存'),
      isSelected && !e.dirty
        ? h('span', { class: 'badge badge-green', text: '✓ 下一局使用中' })
        : h('button', {
          type: 'button', class: 'btn btn-outline btn-sm', onclick: useLineupForNextRun,
          title: e.seedText ? `保存，并让下一局用这个方案：种子 ${e.seedText} + 安排好的对手` : '保存，并让下一局按这个方案安排对手',
        }, e.seedText ? '▶ 下一局用它（种子+对手）' : '▶ 下一局用它'),
      h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: shareLineup, title: '生成分享码：种子 + 每天的对手一起带走' }, '🔗 分享'),
      h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: duplicateLineup, title: '复制成一个新方案' }, '复制'),
      e.lineupId
        ? h('button', { type: 'button', class: 'btn btn-danger btn-sm', onclick: deleteLineup }, '删除')
        : h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: discardNewLineup, title: '这个新方案还没保存过，放弃它' }, '放弃'));

    const descInput = h('input', { type: 'text', class: 'editor-desc', maxlength: state.limits.maxDescription, placeholder: '备注（可选），例如：前 5 天全是低血对手，练运营用', value: e.description });
    descInput.addEventListener('input', () => { e.description = descInput.value; markDirty(); });

    ed.appendChild(head);
    ed.appendChild(descInput);
    ed.appendChild(h('div', { class: 'seed-row' },
      h('span', { class: 'seed-row-label', text: '配套种子' }),
      e.seedText
        ? h('span', null, h('b', { class: 'mono seed-chip', text: e.seedText }), h('span', { class: 'muted', text: e.seedHero ? `（${heroName(e.seedHero)}）` : '' }))
        : h('span', { class: 'muted', text: '没配 —— 分享时会用你「下一局的种子」' }),
      h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: editLineupSeed }, e.seedText ? '换一个' : '配一个'),
      e.seedText ? h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => { e.seedText = null; e.seedHero = null; markDirty(); renderEditor(); } }, '去掉') : null,
      h('span', { class: 'muted seed-row-note', text: '方案 = 种子 + 每天的对手，分享时一起带走' })));

    if (e.missingMods && e.missingMods.length) {
      ed.appendChild(h('div', { class: 'err-box' },
        `这个方案用到的 Mod 在服务器上已经没有了：${e.missingMods.map((m) => m.name || m.modId).join('、')}。用到这些 Mod 的卡牌在开局时会被去掉，如果某天整套阵容都没了，那天就改成随机对手。`));
    }
    if (isSelected && state.ctx && state.ctx.activeRun && !state.ctx.activeRun.isFinished) {
      ed.appendChild(h('div', { class: 'hint-box' }, '你有进行中的对局：它开局时已经锁定了当时的方案，这里的修改从下一局开始生效。'));
    }

    // 时间轴
    const maxDays = state.limits.maxLineupDays;
    const visible = e.showAllDays ? maxDays : VISIBLE_DAYS_DEFAULT;
    const tl = h('div', { class: 'day-timeline' });
    for (let d = 1; d <= visible; d++) tl.appendChild(renderDayChip(d));
    ed.appendChild(h('div', { class: 'board-title' },
      h('span', null, '每天的对手（点一天开始编辑；空着的天开局时随机匹配）'),
      h('span', { text: `已安排 ${Object.keys(e.days).length} 天` })));
    ed.appendChild(tl);
    if (maxDays > VISIBLE_DAYS_DEFAULT) {
      ed.appendChild(h('div', { class: 'timeline-more' },
        h('button', {
          type: 'button', class: 'btn btn-subtle btn-xs',
          onclick: () => { e.showAllDays = !e.showAllDays; if (!e.showAllDays && e.selectedDay > VISIBLE_DAYS_DEFAULT) e.selectedDay = 1; renderEditor(); },
        }, e.showAllDays ? `收起第 ${VISIBLE_DAYS_DEFAULT + 1}–${maxDays} 天` : `显示第 ${VISIBLE_DAYS_DEFAULT + 1}–${maxDays} 天（加时 / 无尽）`)));
    }

    ed.appendChild(h('div', { id: 'dayPanel' }));
    renderDayPanel();
    updateSaveButton();
  }

  function renderDayChip(d) {
    const e = state.editor;
    const b = e.days[d];
    const auto = !!(b && e.autoDays && e.autoDays.has(d));
    const chip = h('button', {
      type: 'button',
      class: `day-chip ${b ? 'filled' : ''} ${auto ? 'auto' : ''} ${e.selectedDay === d ? 'active' : ''} ${dayHasError(d) ? 'has-error' : ''}`,
      onclick: () => { e.selectedDay = d; e.selectedItem = -1; e.selectedSkill = -1; refreshTimeline(); renderDayPanel(); },
      title: b ? `${heroName(b.hero)} · ${b.healthMax} 血 · ${b.items.length} 件物品${auto ? '\n自动从镜像池补的，可以换' : ''}` : '这天没安排，开局时随机匹配',
    },
      auto ? h('span', { class: 'dc-auto', text: '补' }) : null,
      h('div', { class: 'dc-day', text: `第${d}天` }),
      h('div', { class: 'dc-hero', text: b ? (b.displayName || heroName(b.hero)) : '随机' }),
      h('div', { class: 'dc-sub', text: b ? `${b.healthMax}血 · ${b.items.length}件` : '—' }));
    return chip;
  }

  function refreshTimeline() {
    const tl = document.querySelector('#lineupEditor .day-timeline');
    if (!tl || !state.editor) return;
    const visible = state.editor.showAllDays ? state.limits.maxLineupDays : VISIBLE_DAYS_DEFAULT;
    clear(tl);
    for (let d = 1; d <= visible; d++) tl.appendChild(renderDayChip(d));
    const count = document.querySelector('#lineupEditor .board-title span:last-child');
    if (count) count.textContent = `已安排 ${Object.keys(state.editor.days).length} 天`;
  }

  function suggestedBoard(day, hero) {
    return {
      boardId: '', day, hero: hero || 'Vanessa',
      level: clamp(day + 1, 1, state.editRules.levelMax),
      healthMax: Math.round(300 + (day - 1) * 110),
      prestige: 20, wins: Math.max(0, day - 1), losses: 0,
      unlockedSlotsMask: 0, powerBucket: 'Normal', source: 'custom', isPlayerGhost: false, poolPartition: 'standard',
      displayName: null, items: [], skills: [], socketEffects: [],
    };
  }

  function prevHero(day) {
    const e = state.editor;
    for (let d = day - 1; d >= 1; d--) if (e.days[d]) return e.days[d].hero;
    for (let d = day + 1; d <= state.limits.maxLineupDays; d++) if (e.days[d]) return e.days[d].hero;
    return state.heroes[0] ? state.heroes[0].id : 'Vanessa';
  }

  function setDayBoard(day, board) {
    const e = state.editor;
    e.editVersion[day] = (e.editVersion[day] || 0) + 1; // 让还在路上的旧校验结果作废
    if (e.autoDays) e.autoDays.delete(day);
    e.selectedSkill = -1;
    if (board) {
      e.days[day] = normBoard(board, day);
      ensureCards([board]);
    } else {
      delete e.days[day];
      delete e.validation[day];
    }
    e.selectedItem = -1;
    markDirty();
    refreshTimeline();
    renderDayPanel();
    if (board) scheduleValidate(day);
  }

  // ── 单天面板 ──
  /** 重绘单天面板，并把光标放回原来的输入框（后台校验结果回来时不打断正在输入的内容）。 */
  function renderDayPanel() {
    const panel = $('dayPanel');
    const act = document.activeElement;
    let fk = null; let val = null; let selStart = null; let selEnd = null;
    if (act && panel && panel.contains(act) && act.dataset && act.dataset.fk) {
      fk = act.dataset.fk;
      if (act.tagName === 'INPUT') val = act.value;
      try { selStart = act.selectionStart; selEnd = act.selectionEnd; } catch { /* number 输入框不支持读取光标位置 */ }
    }
    renderDayPanelInner();
    if (!fk) return;
    const p2 = $('dayPanel');
    const again = p2 && p2.querySelector(`[data-fk="${CSS.escape(fk)}"]`);
    if (!again) return;
    if (val !== null && again.tagName === 'INPUT') again.value = val;
    again.focus();
    try { if (selStart !== null && selStart !== undefined) again.setSelectionRange(selStart, selEnd); } catch { /* 同上 */ }
  }

  function renderDayPanelInner() {
    const panel = $('dayPanel');
    if (!panel || !state.editor) return;
    clear(panel);
    const e = state.editor;
    const d = e.selectedDay;
    const b = e.days[d];
    const wrap = h('div', { class: 'day-panel' });
    panel.appendChild(wrap);

    const actions = h('div', { class: 'btn-row', style: { marginTop: '0' } },
      h('button', { type: 'button', class: 'btn btn-gold btn-sm', onclick: () => openPoolPicker(d) }, '🔍 从镜像池挑一个'),
      h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: () => handcraftDay(d) }, '✋ 手搓新对手'));
    if (b) {
      actions.appendChild(h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: () => openCopyToDays(d) }, '复制到其他天'));
      actions.appendChild(h('button', { type: 'button', class: 'btn btn-danger btn-sm', onclick: () => clearDay(d) }, '清空这天'));
    }
    wrap.appendChild(h('div', { class: 'day-panel-head' }, h('h3', { text: `第 ${d} 天晚上的 PvP 对手` }), actions));
    if (b && e.autoDays && e.autoDays.has(d)) {
      wrap.appendChild(h('div', { class: 'hint-box auto-hint', text: '这天是自动从镜像池补的（那局没打到这天）。看着不合适就点「从镜像池挑一个」换掉；改动过就不算「补」的了。' }));
    }

    if (!b) {
      const copyPrev = e.days[d - 1];
      wrap.appendChild(h('div', { class: 'empty-state', style: { padding: '24px 10px' } },
        h('div', { class: 'es-icon', text: '🎲' }),
        h('h3', { text: `第 ${d} 天还没安排对手` }),
        h('p', { class: 'muted', text: '不安排的话，开局时这一天会像平常一样随机匹配一个对手。' }),
        copyPrev ? h('div', { class: 'btn-row' },
          h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: () => setDayBoard(d, deepClone(copyPrev)) }, `沿用第 ${d - 1} 天的对手`)) : null));
      return;
    }

    // 基础数值
    const heroSel = h('select', { dataset: { fk: 'hero' } }, state.heroes.map((x) => h('option', { value: x.id, selected: x.id === b.hero }, x.nameZh)));
    heroSel.addEventListener('change', () => { b.hero = heroSel.value; onBoardEdited(d, { rerenderChip: true }); });
    const nameIn = h('input', { type: 'text', dataset: { fk: 'name' }, maxlength: state.limits.maxDisplayName, placeholder: `留空显示「${heroName(b.hero)}」`, value: b.displayName || '' });
    nameIn.addEventListener('input', () => { b.displayName = nameIn.value.trim() ? nameIn.value : null; onBoardEdited(d, { rerenderChip: true }); });
    const numIn = (key, min, max) => {
      const inp = h('input', { type: 'number', dataset: { fk: key }, min, max, step: 1, value: b[key] });
      inp.addEventListener('change', () => {
        let v = parseInt(inp.value, 10);
        if (!Number.isFinite(v)) v = b[key];
        v = clamp(v, min, max);
        inp.value = v;
        b[key] = v;
        onBoardEdited(d, { rerenderChip: key === 'healthMax' });
      });
      return inp;
    };
    wrap.appendChild(h('div', { class: 'stat-row' },
      h('label', { class: 'field' }, h('span', { text: '英雄' }), heroSel),
      h('label', { class: 'field' }, h('span', { text: '名牌（对手名字）' }), nameIn),
      h('label', { class: 'field' }, h('span', { text: '最大生命' }), numIn('healthMax', state.editRules.healthMin, state.editRules.healthMax)),
      h('label', { class: 'field' }, h('span', { text: '等级' }), numIn('level', 1, state.editRules.levelMax)),
      h('label', { class: 'field' }, h('span', { text: '声望' }), numIn('prestige', 1, state.editRules.prestigeMax))));

    // 10 格棋盘与下方插槽特效条
    const used = b.items.reduce((s, it) => s + itemWidth(it), 0);
    wrap.appendChild(h('div', { class: 'board-title' },
      h('span', null, '物品（点空格添加，拖动换位置；带斜纹的格子这一天本来没解锁，放了东西或插槽特效会自动解锁）'),
      h('span', { text: `占用 ${used}/10 格` })));
    wrap.appendChild(renderBoard(d, b));
    wrap.appendChild(renderSocketStrip(d, b));

    // 技能（点一下在下面看效果、改品阶）
    const skillsRow = h('div', { class: 'skills-row' }, h('span', { class: 'muted', text: '技能（点开看效果）：' }));
    (b.skills || []).forEach((sk, idx) => {
      const chip = h('span', {
        class: `skill-chip tier-${sk.tier || 'Bronze'} ${e.selectedSkill === idx ? 'selected' : ''}`,
        title: '点开看效果、改品阶',
      },
        h('span', {
          style: { cursor: 'pointer' },
          onclick: () => { e.selectedSkill = e.selectedSkill === idx ? -1 : idx; e.selectedItem = -1; renderDayPanel(); },
        }, h('span', { class: 'tier-text', text: tierZh(sk.tier) }), ' ', sk.zhName || sk.internalName || '技能'),
        h('button', { type: 'button', class: 'x', title: '移除技能', onclick: () => { b.skills.splice(idx, 1); e.selectedSkill = -1; onBoardEdited(d, { rerenderPanel: true }); } }, '×'));
      skillsRow.appendChild(chip);
    });
    if ((b.skills || []).length < 20) {
      skillsRow.appendChild(h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => openCardPicker(d, 'Skill') }, '+ 技能'));
    }
    wrap.appendChild(skillsRow);

    // 物品 / 技能详情
    if (e.selectedItem >= 0 && b.items[e.selectedItem]) wrap.appendChild(renderInspector(d, b, e.selectedItem));
    else if (e.selectedSkill >= 0 && b.skills[e.selectedSkill]) wrap.appendChild(renderSkillInspector(d, b, e.selectedSkill));

    // 校验结果
    wrap.appendChild(renderValidation(d));
  }

  // ── 开关 / 微型 / 任务 / 标签 / 插槽特效辅助 ──
  function lookupNumCaseInsensitive(obj, key) {
    if (!obj || typeof obj !== 'object' || !key) return undefined;
    if (obj[key] !== undefined) return obj[key];
    const kl = String(key).toLowerCase();
    for (const [k, v] of Object.entries(obj)) {
      if (k.toLowerCase() === kl) return v;
    }
    return undefined;
  }
  function hasTagCaseInsensitive(tags, tag) {
    if (!Array.isArray(tags) || !tag) return false;
    const tl = String(tag).toLowerCase();
    return tags.some((t) => String(t).toLowerCase() === tl);
  }
  function hasNativeToggle(card, key) {
    if (!card || !Array.isArray(card.nativeToggles) || !key) return false;
    const kl = String(key).toLowerCase();
    return card.nativeToggles.some((t) => String(t).toLowerCase() === kl);
  }
  /** 开关状态读取优先级：editedToggles -> card.nativeToggles -> permanentAdd/permanentAttributes（Tiny 额外兼容历史 extraTags）。 */
  function isToggleOn(it, key, cardOverride) {
    if (!it || !key) return false;
    const card = cardOverride !== undefined ? cardOverride : state.cardCache.get(it.templateId);
    if (it.editedToggles && Object.prototype.hasOwnProperty.call(it.editedToggles, key)) {
      return !!it.editedToggles[key];
    }
    if (hasNativeToggle(card, key)) return true;
    const pAdd = lookupNumCaseInsensitive(it.permanentAdd, key);
    const pAttr = lookupNumCaseInsensitive(it.permanentAttributes, key);
    if (Number(pAdd) > 0 || Number(pAttr) > 0) return true;
    if (String(key).toLowerCase() === 'tiny' && hasTagCaseInsensitive(it.extraTags, 'Tiny')) return true;
    return false;
  }
  function isSmallItem(it, cardOverride) {
    const c = cardOverride !== undefined ? cardOverride : state.cardCache.get(it.templateId);
    if (c && c.size) return c.size === 'Small';
    return !isToggleOn(it, 'Tiny', c) && (parseInt(it.width, 10) || 1) <= 1;
  }
  function itemBaseWidth(it) {
    const c = state.cardCache.get(it.templateId);
    if (c && SIZE_W[c.size]) return SIZE_W[c.size];
    const w = clamp(parseInt(it.width, 10) || 1, 1, 3);
    if (isToggleOn(it, 'Tiny', c) && w < 3) return w + 1;
    return w;
  }
  function itemWidth(it) {
    const c = state.cardCache.get(it.templateId);
    const tiny = isToggleOn(it, 'Tiny', c);
    if (c && SIZE_W[c.size]) {
      const base = SIZE_W[c.size];
      return (tiny && base > 1) ? base - 1 : base;
    }
    return clamp(parseInt(it.width, 10) || 1, 1, 3);
  }
  function completedQuestCount(it) {
    if (!it || !Array.isArray(it.completedQuests) || !it.completedQuests.length) return 0;
    const seen = new Set();
    for (const q of it.completedQuests) {
      if (q && String(q).trim()) seen.add(String(q).trim().toLowerCase());
    }
    return seen.size;
  }
  function hasQuestCompleted(it, attr) {
    if (!it || !Array.isArray(it.completedQuests) || !attr) return false;
    const al = String(attr).toLowerCase();
    return it.completedQuests.some((q) => String(q).toLowerCase() === al);
  }
  function getQuestProgress(it, attr) {
    const v = lookupNumCaseInsensitive(it && it.questProgress, attr);
    return Number.isFinite(Number(v)) ? Math.max(0, Math.round(Number(v))) : 0;
  }
  function setQuestCompletedFlag(it, attr, completed) {
    const al = String(attr).toLowerCase();
    const list = Array.isArray(it.completedQuests)
      ? it.completedQuests.filter((q) => String(q).toLowerCase() !== al)
      : [];
    if (completed) list.push(attr);
    it.completedQuests = list;
  }
  function setQuestProgressVal(it, attr, val) {
    const al = String(attr).toLowerCase();
    const next = {};
    if (it.questProgress && typeof it.questProgress === 'object') {
      for (const [k, v] of Object.entries(it.questProgress)) {
        if (k.toLowerCase() !== al) next[k] = v;
      }
    }
    if (val > 0) next[attr] = val;
    it.questProgress = next;
  }
  function getItemExtraTags(it, cardOverride) {
    if (!it || !Array.isArray(it.extraTags)) return [];
    const card = cardOverride !== undefined ? cardOverride : state.cardCache.get(it.templateId);
    const nativeLower = new Set((card && Array.isArray(card.tags) ? card.tags : []).map((t) => String(t).toLowerCase()));
    const seen = new Set();
    const out = [];
    for (const raw of it.extraTags) {
      const s = String(raw || '').trim();
      if (!s) continue;
      const sl = s.toLowerCase();
      if (sl === 'tiny' || nativeLower.has(sl) || seen.has(sl)) continue;
      seen.add(sl);
      out.push(s);
    }
    return out;
  }
  function findSocketCatalog(templateId) {
    const list = Array.isArray(state.editRules.socketEffects) ? state.editRules.socketEffects : [];
    return list.find((x) => x.templateId === templateId) || null;
  }
  function socketEffectKind(meta) {
    if (!meta) return 'mod';
    const id = String(meta.templateId || '').toLowerCase();
    const s = `${meta.internalName || ''} ${meta.nameZh || ''}`;
    if (id === 'c017c0dd-af3c-47e0-b510-3661d9dcd7f2' || /stove|heat|炉灶|火炉/i.test(s)) return 'stove';
    if (id === 'd5515ae3-b98d-4272-8add-82f441969ffe' || /cooler|chill|冷藏|冰柜/i.test(s)) return 'cooler';
    if (/note|music|音符/i.test(s)) return 'note';
    return 'mod';
  }
  function socketEffectIcon(meta) {
    const k = socketEffectKind(meta);
    if (k === 'stove') return '🔥';
    if (k === 'cooler') return '❄️';
    if (k === 'note') return '🎵';
    return '✦';
  }
  function socketEffectDisplayName(meta) {
    if (!meta) return '未知插槽特效';
    const raw = (meta.nameZh || meta.internalName || meta.templateId || '').trim();
    const icon = socketEffectIcon(meta);
    return raw.startsWith(icon) ? raw : `${icon}${raw}`;
  }
  function socketEffectShortName(meta) {
    if (!meta) return '✦未知';
    const raw = (meta.nameZh || meta.internalName || meta.templateId || '').trim();
    const compact = raw
      .replace(/[（(][^）)]*[）)]/g, '')
      .replace(/^(音符)\s+([A-G])$/i, '$1$2')
      .trim() || raw;
    const icon = socketEffectIcon(meta);
    return compact.startsWith(icon) ? compact : `${icon}${compact}`;
  }
  function baseMaskForDay(day) {
    const base = state.editRules.dayBaseSlots || DEFAULT_EDIT_RULES.dayBaseSlots;
    const n = day >= 4 ? 10 : (base[day] || 10);
    if (n >= 10) return 1023;
    const start = Math.floor((10 - n) / 2);
    let m = 0;
    for (let i = start; i < start + n; i++) m |= 1 << i;
    return m;
  }
  function occupancy(items, ignoreIdx = -1) {
    const occ = new Array(10).fill(-1);
    items.forEach((it, idx) => {
      if (idx === ignoreIdx) return;
      const w = itemWidth(it);
      for (let k = 0; k < w; k++) { const s = it.slot + k; if (s >= 0 && s < 10) occ[s] = idx; }
    });
    return occ;
  }
  function canPlace(items, width, slot, ignoreIdx = -1) {
    if (slot < 0 || slot + width > 10) return false;
    const occ = occupancy(items, ignoreIdx);
    for (let k = 0; k < width; k++) if (occ[slot + k] !== -1) return false;
    return true;
  }
  function nearestFit(items, width, preferred, ignoreIdx = -1) {
    const order = [];
    for (let s = 0; s <= 10 - width; s++) order.push(s);
    order.sort((a, b) => Math.abs(a - preferred) - Math.abs(b - preferred) || a - b);
    return order.find((s) => canPlace(items, width, s, ignoreIdx));
  }
  function longestFreeRun(items) {
    const occ = occupancy(items);
    let best = 0; let cur = 0;
    for (let s = 0; s < 10; s++) { if (occ[s] === -1) { cur++; best = Math.max(best, cur); } else cur = 0; }
    return best;
  }
  function hasHiddenGrowth(it) {
    const nonEmpty = (o) => o && (Array.isArray(o) ? o.length : Object.keys(o).length);
    return nonEmpty(it.permanentAdd) || nonEmpty(it.permanentMult) || nonEmpty(it.completedQuests) || nonEmpty(it.questProgress) || nonEmpty(it.extraTags);
  }

  let dragIdx = -1;
  let dragGrab = 0; // 拖的是物品的第几格（宽物品抓中间拖时，落点按抓的位置对齐）
  function renderBoard(day, b) {
    const board = h('div', { class: 'board' });
    const base = baseMaskForDay(day);
    const occ = occupancy(b.items);
    const seBySlot = new Map();
    for (const se of (b.socketEffects || [])) {
      const sl = clamp(parseInt(se.slot, 10) || 0, 0, 9);
      if (!seBySlot.has(sl)) seBySlot.set(sl, se);
    }
    const slots = [];
    const clearMarks = () => slots.forEach((x) => x.classList.remove('drop-ok', 'drop-bad'));
    for (let s = 0; s < 10; s++) {
      const locked = !(base & (1 << s));
      const se = seBySlot.get(s) || null;
      const seMeta = se ? (findSocketCatalog(se.templateId) || se) : null;
      const seKind = se ? socketEffectKind(seMeta) : '';
      const seDesc = seMeta && seMeta.descriptionZh ? `：${seMeta.descriptionZh}` : '';
      const slot = h('div', {
        class: `slot ${locked ? 'locked' : ''} ${se ? `has-se se-${seKind}` : ''}`,
        style: { gridColumn: `${s + 1} / span 1` },
        title: [
          locked ? `第 ${s + 1} 格：这一天本来没解锁，放物品或插槽特效会自动解锁` : `第 ${s + 1} 格：点击放一件物品`,
          se ? `插槽特效：${socketEffectDisplayName(seMeta)}${seDesc}` : '',
        ].filter(Boolean).join('\n'),
        onclick: () => { if (occ[s] === -1) openCardPicker(day, 'Item', s); },
      }, h('span', { class: 'slot-plus', text: '+' }), `${s + 1}`);
      slot.addEventListener('dragover', (ev) => {
        if (dragIdx < 0) return;
        ev.preventDefault();
        const it = b.items[dragIdx];
        if (!it) return;
        const w = itemWidth(it);
        const t = s - dragGrab;
        const ok = canPlace(b.items, w, t, dragIdx);
        clearMarks();
        for (let k = 0; k < w; k++) { const c = slots[t + k]; if (c) c.classList.add(ok ? 'drop-ok' : 'drop-bad'); }
        if (!ok) slot.classList.add('drop-bad');
        ev.dataTransfer.dropEffect = ok ? 'move' : 'none';
      });
      slot.addEventListener('drop', (ev) => {
        ev.preventDefault();
        clearMarks();
        const idx = dragIdx;
        dragIdx = -1;
        const it = b.items[idx];
        if (!it) return;
        const t = s - dragGrab;
        if (!canPlace(b.items, itemWidth(it), t, idx)) { toast('放不下：会和别的物品重叠，或者超出了 10 个格子', 'warn'); return; }
        if (t === it.slot) return;
        it.slot = t;
        b.items.sort((x, y) => x.slot - y.slot);
        state.editor.selectedItem = b.items.indexOf(it);
        onBoardEdited(day, { rerenderPanel: true });
      });
      slots.push(slot);
      board.appendChild(slot);
      if (se) {
        // 底边高亮条置于卡片上方且不拦截鼠标事件，确保格上有物品（含宽物品）时仍清晰可见
        board.appendChild(h('div', {
          class: `slot-se-bar se-${seKind}`,
          style: { gridColumn: `${s + 1} / span 1` },
        }, h('span', { class: 'slot-se-pip', text: socketEffectIcon(seMeta) })));
      }
    }
    board.addEventListener('dragleave', (ev) => { if (!board.contains(ev.relatedTarget)) clearMarks(); });
    b.items.forEach((it, idx) => {
      const w = itemWidth(it);
      const attrs = summarizeAttrs(it);
      const isTiny = isToggleOn(it, 'Tiny');
      const qCount = completedQuestCount(it);
      const badges = [];
      if (isTiny) badges.push(h('span', { class: 'it-badge it-badge-tiny', title: '微型：占格缩小 1 格', text: '微型' }));
      if (qCount > 0) badges.push(h('span', { class: 'it-badge it-badge-quest', title: `已完成 ${qCount} 个任务`, text: `✓任务×${qCount}` }));
      const hidden = hasHiddenGrowth(it);
      const titleParts = [
        `${it.zhName || it.internalName || it.templateId}（${tierZh(it.tier)}${isTiny ? ' · 微型' : ''}${qCount > 0 ? ` · ✓任务×${qCount}` : ''}）`,
        '点击编辑，拖动换位置',
      ];
      const tile = h('div', {
        class: `item-tile tier-${it.tier || 'Bronze'} ${state.editor.selectedItem === idx ? 'selected' : ''} ${isTiny ? 'is-tiny' : ''} ${hidden ? 'has-hidden' : ''}`,
        style: { gridColumn: `${clamp(it.slot, 0, 9) + 1} / span ${w}` },
        draggable: 'true',
        title: titleParts.join('\n'),
        onclick: () => { state.editor.selectedItem = idx; state.editor.selectedSkill = -1; renderDayPanel(); },
      },
        hidden ? h('span', { class: 'it-hidden', title: '带有成长 / 任务 / 永久加成，未修改时原样保留', text: '★' }) : null,
        h('div', { class: 'it-name', text: it.zhName || it.internalName || '物品' }),
        h('div', { class: 'it-tier', text: tierZh(it.tier) }),
        it.enchantment ? h('div', { class: 'it-ench', text: ENCH_ZH[it.enchantment] || it.enchantment }) : null,
        badges.length ? h('div', { class: 'it-badges' }, ...badges) : null,
        attrs ? h('div', { class: 'it-attrs', text: attrs }) : null,
        cooldownText(it) ? h('div', { class: 'it-cd', text: `⏱ ${cooldownText(it)}` }) : null);
      tile.addEventListener('dragstart', (ev) => {
        dragIdx = idx;
        const rect = tile.getBoundingClientRect();
        dragGrab = clamp(Math.floor((ev.clientX - rect.left) / ((rect.width / w) || 1)), 0, w - 1);
        tile.classList.add('dragging');
        ev.dataTransfer.effectAllowed = 'move';
        try { ev.dataTransfer.setData('text/plain', String(idx)); } catch { /* 部分浏览器限制 */ }
        // 拖起来以后让物品对鼠标“透明”，才能放到和它原位置重叠的格子上（比如右移一格）
        setTimeout(() => board.classList.add('drag-active'), 0);
      });
      tile.addEventListener('dragend', () => {
        tile.classList.remove('dragging');
        board.classList.remove('drag-active');
        dragIdx = -1;
        clearMarks();
      });
      board.appendChild(tile);
    });
    return board;
  }

  /** 10 格棋盘下方的插槽特效条：与 1–10 格垂直对齐，支持配置炉灶 / 冷藏箱 / 音符 A–G 等。 */
  function renderSocketStrip(day, b) {
    const catalogList = Array.isArray(state.editRules.socketEffects) ? state.editRules.socketEffects : [];
    const curList = Array.isArray(b.socketEffects) ? b.socketEffects : [];
    if (!catalogList.length && !curList.length) return h('div', { class: 'hidden' });

    const seBySlot = new Map();
    for (const se of curList) {
      const sl = clamp(parseInt(se.slot, 10) || 0, 0, 9);
      if (!seBySlot.has(sl)) seBySlot.set(sl, se);
    }

    const wrap = h('div', { class: 'socket-strip-wrap' });
    const headRight = h('div', { class: 'socket-strip-actions' },
      curList.length ? h('span', { class: 'muted', text: `已配置 ${curList.length} 格` }) : h('span', { class: 'muted', text: '全部为空' }),
      curList.length ? h('button', {
        type: 'button', class: 'btn btn-subtle btn-xs',
        onclick: () => { b.socketEffects = []; onBoardEdited(day, { rerenderPanel: true }); },
      }, '清空插槽') : null);
    wrap.appendChild(h('div', { class: 'socket-strip-head' },
      h('span', { class: 'muted', text: '插槽特效（对应上方第 1–10 格：炉灶 / 冷藏箱 / 音符）' }),
      headRight));

    const strip = h('div', { class: 'socket-strip' });
    const activeSummaries = [];
    for (let s = 0; s < 10; s++) {
      const curSe = seBySlot.get(s) || null;
      const curMeta = curSe ? findSocketCatalog(curSe.templateId) : null;
      const curKind = curSe ? socketEffectKind(curMeta || curSe) : '';
      const sel = h('select', {
        class: `socket-select ${curSe ? `active se-${curKind}` : ''}`,
        dataset: { fk: `socket-${s}` },
        'aria-label': `第 ${s + 1} 格插槽特效`,
        title: curSe
          ? `第 ${s + 1} 格：${socketEffectDisplayName(curMeta || curSe)}${curMeta && curMeta.descriptionZh ? `\n${curMeta.descriptionZh}` : ''}`
          : `第 ${s + 1} 格插槽特效（无）`,
      },
        h('option', { value: '' }, '—'),
        catalogList.map((opt) => {
          const shortLbl = socketEffectShortName(opt);
          const fullLbl = socketEffectDisplayName(opt);
          return h('option', {
            value: opt.templateId,
            selected: !!(curSe && curSe.templateId === opt.templateId),
            title: opt.descriptionZh ? `${fullLbl}：${opt.descriptionZh}` : fullLbl,
          }, shortLbl);
        }));
      if (curSe && !curMeta) {
        sel.appendChild(h('option', { value: curSe.templateId, selected: true }, `✦ 已有特效`));
      }
      sel.addEventListener('change', () => {
        const nextVal = sel.value;
        b.socketEffects = (b.socketEffects || []).filter((x) => clamp(parseInt(x.slot, 10) || 0, 0, 9) !== s);
        if (nextVal) {
          const meta = findSocketCatalog(nextVal);
          if (meta) {
            b.socketEffects.push({ slot: s, templateId: meta.templateId, tier: meta.defaultTier || 'Diamond' });
          } else if (curSe && curSe.templateId === nextVal) {
            b.socketEffects.push(curSe);
          }
        }
        b.socketEffects.sort((a, c) => a.slot - c.slot);
        onBoardEdited(day, { rerenderPanel: true });
      });
      if (curSe) {
        activeSummaries.push({
          slot: s,
          kind: curKind,
          label: socketEffectDisplayName(curMeta || curSe),
          desc: curMeta && curMeta.descriptionZh ? curMeta.descriptionZh : '',
        });
      }
      strip.appendChild(h('div', { class: 'socket-cell' }, sel));
    }
    wrap.appendChild(strip);

    if (activeSummaries.length) {
      wrap.appendChild(h('div', { class: 'socket-summary-list' },
        activeSummaries.map((item) => h('span', { class: `socket-summary-chip se-${item.kind}` },
          h('b', { text: `第${item.slot + 1}格 ` }),
          item.label,
          item.desc ? `：${item.desc}` : ''))));
    }
    return wrap;
  }

  function currentAttrValue(it, key) {
    if (it.editedAttributes && it.editedAttributes[key] !== undefined) return it.editedAttributes[key];
    if (it.permanentAttributes && it.permanentAttributes[key] !== undefined) return it.permanentAttributes[key];
    const c = state.cardCache.get(it.templateId);
    const base = c && c.baseAttributesByTier && c.baseAttributesByTier[it.tier || 'Bronze'];
    if (base && base[key] !== undefined) return base[key];
    return undefined;
  }
  /** 冷却按秒显示（数据里是毫秒）；被动物品没有冷却返回空串。 */
  function cooldownText(it) {
    const v = currentAttrValue(it, 'CooldownMax');
    if (!(v > 0)) return '';
    return `${Math.round(v / 100) / 10}秒`;
  }
  function hasCooldown(it) {
    const c = state.cardCache.get(it.templateId);
    const base = c && c.baseAttributesByTier && c.baseAttributesByTier[it.tier || 'Bronze'];
    return !!(base && base.CooldownMax > 0);
  }

  // ── 效果说明（服务器按品阶 / 附魔 / 当前数值 / 任务 / 状态开关 / 附加标签渲染官方中文说明，带缓存）──
  const describeCache = new Map();
  function describeKey(x) {
    const card = state.cardCache.get(x.templateId);
    const attrs = {};
    for (const key of state.editRules.quantityAttributes) {
      let v;
      if (x.editedAttributes && x.editedAttributes[key] !== undefined) v = x.editedAttributes[key];
      else if (x.permanentAttributes && x.permanentAttributes[key] !== undefined) v = x.permanentAttributes[key];
      if (v !== undefined) attrs[key] = v;
    }

    const completedQuests = [];
    if (Array.isArray(x.completedQuests) && x.completedQuests.length) {
      const seen = new Set();
      for (const q of x.completedQuests) {
        const s = String(q || '').trim();
        if (!s) continue;
        const sl = s.toLowerCase();
        if (seen.has(sl)) continue;
        seen.add(sl);
        completedQuests.push(s);
      }
      completedQuests.sort();
    }

    const questProgress = {};
    if (x.questProgress && typeof x.questProgress === 'object') {
      for (const qk of Object.keys(x.questProgress).sort()) {
        const v = Math.round(Number(x.questProgress[qk]));
        if (Number.isFinite(v) && v > 0) questProgress[qk] = v;
      }
    }

    const extraTags = getItemExtraTags(x, card).slice().sort();

    const toggles = {};
    const isItem = Object.prototype.hasOwnProperty.call(x, 'slot');
    if (isItem) {
      const ruleToggles = Array.isArray(state.editRules.editableToggles) ? state.editRules.editableToggles : DEFAULT_EDIT_RULES.editableToggles;
      for (const tg of ruleToggles) {
        const k = tg.key;
        if (!k) continue;
        if ((tg.scope === 'nonSmallItem' || k === 'Tiny') && isSmallItem(x, card)) continue;
        if (hasNativeToggle(card, k)) continue;
        if (isToggleOn(x, k, card)) toggles[k] = true;
      }
    }

    const req = {
      templateId: x.templateId,
      tier: x.tier || null,
      enchantment: x.enchantment || null,
      attributes: attrs,
    };
    if (completedQuests.length) req.completedQuests = completedQuests;
    if (Object.keys(questProgress).length) req.questProgress = questProgress;
    if (extraTags.length) req.extraTags = extraTags;
    if (Object.keys(toggles).length) req.toggles = toggles;

    const key = JSON.stringify([
      req.templateId,
      req.tier || '',
      req.enchantment || '',
      attrs,
      completedQuests,
      questProgress,
      extraTags,
      toggles,
    ]);
    return { key, attrs, req };
  }
  async function describeCards(list) {
    const want = [];
    const pendingByKey = new Map();
    const out = new Map();
    for (const x of list) {
      if (!x || !x.templateId) continue;
      const { key, req } = describeKey(x);
      if (describeCache.has(key)) { out.set(x, describeCache.get(key)); continue; }
      if (pendingByKey.has(key)) { pendingByKey.get(key).targets.push(x); continue; }
      const entry = { key, req, targets: [x] };
      pendingByKey.set(key, entry);
      want.push(entry);
    }
    for (let i = 0; i < want.length; i += 40) {
      const chunk = want.slice(i, i + 40);
      try {
        const r = await api('catalog/describe', { method: 'POST', json: { cards: chunk.map((w) => w.req), selectedModIds: state.modEnv || [] } });
        const items = r.items || [];
        // 服务器严格按请求顺序返回、仅跳过未知卡；使用顺序游标逐条配对，避免同卡同品阶但不同任务/开关态互相覆盖缓存
        let cursor = 0;
        chunk.forEach((w) => {
          let hit = null;
          if (cursor < items.length && items[cursor] && items[cursor].templateId === w.req.templateId) {
            hit = items[cursor++];
          } else {
            const foundIdx = items.findIndex((it, idx) => idx >= cursor && it && it.templateId === w.req.templateId);
            if (foundIdx >= 0) {
              hit = items[foundIdx];
              cursor = foundIdx + 1;
            }
          }
          const lines = hit ? (hit.lines || []) : [];
          describeCache.set(w.key, lines);
          for (const target of w.targets) out.set(target, lines);
        });
      } catch {
        chunk.forEach((w) => { for (const target of w.targets) out.set(target, null); }); // 拿不到说明就不显示，不影响编辑
      }
    }
    return out;
  }
  /** 一个放效果说明的盒子：先显示「读取中」，拿到后原地填进去（不重画整个面板，不打断输入）。 */
  function fillEffectLines(box, lines) {
    clear(box);
    if (!lines || !lines.length) { box.appendChild(h('span', { class: 'muted', text: lines === null ? '效果说明暂时读不到' : '这张卡没有文字说明' })); return; }
    for (const ln of lines) box.appendChild(h('div', { class: 'effect-line', text: ln }));
  }
  function effectBox(x) {
    const box = h('div', { class: 'effect-lines' });
    const cached = describeCache.get(describeKey(x).key);
    if (cached !== undefined) { fillEffectLines(box, cached); return box; }
    box.appendChild(h('span', { class: 'muted', text: '正在读取效果说明…' }));
    describeCards([x]).then((m) => fillEffectLines(box, m.get(x)));
    return box;
  }

  /** 技能详情：品阶、效果说明、移除。 */
  function renderSkillInspector(day, b, idx) {
    const sk = b.skills[idx];
    const card = state.cardCache.get(sk.templateId);
    const tiers = card && card.tiers && card.tiers.length ? card.tiers : TIERS;
    const box = h('div', { class: 'inspector' });
    box.appendChild(h('div', { class: 'inspector-head' },
      h('h4', { text: `技能：${sk.zhName || sk.internalName || '技能'}` }),
      h('div', { class: 'btn-row', style: { marginTop: '0' } },
        h('button', { type: 'button', class: 'btn btn-danger btn-xs', onclick: () => { b.skills.splice(idx, 1); state.editor.selectedSkill = -1; onBoardEdited(day, { rerenderPanel: true }); } }, '移除'),
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => { state.editor.selectedSkill = -1; renderDayPanel(); } }, '收起'))));
    const tierPicker = h('div', { class: 'tier-picker' }, TIERS.map((t) => h('button', {
      type: 'button',
      class: `tier-btn tier-${t} ${sk.tier === t ? 'active' : ''}`,
      disabled: !tiers.includes(t),
      title: tiers.includes(t) ? '' : '这个技能没有这个品阶',
      onclick: () => { retier(sk, t); onBoardEdited(day, { rerenderPanel: true }); },
    }, tierZh(t))));
    box.appendChild(h('div', { class: 'inspector-grid' },
      h('span', { text: '品阶' }), tierPicker,
      h('span', { text: '效果' }), effectBox(sk)));
    return box;
  }

  function summarizeAttrs(it) {
    const parts = [];
    for (const key of state.editRules.quantityAttributes) {
      if (key === 'CooldownMax') continue; // 冷却单独显示
      const v = currentAttrValue(it, key);
      if (v !== undefined && v !== 0) parts.push(`${state.editRules.attributeLabelsZh[key] || key}${v}`);
      if (parts.length >= 2) break;
    }
    return parts.join(' ');
  }

  /** 任务进度分区（仅对 card.quests 非空的任务卡渲染）：支持独立任务与互斥多分支单选。 */
  function renderQuestSection(day, it, card) {
    const groups = card && Array.isArray(card.quests) ? card.quests : [];
    const wrap = h('div', { class: 'quest-section' });

    for (const group of groups) {
      const entries = Array.isArray(group.entries) ? group.entries : [];
      if (!entries.length) continue;

      if (group.mutuallyExclusive && entries.length > 1) {
        const activeEntry = entries.find((en) => hasQuestCompleted(it, en.attributeType)) || null;
        const gBox = h('div', { class: 'quest-group quest-mutex' });
        gBox.appendChild(h('div', { class: 'quest-group-head' },
          h('span', { class: 'quest-group-title', text: `互斥任务分支（${entries.length} 选 1）` }),
          h('span', { class: 'muted', text: activeEntry ? '已选定完成分支' : '未完成' })));

        const chips = h('div', { class: 'quest-chips' });
        chips.appendChild(h('button', {
          type: 'button',
          class: `quest-chip quest-chip-none ${!activeEntry ? 'active' : ''}`,
          onclick: () => {
            for (const en of entries) {
              setQuestCompletedFlag(it, en.attributeType, false);
              const t = Math.max(1, parseInt(en.target, 10) || 1);
              if (getQuestProgress(it, en.attributeType) >= t) setQuestProgressVal(it, en.attributeType, 0);
            }
            onBoardEdited(day, { rerenderPanel: true });
          },
        }, h('span', { class: 'qc-head', text: !activeEntry ? '● 未完成' : '○ 未完成' })));

        entries.forEach((en, ei) => {
          const isDone = activeEntry === en;
          const target = Math.max(1, parseInt(en.target, 10) || 1);
          const prog = getQuestProgress(it, en.attributeType);
          const branchName = `分支 ${String.fromCharCode(65 + ei)}`;
          chips.appendChild(h('button', {
            type: 'button',
            class: `quest-chip quest-branch ${isDone ? 'active' : ''}`,
            onclick: () => {
              for (const other of entries) {
                if (other === en) {
                  setQuestCompletedFlag(it, other.attributeType, true);
                  setQuestProgressVal(it, other.attributeType, target);
                } else {
                  setQuestCompletedFlag(it, other.attributeType, false);
                  setQuestProgressVal(it, other.attributeType, 0);
                }
              }
              onBoardEdited(day, { rerenderPanel: true });
            },
          },
            h('span', { class: 'qc-head' },
              h('b', { text: `${isDone ? '✓ ' : ''}${branchName}` }),
              (!activeEntry && prog > 0 && target > 1)
                ? h('span', { class: 'qc-prog-tag', text: `进度 ${Math.min(prog, target - 1)}/${target}` })
                : null),
            h('span', { class: 'qc-goal', text: en.goalZh || en.attributeType }),
            en.rewardZh ? h('span', { class: 'qc-reward', text: `奖励：${en.rewardZh}` }) : null));
        });

        gBox.appendChild(chips);
        wrap.appendChild(gBox);
        continue;
      }

      // 独立任务组（单条或非互斥多条，含可重复任务）
      const gBox = h('div', { class: 'quest-group quest-indep' });
      const repeatable = !!group.isRepeatable;
      for (const en of entries) {
        const attr = en.attributeType;
        const target = Math.max(1, parseInt(en.target, 10) || 1);
        const maxProg = repeatable ? Math.max(0, target - 1) : target;
        const done = !repeatable && hasQuestCompleted(it, attr);
        const rawProg = getQuestProgress(it, attr);
        const curProg = done ? target : clamp(rawProg, 0, maxProg);

        let leftCtrl;
        if (!repeatable) {
          const cb = h('input', { type: 'checkbox', checked: done });
          cb.addEventListener('change', () => {
            if (cb.checked) {
              setQuestCompletedFlag(it, attr, true);
              setQuestProgressVal(it, attr, target);
            } else {
              setQuestCompletedFlag(it, attr, false);
              setQuestProgressVal(it, attr, 0);
            }
            onBoardEdited(day, { rerenderPanel: true });
          });
          leftCtrl = h('label', { class: `quest-check ${done ? 'done' : ''}` },
            cb,
            h('span', { text: done ? '✓ 已完成' : '已完成' }));
        } else {
          leftCtrl = h('span', {
            class: 'badge badge-blue quest-rep-badge',
            title: maxProg > 0 ? `可重复任务不锁定已完成状态，进度上限 ${maxProg}` : '可重复任务触发即结算，不锁定已完成状态',
            text: '↻ 可重复',
          });
        }

        let rightCtrl = null;
        if (target > 1) {
          const inp = h('input', {
            type: 'number',
            class: 'quest-prog-input',
            dataset: { fk: `quest-prog-${attr}` },
            min: 0,
            max: maxProg,
            step: 1,
            value: curProg,
            title: repeatable ? `可重复任务当前累计进度（0..${maxProg}）` : `当前进度（0..${target}，填 ${target} 自动勾选已完成）`,
          });
          inp.addEventListener('change', () => {
            let v = parseInt(inp.value, 10);
            if (!Number.isFinite(v)) v = 0;
            v = clamp(v, 0, maxProg);
            inp.value = String(v);
            if (repeatable) {
              setQuestCompletedFlag(it, attr, false);
              setQuestProgressVal(it, attr, v);
            } else if (v >= target) {
              setQuestCompletedFlag(it, attr, true);
              setQuestProgressVal(it, attr, target);
            } else {
              setQuestCompletedFlag(it, attr, false);
              setQuestProgressVal(it, attr, v);
            }
            onBoardEdited(day, { rerenderPanel: true });
          });
          rightCtrl = h('div', { class: 'quest-prog' },
            inp,
            h('span', { class: 'quest-prog-max', text: `/ ${target}` }));
        } else if (repeatable) {
          rightCtrl = h('span', { class: 'muted quest-prog-note', text: '触发即生效' });
        }

        gBox.appendChild(h('div', { class: `quest-row ${done ? 'done' : ''}` },
          leftCtrl,
          h('div', { class: 'quest-info' },
            h('div', { class: 'quest-goal', text: en.goalZh || attr }),
            en.rewardZh ? h('div', { class: 'quest-reward', text: `奖励：${en.rewardZh}` }) : null),
          rightCtrl));
      }
      wrap.appendChild(gBox);
    }

    return wrap;
  }

  /** 状态开关区（Tiny / Lifesteal / Flying / DestroyImmunity）。 */
  function renderToggleControls(day, b, idx, it, card) {
    const ruleToggles = Array.isArray(state.editRules.editableToggles) && state.editRules.editableToggles.length
      ? state.editRules.editableToggles
      : DEFAULT_EDIT_RULES.editableToggles;
    const row = h('div', { class: 'toggle-bar' });

    for (const tg of ruleToggles) {
      const key = tg.key;
      const label = tg.labelZh || key;
      const isTinyKey = key === 'Tiny';
      const native = hasNativeToggle(card, key);
      const disabledBySize = (tg.scope === 'nonSmallItem' || isTinyKey) && isSmallItem(it, card);
      const checked = disabledBySize ? false : (native ? true : isToggleOn(it, key, card));
      const cb = h('input', { type: 'checkbox', checked, disabled: native || disabledBySize });

      cb.addEventListener('change', () => {
        if (native || disabledBySize) return;
        const wantOn = !!cb.checked;
        if (isTinyKey) {
          if (wantOn) {
            it.editedToggles = it.editedToggles || {};
            it.editedToggles.Tiny = true;
            it.width = itemWidth(it);
            if (Array.isArray(it.extraTags)) {
              it.extraTags = it.extraTags.filter((t) => String(t).toLowerCase() !== 'tiny');
            }
            onBoardEdited(day, { rerenderPanel: true });
          } else {
            const origW = itemBaseWidth(it);
            if (canPlace(b.items, origW, it.slot, idx)) {
              it.editedToggles = it.editedToggles || {};
              it.editedToggles.Tiny = false;
              it.width = origW;
              if (Array.isArray(it.extraTags)) {
                it.extraTags = it.extraTags.filter((t) => String(t).toLowerCase() !== 'tiny');
              }
              onBoardEdited(day, { rerenderPanel: true });
            } else {
              const fitSlot = nearestFit(b.items, origW, it.slot, idx);
              if (fitSlot !== undefined) {
                it.slot = fitSlot;
                it.editedToggles = it.editedToggles || {};
                it.editedToggles.Tiny = false;
                it.width = origW;
                if (Array.isArray(it.extraTags)) {
                  it.extraTags = it.extraTags.filter((t) => String(t).toLowerCase() !== 'tiny');
                }
                b.items.sort((x, y) => x.slot - y.slot);
                state.editor.selectedItem = b.items.indexOf(it);
                toast(`已取消微型：恢复为 ${origW} 格并自动挪至第 ${fitSlot + 1} 格`, 'ok');
                onBoardEdited(day, { rerenderPanel: true });
              } else {
                cb.checked = true;
                toast(`无法取消微型：棋盘没有连续 ${origW} 个空位，请先挪动或移除其他物品`, 'warn', 5000);
              }
            }
          }
          return;
        }
        it.editedToggles = it.editedToggles || {};
        it.editedToggles[key] = wantOn;
        onBoardEdited(day, { rerenderPanel: true });
      });

      const title = native
        ? '原卡自带该状态（只读）'
        : (disabledBySize ? '小型物品本身仅占 1 格，无需微型化' : '');
      row.appendChild(h('label', {
        class: `toggle-chip ${checked ? 'on' : ''} ${native ? 'native' : ''} ${disabledBySize ? 'disabled' : ''}`,
        title,
      },
        cb,
        h('span', { text: label }),
        native ? h('span', { class: 'toggle-tag', text: '原卡自带' }) : null,
        (!native && disabledBySize) ? h('span', { class: 'toggle-tag', text: '小型禁用' }) : null));
    }

    return row;
  }

  /** 附加类型标签芯片选择器（排除原卡自带 tags 与 "Tiny"，单卡最多 maxExtraTags 个）。 */
  function renderExtraTagControls(day, it, card) {
    const ruleTags = Array.isArray(state.editRules.editableTags) && state.editRules.editableTags.length
      ? state.editRules.editableTags
      : DEFAULT_EDIT_RULES.editableTags;
    const maxTags = parseInt(state.editRules.maxExtraTags, 10) || 6;
    const nativeTagsRaw = (card && Array.isArray(card.tags)) ? card.tags : [];
    const nativeLower = new Set(nativeTagsRaw.map((t) => String(t).toLowerCase()));
    const tagZhMap = new Map(ruleTags.map((t) => [String(t.id).toLowerCase(), t.nameZh || t.id]));

    const selected = getItemExtraTags(it, card);
    const selectedLower = new Set(selected.map((t) => t.toLowerCase()));
    const selectable = ruleTags.filter((t) => {
      const tl = String(t.id || '').toLowerCase();
      return tl && tl !== 'tiny' && !nativeLower.has(tl);
    });
    const knownLower = new Set(ruleTags.map((t) => String(t.id).toLowerCase()));
    const extraCustom = selected.filter((t) => !knownLower.has(t.toLowerCase()));

    const nativeKnownZh = nativeTagsRaw
      .map((t) => tagZhMap.get(String(t).toLowerCase()))
      .filter(Boolean);

    const wrap = h('div', { class: 'extra-tags-wrap' });
    wrap.appendChild(h('div', { class: 'extra-tags-meta' },
      h('span', { class: 'muted', text: `已附加 ${selected.length}/${maxTags}` }),
      nativeKnownZh.length ? h('span', { class: 'muted', text: `· 原卡自带：${nativeKnownZh.join('、')}` }) : null));

    const chips = h('div', { class: 'tag-chips' });
    const toggleTag = (tagId) => {
      const tl = String(tagId).toLowerCase();
      const next = getItemExtraTags(it, card);
      const existsIdx = next.findIndex((x) => x.toLowerCase() === tl);
      if (existsIdx >= 0) {
        next.splice(existsIdx, 1);
      } else {
        if (next.length >= maxTags) {
          toast(`单卡最多附加 ${maxTags} 个类型标签`, 'warn');
          return;
        }
        next.push(tagId);
      }
      it.extraTags = next;
      onBoardEdited(day, { rerenderPanel: true });
    };

    for (const tag of selectable) {
      const active = selectedLower.has(String(tag.id).toLowerCase());
      const atLimit = !active && selected.length >= maxTags;
      chips.appendChild(h('button', {
        type: 'button',
        class: `tag-chip ${active ? 'active' : ''}`,
        disabled: atLimit,
        title: atLimit ? `单卡最多附加 ${maxTags} 个类型标签` : (active ? '点击移除该附加标签' : `附加「${tag.nameZh}」类型标签`),
        onclick: () => toggleTag(tag.id),
      }, `${active ? '✓ ' : '+ '}${tag.nameZh}`));
    }
    for (const customTag of extraCustom) {
      chips.appendChild(h('button', {
        type: 'button',
        class: 'tag-chip active',
        title: '点击移除该附加标签',
        onclick: () => toggleTag(customTag),
      }, `✓ ${customTag}`));
    }

    wrap.appendChild(chips);
    return wrap;
  }

  function renderInspector(day, b, idx) {
    const it = b.items[idx];
    const card = state.cardCache.get(it.templateId);
    const w = itemWidth(it);
    const isTiny = isToggleOn(it, 'Tiny', card);
    const tiers = card && card.tiers && card.tiers.length ? card.tiers : TIERS;
    const box = h('div', { class: 'inspector' });

    const moveBy = (delta) => {
      const target = it.slot + delta;
      if (!canPlace(b.items, w, target, idx)) { toast(delta < 0 ? '左边没空位了' : '右边没空位了', 'warn'); return; }
      it.slot = target;
      onBoardEdited(day, { rerenderPanel: true });
    };
    box.appendChild(h('div', { class: 'inspector-head' },
      h('h4', { text: `${it.zhName || it.internalName || '物品'}（占 ${w} 格${isTiny ? ' · 微型' : ''} · 第 ${it.slot + 1}${w > 1 ? `–${it.slot + w}` : ''} 格）` }),
      h('div', { class: 'btn-row', style: { marginTop: '0' } },
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => moveBy(-1) }, '← 左移'),
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => moveBy(1) }, '右移 →'),
        h('button', { type: 'button', class: 'btn btn-danger btn-xs', onclick: () => { b.items.splice(idx, 1); state.editor.selectedItem = -1; onBoardEdited(day, { rerenderPanel: true }); } }, '移除'),
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => { state.editor.selectedItem = -1; renderDayPanel(); } }, '收起'))));

    const tierPicker = h('div', { class: 'tier-picker' }, TIERS.map((t) => h('button', {
      type: 'button',
      class: `tier-btn tier-${t} ${it.tier === t ? 'active' : ''}`,
      disabled: !tiers.includes(t),
      title: tiers.includes(t) ? '' : '这张卡没有这个品阶',
      onclick: () => { retier(it, t); onBoardEdited(day, { rerenderPanel: true }); },
    }, tierZh(t))));

    const enchList = card && Array.isArray(card.enchantments) ? card.enchantments : Object.keys(ENCH_ZH);
    const enchSummaries = (card && card.enchantmentSummaries && typeof card.enchantmentSummaries === 'object') ? card.enchantmentSummaries : {};
    const getEnchSummary = (k) => {
      if (!k) return '';
      if (enchSummaries[k]) return enchSummaries[k];
      const kl = String(k).toLowerCase();
      for (const [ek, ev] of Object.entries(enchSummaries)) {
        if (ek.toLowerCase() === kl) return ev || '';
      }
      return '';
    };
    const enchSel = h('select', { dataset: { fk: 'ench' }, disabled: card && Array.isArray(card.enchantments) && card.enchantments.length === 0 },
      h('option', { value: '' }, '无附魔'),
      enchList.map((k) => {
        const zh = ENCH_ZH[k] || k;
        const sum = getEnchSummary(k);
        return h('option', { value: k, selected: it.enchantment === k, title: sum || zh }, sum ? `${zh} — ${sum}` : zh);
      }));
    if (it.enchantment && !enchList.includes(it.enchantment)) {
      enchSel.appendChild(h('option', { value: it.enchantment, selected: true }, `${ENCH_ZH[it.enchantment] || it.enchantment}（不支持）`));
    }
    enchSel.addEventListener('change', () => { it.enchantment = enchSel.value || null; onBoardEdited(day, { rerenderPanel: true }); });

    const selEnchSummary = it.enchantment ? getEnchSummary(it.enchantment) : '';
    const enchControl = h('div', { class: 'ench-field' },
      enchSel,
      selEnchSummary
        ? h('div', { class: 'ench-summary' },
          h('span', { class: 'ench-summary-tag', text: ENCH_ZH[it.enchantment] || it.enchantment }),
          h('span', { text: selEnchSummary }))
        : null);

    const hasQuests = !!(card && Array.isArray(card.quests) && card.quests.length > 0);
    const inspectorRows = [
      h('span', { text: '品阶' }), tierPicker,
      h('span', { text: '附魔' }), enchControl,
      h('span', { text: '效果' }), effectBox(it),
    ];
    if (hasQuests) {
      inspectorRows.push(h('span', { text: '任务进度' }), renderQuestSection(day, it, card));
    }
    inspectorRows.push(
      h('span', { text: '特殊状态' }), renderToggleControls(day, b, idx, it, card),
      h('span', { text: '附加标签' }), renderExtraTagControls(day, it, card));

    box.appendChild(h('div', { class: 'inspector-grid' }, ...inspectorRows));

    // 数量类数值
    const grid = h('div', { class: 'attr-grid' });
    if (hasCooldown(it) || currentAttrValue(it, 'CooldownMax') > 0) {
      const curMs = currentAttrValue(it, 'CooldownMax');
      const cdIn = h('input', { type: 'number', dataset: { fk: 'attr-CooldownMax' }, min: 1, max: 999, step: 0.1, value: curMs > 0 ? Math.round(curMs / 100) / 10 : '' });
      cdIn.addEventListener('change', () => {
        it.editedAttributes = it.editedAttributes || {};
        if (cdIn.value === '') {
          delete it.editedAttributes.CooldownMax;
        } else {
          const sec0 = parseFloat(cdIn.value);
          if (Number.isFinite(sec0)) {
            const sec = clamp(Math.round(sec0 * 10) / 10, 1, 999); // 最低 1 秒，防止冷却 0 把战斗卡死
            cdIn.value = sec;
            it.editedAttributes.CooldownMax = Math.round(sec * 1000);
          } else {
            delete it.editedAttributes.CooldownMax;
            cdIn.value = curMs > 0 ? Math.round(curMs / 100) / 10 : '';
          }
        }
        if (!Object.keys(it.editedAttributes).length) delete it.editedAttributes;
        onBoardEdited(day, { rerenderTile: true });
      });
      grid.appendChild(h('label', { class: 'field' }, h('span', { text: '冷却(秒)' }), cdIn));
    }
    for (const key of state.editRules.quantityAttributes) {
      if (key === 'CooldownMax') continue;
      const cur = currentAttrValue(it, key);
      const inp = h('input', { type: 'number', dataset: { fk: `attr-${key}` }, min: 0, max: state.editRules.quantityMax, step: 1, placeholder: cur === undefined ? '默认' : '', value: cur === undefined ? '' : cur });
      inp.addEventListener('change', () => {
        it.editedAttributes = it.editedAttributes || {};
        if (inp.value === '') {
          delete it.editedAttributes[key];
        } else {
          let v = parseFloat(inp.value);
          if (!Number.isFinite(v)) v = 0;
          v = clamp(Math.round(v), 0, state.editRules.quantityMax);
          inp.value = v;
          it.editedAttributes[key] = v;
        }
        if (!Object.keys(it.editedAttributes).length) delete it.editedAttributes;
        onBoardEdited(day, { rerenderTile: true });
      });
      grid.appendChild(h('label', { class: 'field' }, h('span', { text: state.editRules.attributeLabelsZh[key] || key }), inp));
    }
    box.appendChild(grid);
    box.appendChild(h('div', { class: 'attr-locked', text: '冷却最低 1 秒；附魔（比如「极速」）带来的冷却缩短会在战斗里另外算。多重触发、弹药保持卡牌原样，不能手改。' }));
    if (hasHiddenGrowth(it)) {
      box.appendChild(h('div', { class: 'attr-locked', text: '★ 这件物品带有成长、任务或永久加成（来自真实对局），你不改它们就会原样保留。' }));
    }
    return box;
  }

  function cycleSkillTier(sk) {
    const card = state.cardCache.get(sk.templateId);
    const tiers = card && card.tiers && card.tiers.length ? card.tiers : TIERS;
    const i = tiers.indexOf(sk.tier);
    retier(sk, tiers[(i + 1) % tiers.length]);
  }

  /**
   * 改品阶：镜像池里的阵容把数值存成「绝对值」（如青铜时伤害 = 10），直接改品阶会让旧数值原样保留。
   * 这里按「基础值跟着品阶走、原有的个人加成保留」换算成新品阶下的目标值（editedAttributes），交给服务端折算。
   * 已经带分层成长（permanentAdd / permanentMult）的卡，服务端会按新品阶自动重算，这里不动。
   */
  function retier(obj, newTier) {
    const oldTier = obj.tier || 'Bronze';
    if (oldTier === newTier) return;
    const c = state.cardCache.get(obj.templateId);
    const bases = c && c.baseAttributesByTier;
    const layered = (o) => !!(o && typeof o === 'object' && Object.keys(o).length);
    if (bases && !layered(obj.permanentAdd) && !layered(obj.permanentMult)) {
      const bOld = bases[oldTier] || {};
      const bNew = bases[newTier] || {};
      for (const key of state.editRules.quantityAttributes) {
        if (bOld[key] === undefined || bNew[key] === undefined) continue;
        const fixed = (obj.editedAttributes && obj.editedAttributes[key] !== undefined)
          || (obj.permanentAttributes && obj.permanentAttributes[key] !== undefined);
        if (!fixed) continue; // 没有固定值的属性，服务端本来就用新品阶的基础值
        const cur = currentAttrValue(obj, key);
        if (cur === undefined) continue;
        const v = clamp(Math.round(bNew[key] + (cur - bOld[key])), 0, state.editRules.quantityMax);
        obj.editedAttributes = obj.editedAttributes || {};
        obj.editedAttributes[key] = v;
      }
    }
    obj.tier = newTier;
  }

  function poolSourceZh(g) {
    if (g.isPlayerGhost) return '本服玩家';
    const s = String(g.source || '');
    if (/(^|_)(ai|bot|synthetic|generated|gen)(_|$)/i.test(s)) return 'AI 生成';
    if (/real|community|hub/i.test(s)) return '社区真实阵容';
    return s ? '镜像池' : '';
  }

  /** 任一编辑后的统一出口：标脏、局部刷新、排队校验。 */
  function onBoardEdited(day, opt = {}) {
    markDirty();
    const e = state.editor;
    e.editVersion[day] = (e.editVersion[day] || 0) + 1;
    if (e.autoDays && e.autoDays.delete(day)) opt.rerenderChip = true; // 改过的天不再算「自动补的」
    if (opt.rerenderPanel || opt.rerenderTile) renderDayPanel();
    if (opt.rerenderChip || opt.rerenderPanel) refreshTimeline();
    scheduleValidate(day);
  }

  function renderValidation(day) {
    const v = state.editor.validation[day];
    const box = h('div', { class: `validate-box ${v && v.checking ? 'checking' : ''}` });
    if (!v) return box;
    if (v.checking) { box.textContent = '正在检查这套阵容...'; return box; }
    if (v.networkError) { box.appendChild(h('div', { class: 'warn-box', text: `暂时没法检查（${v.networkError}），保存时服务器会再检查一次。` })); return box; }
    if (v.errors && v.errors.length) {
      box.appendChild(h('div', { class: 'err-box' }, h('b', { text: '这一天的阵容有问题，修好才能保存：' }), h('ul', null, v.errors.map((x) => h('li', { text: x })))));
    }
    if (v.warnings && v.warnings.length) {
      box.appendChild(h('div', { class: 'warn-box' }, h('ul', null, v.warnings.map((x) => h('li', { text: x })))));
    }
    if (v.ok && (!v.warnings || !v.warnings.length)) box.appendChild(h('div', { class: 'ok-box', text: '✓ 阵容检查通过' }));
    return box;
  }

  const validateTimers = new Map();
  function scheduleValidate(day) {
    const e = state.editor;
    if (!e || !e.days[day]) return;
    e.validation[day] = Object.assign({}, e.validation[day] || {}, { checking: true });
    const vb = document.querySelector('#dayPanel .validate-box');
    if (vb && e.selectedDay === day) { vb.className = 'validate-box checking'; vb.textContent = '正在检查这套阵容...'; }
    clearTimeout(validateTimers.get(day));
    validateTimers.set(day, setTimeout(() => runValidate(day), 400));
  }

  async function runValidate(day) {
    const e = state.editor;
    if (!e || !e.days[day]) return;
    const version = e.editVersion[day] || 0;
    const editorRef = e;
    const board = deepClone(e.days[day]);
    board.day = day;
    try {
      const r = await api('lineups/validate-board', { method: 'POST', json: { day, board, selectedModIds: state.modEnv || [] } });
      if (state.editor !== editorRef || (editorRef.editVersion[day] || 0) !== version) return; // 期间又改过，丢弃
      editorRef.validation[day] = { ok: !!r.ok, errors: r.errors || [], warnings: r.warnings || [], checking: false };
      if (r.ok && r.board && editorRef.days[day]) {
        // 用服务端归一化后的阵容替换（手改数值折算成最终值、自动解锁格子等），按格子位置保留当前选中
        const prev = editorRef.days[day];
        const selSlot = editorRef.selectedDay === day && prev.items[editorRef.selectedItem] ? prev.items[editorRef.selectedItem].slot : null;
        editorRef.days[day] = normBoard(r.board, day);
        if (selSlot !== null) editorRef.selectedItem = editorRef.days[day].items.findIndex((x) => x.slot === selSlot);
      }
    } catch (err) {
      if (state.editor !== editorRef) return;
      editorRef.validation[day] = { ok: true, errors: [], warnings: [], checking: false, networkError: err.message };
    }
    if (state.editor === editorRef) {
      refreshTimeline();
      if (editorRef.selectedDay === day) renderDayPanel();
      updateSaveButton();
    }
  }

  function revalidateAllDays() {
    if (!state.editor) return;
    for (const d of Object.keys(state.editor.days)) scheduleValidate(Number(d));
  }

  async function handcraftDay(day) {
    const e = state.editor;
    if (e.days[day] && e.days[day].items.length) {
      const ok = await confirmDialog('手搓新对手', `第 ${day} 天已经有对手了，手搓会清掉现在的阵容。继续？`, '清掉并手搓', 'btn-danger');
      if (!ok) return;
    }
    setDayBoard(day, suggestedBoard(day, prevHero(day)));
    toast('已生成一个空阵容：点棋盘上的空格添加物品');
  }

  async function clearDay(day) {
    const ok = await confirmDialog('清空这天', `第 ${day} 天会改回随机匹配对手。确定？`, '清空', 'btn-danger');
    if (ok) setDayBoard(day, null);
  }

  function openCopyToDays(fromDay) {
    const e = state.editor;
    const max = state.limits.maxLineupDays;
    const chosen = new Set();
    const grid = h('div', { class: 'filter-grid' });
    for (let d = 1; d <= max; d++) {
      if (d === fromDay) continue;
      const cb = h('input', { type: 'checkbox' });
      cb.addEventListener('change', () => { if (cb.checked) chosen.add(d); else chosen.delete(d); });
      grid.appendChild(h('label', { class: 'inline-check' }, cb, `第 ${d} 天${e.days[d] ? '（会覆盖）' : ''}`));
    }
    openModal({
      title: `把第 ${fromDay} 天的对手复制到…`,
      body: grid,
      footer: [
        { label: '取消' },
        {
          label: '复制', kind: 'btn-gold', onClick: () => {
            if (!chosen.size) { toast('先勾选要复制到哪几天', 'warn'); return false; }
            for (const d of chosen) {
              const copy = deepClone(e.days[fromDay]);
              copy.day = d;
              e.days[d] = copy;
              e.editVersion[d] = (e.editVersion[d] || 0) + 1;
              scheduleValidate(d);
            }
            if ([...chosen].some((d) => d > VISIBLE_DAYS_DEFAULT)) e.showAllDays = true;
            markDirty();
            renderEditor();
            toast(`已复制到 ${chosen.size} 天`);
            return true;
          },
        },
      ],
    });
  }

  // ── 选卡 ──
  function cacheCards(items) { for (const c of items || []) state.cardCache.set(c.templateId, c); }

  function openCardPicker(day, type, slot = 0) {
    const e = state.editor;
    const b = e.days[day];
    if (!b) return;
    if (type === 'Skill' && (b.skills || []).length >= 20) { toast('技能最多 20 个', 'warn'); return; }
    const freeRun = type === 'Item' ? longestFreeRun(b.items) : 3;
    if (type === 'Item' && freeRun === 0) { toast('10 个格子都满了，先移除一件物品', 'warn'); return; }

    const q = h('input', { type: 'text', placeholder: '输入中文名或英文名搜索…' });
    const heroSel = h('select', null,
      h('option', { value: b.hero }, `${heroName(b.hero)} 的卡`),
      h('option', { value: '' }, '全部英雄'));
    const list = h('div', { class: 'pick-list' }, h('div', { class: 'loading-line', text: '正在加载…' }));
    const meta = h('div', { class: 'muted' });

    let seq = 0;
    const doSearch = async () => {
      const mySeq = ++seq;
      try {
        const r = await api('catalog/cards', { query: { q: q.value.trim(), type, hero: heroSel.value, limit: 40, selectedModIds: state.modEnv || [] } });
        if (mySeq !== seq) return;
        cacheCards(r.items);
        clear(list);
        meta.textContent = `找到 ${r.total} 张${r.total > (r.items || []).length ? `（显示前 ${(r.items || []).length} 张，可以再输关键词缩小范围）` : ''}`;
        if (!r.items || !r.items.length) {
          list.appendChild(h('div', { class: 'loading-line' }, '没有找到。',
            heroSel.value
              ? h('button', { type: 'button', class: 'btn btn-subtle btn-xs', style: { marginLeft: '8px' }, onclick: () => { heroSel.value = ''; doSearch(); } }, '在全部英雄的卡里找')
              : '换个关键词试试。'));
          return;
        }
        for (const c of r.items) {
          const w = SIZE_W[c.size] || 1;
          const fits = type !== 'Item' || w <= freeRun;
          list.appendChild(h('div', { class: `pick-row ${fits ? '' : 'disabled'}` },
            h('div', { class: 'pr-main' },
              h('div', { class: 'pr-name', text: c.nameZh || c.nameEn || c.templateId }),
              h('div', { class: 'pr-sub', text: [type === 'Item' ? `${w} 格` : '技能', (c.heroes || []).map(heroName).join('/') || '通用', c.isMod ? `Mod：${modName(c.modId)}` : '', (c.tiers || []).map(tierZh).join('/')].filter(Boolean).join(' · ') })),
            fits
              ? h('button', { type: 'button', class: 'btn btn-gold btn-sm', onclick: () => { addCard(day, type, c, slot); modal.close(); } }, type === 'Item' ? '放入' : '添加')
              : h('span', { class: 'badge badge-gray', text: `放不下（最多 ${freeRun} 格）` })));
        }
      } catch (err) {
        if (mySeq !== seq) return;
        clear(list).appendChild(h('div', { class: 'err-box', text: err.message }));
      }
    };
    const debounced = debounce(doSearch, 250);
    q.addEventListener('input', debounced);
    heroSel.addEventListener('change', doSearch);

    const modal = openModal({
      title: type === 'Item' ? `第 ${day} 天 · 在第 ${slot + 1} 格附近放一件物品` : `第 ${day} 天 · 添加技能`,
      size: 'wide',
      body: [
        h('div', { class: 'form-row' }, h('label', { class: 'field grow' }, h('span', { text: '搜索' }), q), h('label', { class: 'field' }, h('span', { text: '范围' }), heroSel)),
        type === 'Item' ? h('p', { class: 'muted', text: `现在棋盘上最多还能连续放 ${freeRun} 格的物品。会优先放在你点的位置，放不下就放到最近的空位。` }) : null,
        meta,
        list,
      ],
    });
    setTimeout(() => q.focus(), 30);
    doSearch();
  }

  function addCard(day, type, c, slot) {
    const b = state.editor.days[day];
    if (!b) return;
    if (type === 'Item') {
      const w = SIZE_W[c.size] || 1;
      const pos = canPlace(b.items, w, slot) ? slot : nearestFit(b.items, w, slot);
      if (pos === undefined) { toast('放不下这件物品', 'warn'); return; }
      b.items.push({
        slot: pos, width: w, templateId: c.templateId, internalName: c.nameEn || '', zhName: c.nameZh || c.nameEn || '',
        tier: c.startingTier || (c.tiers && c.tiers[0]) || 'Bronze', enchantment: null, permanentAttributes: {},
      });
      b.items.sort((x, y) => x.slot - y.slot);
      state.editor.selectedItem = b.items.findIndex((x) => x.slot === pos);
    } else {
      b.skills = b.skills || [];
      b.skills.push({ templateId: c.templateId, internalName: c.nameEn || '', zhName: c.nameZh || c.nameEn || '', tier: c.startingTier || 'Bronze' });
    }
    onBoardEdited(day, { rerenderPanel: true });
  }

  // ── 镜像池 ──
  function openPoolPicker(day) {
    const f = {
      day: h('input', { type: 'number', min: 1, max: 99, value: Math.min(day, 15) }),
      hero: h('select', null, h('option', { value: '' }, '全部'), state.heroes.map((x) => h('option', { value: x.id }, x.nameZh))),
      power: h('select', null, h('option', { value: '' }, '全部'), Object.entries(POWER_ZH).map(([k, v]) => h('option', { value: k }, v))),
      source: h('select', null, h('option', { value: '' }, '全部'), h('option', { value: 'community' }, '社区真实阵容'), h('option', { value: 'player' }, '本服玩家'), h('option', { value: 'ai' }, 'AI 生成')),
      hpMin: h('input', { type: 'number', min: 1, placeholder: '不限' }),
      hpMax: h('input', { type: 'number', min: 1, placeholder: '不限' }),
      keyword: h('input', { type: 'text', placeholder: '含某张卡，例如：短剑' }),
    };
    const results = h('div', { class: 'pool-grid' });
    const meta = h('div', { class: 'muted', style: { marginTop: '8px' } });
    const pager = h('div', { class: 'pager' });
    let page = 1;
    let seq = 0;

    const search = async (p = 1) => {
      page = p;
      const mySeq = ++seq;
      clear(results).appendChild(h('div', { class: 'loading-line', text: '正在搜索镜像池…' }));
      clear(pager);
      const num = (inp) => { const v = parseInt(inp.value, 10); return Number.isFinite(v) ? v : null; };
      try {
        const r = await api('ghost-pool/search', {
          method: 'POST',
          json: {
            day: num(f.day), hero: f.hero.value, powerBucket: f.power.value, source: f.source.value,
            hpMin: num(f.hpMin), hpMax: num(f.hpMax), levelMin: null, levelMax: null,
            keyword: f.keyword.value.trim(), page, pageSize: 20, selectedModIds: state.modEnv || [],
          },
        });
        if (mySeq !== seq) return;
        clear(results);
        const notes = [`共 ${r.total} 个${r.truncated ? '（太多了，只列出前一部分，可以加条件缩小范围）' : ''}`];
        if (r.indexBuilding) notes.push('按卡名搜索的索引还在准备，稍等十几秒再搜卡名会更准');
        meta.textContent = notes.join('；');
        if (!r.items || !r.items.length) { results.appendChild(h('div', { class: 'loading-line', text: '没有符合条件的阵容，放宽条件试试。' })); return; }
        for (const g of r.items) results.appendChild(renderPoolCard(g, day, modal));
        const totalPages = Math.max(1, Math.ceil(r.total / (r.pageSize || 20)));
        pager.appendChild(h('button', { type: 'button', class: 'btn btn-subtle btn-xs', disabled: page <= 1, onclick: () => search(page - 1) }, '上一页'));
        pager.appendChild(h('span', { text: `第 ${page} / ${totalPages} 页` }));
        pager.appendChild(h('button', { type: 'button', class: 'btn btn-subtle btn-xs', disabled: page >= totalPages, onclick: () => search(page + 1) }, '下一页'));
      } catch (err) {
        if (mySeq !== seq) return;
        clear(results).appendChild(h('div', { class: 'err-box', text: err.message }));
      }
    };

    const modal = openModal({
      title: `从镜像池给第 ${day} 天挑一个对手`,
      size: 'wide',
      body: [
        h('div', { class: 'filter-grid' },
          h('label', { class: 'field' }, h('span', { text: '录制于第几天' }), f.day),
          h('label', { class: 'field' }, h('span', { text: '英雄' }), f.hero),
          h('label', { class: 'field' }, h('span', { text: '强度' }), f.power),
          h('label', { class: 'field' }, h('span', { text: '来源' }), f.source),
          h('label', { class: 'field' }, h('span', { text: '血量 ≥' }), f.hpMin),
          h('label', { class: 'field' }, h('span', { text: '血量 ≤' }), f.hpMax),
          h('label', { class: 'field', style: { gridColumn: 'span 2' } }, h('span', { text: '包含卡牌' }), f.keyword),
          h('button', { type: 'button', class: 'btn btn-gold', onclick: () => search(1) }, '搜索')),
        h('p', { class: 'muted', style: { marginTop: '6px' }, text: '挑中以后可以继续改：换装备、调品阶、改血量。不同天录制的阵容也可以放到任意一天。' }),
        meta, results, pager,
      ],
    });
    f.keyword.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') search(1); });
    search(1);
  }

  function renderMiniBoard(g) {
    const mb = h('div', { class: 'mini-board' });
    const seBySlot = new Map();
    for (const se of (g.socketEffects || [])) seBySlot.set(clamp(parseInt(se.slot, 10) || 0, 0, 9), se);
    for (let s = 0; s < 10; s++) {
      const se = seBySlot.get(s);
      const seMeta = se ? (findSocketCatalog(se.templateId) || se) : null;
      mb.appendChild(h('div', {
        class: `mini-slot ${se ? `has-se se-${socketEffectKind(seMeta)}` : ''}`,
        style: { gridColumn: `${s + 1} / span 1` },
      }));
    }
    for (const it of g.items || []) {
      const w = clamp(parseInt(it.width, 10) || 1, 1, 3);
      const isTiny = isToggleOn(it, 'Tiny', null);
      mb.appendChild(h('div', {
        class: `mini-item tier-${it.tier || 'Bronze'} ${isTiny ? 'is-tiny' : ''}`,
        style: { gridColumn: `${clamp(it.slot, 0, 9) + 1} / span ${w}` },
        title: `${it.zhName || it.internalName}（${tierZh(it.tier)}${isTiny ? ' · 微型' : ''}${it.enchantment ? ` · ${ENCH_ZH[it.enchantment] || it.enchantment}` : ''}）`,
        text: it.zhName || it.internalName || '',
      }));
    }
    return mb;
  }

  function renderPoolCard(g, day, modal) {
    return h('div', { class: 'pool-card' },
      h('div', { class: 'pc-head' },
        h('span', { class: 'pc-hero', text: g.displayName || heroName(g.hero) }),
        h('span', { class: `badge ${g.powerBucket === 'Strong' || g.powerBucket === 'MetaBoss' ? 'badge-red' : g.powerBucket === 'Weak' ? 'badge-green' : 'badge-gray'}`, text: POWER_ZH[g.powerBucket] || g.powerBucket || '普通' })),
      h('div', { class: 'pc-stats' },
        h('span', { text: `第 ${g.day} 天录制` }),
        h('span', { text: `${g.healthMax} 血` }),
        h('span', { text: `${g.level} 级` }),
        h('span', { text: `${g.wins || 0} 胜 ${g.losses || 0} 负` }),
        h('span', { text: poolSourceZh(g) })),
      renderMiniBoard(g),
      h('div', { class: 'pc-skills' },
        (g.skills && g.skills.length)
          ? g.skills.map((s) => h('span', { class: `skill-chip small tier-${s.tier || 'Bronze'}`, text: s.zhName || s.internalName || '技能' }))
          : h('span', { class: 'muted', text: '没有技能' })),
      h('div', { class: 'btn-row', style: { marginTop: '4px' } },
        h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: () => openBoardDetail(g, () => useIt()) }, '🔍 看效果'),
        h('button', { type: 'button', class: 'btn btn-gold btn-sm', onclick: () => useIt() }, `用作第 ${day} 天的对手`)));

    async function useIt() {
      const e = state.editor;
      if (e.days[day] && e.days[day].items.length) {
        const ok = await confirmDialog('替换对手', `第 ${day} 天已经有对手了，要换成这个吗？`, '替换');
        if (!ok) return false;
      }
      const copy = deepClone(g);
      setDayBoard(day, copy);
      modal.close();
      toast(`已设为第 ${day} 天的对手，可以继续微调`);
      return true;
    }
  }

  /** 一套阵容的完整说明：每件物品、每个技能的品阶和效果（挑对手时看清楚再选）。 */
  function openBoardDetail(g, onUse) {
    const items = [...(g.items || [])].sort((a, b) => (a.slot || 0) - (b.slot || 0));
    const skills = g.skills || [];
    const row = (x, kind) => {
      const box = h('div', { class: 'effect-lines' }, h('span', { class: 'muted', text: '读取中…' }));
      const cd = h('span', { class: 'muted' });   // 冷却要等卡牌资料到了才知道
      const isTiny = kind === 'item' && isToggleOn(x, 'Tiny', null);
      const qCount = kind === 'item' ? completedQuestCount(x) : 0;
      const node = h('div', { class: `detail-row tier-${x.tier || 'Bronze'}` },
        h('div', { class: 'detail-name' },
          h('b', { text: x.zhName || x.internalName || (kind === 'item' ? '物品' : '技能') }),
          h('span', { class: 'tier-text', text: ` ${tierZh(x.tier)}` }),
          x.enchantment ? h('span', { class: 'it-ench', text: ` · ${ENCH_ZH[x.enchantment] || x.enchantment}` }) : null,
          isTiny ? h('span', { class: 'it-badge it-badge-tiny', style: { marginLeft: '5px' }, text: '微型' }) : null,
          qCount > 0 ? h('span', { class: 'it-badge it-badge-quest', style: { marginLeft: '5px' }, text: `✓任务×${qCount}` }) : null,
          cd),
        box);
      return { node, box, x, cd, kind };
    };
    const itemRows = items.map((x) => row(x, 'item'));
    const skillRows = skills.map((x) => row(x, 'skill'));
    const socketList = [...(g.socketEffects || [])].sort((a, c) => (a.slot || 0) - (c.slot || 0));
    const body = h('div', { class: 'board-detail' },
      h('div', { class: 'pc-stats' },
        h('span', { text: g.displayName || heroName(g.hero) }),
        h('span', { text: `${g.healthMax} 血` }),
        h('span', { text: `${g.level} 级` }),
        h('span', { text: `第 ${g.day} 天录制` }),
        h('span', { text: POWER_ZH[g.powerBucket] || g.powerBucket || '普通' })),
      renderMiniBoard(g),
      socketList.length ? h('div', { class: 'socket-summary-list', style: { marginTop: '6px' } },
        socketList.map((se) => {
          const meta = findSocketCatalog(se.templateId) || se;
          return h('span', { class: `socket-summary-chip se-${socketEffectKind(meta)}` },
            h('b', { text: `第${(se.slot || 0) + 1}格 ` }),
            socketEffectDisplayName(meta),
            meta && meta.descriptionZh ? `：${meta.descriptionZh}` : '');
        })) : null,
      h('h4', { text: `物品（${items.length}）` }),
      itemRows.length ? itemRows.map((r) => r.node) : h('div', { class: 'muted', text: '没有物品' }),
      h('h4', { text: `技能（${skills.length}）` }),
      skillRows.length ? skillRows.map((r) => r.node) : h('div', { class: 'muted', text: '没有技能' }));
    openModal({
      title: '阵容详情',
      size: 'wide',
      body: [body],
      footer: [
        { label: '返回', kind: 'btn-subtle' },
        onUse ? { label: '就用它', kind: 'btn-gold', onClick: async () => { const ok = await onUse(); return ok ? undefined : false; } } : null,
      ].filter(Boolean),
    });
    // 卡牌资料（品阶基础值）先拿到，数值说明才准
    ensureCards([g]).finally(() => {
      const all = [...itemRows, ...skillRows];
      for (const r of itemRows) { const t = cooldownText(r.x); r.cd.textContent = t ? ` · ⏱ ${t}` : ''; }
      describeCards(all.map((r) => r.x)).then((m) => { for (const r of all) fillEffectLines(r.box, m.get(r.x)); });
    });
  }

  // ── 保存 / 使用 / 删除 ──
  function buildSavePayload() {
    const e = state.editor;
    const days = {};
    for (const [k, b] of Object.entries(e.days)) {
      const d = Number(k);
      const copy = deepClone(b);
      copy.day = d;
      let mask = baseMaskForDay(d);
      for (const it of copy.items) {
        const w = itemWidth(it);
        it.width = w;
        for (let i = 0; i < w; i++) { const s = it.slot + i; if (s >= 0 && s < 10) mask |= 1 << s; }
      }
      copy.socketEffects = Array.isArray(copy.socketEffects) ? copy.socketEffects : [];
      copy.socketEffects.sort((a, c) => (a.slot || 0) - (c.slot || 0));
      for (const se of copy.socketEffects) {
        const s = clamp(parseInt(se.slot, 10) || 0, 0, 9);
        se.slot = s;
        mask |= 1 << s;
      }
      copy.unlockedSlotsMask = mask & 1023;
      days[d] = copy;
    }
    return {
      lineupId: e.lineupId, name: e.name.trim(), description: e.description.trim(), selectedModIds: state.modEnv || [], days,
      seedText: e.seedText || '', seedHero: e.seedHero || null,   // "" = 没配种子
    };
  }

  async function saveLineup() {
    const e = state.editor;
    if (!e) return false;
    if (!e.name.trim()) { toast('先给方案起个名字', 'warn'); return false; }
    if (anyDayHasError()) { toast('有天的对手存在问题（红色感叹号），修好后再保存', 'warn'); return false; }
    if (anyDayChecking()) {
      // 立刻跑完还在排队的校验，避免拿旧结果保存
      const pending = Object.keys(e.days).map(Number).filter((d) => e.validation[d] && e.validation[d].checking);
      for (const d of pending) clearTimeout(validateTimers.get(d));
      await Promise.all(pending.map((d) => runValidate(d)));
      if (state.editor !== e) return false;
      if (anyDayHasError()) { toast('有天的对手存在问题（红色感叹号），修好后再保存', 'warn'); return false; }
    }
    const btn = $('btnSaveLineup');
    if (btn) { btn.disabled = true; btn.textContent = '保存中…'; }
    try {
      const r = await api('lineups', { method: 'POST', json: buildSavePayload() });
      const keepDay = e.selectedDay;
      const showAll = e.showAllDays;
      const keepAuto = e.autoDays;
      state.editor = editorFromFull(r.lineup);
      state.editor.selectedDay = keepDay;
      state.editor.showAllDays = showAll || state.editor.showAllDays;
      if (keepAuto) state.editor.autoDays = keepAuto;
      await refreshContextQuietly(true);
      renderEditor();
      renderLineupList();
      const ws = r.warnings || [];
      toast(ws.length ? `方案已保存（有 ${ws.length} 条提示，显示在对应那天的下方）` : '方案已保存');
      revalidateAllDays(); // 把每天的提示重新挂回各自的面板（只检查不改动，不会变成“未保存”）
      return true;
    } catch (err) {
      if (err.status === 404 && e.lineupId && state.editor === e) {
        // 方案在别处（游戏里 / 另一个网页）被删掉了：给一条退路，不丢掉正在编辑的内容
        const again = await confirmDialog('这个方案已经不在了', '它可能在游戏里或另一个网页里被删掉了。要把你现在编辑的内容另存为一个新方案吗？', '另存为新方案', 'btn-gold', '先不保存');
        if (again && state.editor === e) { e.lineupId = null; return await saveLineup(); }
        refreshContextQuietly(true);
        return false;
      }
      if (err.code === 'BOARD_INVALID' && Array.isArray(err.details)) {
        for (const det of err.details) {
          const d = Number(det.day);
          const v = e.validation[d] || {};
          e.validation[d] = { ok: false, errors: [...(v.errors || []), det.messageZh], warnings: v.warnings || [], checking: false };
        }
        refreshTimeline();
        renderDayPanel();
      }
      showError(err, '保存失败');
      return false;
    } finally {
      const b2 = $('btnSaveLineup');
      if (b2) { b2.textContent = '保存'; updateSaveButton(); }
    }
  }

  async function useLineupForNextRun() {
    const e = state.editor;
    if (!e) return;
    if (!Object.keys(e.days).length) { toast('这个方案还没安排任何一天的对手', 'warn'); return; }
    if (e.dirty || !e.lineupId) {
      const ok = await saveLineup();
      if (!ok) return;
    }
    const summary = state.lineups.find((l) => l.lineupId === state.editor.lineupId);
    const need = (summary ? summary.requiredModIds : state.editor.requiredModIds) || [];
    const mine = new Set(rules().selectedModIds || []);
    const defaults = new Set(serverDefaultMods().map((m) => m.modId));
    const available = modsById();
    const toAdd = need.filter((id) => !mine.has(id) && !defaults.has(id) && available.has(id));
    let addModIds = [];
    if (toAdd.length) {
      const ok = await confirmDialog('这个方案用到了 Mod', [
        h('p', { text: `方案里有这些 Mod 的卡牌，你的游戏设置里还没开：${toAdd.map(modName).join('、')}。` }),
        h('p', { class: 'muted', text: '一起开启后，下一局会带上这些 Mod；不开的话，这些卡牌在开局时会被去掉。' }),
      ], '一起开启', 'btn-gold', '不开，只用方案');
      if (ok) addModIds = toAdd;
    }
    const days = Object.keys(state.editor.days).map(Number).sort((a, b) => a - b);
    const seed = state.editor.seedText;
    const patch = { ghostOpponentMode: 'FixedLineup', selectedGhostLineupId: state.editor.lineupId, addModIds };
    if (seed) patch.customSeedText = seed; // 方案配了种子：种子一起换上，事件才和方案对得上
    const r = await applyRulesPatch(patch,
      `下一局起：${seed ? `种子 ${seed}，` : ''}${fmtDays(days)}的对手按方案出战，其他天随机`);
    if (r) { renderEditor(); renderLineupList(); syncSeedInputFromRules(true); }
  }

  /** 给方案配种子：只认固定格式的种子码。 */
  function editLineupSeed() {
    const e = state.editor;
    if (!e) return;
    const inp = h('input', { type: 'text', class: 'mono', maxlength: 12, placeholder: '例如 S3KZ9QW1', value: e.seedText || '', spellcheck: 'false' });
    const msg = h('div', { class: 'muted', text: SEED_FORMAT_HINT });
    const cur = state.ctx && state.ctx.currentSeedText;
    const rr = state.ctx && (state.ctx.activeRun || state.ctx.recentRun);
    const heroSel = h('select', null, state.heroes.map((x) => h('option', { value: x.id, selected: x.id === (e.seedHero || (rr && rr.hero) || (state.heroes[0] && state.heroes[0].id)) }, x.nameZh)));
    const modal = openModal({
      title: '给这个方案配种子',
      body: [
        h('p', { class: 'muted', text: '配了种子以后：分享这个方案，朋友会遇到和你一样的事件和商店；点「下一局用它」，种子也会一起换上。' }),
        h('div', { class: 'seed-input-row' }, inp,
          h('button', {
            type: 'button', class: 'btn btn-subtle',
            onclick: async () => { try { const r = await api('seed/random', { method: 'POST', json: {} }); inp.value = r.seedText || ''; } catch (err) { showError(err, '生成失败'); } },
          }, '🎲 随机'),
          cur ? h('button', { type: 'button', class: 'btn btn-subtle', onclick: () => { inp.value = cur; } }, `用下一局的种子 ${cur}`) : null),
        msg,
        h('label', { class: 'field', style: { maxWidth: '260px', marginTop: '10px' } },
          h('span', { text: '这个种子用哪个英雄打（换英雄事件会不一样）' }), heroSel),
      ],
      footer: [
        { label: '取消', kind: 'btn-subtle' },
        {
          label: '确定', kind: 'btn-gold', onClick: () => {
            const code = normalizeSeedCode(inp.value);
            if (!code) { msg.className = 'err-box'; msg.textContent = `格式不对。${SEED_FORMAT_HINT}`; return false; }
            e.seedText = code;
            e.seedHero = heroSel.value || null;
            markDirty();
            renderEditor();
            modal.close();
            return true;
          },
        },
      ],
    });
    setTimeout(() => { inp.focus(); inp.select(); }, 30);
  }

  /** 分享当前方案：先保存，再生成分享码（种子 + 方案里每一天的对手）。 */
  async function shareLineup() {
    const e = state.editor;
    if (!e) return;
    if (!Object.keys(e.days).length) { toast('这个方案还没安排任何一天的对手', 'warn'); return; }
    if (!e.seedText && !(state.ctx && state.ctx.currentSeedText)) {
      toast('先给方案配一个种子（名字下面的「配套种子」）', 'warn');
      editLineupSeed();
      return;
    }
    if (e.dirty || !e.lineupId) {
      const ok = await saveLineup();
      if (!ok) return;
    }
    try {
      const r = await api('blueprints/export', {
        method: 'POST',
        json: { source: 'lineup', lineupId: state.editor.lineupId, title: state.editor.name, includeRules: false, hero: state.editor.seedHero || null },
      });
      openShareResultModal(r, state.editor.name);
      loadMine();
    } catch (err) {
      if (err.code === 'SEED_UNAVAILABLE') { toast(err.message, 'warn'); editLineupSeed(); return; }
      showError(err, '生成分享码失败');
    }
  }

  function openShareResultModal(r, title) {
    const body = h('div', { class: 'export-result' });
    renderShareCodes(body, r);
    openModal({ title: title ? `分享「${title}」` : '分享码', size: 'wide', body: [body], footer: [{ label: '好了', kind: 'btn-gold' }] });
  }

  /** 分享码展示：同服短码 + 跨服长码 / 文件，两块分开写清楚。网页分享页和弹窗共用。 */
  function renderShareCodes(box, r) {
    const s = r.summary || {};
    const ghostDays = s.ghostDays || [];
    box.appendChild(h('div', { class: 'summary-grid' },
      h('span', { text: '种子' }), h('span', { class: 'mono', text: s.seedText || '' }),
      h('span', { text: '对手' }), h('span', { text: ghostDays.length ? `${fmtDays(ghostDays)}固定，其他天随机` : '全随机（只带了种子）' }),
      h('span', { text: '英雄' }), h('span', { text: `${s.heroZh || heroName(s.hero)}（朋友开局选同一个英雄，事件才一样）` }),
      h('span', { text: '规则' }), h('span', { text: s.includeRules ? '带上了你的规则' : '不带规则' })));

    box.appendChild(h('div', { class: 'share-block' },
      h('div', { class: 'share-block-title', text: '① 朋友和你在同一个服务器' }),
      h('div', { class: 'code-big' },
        h('span', { class: 'code', text: r.shortCode }),
        h('button', { type: 'button', class: 'btn btn-gold btn-sm', onclick: () => copyText(r.shortCode, '短码已复制') }, '复制短码'))));

    const longArea = h('textarea', { readonly: true, rows: 2, class: 'mono long-code' });
    longArea.value = r.offlineCode || '';
    box.appendChild(h('div', { class: 'share-block' },
      h('div', { class: 'share-block-title', text: '② 朋友在别的服务器（短码在那边用不了）' }),
      longArea,
      h('div', { class: 'btn-row' },
        h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: () => copyText(r.offlineCode || '', '长码已复制') }, '复制长码'),
        h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: () => downloadShareFile(r) }, '⬇ 下载成分享文件')),
      h('div', { class: 'muted small-note', text: '长码直接粘贴就能导入（游戏里也行）；嫌长就发文件，对方在「分享码」页点「从文件导入」。' })));
  }

  function downloadShareFile(r) {
    const s = r.summary || {};
    const name = `大巴扎分享_${s.seedText || 'seed'}_${(r.shortCode || '').replace(/[^A-Z0-9-]/gi, '')}.txt`;
    const blob = new Blob([`${r.offlineCode || ''}\r\n`], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = h('a', { href: url, download: name });
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    toast('分享文件已下载，发给朋友即可');
  }

  /** 把一局（进行中 / 刚打完的）存成方案：打过的天用实际遇到的对手，没打到的天自动从镜像池补。 */
  async function openFromRunModal() {
    if (!(await guardDirty())) return;
    const a = state.ctx && state.ctx.activeRun;
    const rr = state.ctx && state.ctx.recentRun;
    const opts = [];
    if (a && !a.isFinished && a.seedText) opts.push({ value: 'active', run: a, label: `正在打的这局：${a.heroZh || heroName(a.hero)} · 第 ${a.day} 天` });
    if (rr && rr.seedText) opts.push({ value: 'recent', run: rr, label: `上一局：${rr.heroZh || heroName(rr.hero)} · 打到第 ${rr.day} 天` });
    if (!opts.length) { toast('还没有能用的对局：先去游戏里打一局', 'warn'); return; }
    if (state.lineups.length >= state.limits.maxLineups) { toast(`方案最多 ${state.limits.maxLineups} 套，请先删掉不用的`, 'warn'); return; }

    const srcGroup = h('div', { class: 'radio-cards' }, opts.map((o, i) => h('label', { class: 'radio-card' },
      h('input', { type: 'radio', name: 'fromRunSrc', value: o.value, checked: i === 0 }),
      h('span', { class: 'rc-title', text: o.label }),
      h('span', { class: 'rc-desc', text: `种子 ${o.run.seedText} · 已打 ${(o.run.facedDays || []).length} 天 PvP` }))));
    const fillSel = h('select', null, [10, 15, 20, 30].map((n) => h('option', { value: n, selected: n === 15 }, `补到第 ${n} 天`)));
    let busy = false;
    const modal = openModal({
      title: '把一局存成方案（改完可以再分享）',
      body: [
        h('ol', { class: 'auth-steps' },
          h('li', { text: '打过的天：放你实际遇到的对手' }),
          h('li', { text: '还没打到的天：按天数自动从镜像池挑一个普通强度的真实阵容，标着「补」，不喜欢就换' }),
          h('li', { text: '这局的种子会记成方案的「配套种子」，分享时一起带走' })),
        srcGroup,
        h('label', { class: 'field', style: { maxWidth: '200px' } }, h('span', { text: '一直补到' }), fillSel),
      ],
      footer: [
        { label: '取消', kind: 'btn-subtle' },
        {
          label: '存成方案', kind: 'btn-gold', onClick: async () => {
            if (busy) return false;   // 防止连点建出两个方案
            busy = true;
            const src = (modal.modal.querySelector('input[name="fromRunSrc"]:checked') || {}).value || 'active';
            try {
              const r = await api('lineups/from-run', { method: 'POST', json: { source: src, fillToDay: parseInt(fillSel.value, 10) || 15 } });
              modal.close();
              await refreshContextQuietly(true);
              openSeq++;
              state.editor = editorFromFull(r.lineup);
              state.editor.autoDays = new Set(r.autoFilledDays || []);
              const maxDay = Math.max(0, ...Object.keys(state.editor.days).map(Number));
              if (maxDay > VISIBLE_DAYS_DEFAULT) state.editor.showAllDays = true;
              switchTab('lineups');
              renderEditor();
              renderLineupList();
              ensureCards(Object.values(state.editor.days));
              toast(r.messageZh || '已存成方案', 'ok', 7000);
            } catch (err) {
              showError(err, '存成方案失败');
            } finally {
              busy = false;
            }
            return false;
          },
        },
      ],
    });
  }

  function duplicateLineup() {
    const e = state.editor;
    if (!e) return;
    if (state.lineups.length >= state.limits.maxLineups) { toast(`方案最多 ${state.limits.maxLineups} 套`, 'warn'); return; }
    openSeq++;
    state.editor = Object.assign(deepClone({ ...e, validation: {}, editVersion: {}, autoDays: null }), {
      lineupId: null, name: `${e.name || '方案'}（副本）`.slice(0, state.limits.maxLineupName), dirty: true, sourceType: 'custom',
      autoDays: new Set(e.autoDays || []),
    });
    renderEditor();
    renderLineupList();
    toast(e.dirty && e.lineupId ? '已复制为新方案（原方案没保存的修改只带到了副本里），记得保存' : '已复制为新方案，记得保存');
    revalidateAllDays();
  }

  async function discardNewLineup() {
    if (!state.editor || state.editor.lineupId) return;
    if (!(await guardDirty())) return;
    openSeq++;
    state.editor = null;
    renderEditor();
    renderLineupList();
  }

  async function deleteLineup() {
    const e = state.editor;
    if (!e || !e.lineupId) return;
    const ok = await confirmDialog('删除方案', `确定删除方案「${e.name}」？删除后不能恢复。`, '删除', 'btn-danger');
    if (!ok) return;
    try {
      const r = await api(`lineups/${encodeURIComponent(e.lineupId)}`, { method: 'DELETE' });
      state.editor = null;
      await refreshContextQuietly(true);
      renderEditor();
      renderLineupList();
      toast(r.rulesChanged ? '已删除；它原本是下一局的对手，现在改回全随机' : '已删除');
    } catch (err) {
      showError(err, '删除失败');
    }
  }

  // ════════════════ Tab 2：种子 ════════════════
  function syncSeedInputFromRules(force = false) {
    const inp = $('seedInput');
    if (!inp) return;
    // 用户正在改、或改了还没点应用时，不拿服务器的值覆盖输入框
    if (force || (!state.seedInputDirty && document.activeElement !== inp)) { inp.value = (state.ctx && state.ctx.currentSeedText) || ''; state.seedInputDirty = false; }
    renderSeedStatus();
  }

  function renderSeedStatus(extraKind, extraMsg) {
    const st = $('seedStatus');
    clear(st);
    const r = rules();
    const cur = state.ctx && state.ctx.currentSeedText;
    if (extraMsg) st.appendChild(h('div', { class: extraKind === 'err' ? 'err-box' : extraKind === 'warn' ? 'warn-box' : 'ok-box', text: extraMsg }));
    if (cur) {
      st.appendChild(h('div', null, '下一局种子：', h('b', { class: 'mono', text: cur })));
    } else {
      st.appendChild(h('div', { class: 'muted', text: '现在没有设置种子，下一局是普通随机局。' }));
    }
    if (cur && r.specialModeId === 'DailyChallenge') {
      st.appendChild(h('div', { class: 'warn-box', text: '你当前选的是「每日挑战」模式：每日挑战使用当天的官方种子，这里的种子要切换到其他模式后才会生效。' }));
    }
    const run = state.ctx && state.ctx.activeRun;
    if (run && !run.isFinished) {
      st.appendChild(h('div', { class: 'hint-box', text: `你有一局正在进行（${run.heroZh || heroName(run.hero)} 第 ${run.day} 天）。它不受影响，新种子从下一局开始用。` }));
    }
  }

  async function applySeed() {
    const raw = $('seedInput').value.trim();
    const v = raw ? normalizeSeedCode(raw) : '';
    if (raw && !v) { renderSeedStatus('err', `种子码格式不对。${SEED_FORMAT_HINT}`); return; }
    $('seedInput').value = v;
    const btn = $('btnSeedApply');
    btn.disabled = true;
    try {
      const r = await applyRulesPatch({ customSeedText: v }, null,
        (e) => renderSeedStatus('err', e.code === 'SEED_INVALID' ? `种子格式不对：${e.message}` : `保存失败：${e.message}`));
      if (r) {
        syncSeedInputFromRules(true);
        renderSeedStatus('ok', v ? '已应用。' : '已清空种子，下一局恢复随机。');
        renderGameStrip();
      }
    } finally {
      btn.disabled = false;
    }
  }

  async function clearSeed() {
    $('seedInput').value = '';
    await applySeed();
  }

  async function randomSeed() {
    try {
      const r = await api('seed/random', { method: 'POST', json: {} });
      $('seedInput').value = r.seedText || '';
      state.seedInputDirty = true;
      renderSeedStatus('warn', '已生成一个随机种子，点「应用到下一局」才会生效。');
    } catch (e) {
      showError(e, '生成失败');
    }
  }

  function fillHeroSelect(sel, preferred) {
    const cur = sel.value || preferred;
    clear(sel);
    for (const x of state.heroes) sel.appendChild(h('option', { value: x.id, selected: x.id === cur }, x.nameZh));
  }

  async function previewCalendar(seedOverride, heroOverride, highlight) {
    const rawSeed = (seedOverride || $('calSeed').value.trim() || $('seedInput').value.trim() || (state.ctx && state.ctx.currentSeedText) || '').trim();
    if (!rawSeed) { toast('先填一个种子（上面的「下一局的种子」或这里的输入框）', 'warn'); return; }
    const seedText = normalizeSeedCode(rawSeed);
    if (!seedText) { toast(`种子码格式不对。${SEED_FORMAT_HINT}`, 'warn', 6000); return; }
    if (heroOverride) $('calHero').value = heroOverride;
    if (seedOverride) $('calSeed').value = seedOverride;
    state.calendarHighlight = highlight || null;
    const out = $('calendarOut');
    clear(out).appendChild(h('div', { class: 'loading-line', text: '正在推演…' }));
    const btn = $('btnCalPreview');
    btn.disabled = true;
    try {
      const r = await api('seed-preview', {
        method: 'POST',
        json: { seedText, hero: $('calHero').value, maxDay: parseInt($('calDays').value, 10) || 5, selectedModIds: state.modEnv || [], useMyRules: $('calUseRules').checked },
      });
      renderCalendar(r);
    } catch (e) {
      clear(out).appendChild(h('div', { class: 'err-box', text: e.message }));
    } finally {
      btn.disabled = false;
    }
  }

  function renderCalendar(r) {
    const out = $('calendarOut');
    clear(out);
    const hl = state.calendarHighlight;
    const isHit = (day, hour, tid) => !!(hl && hl.some((m) => m.day === day && m.hour === hour && m.templateId === tid));
    out.appendChild(h('div', { class: 'muted', style: { marginBottom: '4px' } }, '种子 ', h('b', { class: 'mono', text: r.seedText }), ` · ${heroName(r.hero)}`));
    out.appendChild(h('div', { class: 'legend' },
      h('span', null, h('i', { style: { background: 'var(--blue)' } }), '奇遇'),
      h('span', null, h('i', { style: { background: 'var(--gold-main)' } }), '商人'),
      h('span', null, h('i', { style: { background: 'var(--red)' } }), '野怪'),
      h('span', null, h('i', { style: { background: 'var(--tier-legendary)' } }), '祭坛'),
      hl ? h('span', null, h('i', { style: { background: 'var(--green)' } }), '你要找的') : null));
    // 小时列按服务端实际返回的来，不写死；某一列全是野怪就标注出来
    const hourSet = new Set();
    for (const dr of r.calendar || []) for (const x of dr.hours || []) hourSet.add(x.hour);
    const hours = [...hourSet].sort((a, b) => a - b);
    const allMonster = (hr) => (r.calendar || []).every((dr) => {
      const o = (dr.hours || []).find((x) => x.hour === hr);
      return !o || !(o.options || []).length || o.options.every((op) => op.kind === 'monster');
    });
    const table = h('table', { class: 'calendar' },
      h('thead', null, h('tr', null, h('th', null, ''), hours.map((x) => h('th', { text: allMonster(x) ? `第 ${x} 小时（野怪）` : `第 ${x} 小时` })))));
    const tbody = h('tbody');
    for (const dayRow of r.calendar || []) {
      const tr = h('tr', null, h('td', { class: 'day-cell', text: `第${dayRow.day}天` }));
      for (const hr of hours) {
        const cell = h('td');
        const hourObj = (dayRow.hours || []).find((x) => x.hour === hr);
        if (!hourObj || !hourObj.options || !hourObj.options.length) cell.appendChild(h('span', { class: 'muted', text: '—' }));
        else for (const o of hourObj.options) {
          cell.appendChild(h('span', { class: `opt k-${o.kind || 'event'} ${isHit(dayRow.day, hr, o.templateId) ? 'hit' : ''}`, title: KIND_ZH[o.kind] || '', text: o.nameZh || o.templateId }));
        }
        tr.appendChild(cell);
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    out.appendChild(table);
    for (const n of r.notes || []) out.appendChild(h('p', { class: 'muted small-note', text: n }));
    out.appendChild(h('div', { class: 'btn-row' },
      h('button', { type: 'button', class: 'btn btn-gold btn-sm', onclick: () => useSeed(r.seedText) }, '用这个种子开下一局'),
      h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: () => copyText(r.seedText, '种子已复制') }, '复制种子')));
  }

  async function useSeed(seedText) {
    $('seedInput').value = seedText;
    const r = await applyRulesPatch({ customSeedText: seedText }, `下一局种子已设为 ${seedText}`);
    if (r) { syncSeedInputFromRules(true); renderGameStrip(); }
  }

  // ── 搜种器 ──
  async function loadEvents(hero) {
    const key = `${hero}|${(state.modEnv || []).join(',')}`;
    if (state.eventsCache.has(key)) return state.eventsCache.get(key);
    const r = await api('catalog/events', { query: { hero, selectedModIds: state.modEnv || [] } });
    const items = r.items || [];
    state.eventsCache.set(key, items);
    return items;
  }

  function ensureFinderInit() {
    fillHeroSelect($('calHero'), $('calHero').value || (state.heroes[0] && state.heroes[0].id));
    fillHeroSelect($('findHero'), $('findHero').value || (state.heroes[0] && state.heroes[0].id));
    if (!state.finder.conds.length) state.finder.conds.push(newCond());
    renderConds();
    syncSeedInputFromRules();
  }

  function newCond() { return { templateId: '', nameZh: '', kind: '', windowMin: 1, windowMax: 10, minDay: 1, maxDay: 10, text: '' }; }

  function renderConds() {
    const box = $('condList');
    clear(box);
    state.finder.conds.forEach((c, idx) => box.appendChild(renderCondRow(c, idx)));
    $('btnAddCond').disabled = state.finder.conds.length >= 5 || state.finder.running;
  }

  function renderCondRow(c, idx) {
    const input = h('input', { type: 'text', placeholder: '输入事件、商人或野怪的名字…', value: c.nameZh || c.text, disabled: state.finder.running });
    const list = h('div', { class: 'combo-list hidden' });
    const combo = h('div', { class: 'combo' }, input, list);
    const daySel = (val, onChange) => {
      const sel = h('select', { disabled: !c.templateId || state.finder.running });
      for (let d = c.windowMin; d <= c.windowMax; d++) sel.appendChild(h('option', { value: d, selected: d === val }, `第 ${d} 天`));
      sel.addEventListener('change', () => onChange(parseInt(sel.value, 10)));
      return sel;
    };
    const minSel = daySel(c.minDay, (v) => { c.minDay = v; if (c.maxDay < v) { c.maxDay = v; renderConds(); } });
    const maxSel = daySel(c.maxDay, (v) => { c.maxDay = v; if (c.minDay > v) { c.minDay = v; renderConds(); } });

    let hlIdx = -1;
    let options = [];
    const pick = (ev) => {
      c.templateId = ev.templateId;
      c.nameZh = ev.nameZh;
      c.kind = ev.kind;
      c.windowMin = Math.max(1, ev.minDay || 1);
      c.windowMax = Math.max(c.windowMin, Math.min(ev.maxDay || 10, 10));
      c.minDay = c.windowMin;
      c.maxDay = c.windowMax;
      renderConds();
    };
    const showList = async () => {
      let events;
      try { events = await loadEvents($('findHero').value); } catch (e) { list.classList.add('hidden'); showError(e, '读取事件列表失败'); return; }
      const q = input.value.trim().toLowerCase();
      options = events.filter((ev) => !q || (ev.nameZh || '').toLowerCase().includes(q)).slice(0, 60);
      clear(list);
      hlIdx = -1;
      if (!options.length) list.appendChild(h('div', { class: 'combo-opt', text: '没有匹配的事件' }));
      options.forEach((ev, i) => list.appendChild(h('div', {
        class: 'combo-opt',
        onmousedown: (e2) => { e2.preventDefault(); pick(ev); },
        dataset: { i: String(i) },
      }, h('span', { text: ev.nameZh }), h('small', { text: `${KIND_ZH[ev.kind] || ''} · 第 ${ev.minDay}–${ev.maxDay} 天${ev.isMod ? ' · Mod' : ''}` }))));
      list.classList.remove('hidden');
    };
    input.addEventListener('focus', showList);
    input.addEventListener('input', () => { c.text = input.value; c.templateId = ''; minSel.disabled = true; maxSel.disabled = true; showList(); });
    input.addEventListener('blur', () => setTimeout(() => list.classList.add('hidden'), 120));
    input.addEventListener('keydown', (ev) => {
      const nodes = list.querySelectorAll('.combo-opt[data-i]');
      if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
        ev.preventDefault();
        if (!nodes.length) return;
        hlIdx = (hlIdx + (ev.key === 'ArrowDown' ? 1 : -1) + nodes.length) % nodes.length;
        nodes.forEach((n, i) => n.classList.toggle('hl', i === hlIdx));
        nodes[hlIdx].scrollIntoView({ block: 'nearest' });
      } else if (ev.key === 'Enter' && hlIdx >= 0 && options[hlIdx]) {
        ev.preventDefault();
        pick(options[hlIdx]);
      }
    });

    return h('div', { class: 'cond-row' },
      h('label', { class: 'field' }, h('span', { text: `条件 ${idx + 1}：想遇到` }), combo),
      h('label', { class: 'field' }, h('span', { text: '最早' }), minSel),
      h('label', { class: 'field' }, h('span', { text: '最晚' }), maxSel),
      h('button', {
        type: 'button', class: 'btn btn-subtle btn-sm', title: '删除这个条件', disabled: state.finder.running,
        onclick: () => { state.finder.conds.splice(idx, 1); if (!state.finder.conds.length) state.finder.conds.push(newCond()); renderConds(); },
      }, '删除'));
  }

  function stopFinderPolling() {
    if (state.finder.timer) clearTimeout(state.finder.timer);
    state.finder.timer = null;
  }

  function setFinderRunning(running) {
    state.finder.running = running;
    $('btnFindStart').classList.toggle('hidden', running);
    $('btnFindStop').classList.toggle('hidden', !running);
    $('findHero').disabled = running;
    $('findUseRules').disabled = running;
    renderConds();
  }

  async function startFinder(continueFrom) {
    const conds = state.finder.conds.filter((c) => c.templateId);
    if (!conds.length) { toast('至少选一个想遇到的事件', 'warn'); return; }
    if (state.finder.conds.some((c) => !c.templateId && (c.text || '').trim())) { toast('有条件还没从下拉列表里选中具体事件', 'warn'); return; }
    const req = continueFrom ? Object.assign({}, state.finder.lastReq, { startSeed: continueFrom }) : {
      hero: $('findHero').value,
      selectedModIds: state.modEnv || [],
      useMyRules: $('findUseRules').checked,
      maxDay: Math.max(...conds.map((c) => c.maxDay)),
      startSeed: null,
      conditions: conds.map((c) => ({ templateId: c.templateId, minDay: c.minDay, maxDay: c.maxDay })),
    };
    if (!continueFrom) { state.finder.hits = []; state.finder.scanned = 0; renderFinderResults(); }
    state.finder.lastReq = req;
    state.finder.nextSeed = null;
    try {
      const r = await api('seed-finder/jobs', { method: 'POST', json: req });
      state.finder.jobId = r.jobId;
      state.finder.scannedBase = state.finder.scanned;
      setFinderRunning(true);
      $('finderProgress').classList.remove('hidden');
      $('finderBar').style.width = '';
      $('finderBar').classList.add('indeterminate');
      $('finderText').textContent = '已开始…';
      pollFinder();
    } catch (e) {
      if (e.code === 'CONDITION_IMPOSSIBLE' && Array.isArray(e.details)) {
        const box = $('finderResults');
        clear(box).appendChild(h('div', { class: 'err-box' }, h('b', { text: '有条件不可能满足：' }), h('ul', null, e.details.map((d) => {
          const c = conds.find((x) => x.templateId === d.templateId);
          return h('li', { text: `${c ? c.nameZh : d.templateId}：${d.reasonZh}` });
        }))));
      } else {
        showError(e, '开始搜索失败');
      }
    }
  }

  async function pollFinder() {
    stopFinderPolling();
    const id = state.finder.jobId;
    if (!id) return;
    let r;
    try {
      r = await api(`seed-finder/jobs/${encodeURIComponent(id)}`);
    } catch (e) {
      if (state.finder.jobId !== id) return;
      $('finderText').textContent = `查询进度失败：${e.message}（稍后重试）`;
      state.finder.timer = setTimeout(pollFinder, 3000);
      return;
    }
    if (state.finder.jobId !== id) return;
    const known = new Set(state.finder.hits.map((x) => x.masterSeed));
    for (const hit of r.hits || []) if (!known.has(hit.masterSeed)) state.finder.hits.push(hit);
    state.finder.scanned = (state.finder.scannedBase || 0) + (r.scanned || 0);
    state.finder.nextSeed = r.nextSeed;
    const sec = ((r.elapsedMs || 0) / 1000).toFixed(1);
    const statusZh = { queued: '排队中（前面有别人在搜，稍等）', running: '搜索中', done: '搜索结束', cancelled: '已停止', failed: '搜索出错' }[r.status] || r.status;
    const extra = r.messageZh && r.messageZh !== statusZh ? ` · ${r.messageZh}` : '';
    $('finderText').textContent = `${statusZh} · 已试 ${state.finder.scanned} 个种子 · ${sec} 秒 · 找到 ${state.finder.hits.length} 个${extra}`;
    renderFinderResults();
    if (r.status === 'queued' || r.status === 'running') {
      state.finder.timer = setTimeout(pollFinder, 1000);
    } else {
      state.finder.jobId = null;
      $('finderBar').classList.remove('indeterminate');
      $('finderBar').style.width = '100%';
      setFinderRunning(false);
      renderFinderResults();
    }
  }

  async function stopFinder() {
    const id = state.finder.jobId;
    if (!id) return;
    try { await api(`seed-finder/jobs/${encodeURIComponent(id)}`, { method: 'DELETE' }); } catch { /* 停止失败也会在下次轮询结束 */ }
  }

  function renderFinderResults() {
    const box = $('finderResults');
    clear(box);
    for (const hit of state.finder.hits) {
      const matched = (hit.matched || []).map((m) => `第${m.day}天第${m.hour}小时 ${m.nameZh || ''}`).join('；');
      box.appendChild(h('div', { class: 'hit-row' },
        h('span', { class: 'hit-seed', text: hit.seedText }),
        h('span', { class: 'hit-matched', text: matched }),
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => { previewCalendar(hit.seedText, state.finder.lastReq && state.finder.lastReq.hero, hit.matched); $('calendarCard').scrollIntoView({ behavior: 'smooth' }); } }, '看日历'),
        h('button', { type: 'button', class: 'btn btn-gold btn-xs', onclick: () => useSeed(hit.seedText) }, '用这个种子'),
        h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => copyText(hit.seedText, '种子已复制') }, '复制')));
    }
    if (!state.finder.running && state.finder.lastReq && state.finder.nextSeed) {
      box.appendChild(h('div', { class: 'btn-row' },
        h('span', { class: 'muted', text: state.finder.hits.length ? '想要更多候选？' : '这一轮没找到，条件可能比较难，可以继续搜或放宽天数。' }),
        h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: () => startFinder(state.finder.nextSeed) }, '继续搜')));
    }
  }

  // ════════════════ Tab 3：分享 ════════════════
  function runLabel(run, prefix) {
    const faced = (run.facedDays || []).length;
    return `${prefix}：${run.heroZh || heroName(run.hero)} · 第 ${run.day} 天${run.isFinished ? ' · 已结束' : ''} · 已打 ${faced} 天 PvP`;
  }

  function renderExportSources() {
    const group = $('exportSourceGroup');
    if (!group || !state.ctx) return;
    const prev = (group.querySelector('input:checked') || {}).value;
    clear(group);
    const opts = [];
    const a = state.ctx.activeRun;
    const rr = state.ctx.recentRun;
    if (a && !a.isFinished) opts.push({ value: 'active', title: '进行中的对局', desc: a.seedText ? runLabel(a, '当前') : '这局没有种子，不能分享', ok: !!a.seedText, run: a });
    if (rr) opts.push({ value: 'recent', title: '最近一局', desc: rr.seedText ? runLabel(rr, '上一局') : '这局没有种子，不能分享', ok: !!rr.seedText, run: rr });
    const lineupsWithDays = state.lineups.filter((l) => (l.dayNumbers || []).length);
    opts.push({
      value: 'lineup', title: '我的一个对手方案',
      desc: lineupsWithDays.length ? '种子 + 方案里安排的每一天（包括没打到的天）' : '还没有安排过对手的方案',
      ok: lineupsWithDays.length > 0, run: null,
    });
    const seed = state.ctx.currentSeedText;
    opts.push({ value: 'rules', title: '只分享我设置的种子', desc: seed ? `种子 ${seed}（不带对手）` : '先在「找种子」里设一个种子', ok: !!seed, run: null });
    let firstOk = opts.find((o) => o.ok && o.value === prev) || opts.find((o) => o.ok);
    for (const o of opts) {
      const radio = h('input', { type: 'radio', name: 'exportSource', value: o.value, disabled: !o.ok, checked: firstOk === o });
      radio.addEventListener('change', updateExportScopeAvailability);
      group.appendChild(h('label', { class: `radio-card ${o.ok ? '' : 'disabled'}`, dataset: { faced: o.run ? String((o.run.facedDays || []).length) : '0' } },
        radio, h('span', { class: 'rc-title', text: o.title }), h('span', { class: 'rc-desc', text: o.desc })));
    }
    $('btnExport').disabled = !firstOk;
    updateExportScopeAvailability();
  }

  function updateExportScopeAvailability() {
    const sel = document.querySelector('input[name="exportSource"]:checked');
    const source = sel ? sel.value : '';
    const card = sel ? sel.closest('.radio-card') : null;
    const faced = card ? parseInt(card.dataset.faced || '0', 10) : 0;
    const ghostsRadio = document.querySelector('input[name="exportScope"][value="events_and_ghosts"]');
    const ghostsCard = $('scopeGhostsCard');
    const isLineup = source === 'lineup';
    $('exportScopeField').classList.toggle('hidden', isLineup);   // 分享方案时总是带对手，不用选
    $('exportLineupField').classList.toggle('hidden', !isLineup);
    if (isLineup) fillLineupSelect();
    const can = source !== 'rules' && faced > 0;
    ghostsRadio.disabled = !can;
    ghostsCard.classList.toggle('disabled', !can);
    $('scopeGhostsDesc').textContent = source === 'rules'
      ? '只导出种子时没有对手可带。'
      : (faced > 0 ? `种子 + 你实际遇到的 ${faced} 天对手（没打到的天朋友那边随机；想补齐就用「安排对手」里的「从对局生成」）。` : '这局还没打过 PvP，暂时没有对手可带。');
    if (!can && ghostsRadio.checked) document.querySelector('input[name="exportScope"][value="events_only"]').checked = true;
    if (can && !isLineup && !ghostsRadio.dataset.touched) ghostsRadio.checked = true; // 有对手可带时默认带上
    $('exportHeroField').classList.toggle('hidden', source !== 'rules');
    if (source === 'rules') fillHeroSelect($('exportHero'), $('exportHero').value || (state.heroes[0] && state.heroes[0].id));
    $('btnExport').disabled = !source;
  }

  function fillLineupSelect() {
    const sel = $('exportLineup');
    const prev = sel.value || (state.editor && state.editor.lineupId) || '';
    clear(sel);
    for (const l of state.lineups.filter((x) => (x.dayNumbers || []).length)) {
      sel.appendChild(h('option', { value: l.lineupId, selected: l.lineupId === prev },
        `${l.name}（${fmtDays(l.dayNumbers)}${l.seedText ? ` · 种子 ${l.seedText}` : ' · 没配种子'}）`));
    }
  }

  async function doExport() {
    const sel = document.querySelector('input[name="exportSource"]:checked');
    if (!sel) { toast('没有可以导出的对局或种子', 'warn'); return; }
    const scope = document.querySelector('input[name="exportScope"]:checked').value;
    const isLineup = sel.value === 'lineup';
    const lineupId = isLineup ? $('exportLineup').value : null;
    if (isLineup && !lineupId) { toast('先选一个方案', 'warn'); return; }
    if (isLineup && state.editor && state.editor.lineupId === lineupId && state.editor.dirty) {
      toast('这个方案还有没保存的修改：先去「安排对手」点保存，或直接用方案上的「🔗 分享」', 'warn', 6000);
      return;
    }
    const btn = $('btnExport');
    btn.disabled = true;
    btn.textContent = '生成中…';
    try {
      const lineupSummary = isLineup ? state.lineups.find((l) => l.lineupId === lineupId) : null;
      const r = await api('blueprints/export', {
        method: 'POST',
        json: {
          source: sel.value, scope: isLineup ? 'events_and_ghosts' : scope, includeRules: $('exportIncludeRules').checked,
          title: $('exportTitle').value.trim() || (lineupSummary ? lineupSummary.name : ''),
          hero: sel.value === 'rules' ? $('exportHero').value : (lineupSummary ? lineupSummary.seedHero || null : null),
          lineupId,
        },
      });
      state.lastExport = r;
      renderExportResult(r);
      loadMine();
    } catch (e) {
      showError(e, '生成失败');
    } finally {
      btn.disabled = false;
      btn.textContent = '生成分享码';
    }
  }

  function renderExportResult(r) {
    const box = $('exportResult');
    clear(box);
    box.classList.remove('hidden');
    renderShareCodes(box, r);
    box.appendChild(h('p', { class: 'muted small-note', text: '朋友在游戏主菜单点「分享码」粘贴就行，也可以在这个网页的「分享码」页导入。' }));
  }

  /** 从分享文件导入：读出文本放进输入框，直接识别。 */
  function importFromFile() {
    const inp = h('input', { type: 'file', accept: '.txt,.bzr,text/plain', style: { display: 'none' } });
    inp.addEventListener('change', async () => {
      const f = inp.files && inp.files[0];
      inp.remove();
      if (!f) return;
      if (f.size > 400 * 1024) { toast('这个文件太大了，不像是分享文件', 'err'); return; }
      try {
        const text = (await f.text()).trim();
        const m = /BZR1:[A-Za-z0-9_\-]+|BZ-[A-Z0-9]{4}-[A-Z0-9]{4}/.exec(text);
        if (!m) { toast('这个文件里没有找到分享码', 'err'); return; }
        $('importInput').value = m[0];
        doImportPreview();
      } catch (e) {
        showError(e, '读取文件失败');
      }
    });
    document.body.appendChild(inp);
    inp.click();
  }

  async function doImportPreview() {
    const code = $('importInput').value.trim();
    const box = $('importPreview');
    if (!code) { toast('先粘贴分享码', 'warn'); return; }
    state.importCode = code;
    clear(box).appendChild(h('div', { class: 'loading-line', text: '正在识别…' }));
    try {
      const p = await api('blueprints/preview', { method: 'POST', json: { code } });
      state.importPreview = p;
      renderImportPreview(p);
    } catch (e) {
      state.importPreview = null;
      clear(box).appendChild(h('div', { class: 'err-box', text: e.code === 'CODE_NOT_FOUND' ? '这个分享码在本服务器上不存在。如果朋友在别的服务器，请让他发长码（BZR1: 开头）或分享文件。' : e.message }));
    }
  }

  function renderImportPreview(p) {
    const box = $('importPreview');
    clear(box);
    const hasGhosts = p.scope === 'events_and_ghosts' && (p.ghostDays || []).length > 0;
    box.appendChild(h('div', { class: 'summary-grid' },
      p.title ? [h('span', { text: '标题' }), h('span', { text: p.title })] : null,
      h('span', { text: '来自' }), h('span', { text: `${p.creatorUsername || '未知'}${p.createdAtUtc ? ` · ${fmtTime(p.createdAtUtc)}` : ''}` }),
      h('span', { text: '英雄' }), h('span', { text: p.heroZh || heroName(p.hero) }),
      h('span', { text: '种子' }), h('span', { class: 'mono', text: p.seedText }),
      h('span', { text: '内容' }), h('span', { text: hasGhosts ? `事件 + ${fmtDays(p.ghostDays)}的对手` : '只有事件（对手随机）' }),
      h('span', { text: '规则' }), h('span', { text: p.hasRules ? (p.rulesSummaryZh || '带有规则') : '没有附带规则' })));
    box.appendChild(h('div', { class: 'hint-box', text: `这个种子是用「${p.heroZh || heroName(p.hero)}」打的。开局请选同一个英雄，否则事件会不一样。` }));

    const md = p.modDiff || {};
    const enableIds = new Set((md.autoEnable || []).map((m) => m.modId));
    if ((md.autoEnable || []).length) {
      const sec = h('div', { class: 'mod-diff-section ok-box' }, h('h5', { text: '需要这些 Mod（本服有，会一起开启）：' }));
      for (const m of md.autoEnable) {
        const cb = h('input', { type: 'checkbox', checked: true });
        cb.addEventListener('change', () => { if (cb.checked) enableIds.add(m.modId); else enableIds.delete(m.modId); });
        sec.appendChild(h('label', { class: 'inline-check mod-line' }, cb, `${m.name} v${m.version || '?'}${m.author ? `（${m.author}）` : ''}`));
      }
      box.appendChild(sec);
    }
    if ((md.missing || []).length) {
      box.appendChild(h('div', { class: 'mod-diff-section err-box' },
        h('h5', { text: '本服没有这些 Mod：' }),
        md.missing.map((m) => h('div', { class: 'mod-line', text: `${m.name || m.modId} v${m.version || '?'}${m.author ? `（${m.author}）` : ''}${(m.affectedDays || []).length ? ` — ${fmtDays(m.affectedDays)}的对手会去掉相关卡牌` : ''}` })),
        h('div', { class: 'mod-line', text: '仍然可以导入，但事件可能和原局不一样。' })));
    }
    if ((md.versionMismatch || []).length) {
      box.appendChild(h('div', { class: 'mod-diff-section warn-box' },
        h('h5', { text: 'Mod 版本不同（事件或数值可能有细微差别）：' }),
        md.versionMismatch.map((m) => h('div', { class: 'mod-line', text: `${m.name || m.modId}：本服 v${m.version || '?'}，原局 v${m.expectedVersion || '?'}` }))));
    }
    if ((md.serverExtra || []).length) {
      box.appendChild(h('div', { class: 'mod-diff-section warn-box' },
        h('h5', { text: '本服额外开着这些玩法 Mod，事件可能和原局不同：' }),
        md.serverExtra.map((m) => h('div', { class: 'mod-line', text: m.name || m.modId }))));
    }
    for (const w of p.warnings || []) box.appendChild(h('div', { class: 'warn-box', text: w }));

    const oppGroup = h('div', { class: 'radio-cards' },
      h('label', { class: `radio-card ${hasGhosts ? '' : 'disabled'}` },
        h('input', { type: 'radio', name: 'importOpp', value: 'lineup', checked: hasGhosts, disabled: !hasGhosts }),
        h('span', { class: 'rc-title', text: '按分享的对手' }),
        h('span', { class: 'rc-desc', text: hasGhosts ? `会新建一个方案，${fmtDays(p.ghostDays)}遇到同样的对手，其他天随机` : '这个分享没有带对手' })),
      h('label', { class: 'radio-card' },
        h('input', { type: 'radio', name: 'importOpp', value: 'random', checked: !hasGhosts }),
        h('span', { class: 'rc-title', text: '对手全随机' }),
        h('span', { class: 'rc-desc', text: '只用种子：事件一样，对手每晚随机' })));
    const rulesCb = h('input', { type: 'checkbox', disabled: !p.hasRules });
    box.appendChild(h('div', { class: 'field-group', style: { marginTop: '10px' } }, h('div', { class: 'group-label', text: '对手怎么安排' }), oppGroup));
    box.appendChild(h('label', { class: 'inline-check', style: { whiteSpace: 'normal' } }, rulesCb,
      p.hasRules ? `同时套用分享里的规则（${p.rulesSummaryZh || '模式 / 词条 / 开局数值'}）——会覆盖你现在的这些设置` : '这个分享没有附带规则'));
    if (p.hasActiveRun) box.appendChild(h('div', { class: 'hint-box', text: '你有一局正在进行，它不受影响；导入的设置从下一局开始生效。' }));

    const applyBtn = h('button', { type: 'button', class: 'btn btn-gold btn-block', style: { marginTop: '10px' } }, '套用到下一局');
    applyBtn.addEventListener('click', async () => {
      const opp = (box.querySelector('input[name="importOpp"]:checked') || {}).value || 'random';
      applyBtn.disabled = true;
      applyBtn.textContent = '导入中…';
      try {
        const r = await api('blueprints/import', {
          method: 'POST',
          json: { code: state.importCode, opponents: opp, applyRules: !!rulesCb.checked, enableModIds: [...enableIds], apply: true },
        });
        await refreshContextQuietly(true);
        syncSeedInputFromRules(true);
        clear(box).appendChild(h('div', { class: 'ok-box' },
          h('div', { text: r.messageZh || '导入成功，下一局生效' }),
          h('div', { class: 'btn-row' },
            r.lineup ? h('button', { type: 'button', class: 'btn btn-outline btn-sm', onclick: () => { switchTab('lineups'); openLineup(r.lineup.lineupId); } }, '去看看导入的方案') : null,
            h('button', { type: 'button', class: 'btn btn-subtle btn-sm', onclick: () => { $('importInput').value = ''; clear(box); } }, '再导入一个'))));
        renderGameStrip();
      } catch (e) {
        showError(e, '导入失败');
        applyBtn.disabled = false;
        applyBtn.textContent = '套用到下一局';
      }
    });
    box.appendChild(applyBtn);
  }

  async function loadMine() {
    const box = $('mineList');
    if (!box) return;
    try {
      const r = await api('blueprints/mine');
      clear(box);
      const items = r.items || [];
      if (!items.length) { box.appendChild(h('div', { class: 'loading-line', text: '还没有生成过分享码。' })); return; }
      const table = h('table', { class: 'mine-table' },
        h('thead', null, h('tr', null, ['分享码', '标题', '英雄', '内容', '被导入', '生成时间', ''].map((x) => h('th', { text: x })))));
      const tb = h('tbody');
      for (const it of items) {
        tb.appendChild(h('tr', null,
          h('td', { class: 'mono', text: it.shortCode }),
          h('td', { text: it.title || '—' }),
          h('td', { text: it.heroZh || heroName(it.hero) }),
          h('td', { text: it.scope === 'events_and_ghosts' ? '事件+对手' : '只有事件' }),
          h('td', { text: `${it.importCount || 0} 次` }),
          h('td', { text: fmtTime(it.createdAtUtc) }),
          h('td', null,
            h('button', { type: 'button', class: 'btn btn-subtle btn-xs', onclick: () => copyText(it.shortCode, '分享码已复制') }, '复制'), ' ',
            h('button', {
              type: 'button', class: 'btn btn-danger btn-xs',
              onclick: async () => {
                if (!(await confirmDialog('删除分享码', `删除后别人就不能再用 ${it.shortCode} 导入了。确定？`, '删除', 'btn-danger'))) return;
                try { await api(`blueprints/${encodeURIComponent(it.shortCode)}`, { method: 'DELETE' }); loadMine(); toast('已删除'); } catch (e) { showError(e, '删除失败'); }
              },
            }, '删除'))));
      }
      table.appendChild(tb);
      box.appendChild(table);
    } catch (e) {
      clear(box).appendChild(h('div', { class: 'err-box', text: e.message }));
    }
  }

  // ════════════════ 启动 ════════════════
  async function enterApp() {
    showApp();
    renderEditor();
    try {
      await loadContext();
    } catch (e) {
      if (e.status === 401) return;
      showAuth(`读取账号信息失败：${e.message}`, 'err');
      return;
    }
    state.unauthorizedShown = false;
    renderGameStrip();
    renderLineupList();
    renderEditor();
    ensureFinderInit();
    renderExportSources();
    syncSeedInputFromRules(true);
  }

  function bindStatic() {
    $('loginForm').addEventListener('submit', onLoginSubmit);
    $('btnLogout').addEventListener('click', logout);
    $('btnModEnv').addEventListener('click', openModEnvModal);
    document.querySelectorAll('.tab-btn').forEach((b) => b.addEventListener('click', () => switchTab(b.dataset.tab)));
    $('gsSeedEdit').addEventListener('click', () => { switchTab('seeds'); setTimeout(() => { $('seedInput').focus(); $('seedInput').select(); }, 30); });
    $('gsOppReset').addEventListener('click', resetOpponentsToRandom);
    $('btnNewLineup').addEventListener('click', newLineup);
    $('btnLineupFromRun').addEventListener('click', openFromRunModal);
    $('btnImportFile').addEventListener('click', importFromFile);
    document.querySelectorAll('input[name="exportScope"]').forEach((r) => r.addEventListener('change', () => {
      document.querySelector('input[name="exportScope"][value="events_and_ghosts"]').dataset.touched = '1';
    }));

    $('btnSeedApply').addEventListener('click', applySeed);
    $('btnSeedClear').addEventListener('click', clearSeed);
    $('btnSeedRandom').addEventListener('click', randomSeed);
    $('seedInput').addEventListener('keydown', (ev) => { if (ev.key === 'Enter') applySeed(); });
    $('seedInput').addEventListener('input', () => { state.seedInputDirty = true; });
    $('btnCalPreview').addEventListener('click', () => previewCalendar());
    $('calSeed').addEventListener('keydown', (ev) => { if (ev.key === 'Enter') previewCalendar(); });
    $('btnAddCond').addEventListener('click', () => { if (state.finder.conds.length < 5) { state.finder.conds.push(newCond()); renderConds(); } });
    $('findHero').addEventListener('change', () => { state.finder.conds = [newCond()]; renderConds(); });
    $('btnFindStart').addEventListener('click', () => startFinder(null));
    $('btnFindStop').addEventListener('click', stopFinder);


    $('btnExport').addEventListener('click', doExport);
    $('btnImportPreview').addEventListener('click', doImportPreview);
    $('importInput').addEventListener('keydown', (ev) => { if (ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey)) doImportPreview(); });
    $('btnImportClear').addEventListener('click', () => { $('importInput').value = ''; clear($('importPreview')); state.importPreview = null; });
    $('btnMineRefresh').addEventListener('click', loadMine);

    window.addEventListener('beforeunload', (ev) => {
      if (state.editor && state.editor.dirty && state.token) { ev.preventDefault(); ev.returnValue = ''; }
    });
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') refreshContextQuietly(false); });
    // 已经开着编排台时，游戏里再点「去网页详细编辑」可能只改了地址栏的 #ticket=…，页面不会重新加载
    window.addEventListener('hashchange', async () => {
      const mm = /[#&]ticket=([^&]+)/.exec(window.location.hash || '');
      if (!mm) return;
      const wantLineup = readHashLineup();
      history.replaceState(null, '', window.location.pathname + window.location.search);
      let tk = mm[1];
      try { tk = decodeURIComponent(tk); } catch { /* 保持原样 */ }
      if (await exchangeTicket(tk)) {
        state.unauthorizedShown = false;
        await enterApp(); // 同一账号时页面里没保存的编辑会保留；换了账号会自动清空
        if (wantLineup) openLineupFromLink(wantLineup);
        toast('已用游戏里的登录刷新');
      } else if (!state.token) {
        showAuth('这个打开链接已经失效（每个链接只能用一次，2 分钟内有效）。请回到游戏里再点一次「去网页详细编辑」。', 'warn');
      }
    });
    window.addEventListener('focus', () => refreshContextQuietly(false));
  }

  /** 游戏里「去网页改」会带上 #ticket=…&lineup=方案ID，登录后直接打开那个方案。 */
  function readHashLineup() {
    const mm = /[#&]lineup=([A-Za-z0-9_\-]{1,64})/.exec(window.location.hash || '');
    return mm ? mm[1] : null;
  }
  function openLineupFromLink(id) {
    switchTab('lineups');
    if (state.lineups.some((l) => l.lineupId === id)) openLineup(id);
    else toast('要打开的方案已经不在了（可能已经删掉）', 'warn');
  }

  async function boot() {
    bindStatic();
    const wantLineup = readHashLineup();
    const m = /[#&]ticket=([^&]+)/.exec(window.location.hash || '');
    if (m) {
      // 立刻从地址栏抹掉一次性票据，防止被历史记录 / 截图带走
      history.replaceState(null, '', window.location.pathname + window.location.search);
      let ticket = m[1];
      try { ticket = decodeURIComponent(ticket); } catch { /* 保持原样 */ }
      const ok = await exchangeTicket(ticket);
      if (!ok) {
        if (state.token) {
          toast('这个打开链接已经用过或过期了，先沿用这个页面原来的登录', 'warn', 6000);
        } else {
          showAuth('这个打开链接已经失效（每个链接只能用一次，2 分钟内有效）。请回到游戏里再点一次「去网页详细编辑」。', 'warn');
          return;
        }
      }
    }
    if (!state.token) { showAuth(); return; }
    await enterApp();
    if (wantLineup) openLineupFromLink(wantLineup);
  }

  boot();
})();
