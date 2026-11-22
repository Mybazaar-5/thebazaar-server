(() => {
  'use strict';

  // 动态前缀兼容：http://127.0.0.1:8080/workshop 与 http://<YOUR_SERVER_IP>:8080/workshop/
  const API_BASE = window.location.pathname
    .replace(/\/workshop(\/.*)?$/i, '')
    .replace(/\/$/, '');
  const apiUrl = (path) => `${API_BASE}${path}`;

  const TIERS = ['Bronze', 'Silver', 'Gold', 'Diamond'];
  const DURATION_ATTRS = new Set(['CooldownMax', 'HasteAmount', 'SlowAmount', 'FreezeAmount', 'ChargeAmount']);
  const MATRIX_ATTRS = [
    { key: 'CooldownMax', label: '冷却时间（填秒）', isSec: true },
    { key: 'BuyPrice', label: '购买价格（金币）', isSec: false, isPrice: true },
    { key: 'SellPrice', label: '出售价格（金币）', isSec: false, isPrice: true },
    { key: 'DamageAmount', label: '伤害', isSec: false },
    { key: 'ShieldApplyAmount', label: '护盾', isSec: false },
    { key: 'BurnApplyAmount', label: '灼烧（层）', isSec: false },
    { key: 'PoisonApplyAmount', label: '剧毒（层）', isSec: false },
    { key: 'HealAmount', label: '治疗', isSec: false },
    { key: 'CritChance', label: '暴击率（%）', isSec: false },
    { key: 'HasteAmount', label: '加速（填秒）', isSec: true },
    { key: 'ChargeAmount', label: '充能（填秒）', isSec: true },
    { key: 'PercentCooldownReduction', label: '冷却缩减（%）', isSec: false },
    { key: 'Custom_0', label: '特殊数值 1', isSec: false },
    { key: 'Custom_1', label: '特殊数值 2', isSec: false },
    { key: 'Custom_2', label: '特殊数值 3', isSec: false },
  ];

  // 官方标准买卖价表（严格对齐官方 gamemodes.json StandardPrices）
  const STANDARD_PRICES = {
    Small:  { Bronze: [2, 1], Silver: [4, 2],  Gold: [8, 4],  Diamond: [16, 8] },
    Medium: { Bronze: [4, 2], Silver: [8, 4],  Gold: [16, 8], Diamond: [32, 16] },
    Large:  { Bronze: [6, 3], Silver: [12, 6], Gold: [24, 12], Diamond: [48, 24] },
    Skill:  { Bronze: [5, 2], Silver: [10, 5], Gold: [20, 10], Diamond: [40, 20] },
  };
  const SIZE_ZH_MAP = { Small: '小型(1格)', Medium: '中型(2格)', Large: '大型(3格)' };
  const TYPE_ZH_MAP = {
    Item: '物品', Skill: '技能', EventEncounter: '奇遇',
    EncounterStep: '奇遇选项', CombatEncounter: '野怪', PedestalEncounter: '基座',
  };

  function getStandardPriceKey(card) {
    if (!card) return 'Medium';
    if (card.type === 'Skill') return 'Skill';
    return STANDARD_PRICES[card.size] ? card.size : 'Medium';
  }

  function applyStandardPricesToCard(card) {
    if (!card) return;
    const key = getStandardPriceKey(card);
    const table = STANDARD_PRICES[key];
    card.tiers = card.tiers || {};
    const abIds = Object.keys(card.abilities || {});
    const auIds = Object.keys(card.auras || {});
    TIERS.forEach((t) => {
      if (!card.tiers[t]) card.tiers[t] = { attributes: {}, abilityIds: [...abIds], auraIds: [...auIds] };
      card.tiers[t].attributes = card.tiers[t].attributes || {};
      card.tiers[t].attributes.BuyPrice = table[t][0];
      card.tiers[t].attributes.SellPrice = table[t][1];
    });
  }

  function isCardUsingStandardPrices(card) {
    if (!card?.tiers) return true;
    const table = STANDARD_PRICES[getStandardPriceKey(card)];
    for (const t of TIERS) {
      const attrs = card.tiers[t]?.attributes;
      if (!attrs) continue;
      const bp = attrs.BuyPrice;
      const sp = attrs.SellPrice;
      if (bp !== undefined && bp !== null && Number(bp) !== table[t][0]) return false;
      if (sp !== undefined && sp !== null && Number(sp) !== table[t][1]) return false;
    }
    return true;
  }

  function formatStandardPriceSummary(card) {
    const key = getStandardPriceKey(card);
    const table = STANDARD_PRICES[key];
    const kindLabel = key === 'Skill' ? '技能' : (SIZE_ZH_MAP[key] || key);
    return `当前自动定价（${kindLabel}）：青铜 买${table.Bronze[0]}/卖${table.Bronze[1]}金 · 白银 买${table.Silver[0]}/卖${table.Silver[1]}金 · 黄金 买${table.Gold[0]}/卖${table.Gold[1]}金 · 钻石 买${table.Diamond[0]}/卖${table.Diamond[1]}金`;
  }

  function randomBase36(len = 6) {
    let s = '';
    while (s.length < len) s += Math.random().toString(36).slice(2);
    return s.slice(0, len);
  }

  // 自动预生成防撞车唯一编号：<扩展包ID>:<简短代号>_<6位随机防撞码>
  function generateUniqueCardId(modId, slugHint = 'card') {
    const safeMod = String(modId || 'my-mod').toLowerCase().replace(/[^a-z0-9-]/g, '-');
    let slug = String(slugHint || 'card').toLowerCase().replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '');
    if (!slug) slug = 'card';
    if (slug.length > 24) slug = slug.slice(0, 24).replace(/_+$/, '');
    let candidate = `${safeMod}:${slug}_${randomBase36(6)}`;
    while ((state.workspace?.cards || []).some((c) => c.customId === candidate)) {
      candidate = `${safeMod}:${slug}_${randomBase36(6)}`;
    }
    return candidate;
  }

  function lookupZhName(list, id) {
    const found = (list || []).find((x) => x.id === id);
    return found ? found.nameZh : id;
  }

  function getArtSpecForCard(card) {
    if (card && card.type === 'Item') {
      if (card.size === 'Small') return { w: 512, h: 1024, desc: '小型 1 格竖图 512×1024' };
      if (card.size === 'Large') return { w: 768, h: 512, desc: '大型 3 格横图 768×512' };
    }
    return { w: 512, h: 512, desc: '中型 / 技能方图 512×512' };
  }

  // 默认合规原版白名单奇遇载体（经过 valid_card_ids.json 核验）
  const DEFAULT_EVENT_VISUAL = '11287697-dd45-4edc-9539-780a26001043';
  const DEFAULT_STEP_VISUAL = '0659ca31-9f4d-412e-84b2-d535b502afa6';
  const DEFAULT_ITEM_SMALL_VISUAL = '03b1fd16-6b0d-4847-a76f-6c586f7c5958';

  const state = {
    meta: null,
    catalog: null,
    mods: [],
    currentModId: null,
    workspace: null,
    assets: [],
    dirty: false,
    selectedCardIdx: -1,
    selectedPatchIdx: -1,
    selectedEventIdx: -1,
    previewTier: 'Bronze',
    previewEnchant: '',
    lastValidation: null,
    token: localStorage.getItem('bazaar_workshop_token') || sessionStorage.getItem('bazaar_workshop_token') || '',
    searchTimer: null,
  };

  const $ = (id) => document.getElementById(id);

  function toast(msg, type = 'ok') {
    const box = $('toastContainer');
    const el = document.createElement('div');
    el.className = `toast ${type}`;
    el.textContent = msg;
    box.appendChild(el);
    setTimeout(() => el.remove(), 4200);
  }

  function escapeHtml(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  async function apiFetch(path, options = {}) {
    const headers = Object.assign({}, options.headers || {});
    if (state.token) headers['X-Workshop-Token'] = state.token;
    if (options.json !== undefined) {
      headers['Content-Type'] = 'application/json; charset=utf-8';
      options.body = JSON.stringify(options.json);
      delete options.json;
    }
    const resp = await fetch(apiUrl(path), { ...options, headers });
    const ct = resp.headers.get('content-type') || '';
    const data = ct.includes('application/json') ? await resp.json() : await resp.text();
    return { ok: resp.ok, status: resp.status, data };
  }

  function markDirty(isDirty = true) {
    state.dirty = isDirty;
    $('dirtyDot').classList.toggle('dirty', isDirty);
    if (isDirty) {
      $('btnPublishReload').disabled = true;
    }
  }

  function switchTab(tabName) {
    document.querySelectorAll('.tab-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === tabName));
    document.querySelectorAll('.tab-panel').forEach((p) => p.classList.toggle('active', p.id === `tab-${tabName}`));
    if (tabName === 'events') renderEventDag();
    window.WorkshopV14?.onTabSwitched?.(tabName);
  }

  // ── 初始化加载 ────────────────────────────────────────────────────────────
  async function init() {
    $('workshopTokenInput').value = state.token;
    bindGlobalEvents();

    await Promise.all([loadMeta(), loadRuleCatalog(), loadModsList()]);
    await searchBaseCards();

    if (state.mods.length > 0 && !state.currentModId) {
      await openMod(state.mods[0].id);
    }
    window.WorkshopV14?.onReady?.();
  }

  async function loadMeta() {
    const res = await apiFetch('/api/workshop/meta');
    if (!res.ok) return;
    state.meta = res.data;
    const badge = $('accessBadge');
    if (res.data.canWrite) {
      badge.classList.remove('readonly');
      badge.textContent = res.data.mode === 'token-rw' ? '🔓 口令已解锁读写' : '🟢 本机全功能读写';
    } else {
      badge.classList.add('readonly');
      badge.textContent = '🔒 公网只读沙盒';
    }
    $('metaStatsText').textContent = `原版库 ${res.data.baseCardCount} 张（白名单 ${res.data.whitelistedCardCount} 张） · 已启用 ${res.data.enabledMods} 个 Mod`;
  }

  async function loadRuleCatalog() {
    const res = await apiFetch('/api/workshop/rule-catalog');
    if (!res.ok) return;
    state.catalog = res.data;

    // 填充英雄下拉框
    const heroSel = $('baseFilterHero');
    heroSel.innerHTML = '<option value="">全部英雄</option>';
    (res.data.heroes || []).forEach((h) => {
      const opt = document.createElement('option');
      opt.value = h.id;
      opt.textContent = h.nameZh;
      heroSel.appendChild(opt);
    });

    // 填充效果积木预设
    const pSel = $('presetSelect');
    pSel.innerHTML = '';
    (res.data.presets || []).filter((p) => p.scope !== 'encounterStep').forEach((p) => {
      const opt = document.createElement('option');
      opt.value = p.id;
      opt.textContent = `${p.scope === 'aura' ? '[光环] ' : '[能力] '}${p.nameZh}`;
      pSel.appendChild(opt);
    });

    // 填充右侧附魔预览下拉框
    const peSel = $('previewEnchantSelect');
    if (peSel) {
      peSel.innerHTML = '<option value="">（无附魔）</option>'
        + (res.data.enchantments || []).map((e) => `<option value="${escapeHtml(e.id)}">${escapeHtml(e.nameZh)}</option>`).join('');
    }
  }

  async function loadModsList() {
    const res = await apiFetch('/api/workshop/mods');
    if (!res.ok) return;
    state.mods = res.data.mods || [];
    renderModList();
  }

  function renderModList() {
    const box = $('modListContainer');
    if (!state.mods.length) {
      box.innerHTML = '<div class="empty-hint">mods/ 目录暂无 Mod，点击右上角「+ 新建空白 Mod」立即开始！</div>';
      return;
    }
    box.innerHTML = state.mods.map((m) => {
      const selected = m.id === state.currentModId ? 'selected' : '';
      const statusBadge = m.loaded
        ? '<span class="badge badge-green">已发布生效</span>'
        : (m.enabled ? '<span class="badge badge-red">校验未通过</span>' : '<span class="badge badge-yellow">草稿未启用</span>');
      return `
        <div class="mod-item ${selected}" data-mod-id="${escapeHtml(m.id)}">
          <div class="mod-item-head">
            <span class="mod-item-title">${escapeHtml(m.name || m.id)} <small class="muted">(${escapeHtml(m.id)})</small></span>
            ${statusBadge}
          </div>
          <div class="mod-item-meta">
            <span>v${escapeHtml(m.version)}</span>
            <span>新卡 ${m.cardCount}</span>
            <span>补丁 ${m.patchCount || 0}</span>
            <span>奇遇 ${m.eventCount}</span>
            <span>PNG ${m.assetCount}</span>
          </div>
        </div>
      `;
    }).join('');

    box.querySelectorAll('.mod-item').forEach((el) => {
      el.addEventListener('click', () => openMod(el.dataset.modId));
    });
  }

  async function openMod(modId) {
    const res = await apiFetch(`/api/workshop/mods/${encodeURIComponent(modId)}`);
    if (!res.ok) {
      toast(`加载 Mod 失败：${res.data?.error || res.status}`, 'err');
      return;
    }
    state.currentModId = res.data.modId;
    state.workspace = res.data.workspace;
    state.workspace.cards = state.workspace.cards || [];
    state.workspace.patches = state.workspace.patches || [];
    state.workspace.events = state.workspace.events || [];
    state.workspace.monsters = state.workspace.monsters || [];
    state.workspace.modes = state.workspace.modes || [];
    state.workspace.mechanics = state.workspace.mechanics || [];
    state.workspace.mutators = state.workspace.mutators || [];
    state.assets = res.data.assets || [];
    state.selectedCardIdx = state.workspace.cards.length > 0 ? 0 : -1;
    state.selectedPatchIdx = -1;
    state.selectedEventIdx = state.workspace.events.length > 0 ? 0 : -1;
    state.lastValidation = null;

    markDirty(false);
    renderModList();
    syncManifestForm();
    renderMineLists();
    renderCardOrPatchEditor();
    renderEventList();
    renderEventEditor();
    renderEventDag();
    window.WorkshopV14?.onWorkspaceLoaded?.();
  }

  function syncManifestForm() {
    const mf = state.workspace?.manifest;
    if (!mf) return;
    $('currentModTitle').textContent = `${mf.name || mf.id} (${mf.id})`;
    $('mfId').value = mf.id || '';
    $('mfName').value = mf.name || '';
    $('mfVersion').value = mf.version || '1.0.0';
    $('mfAuthor').value = mf.author || '';
    $('mfScope').value = mf.scope || 'multiplayer-safe';
    $('mfLoadOrder').value = mf.loadOrder ?? 100;
    $('mfDesc').value = mf.description || '';
    updateCounts();
  }

  function updateCounts() {
    const ws = state.workspace;
    if (!ws) return;
    $('cntCards').textContent = ws.cards.length;
    $('cntPatches').textContent = ws.patches.length;
    if ($('cntMechanics')) $('cntMechanics').textContent = (ws.mechanics || []).length;
    $('cntEvents').textContent = ws.events.length;
    $('cntAssets').textContent = state.assets.length;
    $('mineCardNum').textContent = ws.cards.length;
    $('minePatchNum').textContent = ws.patches.length;
  }

  function readManifestForm() {
    if (!state.workspace) return;
    const mf = state.workspace.manifest;
    mf.id = $('mfId').value.trim().toLowerCase().replace(/[^a-z0-9-]/g, '-');
    mf.name = $('mfName').value.trim();
    mf.version = $('mfVersion').value.trim() || '1.0.0';
    mf.author = $('mfAuthor').value.trim();
    mf.scope = $('mfScope').value;
    mf.loadOrder = parseInt($('mfLoadOrder').value, 10) || 100;
    mf.description = $('mfDesc').value.trim();
    mf.affectsGhostPool = false;
    $('currentModTitle').textContent = `${mf.name || mf.id} (${mf.id})`;
    markDirty(true);
  }

  async function saveDraft() {
    if (!state.workspace) {
      toast('请先新建或选择一个 Mod。', 'err');
      return false;
    }
    readManifestForm();
    const res = await apiFetch('/api/workshop/mods', { method: 'POST', json: state.workspace });
    if (!res.ok) {
      toast(`保存失败：${res.data?.error || res.status}`, 'err');
      return false;
    }
    state.currentModId = res.data.modId;
    markDirty(false);
    toast(res.data.messageZh || '草稿已保存！', 'ok');
    await loadModsList();
    return true;
  }

  // ── Tab 2: 原版 3,368 白名单卡搜索与克隆/原位改卡 ─────────────────────────
  async function searchBaseCards() {
    const q = $('baseSearchInput').value.trim();
    const type = $('baseFilterType').value;
    const size = $('baseFilterSize').value;
    const hero = $('baseFilterHero').value;
    const params = new URLSearchParams({ q, type, size, hero, whitelistOnly: 'true', limit: '40' });
    const res = await apiFetch(`/api/workshop/base-cards?${params}`);
    if (!res.ok) return;

    $('baseSearchMeta').textContent = `命中 ${res.data.total} 张合法白名单原版卡（展示前 ${res.data.items.length} 张）`;
    const list = $('baseCardList');
    if (!res.data.items.length) {
      list.innerHTML = '<div class="empty-hint">未找到匹配原版卡，请更换关键词或放宽筛选。</div>';
      return;
    }
    list.innerHTML = res.data.items.map((c) => {
      const heroNames = (c.heroes || []).map((h) => lookupZhName(state.catalog?.heroes, h)).join(' / ');
      const tagNames = (c.tags || []).slice(0, 4).map((t) => lookupZhName(state.catalog?.tags, t)).join(' · ');
      return `
      <div class="base-card-row">
        <div class="bcr-head">
          <span class="bcr-title">${escapeHtml(c.titleZh)}</span>
          <span class="badge badge-blue">${escapeHtml(TYPE_ZH_MAP[c.type] || c.type)} · ${escapeHtml(SIZE_ZH_MAP[c.size] || c.size)}</span>
        </div>
        <div class="bcr-sub">${escapeHtml(heroNames || '全英雄通用')}${tagNames ? ` · ${escapeHtml(tagNames)}` : ''}</div>
        <div class="bcr-actions">
          <button class="btn btn-gold btn-xs" data-act="clone" data-id="${escapeHtml(c.templateId)}">📋 复制为新卡</button>
          <button class="btn btn-outline btn-xs" data-act="patch" data-id="${escapeHtml(c.templateId)}">🔧 直接改原版</button>
          <button class="btn btn-subtle btn-xs" data-act="extract" data-id="${escapeHtml(c.templateId)}" title="查看并把这张原版卡的某条效果直接复制到正在编辑的卡牌里">🧲 提取效果</button>
        </div>
      </div>
    `;
    }).join('');

    list.querySelectorAll('button[data-act]').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (btn.dataset.act === 'extract') {
          window.WorkshopV14?.openVanillaAffixExtractor?.(btn.dataset.id);
        } else {
          handlePickBaseCard(btn.dataset.id, btn.dataset.act);
        }
      });
    });
  }

  async function handlePickBaseCard(templateId, action) {
    if (!state.workspace) {
      toast('请先在 Tab 1 新建或选择一个 Mod！', 'err');
      switchTab('overview');
      return;
    }
    const res = await apiFetch(`/api/workshop/base-cards/${encodeURIComponent(templateId)}`);
    if (!res.ok) {
      toast(`读取原版蓝图失败：${res.data?.error || res.status}`, 'err');
      return;
    }
    const modId = state.workspace.manifest.id;
    if (action === 'clone') {
      const draft = res.data.cloneDraftTemplate;
      const slugBase = (draft.customId.split(':')[1] || 'custom_card').replace(/[^a-z0-9_]/gi, '_');
      draft.customId = generateUniqueCardId(modId, slugBase);
      draft.spawningEligibility = draft.spawningEligibility || 'Always';
      state.workspace.cards.push(draft);
      state.selectedCardIdx = state.workspace.cards.length - 1;
      state.selectedPatchIdx = -1;
      toast(`已复制「${res.data.titleZh}」为新卡，并自动生成防撞编号！`);
    } else {
      const patch = res.data.patchDraftTemplate;
      const existing = state.workspace.patches.findIndex((p) => p.targetTemplateId === templateId);
      if (existing >= 0) {
        state.selectedPatchIdx = existing;
      } else {
        state.workspace.patches.push(patch);
        state.selectedPatchIdx = state.workspace.patches.length - 1;
      }
      state.selectedCardIdx = -1;
      toast(`已创建原位补丁：${res.data.titleZh}（仅改差量，卸载无损复原）`);
    }

    markDirty(true);
    updateCounts();
    renderMineLists();
    switchSubTab('mine');
    renderCardOrPatchEditor();
  }

  function switchSubTab(sub) {
    document.querySelectorAll('.subtab').forEach((b) => b.classList.toggle('active', b.dataset.sub === sub));
    document.querySelectorAll('.subpanel').forEach((p) => p.classList.toggle('active', p.id === `sub-${sub}`));
  }

  function renderMineLists() {
    const ws = state.workspace;
    if (!ws) return;
    const cardBox = $('mineCardList');
    cardBox.innerHTML = ws.cards.length
      ? ws.cards.map((c, idx) => {
          const exclBadge = c.spawningEligibility === 'GuidOnly'
            ? ' <span class="badge badge-blue">联动限定</span>'
            : (c.spawningEligibility === 'Never' ? ' <span class="badge badge-yellow">暂不投放</span>' : '');
          return `
          <div class="mine-row ${idx === state.selectedCardIdx ? 'active' : ''}" data-cidx="${idx}">
            <strong>${escapeHtml(c.titleZh || c.customId)}${exclBadge}</strong>
            <div class="bcr-sub">${escapeHtml(TYPE_ZH_MAP[c.type] || c.type)} · ${escapeHtml(SIZE_ZH_MAP[c.size] || c.size)}</div>
          </div>
        `;
        }).join('')
      : '<div class="empty-hint">暂无自定义新卡</div>';

    cardBox.querySelectorAll('.mine-row').forEach((el) => {
      el.addEventListener('click', () => {
        state.selectedCardIdx = parseInt(el.dataset.cidx, 10);
        state.selectedPatchIdx = -1;
        renderMineLists();
        renderCardOrPatchEditor();
      });
    });

    const patchBox = $('minePatchList');
    patchBox.innerHTML = ws.patches.length
      ? ws.patches.map((p, idx) => `
          <div class="mine-row ${idx === state.selectedPatchIdx ? 'active' : ''}" data-pidx="${idx}">
            <strong>${escapeHtml(p.titleZh || p.note || p.targetTemplateId)}</strong>
            <div class="bcr-sub">Patch → ${escapeHtml(p.targetTemplateId)}</div>
          </div>
        `).join('')
      : '<div class="empty-hint">暂无原位魔改补丁</div>';

    patchBox.querySelectorAll('.mine-row').forEach((el) => {
      el.addEventListener('click', () => {
        state.selectedPatchIdx = parseInt(el.dataset.pidx, 10);
        state.selectedCardIdx = -1;
        renderMineLists();
        renderCardOrPatchEditor();
      });
    });
  }

  function renderCardOrPatchEditor() {
    const hasCard = state.workspace && state.selectedCardIdx >= 0 && state.workspace.cards[state.selectedCardIdx];
    const hasPatch = state.workspace && state.selectedPatchIdx >= 0 && state.workspace.patches[state.selectedPatchIdx];

    $('cardEditorEmpty').classList.toggle('hidden', !!(hasCard || hasPatch));
    $('customCardEditor').classList.toggle('hidden', !hasCard);
    $('patchCardEditor').classList.toggle('hidden', !hasPatch);

    if (hasCard) {
      populateCustomCardForm(state.workspace.cards[state.selectedCardIdx]);
    } else if (hasPatch) {
      populatePatchCardForm(state.workspace.patches[state.selectedPatchIdx]);
    } else {
      updateLivePreview(null);
    }
    window.WorkshopV14?.onCardOrPatchRendered?.();
  }

  function populateCustomCardForm(card) {
    $('ceHeading').textContent = card.titleZh || card.customId;
    $('ceCustomId').value = card.customId || '';
    $('ceBaseVisualId').value = card.baseVisualTemplateId || '';
    $('ceTitleZh').value = card.titleZh || '';
    $('ceTitleEn').value = card.titleEn || '';
    $('ceType').value = card.type || 'Item';
    $('ceSize').value = card.size || 'Medium';
    $('ceStartingTier').value = card.startingTier || 'Bronze';
    const spawnMode = card.spawningEligibility || 'Always';
    if ($('ceSpawnEligibility')) $('ceSpawnEligibility').value = spawnMode;
    if ($('ceGuidOnlyBanner')) $('ceGuidOnlyBanner').classList.toggle('hidden', spawnMode !== 'GuidOnly');
    $('ceHeroes').value = (card.heroes || ['Common']).join(', ');
    $('ceTags').value = (card.tags || []).join(', ');
    $('ceHiddenTags').value = (card.hiddenTags || []).join(', ');
    $('ceDescZh').value = card.descZh || '';
    $('ceActiveTips').value = (card.activeTooltipsZh || []).join('\n');
    $('cePassiveTips').value = (card.passiveTooltipsZh || []).join('\n');

    const useAutoPrice = isCardUsingStandardPrices(card);
    if ($('ceAutoPriceChk')) $('ceAutoPriceChk').checked = useAutoPrice;
    if (useAutoPrice) applyStandardPricesToCard(card);
    if ($('ceAutoPriceSummary')) {
      $('ceAutoPriceSummary').textContent = useAutoPrice
        ? formatStandardPriceSummary(card)
        : '已切换为手动自定义买卖价，请直接在下表中修改每阶金币价格。';
    }
    if ($('artHintText')) {
      const spec = getArtSpecForCard(card);
      $('artHintText').textContent = `当前卡牌自动裁切目标：${spec.desc}（直接拖入 PNG/JPG 即可自动对齐尺寸）。`;
    }

    renderTierMatrix($('ceTierTableBody'), card.tiers || {}, (tier, attrKey, numVal) => {
      card.tiers = card.tiers || {};
      if (!card.tiers[tier]) card.tiers[tier] = { attributes: {}, abilityIds: Object.keys(card.abilities || {}), auraIds: Object.keys(card.auras || {}) };
      if (numVal === null) {
        delete card.tiers[tier].attributes[attrKey];
      } else {
        card.tiers[tier].attributes[attrKey] = numVal;
      }
      markDirty(true);
      updateLivePreview(card);
    }, useAutoPrice);

    renderAbilitiesSummary(card);
    $('ceAdvancedRulesJson').value = JSON.stringify({ abilities: card.abilities || {}, auras: card.auras || {} }, null, 2);
    updateLivePreview(card);
  }

  function populatePatchCardForm(patch) {
    $('peHeading').textContent = patch.titleZh || patch.targetTemplateId;
    $('peTargetId').value = patch.targetTemplateId || '';
    $('peNote').value = patch.note || '';
    $('peStartingTier').value = patch.startingTier || '';
    $('peTitleZh').value = patch.titleZh || '';
    $('peAddTags').value = (patch.addTags || []).join(', ');
    $('peRemoveTags').value = (patch.removeTags || []).join(', ');
    $('peDescZh').value = patch.descZh || '';

    const fakeTiers = {};
    TIERS.forEach((t) => {
      fakeTiers[t] = { attributes: (patch.tierAttributeOverrides && patch.tierAttributeOverrides[t]) || {} };
    });
    renderTierMatrix($('peTierTableBody'), fakeTiers, (tier, attrKey, numVal) => {
      patch.tierAttributeOverrides = patch.tierAttributeOverrides || {};
      patch.tierAttributeOverrides[tier] = patch.tierAttributeOverrides[tier] || {};
      if (numVal === null) delete patch.tierAttributeOverrides[tier][attrKey];
      else patch.tierAttributeOverrides[tier][attrKey] = numVal;
      markDirty(true);
      updateLivePreviewFromPatch(patch);
    });
    updateLivePreviewFromPatch(patch);
  }

  function renderTierMatrix(tbody, tiersMap, onChange, hidePriceRows = false) {
    const visibleAttrs = hidePriceRows ? MATRIX_ATTRS.filter((m) => !m.isPrice) : MATRIX_ATTRS;
    tbody.innerHTML = visibleAttrs.map((m) => {
      const cells = TIERS.map((t) => {
        const raw = tiersMap?.[t]?.attributes?.[m.key];
        const disp = raw === undefined || raw === null ? '' : (m.isSec ? (raw / 1000) : raw);
        return `<td><input type="number" step="${m.isSec ? '0.1' : '1'}" data-tier="${t}" data-attr="${m.key}" data-sec="${m.isSec ? '1' : '0'}" value="${disp}" placeholder="-" /></td>`;
      }).join('');
      return `<tr><td>${escapeHtml(m.label)}</td>${cells}</tr>`;
    }).join('');

    tbody.querySelectorAll('input').forEach((inp) => {
      inp.addEventListener('change', () => {
        const vStr = inp.value.trim();
        if (vStr === '') {
          onChange(inp.dataset.tier, inp.dataset.attr, null);
        } else {
          const parsed = parseFloat(vStr);
          if (!Number.isFinite(parsed)) return;
          const stored = inp.dataset.sec === '1' ? Math.round(parsed * 1000) : parsed;
          onChange(inp.dataset.tier, inp.dataset.attr, stored);
        }
      });
    });
  }

  function renderAbilitiesSummary(card) {
    const box = $('abilitiesSummaryList');
    const items = [];
    Object.entries(card.abilities || {}).forEach(([k, ab]) => {
      items.push({ kind: 'ability', key: k, trig: ab.trigger?.$type || '?', act: ab.action?.$type || '?' });
    });
    Object.entries(card.auras || {}).forEach(([k, au]) => {
      items.push({ kind: 'aura', key: k, trig: '静态光环', act: au.action?.$type || '?' });
    });
    if (!items.length) {
      box.innerHTML = '<div class="muted">暂无战斗能力或光环，请从上方下拉框一键添加中文效果积木。</div>';
      return;
    }
    box.innerHTML = items.map((it) => {
      const zhSummary = window.WorkshopV14?.summarizeAffixZh
        ? window.WorkshopV14.summarizeAffixZh(it.kind, it.kind === 'aura' ? card.auras[it.key] : card.abilities[it.key])
        : `${it.trig} → ${it.act}`;
      return `
      <div class="ability-item">
        <div class="ab-summary-main">
          <span class="badge ${it.kind === 'aura' ? 'badge-blue' : 'badge-green'}">${it.kind === 'aura' ? '常驻光环' : '战斗效果'} #${escapeHtml(it.key)}</span>
          <span class="ab-zh-text">${escapeHtml(zhSummary)}</span>
        </div>
        <div class="btn-row">
          <button class="btn btn-outline btn-xs" data-edit-kind="${it.kind}" data-edit-key="${escapeHtml(it.key)}">✏️ 修改</button>
          <button class="btn btn-danger btn-xs" data-del-kind="${it.kind}" data-del-key="${escapeHtml(it.key)}">移除</button>
        </div>
      </div>
    `;
    }).join('');

    box.querySelectorAll('button[data-edit-kind]').forEach((btn) => {
      btn.addEventListener('click', () => {
        window.WorkshopV14?.openAffixEditorForActiveTarget?.(btn.dataset.editKind, btn.dataset.editKey);
      });
    });

    box.querySelectorAll('button[data-del-kind]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const kind = btn.dataset.delKind;
        const key = btn.dataset.delKey;
        if (kind === 'ability') delete card.abilities[key];
        else delete card.auras[key];
        syncCardTierAbilityRefs(card);
        markDirty(true);
        populateCustomCardForm(card);
      });
    });
  }

  function syncCardTierAbilityRefs(card) {
    if (!card.tiers) return;
    const abIds = Object.keys(card.abilities || {});
    const auIds = Object.keys(card.auras || {});
    Object.values(card.tiers).forEach((td) => {
      td.abilityIds = [...abIds];
      td.auraIds = [...auIds];
    });
  }

  function updateLivePreview(card) {
    const frame = $('liveCardFrame');
    frame.className = `bazaar-card tier-${state.previewTier}`;
    if (!card) {
      $('pvTitle').textContent = '未选择卡牌';
      $('pvSize').textContent = '-';
      $('pvCooldown').textContent = '-';
      $('pvTags').innerHTML = '';
      $('pvTooltips').textContent = '从左侧检索原版卡开始创作';
      $('pvArtImg').classList.add('hidden');
      $('pvArtPlaceholder').classList.remove('hidden');
      return;
    }

    const attrs = mergeAttrsForTier(card, state.previewTier);
    const exclNote = card.spawningEligibility === 'GuidOnly' ? ' · 联动限定' : (card.spawningEligibility === 'Never' ? ' · 暂不投放' : '');
    $('pvTitle').textContent = card.titleZh || card.customId;
    $('pvSize').textContent = `${TYPE_ZH_MAP[card.type] || card.type} · ${SIZE_ZH_MAP[card.size] || card.size}${exclNote}`;
    $('pvCooldown').textContent = attrs.CooldownMax ? `${(attrs.CooldownMax / 1000).toFixed(1)} 秒` : '被动';
    $('pvBuyPrice').textContent = `买 ${attrs.BuyPrice ?? '-'}金 / 卖 ${attrs.SellPrice ?? '-'}金`;
    const heroesArr = card.heroes && card.heroes.length ? card.heroes : ['Vanessa', 'Dooley', 'Pygmalien', 'Mak', 'Karnok', 'Jules', 'Stelle', 'Hero8'];
    const allPlayableCount = ['Vanessa', 'Dooley', 'Pygmalien', 'Mak', 'Karnok', 'Jules', 'Stelle', 'Hero8'].filter((h) => heroesArr.includes(h)).length;
    $('pvHero').textContent = (allPlayableCount >= 8 && !heroesArr.includes('Common'))
      ? '全 8 英雄常规商店'
      : heroesArr.map((h) => lookupZhName(state.catalog?.heroes, h)).join(' / ');
    $('pvTags').innerHTML = (card.tags || []).map((t) => `<span>${escapeHtml(lookupZhName(state.catalog?.tags, t))}</span>`).join('');

    const lines = [
      ...(card.activeTooltipsZh || []).map((l) => `<div>[主动] ${escapeHtml(renderTooltipPreview(l, attrs))}</div>`),
      ...(card.passiveTooltipsZh || []).map((l) => `<div>[被动] ${escapeHtml(renderTooltipPreview(l, attrs))}</div>`),
    ];

    // 实时附魔横幅与附魔词条预览
    const enchBanner = $('pvEnchantBanner');
    const pvEnch = state.previewEnchant || '';
    if (pvEnch) {
      const enchZh = lookupZhName(state.catalog?.enchantments, pvEnch);
      if (enchBanner) {
        enchBanner.textContent = `✨ 附魔预览：${enchZh}`;
        enchBanner.classList.remove('hidden');
      }
      const enchTips = card.enchantTooltipsZh?.[pvEnch] || [];
      if (enchTips.length) {
        enchTips.forEach((l) => {
          lines.push(`<div class="bc-ench-tip">✨ [${escapeHtml(enchZh)}] ${escapeHtml(renderTooltipPreview(l, attrs))}</div>`);
        });
      } else {
        lines.push(`<div class="bc-ench-tip muted">✨ [${escapeHtml(enchZh)}] 尚未配置此附魔（可在中间「13 种官方附魔配置」一键生成）</div>`);
      }
    } else if (enchBanner) {
      enchBanner.classList.add('hidden');
    }

    $('pvTooltips').innerHTML = lines.length
      ? lines.join('')
      : escapeHtml(card.descZh || '（填写主动/被动文案即可实时预览数值渲染）');

    renderUpgradePreviewBox(card);

    if (card.customArtFile && state.currentModId) {
      const img = $('pvArtImg');
      img.src = apiUrl(`/api/workshop/mods/${encodeURIComponent(state.currentModId)}/assets/${card.customArtFile}?t=${Date.now()}`);
      img.classList.remove('hidden');
      $('pvArtPlaceholder').classList.add('hidden');
    } else {
      $('pvArtImg').classList.add('hidden');
      $('pvArtPlaceholder').classList.remove('hidden');
    }
  }

  function updateLivePreviewFromPatch(patch) {
    const attrs = (patch.tierAttributeOverrides && patch.tierAttributeOverrides[state.previewTier]) || {};
    const frame = $('liveCardFrame');
    frame.className = `bazaar-card tier-${state.previewTier}`;
    $('pvTitle').textContent = patch.titleZh || `原位补丁 (${patch.targetTemplateId.slice(0, 8)}…)`;
    $('pvSize').textContent = '原位 Patch';
    $('pvCooldown').textContent = attrs.CooldownMax ? `${(attrs.CooldownMax / 1000).toFixed(1)} 秒` : '沿用原版';
    $('pvTags').innerHTML = (patch.addTags || []).map((t) => `<span>+${escapeHtml(t)}</span>`).join('');
    $('pvTooltips').textContent = patch.note || patch.descZh || '仅覆写你填写的差量数值，其他属性完全继承原版卡。';
    $('pvBuyPrice').textContent = `买 ${attrs.BuyPrice ?? '原版'}g`;
    $('pvHero').textContent = 'Vanilla Patch';
  }

  function mergeAttrsForTier(card, targetTier) {
    const out = Object.assign({}, card.attributes || {});
    for (const t of TIERS) {
      if (card.tiers?.[t]?.attributes) Object.assign(out, card.tiers[t].attributes);
      if (t === targetTier) break;
    }
    return out;
  }

  const TIER_ZH_MAP = { Bronze: '青铜', Silver: '白银', Gold: '黄金', Diamond: '钻石' };
  const NEXT_TIER_MAP = { Bronze: 'Silver', Silver: 'Gold', Gold: 'Diamond', Diamond: null };

  function formatAttrDisp(key, val) {
    if (val === undefined || val === null) return '-';
    if (DURATION_ATTRS.has(key) && Math.abs(val) >= 100) return `${Number((val / 1000).toFixed(2))}秒`;
    return String(Math.round(val * 10) / 10);
  }

  function renderTooltipUpgradeHtml(tpl, curAttrs, nextAttrs) {
    return escapeHtml(tpl).replace(/\{([^}]+)\}/g, (_, tok) => {
      const parts = tok.split('.');
      const key = parts.length >= 2 ? parts[1] : tok;
      const cv = curAttrs[key];
      const nv = nextAttrs[key];
      if (cv === undefined) return '?';
      const fmtRaw = (v) => (DURATION_ATTRS.has(key) && Math.abs(v) >= 100 ? String(Number((v / 1000).toFixed(2))) : String(Math.round(v * 10) / 10));
      const cStr = fmtRaw(cv);
      if (nv !== undefined && Math.abs(nv - cv) > 1e-6) {
        return `${cStr} → <span class="up-arrow-val">${fmtRaw(nv)}</span>`;
      }
      return cStr;
    });
  }

  function renderUpgradePreviewBox(card) {
    const box = $('upgradePreviewBox');
    if (!box) return;
    if (!card || !card.tiers) {
      box.classList.add('hidden');
      return;
    }
    box.classList.remove('hidden');
    const curTier = state.previewTier;
    const nextTier = NEXT_TIER_MAP[curTier];
    const titleEl = $('upgradePreviewTitle');
    const priceEl = $('upgradePreviewPrice');
    const diffGrid = $('upgradeDiffGrid');
    const tipLines = $('upgradeTipLines');

    if (!nextTier) {
      titleEl.textContent = '💎 已达最高品质（钻石 Diamond）';
      priceEl.textContent = '满阶状态';
      diffGrid.innerHTML = '<span class="muted">点击上方「铜 / 银 / 金」按钮即可预览各阶升级对比效果。</span>';
      tipLines.innerHTML = '';
      return;
    }

    const curAttrs = mergeAttrsForTier(card, curTier);
    const nextAttrs = mergeAttrsForTier(card, nextTier);
    titleEl.textContent = `⬆️ 升级对比预览：${TIER_ZH_MAP[curTier]} → ${TIER_ZH_MAP[nextTier]}`;
    priceEl.textContent = (curAttrs.BuyPrice != null && nextAttrs.BuyPrice != null)
      ? `买价 ${curAttrs.BuyPrice}→${nextAttrs.BuyPrice}金`
      : '';

    const chips = [];
    MATRIX_ATTRS.forEach((m) => {
      if (m.isPrice) return;
      const cv = curAttrs[m.key];
      const nv = nextAttrs[m.key];
      if (cv === undefined && nv === undefined) return;
      if (cv !== undefined && nv !== undefined && Math.abs(nv - cv) > 1e-6) {
        const shortLabel = m.label.replace(/（.*）/, '');
        chips.push(`<span class="upgrade-diff-chip">${escapeHtml(shortLabel)} ${escapeHtml(formatAttrDisp(m.key, cv))} → <b>${escapeHtml(formatAttrDisp(m.key, nv))}</b></span>`);
      }
    });
    diffGrid.innerHTML = chips.length
      ? chips.join('')
      : '<span class="muted">当前品质与下一阶数值相同，修改四阶表格即可实时看到升级成长。</span>';

    const upTips = [
      ...(card.activeTooltipsZh || []).map((l) => `<div>[主动] ${renderTooltipUpgradeHtml(l, curAttrs, nextAttrs)}</div>`),
      ...(card.passiveTooltipsZh || []).map((l) => `<div>[被动] ${renderTooltipUpgradeHtml(l, curAttrs, nextAttrs)}</div>`),
    ];
    tipLines.innerHTML = upTips.join('');
  }

  function renderTooltipPreview(tpl, attrs) {
    return tpl.replace(/\{([^}]+)\}/g, (_, tok) => {
      const parts = tok.split('.');
      const key = parts.length >= 2 ? parts[1] : tok;
      if (attrs[key] === undefined) return '?';
      const v = attrs[key];
      if (DURATION_ATTRS.has(key) && Math.abs(v) >= 100) return String(Number((v / 1000).toFixed(2)));
      return String(Math.round(v * 10) / 10);
    });
  }

  // ── PNG 拖拽裁切与标准化上传 ──────────────────────────────────────────────
  async function handleArtFileUpload(file) {
    if (!state.workspace || !state.currentModId) {
      toast('请先保存当前 Mod 草稿后再上传贴图资产。', 'err');
      return;
    }
    const card = state.selectedCardIdx >= 0 ? state.workspace.cards[state.selectedCardIdx] : null;
    const patch = state.selectedPatchIdx >= 0 ? state.workspace.patches[state.selectedPatchIdx] : null;
    if (!card && !patch) {
      toast('请先选择一张卡牌或原位补丁。', 'err');
      return;
    }

    // 使用浏览器 Canvas 自动裁切并转为合规 PNG（Small 1格 512×1024 竖图；Large 3格 768×512 横图；Medium 2格 / Skill 512×512 方图）
    const artSpec = getArtSpecForCard(card);
    const targetW = artSpec.w;
    const targetH = artSpec.h;

    try {
      const bitmap = await createImageBitmap(file);
      const canvas = $('artCropCanvas');
      canvas.width = targetW;
      canvas.height = targetH;
      const ctx = canvas.getContext('2d');
      const scale = Math.max(targetW / bitmap.width, targetH / bitmap.height);
      const dw = bitmap.width * scale;
      const dh = bitmap.height * scale;
      ctx.clearRect(0, 0, targetW, targetH);
      ctx.drawImage(bitmap, (targetW - dw) / 2, (targetH - dh) / 2, dw, dh);

      const pngBlob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'));
      const slug = (card ? card.customId.split(':').pop() : patch.targetTemplateId.slice(0, 12)) || 'card_art';
      const fd = new FormData();
      fd.append('file', pngBlob, `${slug}.png`);
      fd.append('logicalName', `${slug}.png`);

      const res = await apiFetch(`/api/workshop/mods/${encodeURIComponent(state.currentModId)}/assets`, {
        method: 'POST',
        body: fd,
      });
      if (!res.ok) {
        toast(`上传贴图失败：${res.data?.error || res.status}`, 'err');
        return;
      }

      if (card) {
        card.customArtFile = res.data.logicalPath;
        card.artRect = { width: res.data.width, height: res.data.height, cropMode: 'cover' };
      } else if (patch) {
        patch.customArtFile = res.data.logicalPath;
      }
      state.assets = state.assets.filter((a) => a.logicalPath !== res.data.logicalPath);
      state.assets.push(res.data);

      $('artUploadStatus').textContent = `已转标准 PNG (${res.data.width}×${res.data.height}, ${(res.data.bytes / 1024).toFixed(1)}KB) · SHA256=${res.data.sha256.slice(0, 12)}…`;
      markDirty(true);
      updateCounts();
      if (card) updateLivePreview(card);
      toast('PNG 卡图已自动裁切并绑定成功！');
    } catch (ex) {
      toast(`处理图片失败：${ex.message}`, 'err');
    }
  }

  // ── Tab 3: 奇遇事件链编辑器 & 实时 DAG 防环 ───────────────────────────────
  function renderEventList() {
    const box = $('eventListContainer');
    const events = state.workspace?.events || [];
    if (!events.length) {
      box.innerHTML = '<div class="empty-hint">暂无奇遇事件，点击上方「+ 新建奇遇事件」。</div>';
      return;
    }
    box.innerHTML = events.map((ev, i) => `
      <div class="mine-row ${i === state.selectedEventIdx ? 'active' : ''}" data-eidx="${i}">
        <strong>${escapeHtml(ev.titleZh || ev.eventId)}</strong>
        <div class="bcr-sub">${escapeHtml(ev.eventId)} · ${(ev.steps || []).length} 个选项 · Day ${ev.minDay ?? 1}~${ev.maxDay ?? 99}</div>
      </div>
    `).join('');
    box.querySelectorAll('.mine-row').forEach((el) => {
      el.addEventListener('click', () => {
        state.selectedEventIdx = parseInt(el.dataset.eidx, 10);
        renderEventList();
        renderEventEditor();
      });
    });
  }

  function renderEventEditor() {
    const ev = state.workspace && state.selectedEventIdx >= 0 ? state.workspace.events[state.selectedEventIdx] : null;
    $('eventEditorEmpty').classList.toggle('hidden', !!ev);
    $('eventEditorForm').classList.toggle('hidden', !ev);
    if (!ev) return;

    $('evId').value = ev.eventId || '';
    $('evBaseVisualId').value = ev.baseVisualTemplateId || DEFAULT_EVENT_VISUAL;
    $('evTitleZh').value = ev.titleZh || '';
    $('evHeroes').value = (ev.heroes || ['Common']).join(', ');
    $('evMinDay').value = ev.minDay ?? 2;
    $('evMaxDay').value = ev.maxDay ?? 10;
    $('evWeight').value = ev.weight ?? 5;
    $('evDescZh').value = ev.descZh || '';

    const stepsBox = $('eventStepsContainer');
    const allEventIds = (state.workspace.events || []).map((e) => e.eventId);
    const modCards = state.workspace.cards || [];

    const summarizeStepAction = (st) => {
      const act = st.abilities?.['0']?.action;
      if (!act) return '尚未设置奖励效果（请点上方按钮或下方选择奖励卡牌）';
      if (act.$type === 'TActionGameSpawnCards') {
        const ids = act.spawnContext?.groups?.[0]?.filters?.[0]?.ids || act.ids || (act.id ? [act.id] : []);
        const cid = ids[0] || '';
        const found = modCards.find((c) => c.customId === cid);
        return `🎁 玩家选择此项后，直接获得指定卡牌「${found ? found.titleZh : cid}」`;
      }
      if (act.$type === 'TActionPlayerModifyAttribute' && act.attributeType === 'Gold') {
        return `🪙 玩家选择此项后，获得 ${act.value?.value ?? 0} 金币`;
      }
      if (act.$type === 'TActionCardUpgrade') {
        return '⬆️ 玩家选择此项后，升级最左侧的一张卡牌';
      }
      return `✨ 执行效果：${window.WorkshopV14?.summarizeAffixZh ? window.WorkshopV14.summarizeAffixZh('ability', st.abilities['0']) : act.$type}`;
    };

    const cardPickerOptions = modCards.length
      ? modCards.map((c) => {
          const tag = c.spawningEligibility === 'GuidOnly' ? '[联动限定] ' : '';
          return `<option value="${escapeHtml(c.customId)}">${tag}${escapeHtml(c.titleZh || c.customId)}</option>`;
        }).join('')
      : '<option value="">（当前扩展包还没有自定义新卡，先去第 2 步做一张卡吧）</option>';

    stepsBox.innerHTML = (ev.steps || []).map((st, idx) => {
      const nextTarget = st.nextEncounterOnSelection?.[0]?.id || '';
      const buyPrice = st.attributes?.BuyPrice ?? 0;
      const optionsHtml = ['<option value="">（选完直接结束本次奇遇）</option>']
        .concat(allEventIds.map((eid) => {
          const targetEv = (state.workspace.events || []).find((x) => x.eventId === eid);
          return `<option value="${escapeHtml(eid)}" ${eid === nextTarget ? 'selected' : ''}>选完进入下一个奇遇 → ${escapeHtml(targetEv?.titleZh || eid)}</option>`;
        }))
        .join('');
      return `
        <div class="step-card" data-sidx="${idx}">
          <div class="panel-header compact">
            <strong>奇遇选项 #${idx + 1}</strong>
            <div class="btn-row">
              <button class="btn btn-outline btn-xs" data-sact="gold" data-sidx="${idx}">🪙 设为给 6 金币</button>
              <button class="btn btn-outline btn-xs" data-sact="upgrade" data-sidx="${idx}">⬆️ 设为升级最左卡</button>
              <button class="btn btn-danger btn-xs" data-sact="del" data-sidx="${idx}">删除</button>
            </div>
          </div>
          <div class="form-grid">
            <label class="field"><span>选项标题 *</span><input type="text" class="st-title" value="${escapeHtml(st.titleZh || '')}" /></label>
            <label class="field"><span>需要花费金币（0 表示免费）</span><input type="number" class="st-price" min="0" value="${buyPrice}" /></label>
            <label class="field span-2"><span>选项说明 *</span><input type="text" class="st-desc" value="${escapeHtml(st.descZh || '')}" /></label>
            <label class="field span-2"><span>选完后的流程</span><select class="st-next">${optionsHtml}</select></label>
          </div>
          <div class="step-reward-bar">
            <span class="muted">联动发卡：</span>
            <select class="st-reward-card-sel" data-sidx="${idx}">${cardPickerOptions}</select>
            <button type="button" class="btn btn-gold btn-xs" data-sact="spawnCard" data-sidx="${idx}" ${modCards.length ? '' : 'disabled'}>🎁 设为奖励这张卡</button>
          </div>
          <div class="step-action-badge">${escapeHtml(summarizeStepAction(st))}</div>
        </div>
      `;
    }).join('');

    stepsBox.querySelectorAll('.step-card').forEach((cardEl) => {
      const idx = parseInt(cardEl.dataset.sidx, 10);
      const st = ev.steps[idx];
      cardEl.querySelector('.st-title').addEventListener('input', (e) => { st.titleZh = e.target.value; markDirty(true); });
      cardEl.querySelector('.st-desc').addEventListener('input', (e) => { st.descZh = e.target.value; markDirty(true); });
      cardEl.querySelector('.st-price').addEventListener('change', (e) => {
        st.attributes = st.attributes || {};
        st.attributes.BuyPrice = Math.max(0, parseInt(e.target.value, 10) || 0);
        markDirty(true);
      });
      cardEl.querySelector('.st-next').addEventListener('change', (e) => {
        const val = e.target.value;
        st.nextEncounterOnSelection = val ? [{ id: val, weight: 1.0 }] : [];
        markDirty(true);
        renderEventDag();
      });
    });

    stepsBox.querySelectorAll('button[data-sact]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const idx = parseInt(btn.dataset.sidx, 10);
        const st = ev.steps[idx];
        const act = btn.dataset.sact;
        if (act === 'del') {
          ev.steps.splice(idx, 1);
        } else if (act === 'gold') {
          st.abilities = {
            '0': {
              id: '0',
              trigger: { $type: 'TTriggerOnCardSelected' },
              action: {
                $type: 'TActionPlayerModifyAttribute',
                attributeType: 'Gold',
                operation: 'Add',
                value: { $type: 'TFixedValue', value: 6.0 },
                target: { $type: 'TTargetPlayerAbsolute', targetMode: 'Player' },
              },
            },
          };
          toast('已设为「选择后获得 6 金币」！');
        } else if (act === 'upgrade') {
          st.abilities = {
            '0': {
              id: '0',
              trigger: { $type: 'TTriggerOnCardSelected' },
              action: {
                $type: 'TActionCardUpgrade',
                target: { $type: 'TTargetCardXMost', targetSection: 'SelfHand', targetMode: 'LeftMostCard' },
              },
            },
          };
          toast('已设为「选择后升级最左侧卡牌」！');
        } else if (act === 'spawnCard') {
          const cardEl = btn.closest('.step-card');
          const pickedId = cardEl?.querySelector('.st-reward-card-sel')?.value;
          const pickedCard = modCards.find((c) => c.customId === pickedId);
          if (!pickedId || !pickedCard) {
            toast('请先选择一张要奖励的卡牌。', 'err');
            return;
          }
          st.abilities = {
            '0': {
              id: '0',
              trigger: { $type: 'TTriggerOnCardSelected' },
              action: {
                $type: 'TActionGameSpawnCards',
                targetPlayer: { $type: 'TTargetPlayerAbsolute', targetMode: 'Player' },
                spawnCount: 1,
                spawnContext: {
                  $type: 'TSpawnContextQuery',
                  groups: [
                    {
                      $type: 'TSpawnGroup',
                      filters: [{ $type: 'TSpawnFilterIdList', ids: [pickedId] }],
                    },
                  ],
                },
              },
            },
          };
          if (!st.titleZh || st.titleZh.startsWith('奇遇选项') || st.titleZh === '领取 6 金币补给') {
            st.titleZh = `获得「${pickedCard.titleZh || pickedId}」`;
          }
          if (!st.descZh || st.descZh.includes('金币')) {
            st.descZh = `直接获得专属卡牌「${pickedCard.titleZh || pickedId}」。`;
          }
          toast(`已将选项 #${idx + 1} 设为奖励「${pickedCard.titleZh || pickedId}」！`);
        }
        markDirty(true);
        renderEventEditor();
        renderEventDag();
      });
    });

    window.WorkshopV14?.onEventEditorRendered?.();
  }

  function renderEventDag() {
    const svg = $('eventDagSvg');
    const events = state.workspace?.events || [];
    if (!events.length) {
      svg.innerHTML = '<text x="500" y="95" text-anchor="middle" fill="#6c8299" font-size="14">当前 Mod 暂无奇遇事件节点</text>';
      $('dagStatusText').textContent = '实时三色 DFS 检测：暂无奇遇事件';
      return;
    }

    // 建图与三色 DFS 检环
    const graph = new Map();
    const idSet = new Set(events.map((e) => e.eventId));
    const edges = [];
    events.forEach((ev) => {
      graph.set(ev.eventId, []);
      (ev.steps || []).forEach((st) => {
        (st.nextEncounterOnSelection || []).forEach((n) => {
          if (!n.id) return;
          graph.get(ev.eventId).push(n.id);
          edges.push({ from: ev.eventId, to: n.id, label: st.titleZh || st.customId, dangling: !idSet.has(n.id) });
        });
      });
    });

    const color = new Map();
    let hasCycle = false;
    function dfs(u) {
      color.set(u, 1);
      for (const v of (graph.get(u) || [])) {
        if (!idSet.has(v)) continue;
        if (color.get(v) === 1) hasCycle = true;
        else if (!color.get(v)) dfs(v);
      }
      color.set(u, 2);
    }
    events.forEach((ev) => { if (!color.get(ev.eventId)) dfs(ev.eventId); });

    $('dagStatusText').textContent = hasCycle
      ? '⚠️ 危险：检测到奇遇循环跳转环路！请立即断开红线边，否则 Gate #6 将阻断发布！'
      : `✅ 实时三色 DFS 检测通过：共 ${events.length} 个奇遇节点，${edges.length} 条跳转边，严格无环。`;
    $('dagStatusText').style.color = hasCycle ? 'var(--status-red)' : 'var(--status-green)';

    // 简易水平布局渲染 SVG
    const pos = new Map();
    const gap = Math.max(200, Math.floor(900 / Math.max(1, events.length)));
    events.forEach((ev, idx) => {
      pos.set(ev.eventId, { x: 110 + idx * gap, y: 90 });
    });

    let html = `<defs><marker id="arrowGold" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="${hasCycle ? '#f25f6d' : '#d8a444'}"/></marker></defs>`;
    edges.forEach((ed) => {
      const p1 = pos.get(ed.from);
      const p2 = pos.get(ed.to);
      if (!p1 || !p2) return;
      const stroke = hasCycle || ed.dangling ? '#f25f6d' : '#d8a444';
      if (ed.from === ed.to) {
        html += `<path d="M ${p1.x} ${p1.y - 24} C ${p1.x - 50} ${p1.y - 80}, ${p1.x + 50} ${p1.y - 80}, ${p1.x + 12} ${p1.y - 24}" fill="none" stroke="${stroke}" stroke-width="2.5" marker-end="url(#arrowGold)"/>`;
      } else {
        html += `<line x1="${p1.x + 76}" y1="${p1.y}" x2="${p2.x - 82}" y2="${p2.y}" stroke="${stroke}" stroke-width="2.5" marker-end="url(#arrowGold)"/>`;
      }
    });
    events.forEach((ev) => {
      const p = pos.get(ev.eventId);
      html += `
        <g>
          <rect x="${p.x - 76}" y="${p.y - 24}" width="152" height="48" rx="8" fill="#13253c" stroke="${hasCycle ? '#f25f6d' : '#f4d38a'}" stroke-width="1.5"/>
          <text x="${p.x}" y="${p.y - 2}" text-anchor="middle" fill="#f4d38a" font-size="12.5" font-weight="700">${escapeHtml((ev.titleZh || ev.eventId).slice(0, 12))}</text>
          <text x="${p.x}" y="${p.y + 15}" text-anchor="middle" fill="#9fb3c8" font-size="10.5">${(ev.steps || []).length} 选项</text>
        </g>`;
    });
    svg.innerHTML = html;
  }

  // ── Tab 4: 10 道防 Bug 铁闸 & 50 场真实战斗体检 + 发布热重载 ───────────────
  async function runTenGateValidation(useInMemoryDraft = true) {
    if (!state.workspace) {
      toast('请先在 Tab 1 选择或新建一个 Mod。', 'err');
      return;
    }
    readManifestForm();
    const modId = state.workspace.manifest.id || state.currentModId || 'draft-mod';
    switchTab('validate');
    $('valSummaryBanner').textContent = '⏳ 正在隔离沙盒中执行 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 压测，请稍候...';
    $('gatesGrid').innerHTML = '<div class="empty-hint">压测引擎运行中...</div>';

    const payload = useInMemoryDraft ? { draft: state.workspace, matches: 50, seed: 2026131 } : { matches: 50, seed: 2026131 };
    const res = await apiFetch(`/api/workshop/mods/${encodeURIComponent(modId)}/validate`, {
      method: 'POST',
      json: payload,
    });
    if (!res.ok) {
      toast(`体检请求失败：${res.data?.error || res.status}`, 'err');
      return;
    }

    state.lastValidation = res.data;
    renderValidationReport(res.data);
  }

  function renderValidationReport(rep) {
    $('valSummaryBanner').textContent = `${rep.passed ? '✅' : '❌'} ${rep.summaryZh}（总耗时 ${rep.totalDurationMs}ms）`;
    $('valSummaryBanner').style.color = rep.passed ? 'var(--status-green)' : 'var(--status-red)';
    $('btnPublishReload').disabled = !(rep.passed && state.meta?.canWrite);

    const sr = rep.combatStressReport;
    const dash = $('stressDashboard');
    if (sr) {
      dash.classList.remove('hidden');
      $('kpiMatches').textContent = `${sr.matchesRun} 场`;
      $('kpiExceptions').textContent = String(sr.exceptions);
      $('kpiExceptions').className = sr.exceptions === 0 ? 'ok' : 'bad';
      $('kpiTiming').textContent = `${sr.avgMatchMs} / ${sr.maxMatchMs}ms`;
      $('kpiTimeouts').textContent = String(sr.maxDurationHits);
      $('kpiWinRate').textContent = `${sr.modSideWinRatePct}%`;
      $('kpiBalanceHint').textContent = sr.balanceHintZh;
      $('winrateMarker').style.left = `${Math.max(2, Math.min(98, sr.modSideWinRatePct))}%`;
    } else {
      dash.classList.add('hidden');
    }

    const grid = $('gatesGrid');
    grid.innerHTML = (rep.gates || []).map((g) => {
      const icon = g.status === 'PASS' ? '🟢' : (g.status === 'WARN' ? '🟡' : (g.status === 'FAIL' ? '🔴' : '⚪'));
      const issuesHtml = (g.issues || []).length
        ? `<div class="gate-issues">${g.issues.map((it) => `
            <div class="issue-row">
              <div><strong>[${escapeHtml(it.code)}]</strong> ${escapeHtml(it.message)}</div>
              ${it.fixHintZh ? `<div class="issue-hint">💡 修复建议：${escapeHtml(it.fixHintZh)}</div>` : ''}
            </div>
          `).join('')}</div>`
        : '<div class="muted" style="margin-top:6px;">全部检测点通过，零问题。</div>';

      return `
        <div class="gate-card ${escapeHtml(g.status)}">
          <div class="gate-head">
            <span>${icon} ${escapeHtml(g.nameZh)}</span>
            <span class="muted">${g.durationMs}ms</span>
          </div>
          ${issuesHtml}
        </div>
      `;
    }).join('');
  }

  async function publishAndReload() {
    if (!state.workspace) return;
    if (state.dirty) {
      const saved = await saveDraft();
      if (!saved) return;
    }
    const modId = state.workspace.manifest.id;
    const res = await apiFetch(`/api/workshop/mods/${encodeURIComponent(modId)}/publish-reload`, { method: 'POST' });
    if (!res.ok) {
      toast(res.data?.messageZh || res.data?.error || `发布被拦截（HTTP ${res.status}）`, 'err');
      if (res.data?.validation) renderValidationReport(res.data.validation);
      return;
    }
    toast(res.data.messageZh || '发布并热重载成功！', 'ok');
    await Promise.all([loadMeta(), loadModsList()]);
  }

  // ── 事件绑定 ──────────────────────────────────────────────────────────────
  function bindGlobalEvents() {
    document.querySelectorAll('.tab-btn').forEach((b) => {
      b.addEventListener('click', () => switchTab(b.dataset.tab));
    });
    document.querySelectorAll('.subtab').forEach((b) => {
      b.addEventListener('click', () => switchSubTab(b.dataset.sub));
    });

    $('btnApplyToken').addEventListener('click', async () => {
      state.token = $('workshopTokenInput').value.trim();
      localStorage.setItem('bazaar_workshop_token', state.token);
      sessionStorage.setItem('bazaar_workshop_token', state.token);
      await loadMeta();
      toast(state.meta?.canWrite ? '已解锁公网写权限（已持久化记住）！' : '口令未匹配或服务端未开启 Token 写权限。', state.meta?.canWrite ? 'ok' : 'err');
    });

    $('btnQuickSave').addEventListener('click', saveDraft);
    $('btnOverviewSave').addEventListener('click', saveDraft);
    $('btnQuickValidate').addEventListener('click', () => runTenGateValidation(true));
    $('btnRunValidateDraft').addEventListener('click', () => runTenGateValidation(true));
    $('btnPublishReload').addEventListener('click', publishAndReload);
    $('btnGoToCards').addEventListener('click', () => switchTab('cards'));
    $('btnRefreshMods').addEventListener('click', loadModsList);

    // 新建空白 Mod
    $('btnNewMod').addEventListener('click', () => $('newModModal').classList.remove('hidden'));
    $('btnCancelNewMod').addEventListener('click', () => $('newModModal').classList.add('hidden'));
    $('btnConfirmNewMod').addEventListener('click', async () => {
      const id = $('newModIdInput').value.trim().toLowerCase().replace(/[^a-z0-9-]/g, '-');
      const name = $('newModNameInput').value.trim();
      const author = $('newModAuthorInput').value.trim() || '社区创作者';
      if (!/^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$|^[a-z0-9]{2}$/.test(id)) {
        toast('Mod ID 必须为 2~64 位小写字母、数字或连字符！', 'err');
        return;
      }
      state.currentModId = id;
      state.workspace = {
        manifest: {
          schemaVersion: 1, id, name: name || id, version: '1.0.0', author,
          description: '通过大巴扎零代码 Web 创意工坊创建。',
          scope: 'multiplayer-safe', affectsGhostPool: false, enabledByDefault: false, loadOrder: 100,
          dependencies: [], contentRoots: {}
        },
        cards: [], patches: [], events: [], monsters: [], modes: [], mechanics: [], mutators: [], localization: null
      };
      state.assets = [];
      state.selectedCardIdx = -1;
      state.selectedPatchIdx = -1;
      state.selectedEventIdx = -1;
      $('newModModal').classList.add('hidden');
      syncManifestForm();
      renderMineLists();
      renderCardOrPatchEditor();
      renderEventList();
      renderEventEditor();
      await saveDraft();
    });

    // 导出与删除 Mod
    $('btnExportCurrentZip').addEventListener('click', () => {
      if (!state.currentModId) return;
      window.location.href = apiUrl(`/api/workshop/mods/${encodeURIComponent(state.currentModId)}/export`);
    });
    $('btnDeleteCurrentMod').addEventListener('click', async () => {
      if (!state.currentModId) return;
      if (!confirm(`确认永久删除草稿 Mod「${state.currentModId}」吗？此操作不可撤销。`)) return;
      const res = await apiFetch(`/api/workshop/mods/${encodeURIComponent(state.currentModId)}`, { method: 'DELETE' });
      if (!res.ok) {
        toast(res.data?.error || '删除失败', 'err');
        return;
      }
      toast(res.data.messageZh || '已删除！');
      state.currentModId = null;
      state.workspace = null;
      await loadModsList();
      if (state.mods.length) await openMod(state.mods[0].id);
    });

    // 导入 .bazaarmod.zip
    const zipDz = $('zipDropzone');
    $('btnSelectZip').addEventListener('click', () => $('zipFileInput').click());
    $('zipFileInput').addEventListener('change', (e) => {
      if (e.target.files?.[0]) importZipFile(e.target.files[0]);
    });
    zipDz.addEventListener('dragover', (e) => { e.preventDefault(); zipDz.classList.add('dragover'); });
    zipDz.addEventListener('dragleave', () => zipDz.classList.remove('dragover'));
    zipDz.addEventListener('drop', (e) => {
      e.preventDefault(); zipDz.classList.remove('dragover');
      if (e.dataTransfer.files?.[0]) importZipFile(e.dataTransfer.files[0]);
    });

    // 清单字段变更
    ['mfId', 'mfName', 'mfVersion', 'mfAuthor', 'mfScope', 'mfLoadOrder', 'mfDesc'].forEach((id) => {
      $(id).addEventListener('input', readManifestForm);
    });

    // 原版搜索防抖
    const triggerSearch = () => {
      clearTimeout(state.searchTimer);
      state.searchTimer = setTimeout(searchBaseCards, 180);
    };
    $('baseSearchInput').addEventListener('input', triggerSearch);
    $('baseFilterType').addEventListener('change', searchBaseCards);
    $('baseFilterSize').addEventListener('change', searchBaseCards);
    $('baseFilterHero').addEventListener('change', searchBaseCards);

    // 空白新卡
    $('btnBlankCustomCard').addEventListener('click', () => {
      if (!state.workspace) return;
      const modId = state.workspace.manifest.id;
      const idx = state.workspace.cards.length + 1;
      const newCard = {
        customId: generateUniqueCardId(modId, `custom_item_${idx}`),
        baseVisualTemplateId: DEFAULT_ITEM_SMALL_VISUAL,
        type: 'Item', size: 'Small', startingTier: 'Bronze', spawningEligibility: 'Always',
        heroes: ['Vanessa', 'Dooley', 'Pygmalien', 'Mak', 'Karnok', 'Jules', 'Stelle', 'Hero8'], tags: ['Weapon'], hiddenTags: ['Damage'],
        titleZh: `原创星港短刃 #${idx}`, titleEn: `Custom Blade ${idx}`,
        descZh: '使用全中文创意工坊打造的原创武器。',
        activeTooltipsZh: ['造成 {attr.DamageAmount} 点伤害'],
        passiveTooltipsZh: [],
        tiers: {
          Bronze: { attributes: { CooldownMax: 4000, DamageAmount: 15 }, abilityIds: ['0'], auraIds: [] },
          Silver: { attributes: { CooldownMax: 4000, DamageAmount: 30 }, abilityIds: ['0'], auraIds: [] },
          Gold: { attributes: { CooldownMax: 3500, DamageAmount: 60 }, abilityIds: ['0'], auraIds: [] },
          Diamond: { attributes: { CooldownMax: 3500, DamageAmount: 120 }, abilityIds: ['0'], auraIds: [] },
        },
        abilities: {
          '0': {
            id: '0',
            trigger: { $type: 'TTriggerOnCardFired' },
            activeIn: 'HandOnly',
            action: { $type: 'TActionPlayerDamage', target: { $type: 'TTargetPlayerRelative', targetMode: 'Opponent' } }
          }
        },
        auras: {}
      };
      applyStandardPricesToCard(newCard);
      state.workspace.cards.push(newCard);
      state.selectedCardIdx = state.workspace.cards.length - 1;
      state.selectedPatchIdx = -1;
      markDirty(true);
      updateCounts();
      renderMineLists();
      renderCardOrPatchEditor();
    });

    // 自定义新卡表单双向同步
    const bindCe = (id, fn) => $(id)?.addEventListener('input', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card) return;
      fn(card, $(id).value);
      markDirty(true);
      renderMineLists();
      updateLivePreview(card);
    });
    bindCe('ceCustomId', (c, v) => { c.customId = v.trim(); });
    bindCe('ceBaseVisualId', (c, v) => { c.baseVisualTemplateId = v.trim(); });
    bindCe('ceTitleZh', (c, v) => { c.titleZh = v; $('ceHeading').textContent = v || c.customId; });
    bindCe('ceTitleEn', (c, v) => { c.titleEn = v; });
    bindCe('ceType', (c, v) => {
      c.type = v;
      if ($('ceAutoPriceChk')?.checked) applyStandardPricesToCard(c);
      populateCustomCardForm(c);
    });
    bindCe('ceSize', (c, v) => {
      c.size = v;
      if ($('ceAutoPriceChk')?.checked) applyStandardPricesToCard(c);
      populateCustomCardForm(c);
    });
    bindCe('ceStartingTier', (c, v) => { c.startingTier = v; });
    $('ceSpawnEligibility')?.addEventListener('change', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card) return;
      card.spawningEligibility = $('ceSpawnEligibility').value || 'Always';
      $('ceGuidOnlyBanner')?.classList.toggle('hidden', card.spawningEligibility !== 'GuidOnly');
      markDirty(true);
      renderMineLists();
      updateLivePreview(card);
    });
    $('btnGoLinkInEvent')?.addEventListener('click', () => {
      switchTab('events');
      if (state.workspace && (!state.workspace.events || state.workspace.events.length === 0)) {
        $('btnNewEvent')?.click();
        toast('已为你新建一个联动奇遇草稿，在下方选项点「🎁 设为奖励这张卡」即可完成联动！');
      } else {
        renderEventEditor();
        toast('在右侧任意奇遇选项里选择你的限定卡并点「🎁 设为奖励这张卡」即可完成联动！');
      }
    });
    $('ceAutoPriceChk')?.addEventListener('change', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card) return;
      const checked = $('ceAutoPriceChk').checked;
      if (checked) applyStandardPricesToCard(card);
      markDirty(true);
      populateCustomCardForm(card);
      toast(checked ? '已恢复按官方标准买卖价自动计算。' : '已开启手动改价，可在下表自由填写每阶购买与出售金币。');
    });
    $('btnRegenCardId')?.addEventListener('click', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card || !state.workspace) return;
      const hint = (card.customId.split(':')[1] || 'card').replace(/_[a-z0-9]{6}$/i, '');
      card.customId = generateUniqueCardId(state.workspace.manifest.id, hint);
      $('ceCustomId').value = card.customId;
      markDirty(true);
      renderMineLists();
      toast(`已重新生成防撞编号：${card.customId}`);
    });
    bindCe('ceHeroes', (c, v) => { c.heroes = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindCe('ceTags', (c, v) => { c.tags = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindCe('ceHiddenTags', (c, v) => { c.hiddenTags = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindCe('ceDescZh', (c, v) => { c.descZh = v; });
    bindCe('ceActiveTips', (c, v) => { c.activeTooltipsZh = v.split('\n').map((s) => s.trim()).filter(Boolean); });
    bindCe('cePassiveTips', (c, v) => { c.passiveTooltipsZh = v.split('\n').map((s) => s.trim()).filter(Boolean); });

    // 占位符快捷芯片
    $('placeholderChips').querySelectorAll('.chip').forEach((ch) => {
      ch.addEventListener('click', () => {
        const ta = $('ceActiveTips');
        ta.value = (ta.value ? ta.value + ' ' : '') + ch.dataset.ins;
        ta.dispatchEvent(new Event('input'));
      });
    });

    // 1x/2x/4x/8x 自动推算四阶数值
    $('btnAutoScaleTiers').addEventListener('click', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card || !card.tiers?.Bronze?.attributes) {
        toast('请先填写青铜 Bronze 阶基础数值。', 'err');
        return;
      }
      const b = card.tiers.Bronze.attributes;
      const mults = { Silver: 2, Gold: 4, Diamond: 8 };
      const keepAutoPrice = $('ceAutoPriceChk')?.checked;
      Object.entries(mults).forEach(([t, mult]) => {
        card.tiers[t] = card.tiers[t] || { attributes: {}, abilityIds: Object.keys(card.abilities || {}), auraIds: Object.keys(card.auras || {}) };
        Object.entries(b).forEach(([k, v]) => {
          if (keepAutoPrice && (k === 'BuyPrice' || k === 'SellPrice')) return;
          if (k === 'CooldownMax') card.tiers[t].attributes[k] = Math.max(1000, v - (mult >= 4 ? 500 : 0));
          else if (k === 'HasteAmount' || k === 'ChargeAmount') card.tiers[t].attributes[k] = v + (mult === 2 ? 200 : mult === 4 ? 500 : 1000);
          else card.tiers[t].attributes[k] = Math.round(v * mult);
        });
      });
      if (keepAutoPrice) applyStandardPricesToCard(card);
      markDirty(true);
      populateCustomCardForm(card);
      toast('已按标准 1x/2x/4x/8x 阶梯自动推算四阶数值！');
    });

    // 添加中文效果积木预设
    $('btnAddPreset').addEventListener('click', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card || !state.catalog) return;
      const preset = (state.catalog.presets || []).find((p) => p.id === $('presetSelect').value);
      if (!preset) return;
      const cloned = JSON.parse(JSON.stringify(preset.template));
      if (preset.scope === 'aura') {
        card.auras = card.auras || {};
        const nextId = String(Object.keys(card.auras).length);
        cloned.id = nextId;
        card.auras[nextId] = cloned;
      } else {
        card.abilities = card.abilities || {};
        const nextId = String(Object.keys(card.abilities).length);
        cloned.id = nextId;
        card.abilities[nextId] = cloned;
      }
      syncCardTierAbilityRefs(card);
      markDirty(true);
      populateCustomCardForm(card);
      toast(`已添加效果积木：${preset.nameZh}`);
    });

    $('btnApplyAdvancedJson').addEventListener('click', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card) return;
      try {
        const obj = JSON.parse($('ceAdvancedRulesJson').value);
        if (obj.abilities) card.abilities = obj.abilities;
        if (obj.auras) card.auras = obj.auras;
        syncCardTierAbilityRefs(card);
        markDirty(true);
        populateCustomCardForm(card);
        toast('高级 JSON 规则已应用！');
      } catch (ex) {
        toast(`JSON 语法错误：${ex.message}`, 'err');
      }
    });

    $('btnDeleteCard').addEventListener('click', () => {
      if (!state.workspace || state.selectedCardIdx < 0) return;
      state.workspace.cards.splice(state.selectedCardIdx, 1);
      state.selectedCardIdx = state.workspace.cards.length ? 0 : -1;
      markDirty(true);
      updateCounts();
      renderMineLists();
      renderCardOrPatchEditor();
    });

    // Patch 表单同步
    const bindPe = (id, fn) => $(id).addEventListener('input', () => {
      const patch = state.workspace?.patches[state.selectedPatchIdx];
      if (!patch) return;
      fn(patch, $(id).value);
      markDirty(true);
      renderMineLists();
      updateLivePreviewFromPatch(patch);
    });
    bindPe('peNote', (p, v) => { p.note = v; });
    bindPe('peStartingTier', (p, v) => { p.startingTier = v || null; });
    bindPe('peTitleZh', (p, v) => { p.titleZh = v || null; });
    bindPe('peAddTags', (p, v) => { p.addTags = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindPe('peRemoveTags', (p, v) => { p.removeTags = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindPe('peDescZh', (p, v) => { p.descZh = v || null; });
    $('btnDeletePatch').addEventListener('click', () => {
      if (!state.workspace || state.selectedPatchIdx < 0) return;
      state.workspace.patches.splice(state.selectedPatchIdx, 1);
      state.selectedPatchIdx = state.workspace.patches.length ? 0 : -1;
      markDirty(true);
      updateCounts();
      renderMineLists();
      renderCardOrPatchEditor();
    });

    // 预览品阶切换 & 图片上传
    $('previewTierSwitcher').querySelectorAll('.tier-dot').forEach((d) => {
      d.addEventListener('click', () => {
        state.previewTier = d.dataset.tier;
        $('previewTierSwitcher').querySelectorAll('.tier-dot').forEach((x) => x.classList.toggle('active', x === d));
        const card = state.workspace?.cards[state.selectedCardIdx];
        if (card) updateLivePreview(card);
        else if (state.workspace?.patches[state.selectedPatchIdx]) updateLivePreviewFromPatch(state.workspace.patches[state.selectedPatchIdx]);
      });
    });

    $('previewEnchantSelect')?.addEventListener('change', () => {
      state.previewEnchant = $('previewEnchantSelect').value || '';
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (card) updateLivePreview(card);
    });

    $('btnChooseArt').addEventListener('click', () => $('artFileInput').click());
    $('artFileInput').addEventListener('change', (e) => {
      if (e.target.files?.[0]) handleArtFileUpload(e.target.files[0]);
    });
    $('btnClearArt').addEventListener('click', () => {
      const card = state.workspace?.cards[state.selectedCardIdx];
      if (!card) return;
      card.customArtFile = null;
      markDirty(true);
      updateLivePreview(card);
    });

    // 奇遇编辑事件
    $('btnNewEvent').addEventListener('click', () => {
      if (!state.workspace) return;
      const modId = state.workspace.manifest.id;
      const idx = state.workspace.events.length + 1;
      state.workspace.events.push({
        eventId: `${modId}:event_custom_${idx}`,
        eventType: 'EventEncounter',
        baseVisualTemplateId: DEFAULT_EVENT_VISUAL,
        titleZh: `自定义奇遇 #${idx}`,
        descZh: '一段充满机遇的星港奇遇。',
        heroes: ['Common'], minDay: 2, maxDay: 10, weight: 5.0,
        steps: [
          {
            customId: `${modId}:step_custom_${idx}_a`,
            type: 'EncounterStep', size: 'Medium', startingTier: 'Bronze',
            baseVisualTemplateId: DEFAULT_STEP_VISUAL,
            titleZh: '领取 6 金币补给',
            descZh: '立刻获得 6 金币。',
            attributes: { BuyPrice: 0 },
            abilities: {
              '0': {
                id: '0',
                trigger: { $type: 'TTriggerOnCardSelected' },
                action: {
                  $type: 'TActionPlayerModifyAttribute', attributeType: 'Gold', operation: 'Add',
                  value: { $type: 'TFixedValue', value: 6.0 },
                  target: { $type: 'TTargetPlayerAbsolute', targetMode: 'Player' }
                }
              }
            }
          }
        ]
      });
      state.selectedEventIdx = state.workspace.events.length - 1;
      markDirty(true);
      updateCounts();
      renderEventList();
      renderEventEditor();
      renderEventDag();
    });

    const bindEv = (id, fn) => $(id).addEventListener('input', () => {
      const ev = state.workspace?.events[state.selectedEventIdx];
      if (!ev) return;
      fn(ev, $(id).value);
      markDirty(true);
      renderEventList();
      renderEventDag();
    });
    bindEv('evId', (e, v) => { e.eventId = v.trim(); });
    bindEv('evBaseVisualId', (e, v) => { e.baseVisualTemplateId = v.trim(); });
    bindEv('evTitleZh', (e, v) => { e.titleZh = v; });
    bindEv('evHeroes', (e, v) => { e.heroes = v.split(',').map((s) => s.trim()).filter(Boolean); });
    bindEv('evMinDay', (e, v) => { e.minDay = parseInt(v, 10) || 1; });
    bindEv('evMaxDay', (e, v) => { e.maxDay = parseInt(v, 10) || 99; });
    bindEv('evWeight', (e, v) => { e.weight = parseFloat(v) || 0; });
    bindEv('evDescZh', (e, v) => { e.descZh = v; });

    $('btnAddEventStep').addEventListener('click', () => {
      const ev = state.workspace?.events[state.selectedEventIdx];
      if (!ev) return;
      const modId = state.workspace.manifest.id;
      const idx = (ev.steps?.length || 0) + 1;
      ev.steps = ev.steps || [];
      ev.steps.push({
        customId: `${modId}:step_${Date.now().toString(36)}_${idx}`,
        type: 'EncounterStep', size: 'Medium', startingTier: 'Bronze',
        baseVisualTemplateId: DEFAULT_STEP_VISUAL,
        titleZh: `奇遇选项 #${idx}`, descZh: '获得 4 金币。',
        attributes: { BuyPrice: 0 },
        abilities: {
          '0': {
            id: '0',
            trigger: { $type: 'TTriggerOnCardSelected' },
            action: {
              $type: 'TActionPlayerModifyAttribute', attributeType: 'Gold', operation: 'Add',
              value: { $type: 'TFixedValue', value: 4.0 },
              target: { $type: 'TTargetPlayerAbsolute', targetMode: 'Player' }
            }
          }
        }
      });
      markDirty(true);
      renderEventEditor();
      renderEventDag();
    });

    $('btnDeleteEvent').addEventListener('click', () => {
      if (!state.workspace || state.selectedEventIdx < 0) return;
      state.workspace.events.splice(state.selectedEventIdx, 1);
      state.selectedEventIdx = state.workspace.events.length ? 0 : -1;
      markDirty(true);
      updateCounts();
      renderEventList();
      renderEventEditor();
      renderEventDag();
    });

    // 一键标准化修复（补齐 abilityIds/auraIds、修正冷却秒转 ms）
    $('btnAutoFixCommon').addEventListener('click', () => {
      if (!state.workspace) return;
      let fixed = 0;
      (state.workspace.cards || []).forEach((c) => {
        if (c.tiers) {
          const abIds = Object.keys(c.abilities || {});
          const auIds = Object.keys(c.auras || {});
          Object.values(c.tiers).forEach((td) => {
            if (abIds.length && (!td.abilityIds || !td.abilityIds.length)) { td.abilityIds = [...abIds]; fixed++; }
            if (auIds.length && (!td.auraIds || !td.auraIds.length)) { td.auraIds = [...auIds]; fixed++; }
            if (td.attributes?.CooldownMax > 0 && td.attributes.CooldownMax <= 60) {
              td.attributes.CooldownMax = Math.round(td.attributes.CooldownMax * 1000);
              fixed++;
            }
          });
        }
      });
      if (fixed > 0) {
        markDirty(true);
        toast(`已自动标准化修复 ${fixed} 处常见问题，正在重新跑 10 道体检...`);
      } else {
        toast('未发现需要自动修复的常见格式问题。');
      }
      runTenGateValidation(true);
    });

    // 复制 AI 提示词
    document.querySelectorAll('.copy-prompt-btn').forEach((btn) => {
      btn.addEventListener('click', async () => {
        const text = $(btn.dataset.prompt)?.textContent || '';
        try {
          await navigator.clipboard.writeText(text);
          toast('已复制 AI 提示词到剪贴板！');
        } catch {
          toast('请手动全选文本框复制。', 'err');
        }
      });
    });
  }

  async function importZipFile(file) {
    const fd = new FormData();
    fd.append('file', file, file.name);
    toast('正在检疫导入 .bazaarmod.zip（含 10 闸门 + 50 场战斗校验）...');
    const res = await apiFetch('/api/workshop/mods/import', { method: 'POST', body: fd });
    if (!res.ok) {
      toast(res.data?.error || `导入被拦截（HTTP ${res.status}）`, 'err');
      if (res.data?.validation) {
        switchTab('validate');
        renderValidationReport(res.data.validation);
      }
      return;
    }
    toast(res.data.messageZh || '导入成功！');
    await loadModsList();
    if (res.data.modId) await openMod(res.data.modId);
  }

  window.WorkshopBridge = {
    state,
    $,
    apiFetch,
    apiUrl,
    toast,
    escapeHtml,
    markDirty,
    switchTab,
    updateCounts,
    syncCardTierAbilityRefs,
    renderCardOrPatchEditor,
    renderMineLists,
    renderEventEditor,
    populateCustomCardForm,
    populatePatchCardForm,
    updateLivePreview,
    updateLivePreviewFromPatch,
    saveDraft,
    loadMeta,
    generateUniqueCardId,
    MATRIX_ATTRS,
    TIERS,
    DURATION_ATTRS,
    mergeAttrsForTier,
    renderTooltipPreview,
  };

  document.addEventListener('DOMContentLoaded', init);
})();
