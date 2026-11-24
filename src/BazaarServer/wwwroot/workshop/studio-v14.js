(() => {
  'use strict';

  /**
   * V15.4 创意工坊核心扩展模块：
   * 1. 全量官方词缀可视化「句式积木拼装器」（46+ 触发器 × 43+ 动作链 TActionAnd × 10 目标 × 23 条件 × 11 数值源 × 133 VFX）；
   * 2. 原版 2,034+ 卡牌单条词缀一键「扒取」复用至新卡或原位魔改卡；
   * 3. 卡牌自定义机制自动参数表单、3D 实时计数角标（counterDisplays）与双宿主 13 附魔可视化编辑；
   * 4. 「开放机制工坊 Mechanic Lab」可视化编排 10 大通用管线钩子 + 18 种安全步骤流 + 20 场极限对抗沙盒压测；
   * 5. 扩展内容工坊（自定义野怪 Monsters / 玩法模式 Modes / 肉鸽突变词条 Mutators / 汉化覆盖 Localization）。
   */

  const B = () => window.WorkshopBridge;

  const v14 = {
    serverMechanics: [],
    selectedMechIdx: -1,
    selectedEnchantKey: 'Fiery',
    builderState: {
      mode: 'ability', // 'ability' | 'aura'
      targetHost: 'card', // 'card' | 'patch'
      editKey: null, // null=new, string=overwrite existing key
      actions: [],
      preservedExtra: null,
      preservedPrereqs: null,
      auraPreservedExtra: null,
      auraPreservedPrereqs: null,
    },
    currentVanillaBlueprint: null,
  };

  function readVfxOverrideKey(vfxConfig) {
    if (!vfxConfig || typeof vfxConfig !== 'object') return '';
    return vfxConfig.vfxOverrideKey ?? vfxConfig.vFXOverrideKey ?? vfxConfig.VFXOverrideKey ?? '';
  }

  function buildCamelVfxConfig(vfxKey, prevVfxConfig = null) {
    if (!vfxKey) return undefined;
    const prevShouldPlay = prevVfxConfig?.vfxShouldPlay ?? prevVfxConfig?.vFXShouldPlay ?? prevVfxConfig?.VFXShouldPlay;
    const prevTakeover = prevVfxConfig?.vfxIsTakeover ?? prevVfxConfig?.vFXIsTakeover ?? prevVfxConfig?.VFXIsTakeover;
    return {
      vfxOverrideKey: vfxKey,
      vfxShouldPlay: prevShouldPlay !== undefined ? Boolean(prevShouldPlay) : true,
      vfxIsTakeover: prevTakeover !== undefined ? Boolean(prevTakeover) : false,
    };
  }

  function safeNumAttr(v) {
    if (B()?.safeNumAttr) return B().safeNumAttr(v);
    if (v === null || v === undefined || v === '') return '';
    const n = Number(v);
    return Number.isFinite(n) ? String(n) : '';
  }

  const TARGET_OPTIONS = [
    { value: 'OpponentPlayer', label: '敌方玩家' },
    { value: 'SelfPlayer', label: '己方玩家' },
    { value: 'SelfCard', label: '本卡自身' },
    { value: 'Neighbor', label: '左右相邻物品' },
    { value: 'Left', label: '左侧紧邻物品' },
    { value: 'Right', label: '右侧紧邻物品' },
    { value: 'LeftMostAlly', label: '最左侧己方物品' },
    { value: 'RightMostAlly', label: '最右侧己方物品' },
    { value: 'AllAllies', label: '全部己方物品' },
    { value: 'AllEnemies', label: '全部敌方物品' },
    { value: 'RandomAlly', label: '随机一件己方物品' },
    { value: 'RandomEnemy', label: '随机一件敌方物品' },
    { value: 'TriggerSource', label: '触发来源那张卡' },
    { value: 'TriggerTarget', label: '触发目标那张卡' },
  ];

  const HOOK_SCOPE_ZH = {
    SelfCard: '仅监听本卡自身 (SelfCard)',
    OwnerPlayer: '监听己方玩家 (OwnerPlayer)',
    EnemyPlayer: '监听敌方玩家 (EnemyPlayer)',
    OwnerBoard: '监听己方全场卡牌 (OwnerBoard)',
    EnemyBoard: '监听敌方全场卡牌 (EnemyBoard)',
    Global: '监听全局双方 (Global)',
    Any: '监听任意来源 (Any)',
  };

  const PLAYER_ACTIONS = new Set([
    'TActionPlayerDamage', 'TActionPlayerShieldApply', 'TActionPlayerBurnApply', 'TActionPlayerBurnRemove',
    'TActionPlayerPoisonApply', 'TActionPlayerPoisonRemove', 'TActionPlayerHeal', 'TActionPlayerReviveHeal',
    'TActionPlayerRegenApply', 'TActionPlayerRageApply', 'TActionPlayerTempoApply',
    'TActionPlayerModifyAttribute', 'TActionCostPlayerAttribute',
  ]);

  const DEFAULT_ATTR_FOR_ACTION = {
    TActionPlayerDamage: 'DamageAmount',
    TActionPlayerShieldApply: 'ShieldApplyAmount',
    TActionPlayerBurnApply: 'BurnApplyAmount',
    TActionPlayerPoisonApply: 'PoisonApplyAmount',
    TActionPlayerHeal: 'HealAmount',
    TActionPlayerRegenApply: 'RegenApplyAmount',
    TActionCardHaste: 'HasteAmount',
    TActionCardSlow: 'SlowAmount',
    TActionCardFreeze: 'FreezeAmount',
    TActionCardCharge: 'ChargeAmount',
  };

  function catalog() { return B()?.state?.catalog || {}; }
  function escapeHtml(s) { return B().escapeHtml(s); }
  function $(id) { return B().$(id); }

  function labelFromList(list, id) {
    const found = (list || []).find((x) => x.id === id);
    return found ? found.nameZh : (id || '?');
  }

  // ── 词缀自然语言中文翻译器（同时用于列表摘要、原版扒取器与实时预览） ──────────
  function describeTargetZh(t) {
    if (!t || !t.$type) return '默认目标';
    let base = t.$type;
    switch (t.$type) {
      case 'TTargetCardSelf': base = '本卡自身'; break;
      case 'TTargetPlayerRelative': base = t.targetMode === 'Self' ? '己方玩家' : '敌方玩家'; break;
      case 'TTargetPlayerAbsolute': base = '玩家本人'; break;
      case 'TTargetCardPositional':
        base = t.targetMode === 'Left' ? '左侧物品' : (t.targetMode === 'Right' ? '右侧物品' : '相邻物品');
        break;
      case 'TTargetCardXMost':
        base = `${t.targetSection === 'OpponentHand' ? '敌方' : '己方'}${t.targetMode === 'RightMostCard' ? '最右侧' : '最左侧'}物品`;
        break;
      case 'TTargetCardSection':
        base = t.targetSection === 'OpponentHand' ? '敌方手牌' : (t.targetSection === 'AllHands' ? '双方手牌' : '己方手牌');
        break;
      case 'TTargetCardRandom':
        base = `随机${t.targetSection === 'OpponentHand' ? '敌方' : '己方'}物品`;
        break;
      case 'TTargetCardTriggerSource': base = '触发源卡牌'; break;
      case 'TTargetCardTriggerTarget': base = '触发目标卡牌'; break;
    }
    const condZh = describeConditionZh(t.conditions);
    return condZh ? `${base}（${condZh}）` : base;
  }

  function describeConditionZh(c) {
    if (!c || !c.$type) return '';
    if (c.$type === 'TCardConditionalTag') return `含标签:${(c.tags || []).join('/')}`;
    if (c.$type === 'TCardConditionalHiddenTag') return `含属性类:${(c.tags || []).join('/')}`;
    if (c.$type === 'TCardConditionalAnd') return (c.conditions || []).map(describeConditionZh).filter(Boolean).join(' 且 ');
    if (c.$type === 'TCardConditionalOr') return (c.conditions || []).map(describeConditionZh).filter(Boolean).join(' 或 ');
    if (c.$type === 'TCardConditionalSize') return `尺寸:${(c.sizes || []).join('/')}`;
    if (c.$type === 'TCardConditionalTier') return `品质:${(c.tiers || []).join('/')}`;
    if (c.$type === 'TCardConditionalAttribute') return `${c.attribute || c.attributeType || '属性'}${c.comparisonOperator || ''}`;
    if (c.$type === 'TPlayerConditionalAttribute') return `玩家${c.attribute || c.attributeType || '属性'}${c.comparisonOperator || ''}`;
    return labelFromList(catalog().conditions, c.$type);
  }

  function describeValueZh(v) {
    if (!v || !v.$type) return '';
    if (v.$type === 'TFixedValue') return String(v.value ?? 0);
    if (v.$type === 'TReferenceValueCardAttribute') return `{attr.${v.attributeType || 'Custom_0'}}`;
    if (v.$type === 'TReferenceValuePlayerAttribute') return `玩家${v.attributeType || 'Gold'}`;
    if (v.$type === 'TReferenceValueCardTagCount') return `场上[${(v.tags || v.target?.conditions?.tags || []).join('/')}]标签种类数`;
    if (v.$type === 'TReferenceValueCardCount') {
      const condTags = v.target?.conditions?.tags;
      return condTags?.length ? `场上[${condTags.join('/')}]数量` : '符合条件的卡牌数';
    }
    return v.$type;
  }

  function describeDurationZh(d) {
    if (!d || !d.$type) return '';
    if (d.$type === 'TDeterminantDuration') {
      if (d.durationType === 'UntilEndOfHour') return '（本小时）';
      if (d.durationType === 'UntilEndOfDay') return '（本日）';
      return '（本战）';
    }
    if (d.$type === 'TCombatDuration') {
      const ms = Number(d.durationInMs);
      return Number.isFinite(ms) && ms > 0 ? `（限时 ${Math.round(ms / 100) / 10}s）` : '（本战限时）';
    }
    return '';
  }

  function describeActionZh(a) {
    if (!a || !a.$type) return '未设置动作';
    if (a.$type === 'TActionAnd') {
      return (a.actions || []).map(describeActionZh).join('；并且 ');
    }
    const actName = labelFromList(catalog().actions, a.$type);
    const tgt = describeTargetZh(a.target);
    const val = describeValueZh(a.value);
    if (a.$type === 'TActionCardModifyAttribute' || a.$type === 'TActionPlayerModifyAttribute') {
      const opSym = a.operation === 'Subtract' ? '-' : (a.operation === 'Multiply' ? '×' : '+');
      const dur = describeDurationZh(a.duration);
      return `使【${tgt}】的 ${a.attributeType || 'DamageAmount'} ${opSym}${val || '{attr.Custom_0}'}${dur}`;
    }
    if (a.$type === 'TActionCardCharge' || a.$type === 'TActionCardHaste' || a.$type === 'TActionCardSlow' || a.$type === 'TActionCardFreeze') {
      const defAttr = DEFAULT_ATTR_FOR_ACTION[a.$type];
      return `对【${tgt}】${actName.replace('目标卡牌', '')} ${val || `{attr.${defAttr}}`}`;
    }
    if (a.$type === 'TActionCardEnchant' && a.enchantmentType) {
      return `为【${tgt}】赋予 ${a.enchantmentType} 附魔`;
    }
    return `对【${tgt}】执行「${actName}」${val ? `（数值 ${val}）` : ''}`;
  }

  function summarizeAffixZh(kind, def) {
    if (!def) return '';
    if (kind === 'aura') {
      const a = def.action || {};
      const actName = labelFromList(catalog().auraActions, a.$type);
      const tgt = describeTargetZh(a.target);
      const val = describeValueZh(a.value);
      const opSym = a.operation === 'Subtract' ? '-' : (a.operation === 'Multiply' ? '×' : '+');
      if (a.$type === 'TAuraActionCardAddTagsList') {
        return `光环：赋予【${tgt}】标签 ${(a.tags || []).join('/')}`;
      }
      return `光环：使【${tgt}】${a.attributeType || ''} ${opSym}${val || '0'}（${actName}）`;
    }
    const trig = def.trigger || {};
    let trigZh = labelFromList(catalog().triggers, trig.$type);
    if (trig.$type === 'TTriggerOr' && Array.isArray(trig.triggers)) {
      trigZh = trig.triggers.map((t) => labelFromList(catalog().triggers, t.$type)).join(' 或 ');
    }
    if (trig.subject) {
      trigZh = `${describeTargetZh(trig.subject)} ${trigZh}`;
    }
    return `${trigZh}：${describeActionZh(def.action)}`;
  }

  // ── 目标读写辅助（统一兼容 新建卡牌 与 原位魔改 Patch） ──────────────────────
  function getActiveTargetHost() {
    const st = B().state;
    if (st.workspace && st.selectedCardIdx >= 0 && st.workspace.cards[st.selectedCardIdx]) {
      return { host: 'card', obj: st.workspace.cards[st.selectedCardIdx] };
    }
    if (st.workspace && st.selectedPatchIdx >= 0 && st.workspace.patches[st.selectedPatchIdx]) {
      return { host: 'patch', obj: st.workspace.patches[st.selectedPatchIdx] };
    }
    return null;
  }

  function getAbilitiesMap(hostInfo, createIfMissing = false) {
    if (hostInfo.host === 'card') {
      if (createIfMissing && !hostInfo.obj.abilities) hostInfo.obj.abilities = {};
      return hostInfo.obj.abilities || {};
    }
    if (createIfMissing && !hostInfo.obj.abilitiesOverride) hostInfo.obj.abilitiesOverride = {};
    return hostInfo.obj.abilitiesOverride || {};
  }

  function getAurasMap(hostInfo, createIfMissing = false) {
    if (hostInfo.host === 'card') {
      if (createIfMissing && !hostInfo.obj.auras) hostInfo.obj.auras = {};
      return hostInfo.obj.auras || {};
    }
    if (createIfMissing && !hostInfo.obj.aurasOverride) hostInfo.obj.aurasOverride = {};
    return hostInfo.obj.aurasOverride || {};
  }

  function getMechanicsList(hostInfo, createIfMissing = false) {
    if (hostInfo.host === 'card') {
      if (createIfMissing && !hostInfo.obj.customMechanics) hostInfo.obj.customMechanics = [];
      return hostInfo.obj.customMechanics || [];
    }
    if (createIfMissing && !hostInfo.obj.customMechanicsOverride) hostInfo.obj.customMechanicsOverride = [];
    return hostInfo.obj.customMechanicsOverride || [];
  }

  function nextMapKey(map) {
    let k = 0;
    while (Object.prototype.hasOwnProperty.call(map, String(k))) k++;
    return String(k);
  }

  function refreshActiveHostEditor() {
    const info = getActiveTargetHost();
    if (!info) return;
    if (info.host === 'card') {
      B().syncCardTierAbilityRefs(info.obj);
      B().populateCustomCardForm(info.obj);
    } else {
      B().populatePatchCardForm(info.obj);
    }
    renderPatchAbilitiesSummary();
    renderCardAttachedMechanics();
  }

  // ── 1. 全量官方词缀可视化「句式积木拼装器」 ─────────────────────────────────
  function populateBuilderSelects() {
    const cat = catalog();
    const triggers = cat.triggers || [];
    const tags = cat.tags || [];
    const htags = cat.hiddenTags || [];
    const attrs = cat.attributes || [];

    const fillTriggers = (sel, filterText = '') => {
      const q = filterText.trim().toLowerCase();
      const prev = sel.value;
      const list = q ? triggers.filter((t) => `${t.nameZh} ${t.id} ${t.descZh || ''}`.toLowerCase().includes(q)) : triggers;
      sel.innerHTML = list.map((t) => `<option value="${escapeHtml(t.id)}">${escapeHtml(t.nameZh)}</option>`).join('');
      if (prev && list.some((t) => t.id === prev)) sel.value = prev;
    };

    fillTriggers($('abTriggerType'));
    fillTriggers($('abTriggerType2'));
    $('abTriggerSearch').oninput = () => fillTriggers($('abTriggerType'), $('abTriggerSearch').value);

    const tagOpts = '<option value="">任意物品标签</option>' + tags.map((t) => `<option value="${escapeHtml(t.id)}">${escapeHtml(t.nameZh)}</option>`).join('');
    const htagOpts = '<option value="">任意效果分类</option>' + htags.map((t) => `<option value="${escapeHtml(t.id)}">${escapeHtml(t.nameZh)}</option>`).join('');
    $('abTriggerSubjectTag').innerHTML = tagOpts;
    $('abTriggerSubjectHiddenTag').innerHTML = htagOpts;
    $('abAuraTargetTag').innerHTML = tagOpts;
    $('abAuraAttr').innerHTML = attrs.map((a) => `<option value="${escapeHtml(a.id)}">${escapeHtml(a.nameZh)}</option>`).join('')
      + tags.map((t) => `<option value="TAG:${escapeHtml(t.id)}">[标签] ${escapeHtml(t.nameZh)}</option>`).join('');
    if ($('abAuraVfxKey')) $('abAuraVfxKey').innerHTML = renderVfxOptionsHtml($('abAuraVfxKey').value);
  }

  function buildDurationRuleNode(duration, durationMs) {
    if (duration === 'Combat') {
      return { $type: 'TDeterminantDuration', durationType: 'UntilEndOfCombat' };
    }
    if (duration === 'UntilEndOfHour') {
      return { $type: 'TDeterminantDuration', durationType: 'UntilEndOfHour' };
    }
    if (duration === 'UntilEndOfDay') {
      return { $type: 'TDeterminantDuration', durationType: 'UntilEndOfDay' };
    }
    if (duration === 'TimedCombat') {
      const ms = Math.max(500, parseInt(durationMs, 10) || 2000);
      return { $type: 'TCombatDuration', durationInMs: ms };
    }
    return undefined;
  }

  function parseDurationFromRuleNode(durationNode) {
    if (!durationNode || !durationNode.$type) {
      return { duration: 'Permanent', durationMs: 2000 };
    }
    if (durationNode.$type === 'TDeterminantDuration') {
      if (durationNode.durationType === 'UntilEndOfHour') return { duration: 'UntilEndOfHour', durationMs: 2000 };
      if (durationNode.durationType === 'UntilEndOfDay') return { duration: 'UntilEndOfDay', durationMs: 2000 };
      return { duration: 'Combat', durationMs: 2000 };
    }
    if (durationNode.$type === 'TCombatDuration') {
      const ms = parseInt(durationNode.durationInMs, 10);
      if (Number.isFinite(ms) && ms > 0) return { duration: 'TimedCombat', durationMs: ms };
      return { duration: 'Combat', durationMs: 2000 };
    }
    return { duration: 'Permanent', durationMs: 2000 };
  }

  const PERMANENT_PLAYER_ATTRS = new Set(['Gold', 'Income', 'Experience', 'Prestige']);

  function makeDefaultActionRow(actionType = 'TActionPlayerDamage') {
    const isPlayer = PLAYER_ACTIONS.has(actionType);
    const isSelfHelpful = isSelfPlayerAction(actionType);
    const defaultAttr = actionType === 'TActionPlayerModifyAttribute' ? 'Gold' : 'DamageAmount';
    return {
      actionType,
      targetMode: isPlayer ? (isSelfHelpful ? 'SelfPlayer' : 'OpponentPlayer') : 'Neighbor',
      targetTag: '',
      targetHiddenTag: '',
      randomCount: 1,
      valueMode: 'default', // default | fixed | cardAttr | playerAttr | cardTagCount
      valueInput: '1000',
      attributeType: defaultAttr,
      operation: 'Add',
      duration: (actionType === 'TActionPlayerModifyAttribute' && PERMANENT_PLAYER_ATTRS.has(defaultAttr)) ? 'Permanent' : 'Combat',
      durationMs: 2000,
      enchantmentType: 'Fiery',
      vfxKey: '',
    };
  }

  function isSelfPlayerAction(t) {
    return t === 'TActionPlayerShieldApply' || t === 'TActionPlayerHeal' || t === 'TActionPlayerReviveHeal'
      || t === 'TActionPlayerRegenApply' || t === 'TActionPlayerModifyAttribute' || t === 'TActionPlayerBurnRemove'
      || t === 'TActionPlayerPoisonRemove' || t === 'TActionPlayerRageApply' || t === 'TActionPlayerTempoApply';
  }

  function parseRuleNodeToTargetChoice(tNode) {
    if (!tNode || !tNode.$type) return { targetMode: 'SelfCard', targetTag: '', targetHiddenTag: '', randomCount: 1 };
    let targetMode = 'SelfCard';
    let randomCount = 1;
    switch (tNode.$type) {
      case 'TTargetPlayerRelative': targetMode = tNode.targetMode === 'Self' ? 'SelfPlayer' : 'OpponentPlayer'; break;
      case 'TTargetPlayerAbsolute': targetMode = 'SelfPlayer'; break;
      case 'TTargetCardSelf': targetMode = 'SelfCard'; break;
      case 'TTargetCardPositional': targetMode = tNode.targetMode === 'Left' ? 'Left' : (tNode.targetMode === 'Right' ? 'Right' : 'Neighbor'); break;
      case 'TTargetCardXMost': targetMode = tNode.targetMode === 'RightMostCard' ? 'RightMostAlly' : 'LeftMostAlly'; break;
      case 'TTargetCardSection': targetMode = tNode.targetSection === 'OpponentHand' ? 'AllEnemies' : 'AllAllies'; break;
      case 'TTargetCardRandom': targetMode = tNode.targetSection === 'OpponentHand' ? 'RandomEnemy' : 'RandomAlly'; break;
      case 'TTargetCardTriggerSource': targetMode = 'TriggerSource'; break;
      case 'TTargetCardTriggerTarget': targetMode = 'TriggerTarget'; break;
    }
    let targetTag = '';
    let targetHiddenTag = '';
    const extractCond = (c) => {
      if (!c) return;
      if (c.$type === 'TCardConditionalTag' && c.tags?.[0]) targetTag = c.tags[0];
      if (c.$type === 'TCardConditionalHiddenTag' && c.tags?.[0]) targetHiddenTag = c.tags[0];
      if (Array.isArray(c.conditions)) c.conditions.forEach(extractCond);
    };
    extractCond(tNode.conditions);
    return { targetMode, targetTag, targetHiddenTag, randomCount };
  }

  function parseActionNodeToRows(actionNode) {
    if (!actionNode) return [makeDefaultActionRow()];
    if (actionNode.$type === 'TActionAnd' && Array.isArray(actionNode.actions) && actionNode.actions.length > 0) {
      return actionNode.actions.flatMap(parseActionNodeToRows);
    }
    const tgt = parseRuleNodeToTargetChoice(actionNode.target);
    let valueMode = 'default';
    let valueInput = '';
    if (actionNode.value?.$type === 'TFixedValue') {
      valueMode = 'fixed';
      valueInput = String(actionNode.value.value ?? 0);
    } else if (actionNode.value?.$type === 'TReferenceValueCardAttribute') {
      valueMode = 'cardAttr';
      valueInput = actionNode.value.attributeType || 'Custom_0';
    } else if (actionNode.value?.$type === 'TReferenceValuePlayerAttribute') {
      valueMode = 'playerAttr';
      valueInput = actionNode.value.attributeType || 'Gold';
    } else if (actionNode.value?.$type === 'TReferenceValueCardCount' && actionNode.value.target?.conditions?.tags?.[0]) {
      valueMode = 'cardTagCount';
      valueInput = actionNode.value.target.conditions.tags[0];
    } else if (actionNode.value?.$type === 'TReferenceValueCardTagCount') {
      valueMode = 'cardTagCount';
      valueInput = (actionNode.value.tags || actionNode.value.target?.conditions?.tags || ['Weapon'])[0];
    }
    const parsedDur = parseDurationFromRuleNode(actionNode.duration);
    return [{
      actionType: actionNode.$type || 'TActionPlayerDamage',
      ...tgt,
      valueMode,
      valueInput: valueInput || '10',
      attributeType: actionNode.attributeType || (actionNode.$type === 'TActionPlayerModifyAttribute' ? 'Gold' : 'DamageAmount'),
      operation: actionNode.operation || 'Add',
      duration: parsedDur.duration,
      durationMs: parsedDur.durationMs,
      enchantmentType: actionNode.enchantmentType || 'Fiery',
      vfxKey: '',
    }];
  }

  function buildTargetRuleNode(mode, tag, htag) {
    const conds = [];
    if (tag) conds.push({ $type: 'TCardConditionalTag', tags: [tag], operator: 'Any' });
    if (htag) conds.push({ $type: 'TCardConditionalHiddenTag', tags: [htag], operator: 'Any' });
    const conditions = conds.length === 0 ? undefined : (conds.length === 1 ? conds[0] : { $type: 'TCardConditionalAnd', conditions: conds });

    const withCond = (obj) => { if (conditions) obj.conditions = conditions; return obj; };
    switch (mode) {
      case 'OpponentPlayer': return { $type: 'TTargetPlayerRelative', targetMode: 'Opponent' };
      case 'SelfPlayer': return { $type: 'TTargetPlayerRelative', targetMode: 'Self' };
      case 'SelfCard': return { $type: 'TTargetCardSelf' };
      case 'Neighbor': return withCond({ $type: 'TTargetCardPositional', origin: 'Self', targetMode: 'Neighbor' });
      case 'Left': return withCond({ $type: 'TTargetCardPositional', origin: 'Self', targetMode: 'Left' });
      case 'Right': return withCond({ $type: 'TTargetCardPositional', origin: 'Self', targetMode: 'Right' });
      case 'LeftMostAlly': return withCond({ $type: 'TTargetCardXMost', targetSection: 'SelfHand', targetMode: 'LeftMostCard' });
      case 'RightMostAlly': return withCond({ $type: 'TTargetCardXMost', targetSection: 'SelfHand', targetMode: 'RightMostCard' });
      case 'AllAllies': return withCond({ $type: 'TTargetCardSection', targetSection: 'SelfHand' });
      case 'AllEnemies': return withCond({ $type: 'TTargetCardSection', targetSection: 'OpponentHand' });
      case 'RandomAlly': return withCond({ $type: 'TTargetCardRandom', targetSection: 'SelfHand' });
      case 'RandomEnemy': return withCond({ $type: 'TTargetCardRandom', targetSection: 'OpponentHand' });
      case 'TriggerSource': return withCond({ $type: 'TTargetCardTriggerSource' });
      case 'TriggerTarget': return withCond({ $type: 'TTargetCardTriggerTarget' });
      default: return { $type: 'TTargetCardSelf' };
    }
  }

  function buildValueRuleNode(mode, input) {
    if (mode === 'default') return undefined;
    if (mode === 'fixed') return { $type: 'TFixedValue', value: parseFloat(input) || 0 };
    if (mode === 'cardAttr') return { $type: 'TReferenceValueCardAttribute', attributeType: (input || 'Custom_0').trim(), target: { $type: 'TTargetCardSelf' } };
    if (mode === 'playerAttr') return { $type: 'TReferenceValuePlayerAttribute', attributeType: (input || 'Gold').trim(), target: { $type: 'TTargetPlayerRelative', targetMode: 'Self' } };
    if (mode === 'cardTagCount') {
      return {
        $type: 'TReferenceValueCardCount',
        target: {
          $type: 'TTargetCardSection',
          targetSection: 'SelfHand',
          conditions: { $type: 'TCardConditionalTag', tags: [(input || 'Weapon').trim()], operator: 'Any' },
        },
      };
    }
    return undefined;
  }

  function compilePrereqFromControls(prefix, preservedPrereqs) {
    const pk = $(`${prefix}PrereqKind`)?.value || 'none';
    if (pk === 'preserve' && Array.isArray(preservedPrereqs) && preservedPrereqs.length) {
      return JSON.parse(JSON.stringify(preservedPrereqs));
    }
    if (pk === 'cardCount') {
      const cTag = $(`${prefix}PrereqTag`)?.value || 'Weapon';
      const cMin = parseInt($(`${prefix}PrereqMin`)?.value || '2', 10) || 1;
      return [{
        $type: 'TPrerequisiteCardCount',
        subject: buildTargetRuleNode('AllAllies', cTag, ''),
        comparison: 'GreaterThanOrEqual',
        amount: cMin,
      }];
    }
    if (pk === 'playerAttr') {
      const pAttr = $(`${prefix}PrereqPlayerAttr`)?.value || 'Gold';
      const pOp = $(`${prefix}PrereqPlayerOp`)?.value || 'GreaterThanOrEqual';
      const pVal = parseFloat($(`${prefix}PrereqPlayerVal`)?.value || '20') || 0;
      return [{
        $type: 'TPrerequisitePlayer',
        subject: {
          $type: 'TTargetPlayerRelative',
          targetMode: 'Self',
          conditions: {
            $type: 'TPlayerConditionalAttribute',
            attribute: pAttr,
            comparisonOperator: pOp,
            comparisonValue: { $type: 'TFixedValue', value: pVal },
          },
        },
      }];
    }
    if (pk === 'run') {
      const day = parseInt($(`${prefix}PrereqRunDay`)?.value || '5', 10) || 1;
      const op = $(`${prefix}PrereqRunOp`)?.value || 'GreaterThanOrEqual';
      return [{
        $type: 'TPrerequisiteRun',
        currentDay: day,
        comparisonOperator: op,
      }];
    }
    if (pk === 'cardAttrCmp') {
      const attr1 = $(`${prefix}PrereqCmpAttr1`)?.value || 'DamageAmount';
      const op = $(`${prefix}PrereqCmpOp`)?.value || 'GreaterThanOrEqual';
      const attr2 = $(`${prefix}PrereqCmpAttr2`)?.value || 'ShieldApplyAmount';
      return [{
        $type: 'TPrerequisiteCardAttributeComparator',
        subject: { $type: 'TTargetCardSelf' },
        subjectOther: { $type: 'TTargetCardSelf' },
        attribute: attr1,
        attributeOther: attr2,
        comparisonOperator: op,
      }];
    }
    return undefined;
  }

  function compileBuilderToRuleObject() {
    if (v14.builderState.mode === 'aura') {
      const actType = $('abAuraActionType').value;
      const tgtMode = $('abAuraTargetMode').value;
      const tgtTag = $('abAuraTargetTag').value;
      const rawAttr = $('abAuraAttr').value;
      const op = $('abAuraOp').value;
      const valMode = $('abAuraValMode').value;
      const valInput = $('abAuraValInput').value;

      const targetMap = {
        Self: 'SelfCard', Neighbor: 'Neighbor', Left: 'Left', Right: 'Right',
        SelfHand: 'AllAllies', EnemyHand: 'AllEnemies', PlayerSelf: 'SelfPlayer',
      };
      const action = {
        $type: actType,
        target: buildTargetRuleNode(targetMap[tgtMode] || 'SelfCard', tgtTag, ''),
      };
      if (actType === 'TAuraActionCardAddTagsList') {
        action.tags = [rawAttr.replace(/^TAG:/, '') || 'Weapon'];
      } else {
        action.attributeType = rawAttr.replace(/^TAG:/, '') || 'DamageAmount';
        action.operation = op;
        action.value = buildValueRuleNode(valMode === 'cardAttr' ? 'cardAttr' : 'fixed', valInput);
      }
      const auraObj = Object.assign({}, v14.builderState.auraPreservedExtra || {}, {
        activeIn: $('abAuraActiveIn')?.value || 'HandOnly',
        action,
      });
      const auraPrereqs = compilePrereqFromControls('abAura', v14.builderState.auraPreservedPrereqs);
      if (auraPrereqs) auraObj.prerequisites = auraPrereqs;
      else delete auraObj.prerequisites;

      const auraVfx = $('abAuraVfxKey')?.value || '';
      if (auraVfx) {
        auraObj.vfxConfig = buildCamelVfxConfig(auraVfx, v14.builderState.auraPreservedExtra?.vfxConfig);
      } else if (!auraVfx) {
        delete auraObj.vfxConfig;
      }
      return auraObj;
    }

    // Ability 模式
    const primaryTrigType = $('abTriggerType').value || 'TTriggerOnCardFired';
    const subjMode = $('abTriggerSubjectMode').value;
    const subjTag = $('abTriggerSubjectTag').value;
    const subjHTag = $('abTriggerSubjectHiddenTag').value;

    const buildSubject = () => {
      const map = { Self: 'SelfCard', Neighbor: 'Neighbor', Left: 'Left', Right: 'Right', SelfHand: 'AllAllies', EnemyHand: 'AllEnemies', AnyHand: 'AllAllies' };
      if (subjMode === 'None') return undefined;
      const node = buildTargetRuleNode(map[subjMode] || 'SelfCard', subjTag, subjHTag);
      if (subjMode === 'AnyHand') node.targetSection = 'AllHands';
      return node;
    };

    const trig1 = { $type: primaryTrigType };
    const subj = buildSubject();
    if (subj && primaryTrigType !== 'TTriggerOnCardFired' && primaryTrigType !== 'TTriggerOnFightStarted') {
      trig1.subject = subj;
    }

    let trigger = trig1;
    if ($('abUseOrTrigger').checked) {
      const trig2 = { $type: $('abTriggerType2').value || 'TTriggerOnItemUsed' };
      if (subj && trig2.$type !== 'TTriggerOnCardFired') trig2.subject = subj;
      trigger = { $type: 'TTriggerOr', triggers: [trig1, trig2] };
    }

    // 前置条件（完整支持 4 种合法类型 + 复杂条件无损保留）
    const prerequisites = compilePrereqFromControls('ab', v14.builderState.preservedPrereqs);

    // 动作链
    const builtActions = v14.builderState.actions.map((row) => {
      const a = {
        $type: row.actionType,
        target: buildTargetRuleNode(row.targetMode, row.targetTag, row.targetHiddenTag),
      };
      const valNode = buildValueRuleNode(row.valueMode, row.valueInput);
      if (valNode) a.value = valNode;

      if (row.actionType === 'TActionCardModifyAttribute' || row.actionType === 'TActionPlayerModifyAttribute') {
        a.attributeType = row.attributeType || (row.actionType === 'TActionPlayerModifyAttribute' ? 'Gold' : 'DamageAmount');
        a.operation = row.operation || 'Add';
        if (!a.value) a.value = { $type: 'TReferenceValueCardAttribute', attributeType: 'Custom_0', target: { $type: 'TTargetCardSelf' } };
        const durNode = buildDurationRuleNode(row.duration, row.durationMs);
        if (durNode) a.duration = durNode;
      } else if (row.actionType === 'TActionCardEnchant') {
        a.enchantmentType = row.enchantmentType || 'Fiery';
      }
      return a;
    });

    const finalAction = builtActions.length === 1
      ? builtActions[0]
      : { $type: 'TActionAnd', actions: builtActions };

    const ab = Object.assign({}, v14.builderState.preservedExtra || {}, {
      activeIn: $('abActiveIn').value || 'HandOnly',
      trigger,
      action: finalAction,
    });
    if (prerequisites) ab.prerequisites = prerequisites;
    else delete ab.prerequisites;

    const firstVfx = v14.builderState.actions.find((r) => r.vfxKey)?.vfxKey;
    if (firstVfx) {
      ab.vfxConfig = buildCamelVfxConfig(firstVfx, v14.builderState.preservedExtra?.vfxConfig);
    } else {
      delete ab.vfxConfig;
    }
    return ab;
  }

  function updateAffixBuilderPreview() {
    const obj = compileBuilderToRuleObject();
    $('abChinesePreview').textContent = summarizeAffixZh(v14.builderState.mode, obj);
    $('abGeneratedJson').value = JSON.stringify(obj, null, 2);
  }

  function renderVfxOptionsHtml(selectedVal) {
    const cat = catalog();
    const presets = (cat.mechanicSchema && cat.mechanicSchema.vfxPresets) || [];
    const rawKeys = cat.vfxKeys || [];
    const cur = selectedVal || '';
    const seen = new Set();
    let html = '<option value="">（使用默认特效）</option>';
    if (presets.length > 0) {
      html += '<optgroup label="✨ 官方实测推荐特效（中文预设）">';
      presets.forEach((p) => {
        seen.add(p.id);
        html += `<option value="${escapeHtml(p.id)}" ${cur === p.id ? 'selected' : ''}>${escapeHtml(p.nameZh)} [${escapeHtml(p.category || '特效')}]</option>`;
      });
      html += '</optgroup>';
    }
    const extraRaw = rawKeys.filter((k) => !seen.has(k));
    if (extraRaw.length > 0) {
      html += '<optgroup label="🔧 全部官方原生 Prefab 键">';
      extraRaw.forEach((k) => {
        html += `<option value="${escapeHtml(k)}" ${cur === k ? 'selected' : ''}>${escapeHtml(k)}</option>`;
      });
      html += '</optgroup>';
    }
    if (cur && !seen.has(cur) && !rawKeys.includes(cur)) {
      html += `<option value="${escapeHtml(cur)}" selected>${escapeHtml(cur)}（自定义）</option>`;
    }
    return html;
  }

  function renderBuilderActionsList() {
    const cat = catalog();
    const allActions = (cat.actions || []).filter((a) => a.id !== 'TActionAnd');
    const tags = cat.tags || [];
    const htags = cat.hiddenTags || [];
    const attrs = cat.attributes || [];
    const enchants = cat.enchantments || [];

    const box = $('abActionsList');
    box.innerHTML = v14.builderState.actions.map((row, idx) => {
      const isModAttr = row.actionType === 'TActionCardModifyAttribute' || row.actionType === 'TActionPlayerModifyAttribute';
      const isEnchant = row.actionType === 'TActionCardEnchant';
      const valPlaceholder = row.valueMode === 'cardAttr' ? '如 Custom_0' : (row.valueMode === 'playerAttr' ? '如 Gold / HealthMax' : (row.valueMode === 'cardTagCount' ? '如 Weapon' : '数值（ms 或点）'));
      return `
        <div class="action-chain-card" data-aidx="${idx}">
          <div class="action-chain-head">
            <strong>动作步骤 #${idx + 1}</strong>
            <div class="btn-row">
              ${v14.builderState.actions.length > 1 ? `<button type="button" class="btn btn-danger btn-xs" data-rm-act="${idx}">删除此步</button>` : ''}
            </div>
          </div>
          <div class="form-grid">
            <label class="field">
              <span>要执行什么效果</span>
              <select data-af="actionType" data-aidx="${idx}">
                ${allActions.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === row.actionType ? 'selected' : ''}>${escapeHtml(a.nameZh)}</option>`).join('')}
              </select>
            </label>
            <label class="field">
              <span>对谁生效</span>
              <select data-af="targetMode" data-aidx="${idx}">
                ${TARGET_OPTIONS.map((o) => `<option value="${o.value}" ${o.value === row.targetMode ? 'selected' : ''}>${escapeHtml(o.label)}</option>`).join('')}
              </select>
            </label>
            <label class="field">
              <span>只影响带此标签的目标（可选）</span>
              <div class="inline-pair">
                <select data-af="targetTag" data-aidx="${idx}">
                  <option value="">任意物品标签</option>
                  ${tags.map((t) => `<option value="${escapeHtml(t.id)}" ${t.id === row.targetTag ? 'selected' : ''}>${escapeHtml(t.nameZh)}</option>`).join('')}
                </select>
                <select data-af="targetHiddenTag" data-aidx="${idx}">
                  <option value="">任意效果分类</option>
                  ${htags.map((t) => `<option value="${escapeHtml(t.id)}" ${t.id === row.targetHiddenTag ? 'selected' : ''}>${escapeHtml(t.nameZh)}</option>`).join('')}
                </select>
              </div>
            </label>
            <label class="field">
              <span>数值来源（时间类填毫秒，1秒=1000）</span>
              <div class="inline-pair">
                <select data-af="valueMode" data-aidx="${idx}">
                  <option value="default" ${row.valueMode === 'default' ? 'selected' : ''}>跟随本卡四阶数值表</option>
                  <option value="fixed" ${row.valueMode === 'fixed' ? 'selected' : ''}>填写固定数值</option>
                  <option value="cardAttr" ${row.valueMode === 'cardAttr' ? 'selected' : ''}>读取本卡指定属性（随品质成长）</option>
                  <option value="playerAttr" ${row.valueMode === 'playerAttr' ? 'selected' : ''}>读取玩家当前金币/生命</option>
                  <option value="cardTagCount" ${row.valueMode === 'cardTagCount' ? 'selected' : ''}>按场上某类标签数量计算</option>
                </select>
                ${row.valueMode !== 'default' ? `<input type="text" data-af="valueInput" data-aidx="${idx}" value="${escapeHtml(row.valueInput)}" placeholder="${valPlaceholder}" />` : ''}
              </div>
            </label>
            ${isModAttr ? `
            <label class="field">
              <span>要修改的属性与增减</span>
              <div class="inline-pair">
                <select data-af="attributeType" data-aidx="${idx}">
                  ${(row.actionType === 'TActionPlayerModifyAttribute'
                    ? (cat.mechanicSchema?.playerAttributes || [
                      { id: 'Gold', nameZh: '金币 (Gold)' },
                      { id: 'HealthMax', nameZh: '生命上限 (HealthMax)' },
                      { id: 'Income', nameZh: '每小时收入 (Income)' },
                      { id: 'Experience', nameZh: '经验 (Experience)' },
                      { id: 'Prestige', nameZh: '声望 (Prestige)' },
                      { id: 'HealthRegen', nameZh: '生命再生 (HealthRegen)' },
                      { id: 'PercentDamageReduction', nameZh: '百分比减伤 (PercentDamageReduction)' },
                    ])
                    : attrs).map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === row.attributeType ? 'selected' : ''}>${escapeHtml(a.nameZh)}</option>`).join('')}
                </select>
                <select data-af="operation" data-aidx="${idx}">
                  <option value="Add" ${row.operation === 'Add' ? 'selected' : ''}>+ 增加</option>
                  <option value="Subtract" ${row.operation === 'Subtract' ? 'selected' : ''}>- 减少</option>
                  <option value="Multiply" ${row.operation === 'Multiply' ? 'selected' : ''}>× 乘倍率</option>
                </select>
              </div>
            </label>
            <label class="field">
              <span>持续时长（战中 Buff 默认本战结束复原，防永久滚雪球）</span>
              <div class="inline-pair">
                <select data-af="duration" data-aidx="${idx}">
                  <option value="Combat" ${row.duration === 'Combat' ? 'selected' : ''}>仅本场战斗生效（UntilEndOfCombat）</option>
                  <option value="TimedCombat" ${row.duration === 'TimedCombat' ? 'selected' : ''}>战斗内限时（毫秒后复原）</option>
                  <option value="UntilEndOfHour" ${row.duration === 'UntilEndOfHour' ? 'selected' : ''}>持续至本小时结束</option>
                  <option value="UntilEndOfDay" ${row.duration === 'UntilEndOfDay' ? 'selected' : ''}>持续至本日结束</option>
                  <option value="Permanent" ${row.duration === 'Permanent' ? 'selected' : ''}>整局游戏永久保留</option>
                </select>
                ${row.duration === 'TimedCombat' ? `<input type="number" data-af="durationMs" data-aidx="${idx}" value="${escapeHtml(safeNumAttr(row.durationMs ?? 2000))}" min="500" max="60000" step="500" placeholder="毫秒 ms" />` : ''}
              </div>
            </label>` : ''}
            ${isEnchant ? `
            <label class="field">
              <span>附魔类型</span>
              <select data-af="enchantmentType" data-aidx="${idx}">
                ${enchants.map((e) => `<option value="${escapeHtml(e.id)}" ${e.id === row.enchantmentType ? 'selected' : ''}>${escapeHtml(e.nameZh)}</option>`).join('')}
              </select>
            </label>` : ''}
            <label class="field">
              <span>专属战斗特效（可选，留空用默认特效）</span>
              <select data-af="vfxKey" data-aidx="${idx}">
                ${renderVfxOptionsHtml(row.vfxKey)}
              </select>
            </label>
          </div>
        </div>
      `;
    }).join('');

    box.querySelectorAll('[data-af]').forEach((el) => {
      el.addEventListener('change', () => {
        const i = parseInt(el.dataset.aidx, 10);
        const f = el.dataset.af;
        v14.builderState.actions[i][f] = el.value;
        if (f === 'actionType') {
          const isP = PLAYER_ACTIONS.has(el.value);
          v14.builderState.actions[i].targetMode = isP ? (isSelfPlayerAction(el.value) ? 'SelfPlayer' : 'OpponentPlayer') : 'Neighbor';
          if (el.value === 'TActionPlayerModifyAttribute') {
            v14.builderState.actions[i].attributeType = 'Gold';
            v14.builderState.actions[i].duration = 'Permanent';
          } else if (el.value === 'TActionCardModifyAttribute') {
            v14.builderState.actions[i].attributeType = 'DamageAmount';
            v14.builderState.actions[i].duration = 'Combat';
          }
          renderBuilderActionsList();
        } else if (f === 'attributeType' && v14.builderState.actions[i].actionType === 'TActionPlayerModifyAttribute') {
          v14.builderState.actions[i].duration = PERMANENT_PLAYER_ATTRS.has(el.value) ? 'Permanent' : 'Combat';
          renderBuilderActionsList();
        } else if (f === 'valueMode' || f === 'duration') {
          renderBuilderActionsList();
        }
        updateAffixBuilderPreview();
      });
    });
    box.querySelectorAll('[data-rm-act]').forEach((btn) => {
      btn.addEventListener('click', () => {
        v14.builderState.actions.splice(parseInt(btn.dataset.rmAct, 10), 1);
        renderBuilderActionsList();
        updateAffixBuilderPreview();
      });
    });
  }

  function renderPrereqFields(prefix = 'ab', initValues = null) {
    const sel = $(`${prefix}PrereqKind`);
    const box = $(`${prefix}PrereqFields`);
    if (!sel || !box) return;
    const pk = sel.value;
    if (pk === 'none') {
      box.classList.add('hidden');
      box.innerHTML = '';
      return;
    }
    box.classList.remove('hidden');
    if (pk === 'preserve') {
      const cnt = (prefix === 'ab' ? v14.builderState.preservedPrereqs : v14.builderState.auraPreservedPrereqs)?.length || 1;
      box.innerHTML = `<div class="muted span-2">✅ 已无损保留原有 ${cnt} 条复杂前置条件（切换上方下拉菜单可覆盖修改）。</div>`;
      return;
    }
    if (pk === 'cardCount') {
      const tags = catalog().tags || [];
      box.innerHTML = `
        <label class="field"><span>要求场上标签</span><select id="${prefix}PrereqTag">${tags.map((t) => `<option value="${escapeHtml(t.id)}">${escapeHtml(t.nameZh)}</option>`).join('')}</select></label>
        <label class="field"><span>最少数量 ≥</span><input type="number" id="${prefix}PrereqMin" value="${escapeHtml(safeNumAttr(initValues?.min ?? 2))}" min="1" max="10" /></label>
      `;
      if (initValues?.tag && $(`${prefix}PrereqTag`)) $(`${prefix}PrereqTag`).value = initValues.tag;
    } else if (pk === 'playerAttr') {
      const pAttrs = catalog().mechanicSchema?.playerAttributes || [
        { id: 'Gold', nameZh: '金币 Gold' }, { id: 'Health', nameZh: '当前生命 Health' },
        { id: 'Shield', nameZh: '当前护盾 Shield' }, { id: 'Income', nameZh: '收入 Income' },
      ];
      box.innerHTML = `
        <label class="field"><span>玩家属性</span><select id="${prefix}PrereqPlayerAttr">${pAttrs.map((a) => `<option value="${escapeHtml(a.id)}">${escapeHtml(a.nameZh)}</option>`).join('')}</select></label>
        <label class="field"><span>比较条件与数值</span><div class="inline-pair"><select id="${prefix}PrereqPlayerOp"><option value="GreaterThanOrEqual">≥</option><option value="LessThanOrEqual">≤</option><option value="Equal">=</option></select><input type="number" id="${prefix}PrereqPlayerVal" value="${escapeHtml(safeNumAttr(initValues?.val ?? 20))}" /></div></label>
      `;
      if (initValues?.attr && $(`${prefix}PrereqPlayerAttr`)) $(`${prefix}PrereqPlayerAttr`).value = initValues.attr;
      if (initValues?.op && $(`${prefix}PrereqPlayerOp`)) $(`${prefix}PrereqPlayerOp`).value = initValues.op;
    } else if (pk === 'run') {
      box.innerHTML = `
        <label class="field"><span>局内天数比较</span><select id="${prefix}PrereqRunOp"><option value="GreaterThanOrEqual">≥ 第 N 天</option><option value="LessThanOrEqual">≤ 第 N 天</option><option value="Equal">= 第 N 天</option></select></label>
        <label class="field"><span>天数 (currentDay)</span><input type="number" id="${prefix}PrereqRunDay" value="${escapeHtml(safeNumAttr(initValues?.day ?? 5))}" min="1" max="99" /></label>
      `;
      if (initValues?.op && $(`${prefix}PrereqRunOp`)) $(`${prefix}PrereqRunOp`).value = initValues.op;
    } else if (pk === 'cardAttrCmp') {
      const attrs = catalog().attributes || [];
      const opts = attrs.map((a) => `<option value="${escapeHtml(a.id)}">${escapeHtml(a.nameZh)}</option>`).join('');
      box.innerHTML = `
        <label class="field"><span>属性 A</span><select id="${prefix}PrereqCmpAttr1">${opts}</select></label>
        <label class="field"><span>比较符与属性 B</span><div class="inline-pair"><select id="${prefix}PrereqCmpOp"><option value="GreaterThanOrEqual">≥</option><option value="GreaterThan">&gt;</option><option value="LessThanOrEqual">≤</option><option value="Equal">=</option></select><select id="${prefix}PrereqCmpAttr2">${opts}</select></div></label>
      `;
      if (initValues?.attr1 && $(`${prefix}PrereqCmpAttr1`)) $(`${prefix}PrereqCmpAttr1`).value = initValues.attr1;
      if (initValues?.op && $(`${prefix}PrereqCmpOp`)) $(`${prefix}PrereqCmpOp`).value = initValues.op;
      if (initValues?.attr2 && $(`${prefix}PrereqCmpAttr2`)) $(`${prefix}PrereqCmpAttr2`).value = initValues.attr2;
    }
    box.querySelectorAll('input,select').forEach((el) => el.addEventListener('change', updateAffixBuilderPreview));
  }

  function restorePrereqToControls(prefix, prereqs) {
    const sel = $(`${prefix}PrereqKind`);
    if (!sel) return null;
    const preserveOpt = sel.querySelector('option[value="preserve"]');
    if (!Array.isArray(prereqs) || prereqs.length === 0) {
      if (preserveOpt) preserveOpt.hidden = true;
      sel.value = 'none';
      renderPrereqFields(prefix);
      return null;
    }
    if (prereqs.length === 1) {
      const p = prereqs[0];
      if (p?.$type === 'TPrerequisiteCardCount') {
        if (preserveOpt) preserveOpt.hidden = true;
        const tChoice = parseRuleNodeToTargetChoice(p.subject);
        sel.value = 'cardCount';
        renderPrereqFields(prefix, { tag: tChoice.targetTag || 'Weapon', min: p.amount ?? 2 });
        return null;
      }
      if (p?.$type === 'TPrerequisitePlayer') {
        if (preserveOpt) preserveOpt.hidden = true;
        const cond = p.subject?.conditions;
        const attr = cond?.attribute || cond?.attributeType || 'Gold';
        const op = cond?.comparisonOperator || 'GreaterThanOrEqual';
        const cv = typeof cond?.comparisonValue === 'number' ? cond.comparisonValue : (cond?.comparisonValue?.value ?? 20);
        sel.value = 'playerAttr';
        renderPrereqFields(prefix, { attr, op, val: cv });
        return null;
      }
      if (p?.$type === 'TPrerequisiteRun') {
        if (preserveOpt) preserveOpt.hidden = true;
        sel.value = 'run';
        renderPrereqFields(prefix, { day: p.currentDay ?? 5, op: p.comparisonOperator || 'GreaterThanOrEqual' });
        return null;
      }
      if (p?.$type === 'TPrerequisiteCardAttributeComparator') {
        if (preserveOpt) preserveOpt.hidden = true;
        sel.value = 'cardAttrCmp';
        renderPrereqFields(prefix, {
          attr1: p.attribute || 'DamageAmount',
          op: p.comparisonOperator || 'GreaterThanOrEqual',
          attr2: p.attributeOther || 'ShieldApplyAmount',
        });
        return null;
      }
    }
    // 多条或高级前置条件：无损保留，绝不重置为 none
    const cloned = JSON.parse(JSON.stringify(prereqs));
    if (prefix === 'ab') v14.builderState.preservedPrereqs = cloned;
    else v14.builderState.auraPreservedPrereqs = cloned;
    if (preserveOpt) preserveOpt.hidden = false;
    sel.value = 'preserve';
    renderPrereqFields(prefix);
    return cloned;
  }

  function extractExtraFields(existing, handledKeys) {
    if (!existing || typeof existing !== 'object') return null;
    const extra = {};
    Object.keys(existing).forEach((k) => {
      if (!handledKeys.has(k)) extra[k] = JSON.parse(JSON.stringify(existing[k]));
    });
    return Object.keys(extra).length ? extra : null;
  }

  function openAffixBuilder(mode, editKey = null) {
    const hostInfo = getActiveTargetHost();
    if (!hostInfo) {
      B().toast('请先在左侧选择或创建一张卡牌/补丁！', 'err');
      return;
    }
    populateBuilderSelects();
    v14.builderState.mode = mode;
    v14.builderState.targetHost = hostInfo.host;
    v14.builderState.editKey = editKey;
    v14.builderState.preservedExtra = null;
    v14.builderState.preservedPrereqs = null;
    v14.builderState.auraPreservedExtra = null;
    v14.builderState.auraPreservedPrereqs = null;

    $('abAbilityModePane').classList.toggle('hidden', mode !== 'ability');
    $('abAuraModePane').classList.toggle('hidden', mode !== 'aura');
    $('abModePill').textContent = mode === 'aura' ? '常驻光环' : '自由拼装效果';
    $('abModalTitle').textContent = editKey !== null
      ? `修改${mode === 'aura' ? '常驻光环' : '战斗效果'} #${editKey}`
      : (mode === 'aura' ? '添加常驻光环' : '自由拼装战斗效果（像造句一样组合）');
    $('abSyncTooltipChk').checked = (editKey === null);

    if (mode === 'ability') {
      const existing = editKey !== null ? getAbilitiesMap(hostInfo)[editKey] : null;
      $('abTriggerSearch').value = '';
      if (existing) {
        v14.builderState.preservedExtra = extractExtraFields(existing, new Set(['id', 'trigger', 'activeIn', 'action', 'prerequisites']));
        const trig = existing.trigger || {};
        if (trig.$type === 'TTriggerOr' && Array.isArray(trig.triggers) && trig.triggers.length >= 2) {
          $('abUseOrTrigger').checked = true;
          $('abOrTriggerWrap').classList.remove('hidden');
          $('abTriggerType').value = trig.triggers[0].$type || 'TTriggerOnCardFired';
          $('abTriggerType2').value = trig.triggers[1].$type || 'TTriggerOnItemUsed';
        } else {
          $('abUseOrTrigger').checked = false;
          $('abOrTriggerWrap').classList.add('hidden');
          $('abTriggerType').value = trig.$type || 'TTriggerOnCardFired';
        }
        const rawSubj = trig.subject || trig.triggers?.[0]?.subject;
        const subj = parseRuleNodeToTargetChoice(rawSubj);
        const revMap = { SelfCard: 'Self', Neighbor: 'Neighbor', Left: 'Left', Right: 'Right', AllAllies: 'SelfHand', AllEnemies: 'EnemyHand' };
        if (rawSubj?.targetSection === 'AllHands') {
          $('abTriggerSubjectMode').value = 'AnyHand';
        } else {
          $('abTriggerSubjectMode').value = rawSubj ? (revMap[subj.targetMode] || 'Self') : 'None';
        }
        $('abTriggerSubjectTag').value = subj.targetTag || '';
        $('abTriggerSubjectHiddenTag').value = subj.targetHiddenTag || '';
        $('abActiveIn').value = existing.activeIn || 'HandOnly';
        restorePrereqToControls('ab', existing.prerequisites);
        v14.builderState.actions = parseActionNodeToRows(existing.action);
        const existingVfx = readVfxOverrideKey(existing.vfxConfig);
        if (existingVfx && v14.builderState.actions[0]) {
          v14.builderState.actions[0].vfxKey = existingVfx;
        }
      } else {
        $('abUseOrTrigger').checked = false;
        $('abOrTriggerWrap').classList.add('hidden');
        $('abTriggerType').value = 'TTriggerOnItemUsed';
        $('abTriggerSubjectMode').value = 'Neighbor';
        $('abTriggerSubjectTag').value = '';
        $('abTriggerSubjectHiddenTag').value = '';
        $('abActiveIn').value = 'HandOnly';
        restorePrereqToControls('ab', null);
        v14.builderState.actions = [makeDefaultActionRow('TActionCardCharge')];
        v14.builderState.actions[0].targetMode = 'SelfCard';
      }
      renderBuilderActionsList();
    } else {
      const existing = editKey !== null ? getAurasMap(hostInfo)[editKey] : null;
      if (existing?.action) {
        v14.builderState.auraPreservedExtra = extractExtraFields(existing, new Set(['id', 'activeIn', 'action', 'prerequisites']));
        $('abAuraActionType').value = existing.action.$type || 'TAuraActionCardModifyAttribute';
        const auraTgt = parseRuleNodeToTargetChoice(existing.action.target);
        const revAuraTgt = {
          SelfCard: 'Self', Neighbor: 'Neighbor', Left: 'Left', Right: 'Right',
          AllAllies: 'SelfHand', AllEnemies: 'EnemyHand', SelfPlayer: 'PlayerSelf',
        };
        $('abAuraTargetMode').value = revAuraTgt[auraTgt.targetMode] || 'Self';
        $('abAuraTargetTag').value = auraTgt.targetTag || '';
        $('abAuraOp').value = existing.action.operation || 'Add';
        if (existing.action.$type === 'TAuraActionCardAddTagsList' && existing.action.tags?.[0]) {
          $('abAuraAttr').value = `TAG:${existing.action.tags[0]}`;
        } else if (existing.action.attributeType) {
          $('abAuraAttr').value = existing.action.attributeType;
        }
        if (existing.action.value?.$type === 'TReferenceValueCardAttribute') {
          $('abAuraValMode').value = 'cardAttr';
          $('abAuraValInput').value = existing.action.value.attributeType || 'Custom_0';
        } else {
          $('abAuraValMode').value = 'fixed';
          $('abAuraValInput').value = String(existing.action.value?.value ?? 10);
        }
        if ($('abAuraActiveIn')) $('abAuraActiveIn').value = existing.activeIn || 'HandOnly';
        if ($('abAuraVfxKey')) $('abAuraVfxKey').innerHTML = renderVfxOptionsHtml(readVfxOverrideKey(existing.vfxConfig));
        restorePrereqToControls('abAura', existing.prerequisites);
      } else {
        $('abAuraActionType').value = 'TAuraActionCardModifyAttribute';
        $('abAuraTargetMode').value = 'Self';
        $('abAuraTargetTag').value = '';
        $('abAuraOp').value = 'Add';
        $('abAuraValMode').value = 'fixed';
        $('abAuraValInput').value = '10';
        if ($('abAuraActiveIn')) $('abAuraActiveIn').value = 'HandOnly';
        if ($('abAuraVfxKey')) $('abAuraVfxKey').innerHTML = renderVfxOptionsHtml('');
        restorePrereqToControls('abAura', null);
      }
    }

    updateAffixBuilderPreview();
    $('affixBuilderModal').classList.remove('hidden');
  }

  function confirmAffixBuilder() {
    const hostInfo = getActiveTargetHost();
    if (!hostInfo) return;
    let built;
    try {
      built = JSON.parse($('abGeneratedJson').value);
    } catch (e) {
      B().toast(`规则 JSON 语法有误：${e.message}`, 'err');
      return;
    }

    const mode = v14.builderState.mode;
    const map = mode === 'aura' ? getAurasMap(hostInfo, true) : getAbilitiesMap(hostInfo, true);
    const key = v14.builderState.editKey !== null ? v14.builderState.editKey : nextMapKey(map);
    built.id = key;
    map[key] = built;

    // 自动补全本卡缺失的属性键（防 Gate #7 渲染出 "?" 或无 ChargeAmount/HasteAmount）
    if (hostInfo.host === 'card' && mode === 'ability') {
      ensureDefaultTierAttrsForAction(hostInfo.obj, built.action);
    }

    if ($('abSyncTooltipChk').checked) {
      const zhLine = $('abChinesePreview').textContent.trim();
      if (zhLine) {
        if (hostInfo.host === 'card') {
          const isActive = mode === 'ability' && built.trigger?.$type === 'TTriggerOnCardFired';
          const list = isActive ? (hostInfo.obj.activeTooltipsZh = hostInfo.obj.activeTooltipsZh || []) : (hostInfo.obj.passiveTooltipsZh = hostInfo.obj.passiveTooltipsZh || []);
          if (!list.includes(zhLine)) list.push(zhLine);
        } else {
          hostInfo.obj.passiveTooltipsZh = hostInfo.obj.passiveTooltipsZh || [];
          if (!hostInfo.obj.passiveTooltipsZh.includes(zhLine)) hostInfo.obj.passiveTooltipsZh.push(zhLine);
        }
      }
    }

    B().markDirty(true);
    $('affixBuilderModal').classList.add('hidden');
    refreshActiveHostEditor();
    B().toast(`已写入${mode === 'aura' ? '光环' : '词缀'} #${key}！`);
  }

  function ensureDefaultTierAttrsForAction(card, actionNode) {
    if (!actionNode || !card.tiers) return;
    if (actionNode.$type === 'TActionAnd' && Array.isArray(actionNode.actions)) {
      actionNode.actions.forEach((a) => ensureDefaultTierAttrsForAction(card, a));
      return;
    }
    const needAttr = DEFAULT_ATTR_FOR_ACTION[actionNode.$type];
    if (needAttr && !actionNode.value) {
      const isDur = needAttr.endsWith('Amount') && (needAttr.startsWith('Haste') || needAttr.startsWith('Slow') || needAttr.startsWith('Freeze') || needAttr.startsWith('Charge'));
      const defaults = isDur ? [1000, 1500, 2000, 3000, 3500] : [10, 20, 40, 80, 120];
      (B().TIERS || ['Bronze', 'Silver', 'Gold', 'Diamond', 'Legendary']).forEach((t, i) => {
        if (card.tiers[t]?.attributes && card.tiers[t].attributes[needAttr] === undefined) {
          card.tiers[t].attributes[needAttr] = defaults[Math.min(i, defaults.length - 1)];
        }
      });
    }
  }

  // ── 原位魔改卡 Patch 的能力/光环摘要渲染 ────────────────────────────────────
  function renderPatchAbilitiesSummary() {
    const info = getActiveTargetHost();
    const box = $('peAbilitiesSummaryList');
    if (!box || !info || info.host !== 'patch') return;
    const patch = info.obj;
    const items = [];
    Object.entries(patch.abilitiesOverride || {}).forEach(([k, ab]) => items.push({ kind: 'ability', key: k, def: ab }));
    Object.entries(patch.aurasOverride || {}).forEach(([k, au]) => items.push({ kind: 'aura', key: k, def: au }));
    if (!items.length) {
      box.innerHTML = '<div class="muted">当前未覆写能力与光环（完整继承原版卡机制）。可点上方「📥 载入该卡原版词缀」或「✨ + 自由拼装词缀」自由魔改。</div>';
      return;
    }
    box.innerHTML = items.map((it) => `
      <div class="ability-item">
        <div class="ab-summary-main">
          <span class="badge ${it.kind === 'aura' ? 'badge-blue' : 'badge-green'}">${it.kind === 'aura' ? '光环覆写' : '能力覆写'} #${escapeHtml(it.key)}</span>
          <span class="ab-zh-text">${escapeHtml(summarizeAffixZh(it.kind, it.def))}</span>
        </div>
        <div class="btn-row">
          <button class="btn btn-outline btn-xs" data-pe-edit="${it.kind}" data-pe-key="${escapeHtml(it.key)}">✏️ 编辑</button>
          <button class="btn btn-danger btn-xs" data-pe-del="${it.kind}" data-pe-key="${escapeHtml(it.key)}">移除</button>
        </div>
      </div>
    `).join('');

    box.querySelectorAll('[data-pe-edit]').forEach((btn) => {
      btn.addEventListener('click', () => openAffixBuilder(btn.dataset.peEdit, btn.dataset.peKey));
    });
    box.querySelectorAll('[data-pe-del]').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (btn.dataset.peDel === 'ability') delete patch.abilitiesOverride[btn.dataset.peKey];
        else delete patch.aurasOverride[btn.dataset.peKey];
        B().markDirty(true);
        renderPatchAbilitiesSummary();
      });
    });
  }

  // ── 2. 原版 2,034+ 卡牌词缀一键「扒取」器 ───────────────────────────────────
  async function openVanillaAffixExtractor(templateId) {
    const hostInfo = getActiveTargetHost();
    if (!hostInfo) {
      B().toast('请先在「本 Mod 卡牌 & 补丁」选中或创建一张目标卡，再从原版库扒取词缀！', 'err');
      return;
    }
    const res = await B().apiFetch(`/api/workshop/base-cards/${encodeURIComponent(templateId)}`);
    if (!res.ok || !res.data?.card) {
      B().toast(`读取原版卡词缀失败：${res.data?.error || res.status}`, 'err');
      return;
    }
    v14.currentVanillaBlueprint = res.data;
    const card = res.data.card;
    $('vaModalTitle').textContent = `扒取原版词缀：${res.data.titleZh} (${res.data.titleEn})`;
    const targetName = hostInfo.host === 'card' ? (hostInfo.obj.titleZh || hostInfo.obj.customId) : `补丁 ${hostInfo.obj.targetTemplateId}`;
    $('vaModalSub').textContent = `当前写入目标：「${targetName}」。点击任意词缀即可无损移植（连同依赖的默认属性一并补齐），移植后可立即在句式拼装器里二次改造！`;

    const entries = [];
    Object.entries(card.abilities || {}).forEach(([k, ab]) => entries.push({ kind: 'ability', key: k, def: ab }));
    Object.entries(card.auras || {}).forEach(([k, au]) => entries.push({ kind: 'aura', key: k, def: au }));

    const list = $('vaAffixList');
    if (!entries.length) {
      list.innerHTML = '<div class="empty-hint">该原版卡没有可提取的战斗词缀或光环。</div>';
    } else {
      list.innerHTML = entries.map((e, idx) => `
        <div class="va-affix-card" style="margin-bottom:8px;">
          <div class="action-chain-head">
            <span><span class="badge ${e.kind === 'aura' ? 'badge-blue' : 'badge-green'}">${e.kind === 'aura' ? '常驻光环' : '战斗效果'} #${escapeHtml(e.key)}</span>
              <strong>${escapeHtml(summarizeAffixZh(e.kind, e.def))}</strong></span>
            <button class="btn btn-gold btn-xs" data-extract-idx="${idx}">🧲 复制到我的卡</button>
          </div>
          <details class="advanced-json" style="margin-top:4px;">
            <summary>查看底层配置详情</summary>
            <div class="bcr-sub"><code>${escapeHtml(JSON.stringify(e.def))}</code></div>
          </details>
        </div>
      `).join('');

      list.querySelectorAll('[data-extract-idx]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const picked = entries[parseInt(btn.dataset.extractIdx, 10)];
          const cloned = JSON.parse(JSON.stringify(picked.def));
          const map = picked.kind === 'aura' ? getAurasMap(hostInfo, true) : getAbilitiesMap(hostInfo, true);
          const newKey = nextMapKey(map);
          cloned.id = newKey;
          map[newKey] = cloned;

          // 如果目标为新卡，顺手把原版卡上对应的默认数值（如 ChargeAmount/HasteAmount/Custom_0）补进缺失项（覆盖全部 5 阶）
          if (hostInfo.host === 'card' && card.tiers && hostInfo.obj.tiers) {
            (B().TIERS || ['Bronze', 'Silver', 'Gold', 'Diamond', 'Legendary']).forEach((t) => {
              const srcAttrs = card.tiers[t]?.attributes;
              const dstAttrs = hostInfo.obj.tiers[t]?.attributes;
              if (!srcAttrs || !dstAttrs) return;
              Object.entries(srcAttrs).forEach(([ak, av]) => {
                if (ak !== 'BuyPrice' && ak !== 'SellPrice' && ak !== 'CooldownMax' && dstAttrs[ak] === undefined) {
                  if (JSON.stringify(cloned).includes(ak) || DEFAULT_ATTR_FOR_ACTION[cloned.action?.$type] === ak) {
                    dstAttrs[ak] = av;
                  }
                }
              });
            });
          }

          B().markDirty(true);
          refreshActiveHostEditor();
          B().toast(`已从「${res.data.titleZh}」扒取${picked.kind === 'aura' ? '光环' : '词缀'}并写入 #${newKey}！`);
        });
      });
    }

    $('vanillaAffixModal').classList.remove('hidden');
  }

  // ── 3. 卡牌自定义机制挂载 & 自动渲染参数表单 ─────────────────────────────────
  function getAllAvailableMechanics() {
    const map = new Map();
    (v14.serverMechanics || []).forEach((item) => {
      if (item.mechanic?.id) map.set(item.mechanic.id, { source: `全局:${item.modId}`, mechanic: item.mechanic });
    });
    ((B().state.workspace?.mechanics) || []).forEach((m) => {
      if (m?.id) map.set(m.id, { source: '本 Mod', mechanic: m });
    });
    return Array.from(map.values());
  }

  function renderMechanicTooltipJs(mech, params) {
    let tpl = mech.tooltipTemplateZh || mech.descZh || mech.nameZh || mech.id;
    (mech.parameters || []).forEach((p) => {
      const val = params && params[p.key] !== undefined ? params[p.key] : p.defaultValue;
      const str = val === true ? '是' : (val === false ? '否' : String(val ?? 0));
      tpl = tpl.replaceAll(`{${p.key}}`, str);
    });
    return tpl;
  }

  function renderCardAttachedMechanics() {
    const info = getActiveTargetHost();
    if (!info) return;
    const avail = getAllAvailableMechanics();
    const selId = info.host === 'card' ? 'ceMechSelect' : 'peMechSelect';
    const listId = info.host === 'card' ? 'ceAttachedMechanicsList' : 'peAttachedMechanicsList';
    const sel = $(selId);
    const box = $(listId);
    if (!sel || !box) return;

    sel.innerHTML = avail.length
      ? avail.map((x) => `<option value="${escapeHtml(x.mechanic.id)}">[${escapeHtml(x.source)}] ${escapeHtml(x.mechanic.nameZh || x.mechanic.id)}</option>`).join('')
      : '<option value="">（尚无自定义机制，请先在 Tab 3 机制工坊创建）</option>';

    const attached = getMechanicsList(info, false);
    if (!attached.length) {
      box.innerHTML = '<div class="muted">当前卡牌未挂载开放自定义机制。你可在 Tab 3「开放机制工坊」定义任意全新机制并在此处像积木一样挂载填参。</div>';
      return;
    }

    const cat = catalog();
    const mSchema = cat.mechanicSchema || {};

    box.innerHTML = attached.map((att, idx) => {
      const found = avail.find((x) => x.mechanic.id === att.mechanicId)?.mechanic;
      if (!found) {
        return `<div class="mech-attach-card"><div class="mech-attach-head"><strong style="color:#f87171;">⚠️ 未找到机制：${escapeHtml(att.mechanicId)}</strong><button class="btn btn-danger btn-xs" data-detach-mech="${idx}">卸载</button></div></div>`;
      }
      const paramInputs = (found.parameters || []).map((p) => {
        const cur = att.params && att.params[p.key] !== undefined ? att.params[p.key] : (p.defaultValue ?? '');
        let ctrl = '';
        if (p.type === 'bool') {
          ctrl = `<select data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="bool"><option value="true" ${cur === true || cur === 'true' ? 'selected' : ''}>开启（是）</option><option value="false" ${!(cur === true || cur === 'true') ? 'selected' : ''}>关闭（否）</option></select>`;
        } else if (p.type === 'tag') {
          ctrl = `<select data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="string"><option value="Any">不限标签</option>${(cat.tags || []).map((t) => `<option value="${escapeHtml(t.id)}" ${cur === t.id ? 'selected' : ''}>${escapeHtml(t.nameZh)}</option>`).join('')}</select>`;
        } else if (p.type === 'attribute') {
          ctrl = `<select data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="string">${(cat.attributes || []).map((a) => `<option value="${escapeHtml(a.id)}" ${cur === a.id ? 'selected' : ''}>${escapeHtml(a.nameZh)}</option>`).join('')}</select>`;
        } else if (p.type === 'targetMode') {
          ctrl = `<select data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="string">${(mSchema.targetModes || []).map((tm) => `<option value="${escapeHtml(tm.id)}" ${cur === tm.id ? 'selected' : ''}>${escapeHtml(tm.nameZh)}</option>`).join('')}</select>`;
        } else if (p.type === 'vfxKey') {
          ctrl = `<select data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="string">${renderVfxOptionsHtml(cur)}</select>`;
        } else if (p.type === 'int' || p.type === 'float') {
          const minAttr = safeNumAttr(p.min);
          const maxAttr = safeNumAttr(p.max);
          ctrl = `<input type="number" step="${p.type === 'float' ? '0.1' : '1'}" ${minAttr !== '' ? `min="${escapeHtml(minAttr)}"` : ''} ${maxAttr !== '' ? `max="${escapeHtml(maxAttr)}"` : ''} value="${escapeHtml(safeNumAttr(cur))}" data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="${p.type}" />`;
        } else {
          ctrl = `<input type="text" value="${escapeHtml(cur)}" data-mp-idx="${idx}" data-mp-key="${escapeHtml(p.key)}" data-mp-type="string" />`;
        }
        return `<label class="field"><span>${escapeHtml(p.labelZh || p.key)}</span>${ctrl}</label>`;
      }).join('');

      const tipPreview = renderMechanicTooltipJs(found, att.params);
      return `
        <div class="mech-attach-card">
          <div class="mech-attach-head">
            <div><span class="badge badge-green">开放机制</span> <strong>${escapeHtml(found.nameZh)}</strong> <small class="muted">(${escapeHtml(found.id)})</small></div>
            <button class="btn btn-danger btn-xs" data-detach-mech="${idx}">卸载</button>
          </div>
          ${paramInputs ? `<div class="mech-param-grid">${paramInputs}</div>` : '<div class="muted">此机制无额外参数</div>'}
          <div class="mech-tooltip-preview">预览效果：${escapeHtml(tipPreview)}</div>
        </div>
      `;
    }).join('');

    box.querySelectorAll('[data-mp-key]').forEach((el) => {
      el.addEventListener('change', () => {
        const i = parseInt(el.dataset.mpIdx, 10);
        const k = el.dataset.mpKey;
        const tp = el.dataset.mpType;
        attached[i].params = attached[i].params || {};
        if (tp === 'int') attached[i].params[k] = parseInt(el.value, 10) || 0;
        else if (tp === 'float') attached[i].params[k] = parseFloat(el.value) || 0;
        else if (tp === 'bool') attached[i].params[k] = (el.value === 'true');
        else attached[i].params[k] = el.value;
        B().markDirty(true);
        renderCardAttachedMechanics();
      });
    });

    box.querySelectorAll('[data-detach-mech]').forEach((btn) => {
      btn.addEventListener('click', () => {
        attached.splice(parseInt(btn.dataset.detachMech, 10), 1);
        B().markDirty(true);
        renderCardAttachedMechanics();
      });
    });
  }

  function attachSelectedMechanicToActiveHost() {
    const info = getActiveTargetHost();
    if (!info) return;
    const selId = info.host === 'card' ? 'ceMechSelect' : 'peMechSelect';
    const mechId = $(selId)?.value;
    if (!mechId) {
      B().toast('请先在 Tab 3「开放机制工坊」创建至少一个自定义机制！', 'err');
      return;
    }
    const found = getAllAvailableMechanics().find((x) => x.mechanic.id === mechId)?.mechanic;
    const params = {};
    (found?.parameters || []).forEach((p) => {
      if (p.defaultValue !== undefined) params[p.key] = p.defaultValue;
    });
    const list = getMechanicsList(info, true);
    list.push({ mechanicId: mechId, enabled: true, params });
    B().markDirty(true);
    renderCardAttachedMechanics();
    B().toast(`已挂载自定义机制「${found?.nameZh || mechId}」！`);
  }

  // ── 4. Tab 3：「开放机制工坊（Mechanic Lab）」 ───────────────────────────────
  async function loadServerMechanics() {
    const res = await B().apiFetch('/api/workshop/mechanics');
    if (res.ok && res.data?.mechanics) {
      v14.serverMechanics = res.data.mechanics;
    }
    renderMechanicLists();
    renderCardAttachedMechanics();
  }

  function makeScaffoldMechanic(kind) {
    const modId = B().state.workspace?.manifest?.id || 'my-mod';
    const seq = ((B().state.workspace?.mechanics) || []).length + 1;
    if (kind === 'before_intercept') {
      return {
        id: `${modId}:intercept_mechanic_${seq}`,
        nameZh: `事前管线拦截机制 #${seq}`,
        categoryZh: '管线拦截',
        descZh: '演示如何使用 OnFightStart 黑板计数 + BeforeDamage 拦截取消 + BeforeCardStateChange 状态保护。可自由修改全部步骤与参数。',
        tooltipTemplateZh: '拦截 {count} 次触发，并在耗尽后执行自定义状态变更',
        parameters: [
          { key: 'count', labelZh: '拦截次数', type: 'int', defaultValue: 2, min: 1, max: 20 },
          { key: 'blockRepair', labelZh: '耗尽后禁止修复', type: 'bool', defaultValue: true },
        ],
        hooks: [
          {
            id: 'init', hook: 'OnFightStart', scope: 'SelfCard',
            steps: [{ op: 'ModifyCounter', target: 'Self', key: 'charges', operation: 'Set', value: { param: 'count' }, syncAttribute: 'Custom_0' }],
          },
          {
            id: 'intercept_dmg', hook: 'BeforeDamage', scope: 'OwnerPlayer', filter: { onlyWhenOwnerTarget: true, damageTypes: ['Direct'] },
            steps: [{
              op: 'If',
              condition: { left: { counter: 'charges', source: 'Self' }, compare: 'Gt', right: { const: 0 } },
              then: [
                { op: 'CancelHook' },
                { op: 'ModifyCounter', target: 'Self', key: 'charges', operation: 'Subtract', value: { const: 1 }, syncAttribute: 'Custom_0' },
                {
                  op: 'If',
                  condition: { left: { counter: 'charges', source: 'Self' }, compare: 'Lte', right: { const: 0 } },
                  then: [
                    { op: 'SetFlag', target: 'Self', flag: 'locked_state' },
                    { op: 'ChangeCardState', targetSpec: { mode: 'Self' }, changeKind: 'Disable', reason: 'charges_exhausted', suppressNormalDestroyTriggers: true },
                  ],
                },
              ],
            }],
          },
          {
            id: 'guard_repair', hook: 'BeforeCardStateChange', scope: 'SelfCard',
            filter: { changeKinds: ['Repair'], onlyWhenSelfTarget: true, includeDisabledSelf: true },
            steps: [{
              op: 'If',
              condition: { all: [{ paramBool: 'blockRepair' }, { hasFlag: 'locked_state', flagTarget: 'Self' }] },
              then: [{ op: 'CancelHook' }],
            }],
          },
        ],
      };
    }
    if (kind === 'delayed_chain') {
      return {
        id: `${modId}:chain_mechanic_${seq}`,
        nameZh: `多段连携与自定义事件机制 #${seq}`,
        categoryZh: '事件联动',
        descZh: '演示如何在开火时发出受控多段 Repeat 动作、广播自定义事件并调整友军属性。',
        tooltipTemplateZh: '触发时连续执行 {hits} 段效果，并增益友军',
        parameters: [
          { key: 'hits', labelZh: '连击段数', type: 'int', defaultValue: 3, min: 1, max: 10 },
          { key: 'dmgPerHit', labelZh: '每段伤害', type: 'int', defaultValue: 15, min: 1, max: 500 },
          { key: 'allyBuff', labelZh: '友军伤害加成', type: 'int', defaultValue: 10, min: 0, max: 200 },
        ],
        hooks: [
          {
            id: 'on_fire', hook: 'OnCardFired', scope: 'SelfCard', filter: { onlyWhenSelfSource: true }, maxTriggersPerCombat: 1,
            steps: [
              { op: 'ChangeCardState', targetSpec: { mode: 'Self' }, changeKind: 'Disable', reason: 'custom_burst', suppressNormalDestroyTriggers: true },
              { op: 'EmitCustomEvent', eventName: 'custom_burst_fired', payloadValue: { param: 'dmgPerHit' } },
            ],
          },
          {
            id: 'on_burst', hook: 'OnCustomEvent', scope: 'SelfCard', filter: { customEventName: 'custom_burst_fired', includeDisabledSelf: true },
            steps: [
              { op: 'Repeat', count: { param: 'hits' }, intervalMs: { const: 150 }, steps: [{ op: 'DealDamage', playerTarget: 'Opponent', amount: { param: 'dmgPerHit' } }] },
              { op: 'ModifyCardAttribute', targetSpec: { mode: 'OtherAllies' }, attribute: 'DamageAmount', operation: 'Add', value: { param: 'allyBuff' }, duration: 'Combat' },
            ],
          },
        ],
      };
    }
    if (kind === 'post_combat') {
      return {
        id: `${modId}:post_combat_mechanic_${seq}`,
        nameZh: `战后结转机制 #${seq}`,
        categoryZh: '局外结转',
        descZh: '演示如何在战斗中标记条件并在 PostCombatCommit 阶段安全向局外棋盘提交结转。',
        tooltipTemplateZh: '战内标记后，战后安全结转局外变化',
        parameters: [{ key: 'bonusAttr', labelZh: '永久成长值', type: 'int', defaultValue: 5, min: 1, max: 100 }],
        hooks: [
          { id: 'mark', hook: 'OnCardFired', scope: 'SelfCard', filter: { onlyWhenSelfSource: true }, steps: [{ op: 'SetFlag', target: 'Self', flag: 'triggered_once' }] },
          {
            id: 'commit', hook: 'PostCombatCommit', scope: 'SelfCard', filter: { requireCardFlags: ['triggered_once'], includeDisabledSelf: true },
            steps: [{ op: 'QueuePostCombatModifyAttr', targetSpec: { mode: 'Self', includeDisabled: true }, attribute: 'DamageAmount', operation: 'Add', value: { param: 'bonusAttr' } }],
          },
        ],
      };
    }
    return {
      id: `${modId}:custom_mechanic_${seq}`,
      nameZh: `自定义新机制 #${seq}`,
      categoryZh: '自定义机制',
      descZh: '空白沙盒机制模板，可自由增删管线钩子与安全步骤流。',
      tooltipTemplateZh: '触发自定义机制（数值 {power}）',
      parameters: [{ key: 'power', labelZh: '效果数值', type: 'int', defaultValue: 10, min: 1, max: 500 }],
      hooks: [
        {
          id: 'h0', hook: 'OnCardFired', scope: 'SelfCard', filter: { onlyWhenSelfSource: true },
          steps: [{ op: 'DealDamage', playerTarget: 'Opponent', amount: { param: 'power' } }],
        },
      ],
    };
  }

  function renderMechanicLists() {
    const ws = B().state.workspace;
    const localMechs = ws?.mechanics || [];
    if ($('mechLocalCount')) $('mechLocalCount').textContent = localMechs.length;
    if ($('cntMechanics')) $('cntMechanics').textContent = localMechs.length;

    const localBox = $('mechListContainer');
    if (localBox) {
      localBox.innerHTML = localMechs.length
        ? localMechs.map((m, i) => `
            <div class="mine-row ${i === v14.selectedMechIdx ? 'active' : ''}" data-midx="${i}">
              <strong>${escapeHtml(m.nameZh || m.id)}</strong>
              <div class="bcr-sub">${escapeHtml(m.id)} · ${(m.hooks || []).length} 钩子 / ${(m.parameters || []).length} 参数</div>
            </div>
          `).join('')
        : '<div class="empty-hint">暂无本 Mod 自定义机制，点「+ 空白机制」或选脚手架生成。</div>';
      localBox.querySelectorAll('[data-midx]').forEach((el) => {
        el.addEventListener('click', () => {
          v14.selectedMechIdx = parseInt(el.dataset.midx, 10);
          renderMechanicLists();
          renderMechanicEditor();
        });
      });
    }

    const gBox = $('mechGlobalListContainer');
    if ($('mechGlobalCount')) $('mechGlobalCount').textContent = v14.serverMechanics.length;
    if (gBox) {
      gBox.innerHTML = v14.serverMechanics.length
        ? v14.serverMechanics.map((x) => `
            <div class="mine-row">
              <strong>${escapeHtml(x.mechanic.nameZh || x.mechanic.id)}</strong>
              <div class="bcr-sub">来自 ${escapeHtml(x.modId)} · ${escapeHtml(x.mechanic.id)}</div>
            </div>
          `).join('')
        : '<div class="empty-hint">服务器暂无全局已注册自定义机制。</div>';
    }
  }

  function renderMechanicEditor() {
    const ws = B().state.workspace;
    const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
    $('mechEditorEmpty')?.classList.toggle('hidden', !!mech);
    $('mechEditorForm')?.classList.toggle('hidden', !mech);
    if (!mech) return;

    $('meHeading').textContent = mech.nameZh || mech.id;
    $('meId').value = mech.id || '';
    $('meNameZh').value = mech.nameZh || '';
    $('meCategoryZh').value = mech.categoryZh || '自定义机制';
    $('meTooltipTpl').value = mech.tooltipTemplateZh || '';
    $('meDescZh').value = mech.descZh || '';

    renderMechanicCounterDisplayEditor(mech);
    renderMechanicParamsEditor(mech);
    renderMechanicHooksEditor(mech);
    $('meRawJson').value = JSON.stringify(mech, null, 2);
  }

  function fillCounterDisplayEnums(syncSel, maxAttrSel, styleSel, vtSel, posSel, cur) {
    const ms = catalog().mechanicSchema || {};
    const cdAttrs = ms.counterDisplayAttributes || ['Custom_0', 'Custom_1', 'Custom_2', 'Custom_3', 'Custom_4', 'Custom_5', 'Custom_6', 'Custom_7', 'Custom_8', 'Counter', 'Ammo', 'AmmoMax', 'DamageAmount', 'ShieldApplyAmount', 'BurnApplyAmount', 'PoisonApplyAmount', 'HealAmount', 'RegenApplyAmount', 'CritChance', 'Multicast', 'CooldownMax'];
    const styles = ms.counterDisplayStyles || [{ id: 'auto', nameZh: '自动 (auto)' }, { id: 'pips', nameZh: '格点 (pips)' }, { id: 'bar', nameZh: '进度条 (bar)' }, { id: 'gem', nameZh: '宝石 (gem)' }, { id: 'pill', nameZh: '胶囊 (pill)' }];
    const vts = ms.counterDisplayValueTypes || [{ id: 'count', nameZh: '计数 (count)' }, { id: 'progress', nameZh: '进度比 (progress)' }, { id: 'percent', nameZh: '百分比 (percent)' }, { id: 'seconds', nameZh: '秒数 (seconds)' }, { id: 'multiplier', nameZh: '倍率 (multiplier)' }, { id: 'delta', nameZh: '增减 (delta)' }];
    const poses = ms.counterDisplayPositions || [{ id: 'bottom', nameZh: '底部居中 (bottom)' }, { id: 'bottomRight', nameZh: '右下角 (bottomRight)' }, { id: 'topLeft', nameZh: '左上角 (topLeft)' }, { id: 'topRight', nameZh: '右上角 (topRight)' }];

    if (syncSel) syncSel.innerHTML = cdAttrs.map((a) => `<option value="${escapeHtml(a)}" ${a === (cur?.syncAttribute || 'Custom_3') ? 'selected' : ''}>${escapeHtml(a)}</option>`).join('');
    if (maxAttrSel) maxAttrSel.innerHTML = '<option value="">（无动态上限属性）</option>' + cdAttrs.map((a) => `<option value="${escapeHtml(a)}" ${a === (cur?.maxAttr || '') ? 'selected' : ''}>${escapeHtml(a)}</option>`).join('');
    if (styleSel) styleSel.innerHTML = styles.map((s) => `<option value="${escapeHtml(s.id)}" ${s.id === (cur?.style || 'auto') ? 'selected' : ''}>${escapeHtml(s.nameZh)}</option>`).join('');
    if (vtSel) vtSel.innerHTML = vts.map((v) => `<option value="${escapeHtml(v.id)}" ${v.id === (cur?.valueType || 'count') ? 'selected' : ''}>${escapeHtml(v.nameZh)}</option>`).join('');
    if (posSel) posSel.innerHTML = poses.map((p) => `<option value="${escapeHtml(p.id)}" ${p.id === (cur?.position || 'bottom') ? 'selected' : ''}>${escapeHtml(p.nameZh)}</option>`).join('');
  }

  function renderMechanicCounterDisplayEditor(mech) {
    const chk = $('meCdEnabled');
    const box = $('meCounterDisplayFields');
    if (!chk || !box) return;
    const cd = mech.counterDisplay || null;
    chk.checked = !!cd;
    box.classList.toggle('hidden', !cd);
    fillCounterDisplayEnums($('meCdSyncAttr'), $('meCdMaxAttr'), $('meCdStyle'), $('meCdValueType'), $('meCdPosition'), cd);
    if (!cd) return;
    $('meCdLabelZh').value = cd.labelZh || '';
    $('meCdMaxParam').value = cd.maxParam || '';
    $('meCdMaxConst').value = safeNumAttr(cd.maxConst);
    $('meCdColorHex').value = cd.colorHex || '#F97316';
    $('meCdUnit').value = cd.unit || '';
    $('meCdFormat').value = cd.format || '';
    $('meCdHideWhenZero').checked = !!cd.hideWhenZero;
  }

  function renderStepInlineControlsHtml(st, hi, si, mSchema) {
    const targetModes = mSchema.targetModes || [{ id: 'Self', nameZh: '自身' }, { id: 'Adjacent', nameZh: '相邻' }, { id: 'AllAllies', nameZh: '全部友军' }, { id: 'OtherAllies', nameZh: '其余友军' }, { id: 'AllEnemies', nameZh: '全部敌军' }];
    const statuses = mSchema.statuses || [{ id: 'Shield', nameZh: '护盾' }, { id: 'Burn', nameZh: '灼烧' }, { id: 'Poison', nameZh: '剧毒' }, { id: 'Heal', nameZh: '治疗' }, { id: 'Regen', nameZh: '再生' }, { id: 'Haste', nameZh: '加速' }, { id: 'Slow', nameZh: '减速' }, { id: 'Freeze', nameZh: '冻结' }, { id: 'Charge', nameZh: '充能' }];
    const syncAttrs = mSchema.syncAttributes || ['Custom_0', 'Custom_1', 'Custom_2', 'Custom_3', 'Custom_4', 'Custom_5', 'Custom_6', 'Custom_7', 'Custom_8'];
    const pAttrs = mSchema.playerAttributes || [{ id: 'HealthMax', nameZh: '生命上限' }, { id: 'PercentDamageReduction', nameZh: '减伤%' }, { id: 'Gold', nameZh: '金币' }];
    const cardAttrs = catalog().attributes || [{ id: 'DamageAmount', nameZh: '伤害' }, { id: 'ShieldApplyAmount', nameZh: '护盾' }, { id: 'PercentCooldownReduction', nameZh: '冷却缩减%' }];
    const ops = mSchema.stepOperations || [{ id: 'Add', nameZh: 'Add' }, { id: 'Subtract', nameZh: 'Subtract' }, { id: 'Set', nameZh: 'Set' }, { id: 'Multiply', nameZh: 'Multiply' }];
    const attrsData = `data-hi="${hi}" data-si="${si}"`;
    const parts = [];

    if (st.op === 'ModifyCardAttribute' || st.op === 'QueuePostCombatModifyAttr') {
      const modeVal = st.targetSpec?.mode || 'Self';
      parts.push(`<label>目标<select data-sp="targetMode" ${attrsData}>${targetModes.map((tm) => `<option value="${escapeHtml(tm.id)}" ${tm.id === modeVal ? 'selected' : ''}>${escapeHtml(tm.nameZh)}</option>`).join('')}</select></label>`);
      parts.push(`<label>属性<select data-sp="attribute" ${attrsData}>${cardAttrs.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === (st.attribute || 'DamageAmount') ? 'selected' : ''}>${escapeHtml(a.nameZh)}</option>`).join('')}</select></label>`);
      parts.push(`<label>运算<select data-sp="operation" ${attrsData}>${ops.map((o) => `<option value="${escapeHtml(o.id)}" ${o.id === (st.operation || 'Add') ? 'selected' : ''}>${escapeHtml(o.id)}</option>`).join('')}</select></label>`);
      if (st.op === 'ModifyCardAttribute') {
        parts.push(`<label>时长<select data-sp="duration" ${attrsData}><option value="Combat" ${(st.duration || 'Combat') === 'Combat' ? 'selected' : ''}>Combat</option><option value="Permanent" ${st.duration === 'Permanent' ? 'selected' : ''}>Permanent</option></select></label>`);
      }
      parts.push(`<label title="默认关闭：严防给非武器/非对应产出卡牌误挂伤害红标；仅确需赋予新输出属性时勾选"><input type="checkbox" data-sp="grantIfMissing" ${attrsData} ${st.grantIfMissing ? 'checked' : ''} /> grantIfMissing（允许无此属性时赋予）</label>`);
    } else if (st.op === 'ModifyPlayerAttribute') {
      parts.push(`<label>玩家<select data-sp="playerTarget" ${attrsData}><option value="Self" ${(st.playerTarget || 'Self') === 'Self' ? 'selected' : ''}>Self</option><option value="Opponent" ${st.playerTarget === 'Opponent' ? 'selected' : ''}>Opponent</option></select></label>`);
      parts.push(`<label>玩家属性<select data-sp="attribute" ${attrsData}>${pAttrs.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === (st.attribute || 'HealthMax') ? 'selected' : ''}>${escapeHtml(a.nameZh)}</option>`).join('')}</select></label>`);
      parts.push(`<label>运算<select data-sp="operation" ${attrsData}>${ops.map((o) => `<option value="${escapeHtml(o.id)}" ${o.id === (st.operation || 'Add') ? 'selected' : ''}>${escapeHtml(o.id)}</option>`).join('')}</select></label>`);
    } else if (st.op === 'ModifyCounter') {
      parts.push(`<label>键<input type="text" data-sp="key" ${attrsData} value="${escapeHtml(st.key || 'counter_0')}" style="width:92px;" /></label>`);
      parts.push(`<label>运算<select data-sp="operation" ${attrsData}>${ops.map((o) => `<option value="${escapeHtml(o.id)}" ${o.id === (st.operation || 'Add') ? 'selected' : ''}>${escapeHtml(o.id)}</option>`).join('')}</select></label>`);
      parts.push(`<label>同步槽<select data-sp="syncAttribute" ${attrsData}><option value="">不投影</option>${syncAttrs.map((a) => `<option value="${escapeHtml(a)}" ${a === (st.syncAttribute || '') ? 'selected' : ''}>${escapeHtml(a)}</option>`).join('')}</select></label>`);
    } else if (st.op === 'DealDamage') {
      parts.push(`<label>目标<select data-sp="playerTarget" ${attrsData}><option value="Opponent" ${(st.playerTarget || 'Opponent') === 'Opponent' ? 'selected' : ''}>Opponent</option><option value="Self" ${st.playerTarget === 'Self' ? 'selected' : ''}>Self</option></select></label>`);
      parts.push(`<label>特效<select data-sp="vfxKey" ${attrsData}>${renderVfxOptionsHtml(st.vfxKey || '')}</select></label>`);
      parts.push(`<label><input type="checkbox" data-sp="bypassShield" ${attrsData} ${st.bypassShield ? 'checked' : ''} /> 穿盾</label>`);
    } else if (st.op === 'ApplyStatus') {
      parts.push(`<label>状态<select data-sp="status" ${attrsData}>${statuses.map((s) => `<option value="${escapeHtml(s.id)}" ${s.id === (st.status || 'Haste') ? 'selected' : ''}>${escapeHtml(s.nameZh)}</option>`).join('')}</select></label>`);
      parts.push(`<label>目标<select data-sp="targetMode" ${attrsData}>${targetModes.map((tm) => `<option value="${escapeHtml(tm.id)}" ${tm.id === (st.targetSpec?.mode || 'Adjacent') ? 'selected' : ''}>${escapeHtml(tm.nameZh)}</option>`).join('')}</select></label>`);
    } else if (st.op === 'ChangeCardState') {
      parts.push(`<label>变更<select data-sp="changeKind" ${attrsData}><option value="Disable" ${(st.changeKind || 'Disable') === 'Disable' ? 'selected' : ''}>Disable</option><option value="Destroy" ${st.changeKind === 'Destroy' ? 'selected' : ''}>Destroy</option><option value="Repair" ${st.changeKind === 'Repair' ? 'selected' : ''}>Repair</option></select></label>`);
      parts.push(`<label>目标<select data-sp="targetMode" ${attrsData}>${targetModes.map((tm) => `<option value="${escapeHtml(tm.id)}" ${tm.id === (st.targetSpec?.mode || 'Self') ? 'selected' : ''}>${escapeHtml(tm.nameZh)}</option>`).join('')}</select></label>`);
      parts.push(`<label><input type="checkbox" data-sp="suppressNormalDestroyTriggers" ${attrsData} ${st.suppressNormalDestroyTriggers ? 'checked' : ''} /> 屏蔽常规摧毁联动</label>`);
    } else if (st.op === 'ForceUseCard' || st.op === 'QueuePostCombatRemoveCard') {
      parts.push(`<label>目标<select data-sp="targetMode" ${attrsData}>${targetModes.map((tm) => `<option value="${escapeHtml(tm.id)}" ${tm.id === (st.targetSpec?.mode || 'Self') ? 'selected' : ''}>${escapeHtml(tm.nameZh)}</option>`).join('')}</select></label>`);
    }

    const inlineHtml = parts.length ? `<div class="step-inline-grid">${parts.join('')}</div>` : '';
    return `${inlineHtml}<code style="font-size:11px;color:#cbd5e1;word-break:break-all;margin-top:3px;">${escapeHtml(JSON.stringify(st))}</code>`;
  }

  function renderMechanicParamsEditor(mech) {
    const mSchema = catalog().mechanicSchema || {};
    const paramTypes = mSchema.paramTypes || [{ id: 'int', nameZh: '整数' }, { id: 'bool', nameZh: '开关' }, { id: 'string', nameZh: '文本' }];
    const box = $('meParamsList');
    mech.parameters = mech.parameters || [];
    if (!mech.parameters.length) {
      box.innerHTML = '<div class="muted">暂无对外参数（点击右上角「+ 添加参数」开放可调数值给卡牌配置者）。</div>';
      return;
    }
    box.innerHTML = mech.parameters.map((p, i) => `
      <div class="mech-param-row">
        <div class="form-grid">
          <label class="field"><span>参数键 key *</span><input type="text" value="${escapeHtml(p.key)}" data-pf="key" data-pi="${i}" /></label>
          <label class="field"><span>中文标签 *</span><input type="text" value="${escapeHtml(p.labelZh)}" data-pf="labelZh" data-pi="${i}" /></label>
          <label class="field"><span>参数类型</span><select data-pf="type" data-pi="${i}">${paramTypes.map((pt) => `<option value="${escapeHtml(pt.id)}" ${pt.id === p.type ? 'selected' : ''}>${escapeHtml(pt.nameZh)}</option>`).join('')}</select></label>
          <label class="field"><span>默认值 / 上下限</span>
            <div class="inline-pair">
              <input type="text" value="${escapeHtml(p.defaultValue ?? '')}" placeholder="默认值" data-pf="defaultValue" data-pi="${i}" />
              <input type="number" value="${escapeHtml(p.min ?? '')}" placeholder="min" data-pf="min" data-pi="${i}" style="width:72px;" />
              <input type="number" value="${escapeHtml(p.max ?? '')}" placeholder="max" data-pf="max" data-pi="${i}" style="width:72px;" />
              <button type="button" class="btn btn-danger btn-xs" data-rm-param="${i}">删</button>
            </div>
          </label>
        </div>
      </div>
    `).join('');

    box.querySelectorAll('[data-pf]').forEach((el) => {
      el.addEventListener('change', () => {
        const i = parseInt(el.dataset.pi, 10);
        const f = el.dataset.pf;
        const p = mech.parameters[i];
        if (f === 'min' || f === 'max') p[f] = el.value === '' ? undefined : parseFloat(el.value);
        else if (f === 'defaultValue') {
          if (p.type === 'int') p[f] = parseInt(el.value, 10) || 0;
          else if (p.type === 'float') p[f] = parseFloat(el.value) || 0;
          else if (p.type === 'bool') p[f] = (el.value === 'true');
          else p[f] = el.value;
        } else p[f] = el.value;
        B().markDirty(true);
        $('meRawJson').value = JSON.stringify(mech, null, 2);
      });
    });
    box.querySelectorAll('[data-rm-param]').forEach((btn) => {
      btn.addEventListener('click', () => {
        mech.parameters.splice(parseInt(btn.dataset.rmParam, 10), 1);
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
  }

  function renderMechanicHooksEditor(mech) {
    const mSchema = catalog().mechanicSchema || {};
    const hooksList = mSchema.hooks || [];
    const opsList = mSchema.ops || [];
    const scopeList = mSchema.hookScopes?.length
      ? mSchema.hookScopes
      : Object.entries(HOOK_SCOPE_ZH).map(([id, nameZh]) => ({ id, nameZh }));
    const box = $('meHooksList');
    mech.hooks = mech.hooks || [];
    if (!mech.hooks.length) {
      box.innerHTML = '<div class="muted">暂无管线钩子，点击「+ 添加管线钩子」绑定 10 大通用拦截点之一。</div>';
      return;
    }
    box.innerHTML = mech.hooks.map((h, hi) => `
      <div class="mech-hook-card">
        <div class="mech-hook-head">
          <strong>钩子 #${hi + 1}：${escapeHtml(labelFromList(hooksList, h.hook))}</strong>
          <div class="btn-row">
            <button type="button" class="btn btn-outline btn-xs" data-add-step="${hi}">+ 追加步骤</button>
            <button type="button" class="btn btn-danger btn-xs" data-rm-hook="${hi}">删除钩子</button>
          </div>
        </div>
        <div class="form-grid" style="margin-bottom:8px;">
          <label class="field"><span>管线时机 (Hook)</span>
            <select data-hf="hook" data-hi="${hi}">${hooksList.map((hk) => `<option value="${escapeHtml(hk.id)}" ${hk.id === h.hook ? 'selected' : ''}>${escapeHtml(hk.nameZh)}</option>`).join('')}</select>
          </label>
          <label class="field"><span>触发范围与单场次数上限</span>
            <div class="inline-pair">
              <select data-hf="scope" data-hi="${hi}">
                ${scopeList.map((sc) => `<option value="${escapeHtml(sc.id)}" ${sc.id === (h.scope || 'SelfCard') ? 'selected' : ''}>${escapeHtml(sc.nameZh || HOOK_SCOPE_ZH[sc.id] || sc.id)}</option>`).join('')}
              </select>
              <input type="number" min="1" max="200" value="${escapeHtml(safeNumAttr(h.maxTriggersPerCombat))}" placeholder="不限次数" data-hf="maxTriggersPerCombat" data-hi="${hi}" style="width:92px;" />
            </div>
          </label>
        </div>
        <div class="stack-gap">
          ${(h.steps || []).map((st, si) => `
            <div class="mech-step-row">
              <select data-sf="op" data-hi="${hi}" data-si="${si}">
                ${opsList.map((op) => `<option value="${escapeHtml(op.id)}" ${op.id === st.op ? 'selected' : ''}>${escapeHtml(op.nameZh)}</option>`).join('')}
              </select>
              <div class="mech-step-fields">
                ${renderStepInlineControlsHtml(st, hi, si, mSchema)}
              </div>
              <button type="button" class="btn btn-danger btn-xs" data-rm-step="${hi}:${si}">删</button>
            </div>
          `).join('')}
        </div>
      </div>
    `).join('');

    box.querySelectorAll('[data-hf]').forEach((el) => {
      el.addEventListener('change', () => {
        const hi = parseInt(el.dataset.hi, 10);
        const f = el.dataset.hf;
        mech.hooks[hi][f] = f === 'maxTriggersPerCombat' ? (el.value === '' ? undefined : parseInt(el.value, 10)) : el.value;
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
    box.querySelectorAll('[data-sf="op"]').forEach((el) => {
      el.addEventListener('change', () => {
        const hi = parseInt(el.dataset.hi, 10);
        const si = parseInt(el.dataset.si, 10);
        mech.hooks[hi].steps[si] = makeDefaultStepForOp(el.value);
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
    box.querySelectorAll('[data-sp]').forEach((el) => {
      el.addEventListener('change', () => {
        const hi = parseInt(el.dataset.hi, 10);
        const si = parseInt(el.dataset.si, 10);
        const sp = el.dataset.sp;
        const step = mech.hooks[hi]?.steps?.[si];
        if (!step) return;
        if (sp === 'targetMode') {
          step.targetSpec = Object.assign({}, step.targetSpec || {}, { mode: el.value });
        } else if (sp === 'grantIfMissing' || sp === 'bypassShield' || sp === 'suppressNormalDestroyTriggers') {
          step[sp] = !!el.checked;
        } else if (sp === 'syncAttribute' || sp === 'vfxKey') {
          if (el.value) step[sp] = el.value;
          else delete step[sp];
        } else {
          step[sp] = el.value;
        }
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
    box.querySelectorAll('[data-add-step]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const hi = parseInt(btn.dataset.addStep, 10);
        mech.hooks[hi].steps = mech.hooks[hi].steps || [];
        mech.hooks[hi].steps.push(makeDefaultStepForOp('DealDamage'));
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
    box.querySelectorAll('[data-rm-step]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const [hi, si] = btn.dataset.rmStep.split(':').map(Number);
        mech.hooks[hi].steps.splice(si, 1);
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
    box.querySelectorAll('[data-rm-hook]').forEach((btn) => {
      btn.addEventListener('click', () => {
        mech.hooks.splice(parseInt(btn.dataset.rmHook, 10), 1);
        B().markDirty(true);
        renderMechanicEditor();
      });
    });
  }

  function makeDefaultStepForOp(op) {
    switch (op) {
      case 'CancelHook': return { op: 'CancelHook' };
      case 'Stop': return { op: 'Stop' };
      case 'ModifyHookAmount': return { op: 'ModifyHookAmount', operation: 'Multiply', value: { const: 0.5 } };
      case 'SetFlag': return { op: 'SetFlag', target: 'Self', flag: 'custom_flag' };
      case 'ClearFlag': return { op: 'ClearFlag', target: 'Self', flag: 'custom_flag' };
      case 'ModifyCounter': return { op: 'ModifyCounter', target: 'Self', key: 'counter_0', operation: 'Add', value: { const: 1 }, syncAttribute: 'Custom_3' };
      case 'ApplyStatus': return { op: 'ApplyStatus', status: 'Haste', targetSpec: { mode: 'Adjacent' }, amount: { const: 1000 } };
      case 'ModifyCardAttribute': return { op: 'ModifyCardAttribute', targetSpec: { mode: 'AllAllies' }, attribute: 'DamageAmount', operation: 'Add', value: { const: 10 }, duration: 'Combat', grantIfMissing: false };
      case 'ModifyPlayerAttribute': return { op: 'ModifyPlayerAttribute', playerTarget: 'Self', attribute: 'HealthMax', operation: 'Add', value: { const: 50 } };
      case 'ChangeCardState': return { op: 'ChangeCardState', targetSpec: { mode: 'Self' }, changeKind: 'Disable', reason: 'custom_state', suppressNormalDestroyTriggers: true };
      case 'ForceUseCard': return { op: 'ForceUseCard', targetSpec: { mode: 'Adjacent' } };
      case 'EmitCustomEvent': return { op: 'EmitCustomEvent', eventName: 'my_custom_event', payloadValue: { const: 1 } };
      case 'Repeat': return { op: 'Repeat', count: { const: 3 }, intervalMs: { const: 150 }, steps: [{ op: 'DealDamage', playerTarget: 'Opponent', amount: { const: 10 } }] };
      case 'ScheduleDelayed': return { op: 'ScheduleDelayed', delayMs: { const: 2000 }, steps: [{ op: 'DealDamage', playerTarget: 'Opponent', amount: { const: 25 } }] };
      case 'QueuePostCombatRemoveCard': return { op: 'QueuePostCombatRemoveCard', targetSpec: { mode: 'Self', includeDisabled: true }, reason: 'shattered' };
      case 'QueuePostCombatModifyAttr': return { op: 'QueuePostCombatModifyAttr', targetSpec: { mode: 'Self', includeDisabled: true }, attribute: 'DamageAmount', operation: 'Add', value: { const: 5 }, grantIfMissing: false };
      case 'If': return { op: 'If', condition: { hasFlag: 'custom_flag', flagTarget: 'Self' }, then: [{ op: 'CancelHook' }] };
      default: return { op: 'DealDamage', playerTarget: 'Opponent', amount: { const: 15 } };
    }
  }

  async function stressTestCurrentMechanic() {
    const ws = B().state.workspace;
    const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
    if (!mech) {
      B().toast('请先选择或创建一个自定义机制！', 'err');
      return;
    }
    const out = $('mechStressOutput');
    out.className = 'stress-mini-output';
    out.innerHTML = '<div>⏳ 正在隔离沙盒内执行静态审计 + 20 场极限对抗战斗压测（含高速多伤/DOT/摧毁/修复对手）...</div>';

    const res = await B().apiFetch('/api/workshop/mechanics/stress-test', {
      method: 'POST',
      json: { mechanic: mech, matches: 20 },
    });
    if (!res.ok) {
      out.className = 'stress-mini-output fail';
      out.innerHTML = `<strong style="color:#f87171;">请求失败：</strong> ${escapeHtml(res.data?.error || res.status)}`;
      return;
    }
    const d = res.data;
    out.className = `stress-mini-output ${d.passed ? 'pass' : 'fail'}`;
    const sr = d.combatStressReport;
    const issuesHtml = [...(d.staticIssues || []), ...(d.simIssues || [])].map((is) =>
      `<div style="margin-top:4px;color:${is.severity === 'Error' ? '#f87171' : '#fbbf24'};">[${escapeHtml(is.code)}] ${escapeHtml(is.message)}</div>`
    ).join('');
    const diagHtml = (sr?.mechanicDiagnostics || []).slice(0, 6).map((dg) =>
      `<div style="margin-top:4px;color:#fbbf24;">[${escapeHtml(dg.code)}@${dg.timeMs}ms] ${escapeHtml(dg.messageZh)}</div>`
    ).join('');
    out.innerHTML = `
      <div style="font-weight:700;color:${d.passed ? '#4ade80' : '#f87171'};margin-bottom:6px;">${d.passed ? '✅ 压测安全通过' : '❌ 压测拦截'}</div>
      <div>${escapeHtml(d.summaryZh)}</div>
      ${sr ? `<div style="margin-top:6px;color:#94a3b8;">场次: ${sr.matchesRun} · 异常: ${sr.exceptions} · 熔断: ${sr.mechanicCircuitBreakerTrips} · 超预算: ${sr.mechanicBudgetViolations} · 均耗: ${sr.avgMatchMs}ms · 胜率参考: ${sr.modSideWinRatePct}%</div>` : ''}
      ${issuesHtml}
      ${diagHtml}
    `;
  }

  // ── 事件绑定 ────────────────────────────────────────────────────────────────
  function bindV14Events() {
    $('btnOpenAffixBuilder')?.addEventListener('click', () => openAffixBuilder('ability', null));
    $('btnOpenAuraBuilder')?.addEventListener('click', () => openAffixBuilder('aura', null));
    $('btnPeOpenAffixBuilder')?.addEventListener('click', () => openAffixBuilder('ability', null));
    $('btnPeOpenAuraBuilder')?.addEventListener('click', () => openAffixBuilder('aura', null));
    $('btnCloseAffixBuilder')?.addEventListener('click', () => $('affixBuilderModal').classList.add('hidden'));
    $('btnCancelAffixBuilder')?.addEventListener('click', () => $('affixBuilderModal').classList.add('hidden'));
    $('btnConfirmAffixBuilder')?.addEventListener('click', confirmAffixBuilder);
    $('btnCloseVanillaAffix')?.addEventListener('click', () => $('vanillaAffixModal').classList.add('hidden'));

    $('btnAbAddAction')?.addEventListener('click', () => {
      v14.builderState.actions.push(makeDefaultActionRow('TActionCardHaste'));
      renderBuilderActionsList();
      updateAffixBuilderPreview();
    });
    $('abUseOrTrigger')?.addEventListener('change', () => {
      $('abOrTriggerWrap').classList.toggle('hidden', !$('abUseOrTrigger').checked);
      updateAffixBuilderPreview();
    });
    $('abPrereqKind')?.addEventListener('change', () => {
      renderPrereqFields('ab');
      updateAffixBuilderPreview();
    });
    $('abAuraPrereqKind')?.addEventListener('change', () => {
      renderPrereqFields('abAura');
      updateAffixBuilderPreview();
    });
    ['abTriggerType', 'abTriggerType2', 'abTriggerSubjectMode', 'abTriggerSubjectTag', 'abTriggerSubjectHiddenTag', 'abActiveIn',
      'abAuraActionType', 'abAuraTargetMode', 'abAuraTargetTag', 'abAuraAttr', 'abAuraOp', 'abAuraValMode', 'abAuraValInput',
      'abAuraActiveIn', 'abAuraVfxKey']
      .forEach((id) => $(id)?.addEventListener('change', updateAffixBuilderPreview));

    // 原位补丁：一键载入原版能力 / 清空覆写
    $('btnPeLoadVanillaRules')?.addEventListener('click', async () => {
      const info = getActiveTargetHost();
      if (!info || info.host !== 'patch') return;
      const res = await B().apiFetch(`/api/workshop/base-cards/${encodeURIComponent(info.obj.targetTemplateId)}`);
      if (!res.ok || !res.data?.card) { B().toast('载入原版词缀失败', 'err'); return; }
      info.obj.abilitiesOverride = JSON.parse(JSON.stringify(res.data.card.abilities || {}));
      info.obj.aurasOverride = JSON.parse(JSON.stringify(res.data.card.auras || {}));
      B().markDirty(true);
      renderPatchAbilitiesSummary();
      B().toast('已载入该原版卡的完整词缀，现在可以随意删改或捏合！');
    });
    $('btnPeClearRulesOverride')?.addEventListener('click', () => {
      const info = getActiveTargetHost();
      if (!info || info.host !== 'patch') return;
      delete info.obj.abilitiesOverride;
      delete info.obj.aurasOverride;
      B().markDirty(true);
      renderPatchAbilitiesSummary();
      B().toast('已清空词缀覆写，恢复继承原版机制。');
    });

    // 卡牌挂载自定义机制
    $('btnAttachMechToCard')?.addEventListener('click', attachSelectedMechanicToActiveHost);
    $('btnAttachMechToPatch')?.addEventListener('click', attachSelectedMechanicToActiveHost);
    $('btnGoToMechLab')?.addEventListener('click', () => B().switchTab('mechanics'));

    // 机制工坊 CRUD
    const createMech = (kind) => {
      const ws = B().state.workspace;
      if (!ws) { B().toast('请先在 Tab 1 选择或新建一个 Mod！', 'err'); return; }
      ws.mechanics = ws.mechanics || [];
      ws.mechanics.push(makeScaffoldMechanic(kind));
      v14.selectedMechIdx = ws.mechanics.length - 1;
      B().markDirty(true);
      renderMechanicLists();
      renderMechanicEditor();
      renderCardAttachedMechanics();
      B().toast('已创建新沙盒机制骨架，可自由修改并点右侧一键 20 场对抗压测！');
    };
    $('btnNewMechanic')?.addEventListener('click', () => createMech('blank'));
    $('btnCreateFromScaffold')?.addEventListener('click', () => createMech($('mechScaffoldSelect').value));

    $('btnDeleteMechanic')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws || v14.selectedMechIdx < 0) return;
      ws.mechanics.splice(v14.selectedMechIdx, 1);
      v14.selectedMechIdx = ws.mechanics.length ? 0 : -1;
      B().markDirty(true);
      renderMechanicLists();
      renderMechanicEditor();
      renderCardAttachedMechanics();
    });

    ['meId', 'meNameZh', 'meCategoryZh', 'meTooltipTpl', 'meDescZh'].forEach((id) => {
      $(id)?.addEventListener('input', () => {
        const ws = B().state.workspace;
        const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
        if (!mech) return;
        mech.id = $('meId').value.trim();
        mech.nameZh = $('meNameZh').value.trim();
        mech.categoryZh = $('meCategoryZh').value.trim();
        mech.tooltipTemplateZh = $('meTooltipTpl').value;
        mech.descZh = $('meDescZh').value;
        $('meHeading').textContent = mech.nameZh || mech.id;
        $('meRawJson').value = JSON.stringify(mech, null, 2);
        B().markDirty(true);
        renderMechanicLists();
      });
    });

    $('btnAddMechParam')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
      if (!mech) return;
      mech.parameters = mech.parameters || [];
      const n = mech.parameters.length + 1;
      mech.parameters.push({ key: `param_${n}`, labelZh: `参数 ${n}`, type: 'int', defaultValue: 1, min: 0, max: 100 });
      B().markDirty(true);
      renderMechanicEditor();
    });

    $('btnAddMechHook')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
      if (!mech) return;
      mech.hooks = mech.hooks || [];
      mech.hooks.push({ id: `h${mech.hooks.length}`, hook: 'OnCardFired', scope: 'SelfCard', steps: [{ op: 'DealDamage', playerTarget: 'Opponent', amount: { const: 15 } }] });
      B().markDirty(true);
      renderMechanicEditor();
    });

    $('btnApplyMechRawJson')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws || v14.selectedMechIdx < 0) return;
      try {
        const parsed = JSON.parse($('meRawJson').value);
        ws.mechanics[v14.selectedMechIdx] = parsed;
        B().markDirty(true);
        renderMechanicLists();
        renderMechanicEditor();
        renderCardAttachedMechanics();
        B().toast('已应用 JSON 修改到当前机制！');
      } catch (e) {
        B().toast(`JSON 解析失败：${e.message}`, 'err');
      }
    });

    $('btnFormatMechJson')?.addEventListener('click', () => {
      try {
        $('meRawJson').value = JSON.stringify(JSON.parse($('meRawJson').value), null, 2);
      } catch (e) {
        B().toast(`JSON 语法错误：${e.message}`, 'err');
      }
    });

    $('btnStressTestMechanic')?.addEventListener('click', stressTestCurrentMechanic);

    // 机制级 counterDisplay 双向绑定
    $('meCdEnabled')?.addEventListener('change', () => {
      const ws = B().state.workspace;
      const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
      if (!mech) return;
      if ($('meCdEnabled').checked) {
        mech.counterDisplay = mech.counterDisplay || {
          labelZh: '充能', syncAttribute: 'Custom_3', colorHex: '#F97316',
          style: 'auto', valueType: 'count', position: 'bottom', hideWhenZero: false,
        };
      } else {
        delete mech.counterDisplay;
      }
      B().markDirty(true);
      renderMechanicEditor();
    });
    ['meCdLabelZh', 'meCdSyncAttr', 'meCdMaxParam', 'meCdMaxConst', 'meCdMaxAttr', 'meCdColorHex', 'meCdStyle', 'meCdValueType', 'meCdPosition', 'meCdUnit', 'meCdFormat', 'meCdHideWhenZero'].forEach((id) => {
      $(id)?.addEventListener('change', () => {
        const ws = B().state.workspace;
        const mech = ws && v14.selectedMechIdx >= 0 ? ws.mechanics[v14.selectedMechIdx] : null;
        if (!mech || !mech.counterDisplay) return;
        const cd = mech.counterDisplay;
        cd.labelZh = $('meCdLabelZh').value.trim();
        cd.syncAttribute = $('meCdSyncAttr').value || 'Custom_3';
        cd.maxParam = $('meCdMaxParam').value.trim() || undefined;
        cd.maxConst = $('meCdMaxConst').value === '' ? undefined : parseInt($('meCdMaxConst').value, 10);
        cd.maxAttr = $('meCdMaxAttr').value || undefined;
        cd.colorHex = $('meCdColorHex').value.trim() || undefined;
        cd.style = $('meCdStyle').value || 'auto';
        cd.valueType = $('meCdValueType').value || 'count';
        cd.position = $('meCdPosition').value || 'bottom';
        cd.unit = $('meCdUnit').value.trim() || undefined;
        cd.format = $('meCdFormat').value.trim() || undefined;
        cd.hideWhenZero = !!$('meCdHideWhenZero').checked;
        B().markDirty(true);
        $('meRawJson').value = JSON.stringify(mech, null, 2);
      });
    });

    // 卡牌/补丁级 counterDisplays 增删按钮
    const addCounterDisplayForHost = () => {
      const info = getActiveTargetHost();
      if (!info) return;
      const list = info.host === 'card'
        ? (info.obj.counterDisplays = info.obj.counterDisplays || [])
        : (info.obj.counterDisplaysOverride = info.obj.counterDisplaysOverride || []);
      if (list.length >= 4) {
        B().toast('单卡最多配置 4 个实时计数角标（counterDisplays）！', 'err');
        return;
      }
      list.push({
        labelZh: `计数 ${list.length + 1}`,
        syncAttribute: `Custom_${Math.min(8, 3 + list.length)}`,
        colorHex: '#F97316',
        style: 'auto',
        valueType: 'count',
        position: 'bottom',
        hideWhenZero: false,
      });
      B().markDirty(true);
      renderHostCounterDisplaysEditor();
      if (info.host === 'card') B().updateLivePreview(info.obj);
      else B().updateLivePreviewFromPatch(info.obj);
    };
    $('btnAddCardCounterDisplay')?.addEventListener('click', addCounterDisplayForHost);
    $('btnAddPatchCounterDisplay')?.addEventListener('click', addCounterDisplayForHost);

    // 13 种官方附魔可视化按钮绑定（双宿主：新卡 + 原位补丁）
    const autoGenEnchantsForHost = () => {
      const info = getActiveTargetHost();
      if (!info) return;
      if (info.host === 'card') {
        autoGenerateAll13Enchantments(info.obj);
        B().updateLivePreview(info.obj);
      } else {
        const bp = B().state.vanillaBlueprintCache?.[info.obj.targetTemplateId];
        autoGenerateAll13EnchantmentsForPatch(info.obj, bp?.card || null);
        B().updateLivePreviewFromPatch(info.obj);
      }
      B().markDirty(true);
      renderEnchantmentsEditor();
      B().toast('已按卡牌属性智能生成全部 13 种官方附魔与中文说明！可在右侧「预览附魔」逐一查看。');
    };
    const clearEnchantsForHost = () => {
      const info = getActiveTargetHost();
      if (!info) return;
      if (info.host === 'card') {
        info.obj.enchantments = {};
        info.obj.enchantTooltipsZh = {};
        B().updateLivePreview(info.obj);
      } else {
        info.obj.enchantmentOverrides = {};
        info.obj.enchantTooltipsZh = {};
        B().updateLivePreviewFromPatch(info.obj);
      }
      B().markDirty(true);
      renderEnchantmentsEditor();
      B().toast('已清空附魔配置。');
    };
    $('btnAutoGenAllEnchants')?.addEventListener('click', autoGenEnchantsForHost);
    $('btnClearAllEnchants')?.addEventListener('click', clearEnchantsForHost);
    $('btnPeAutoGenAllEnchants')?.addEventListener('click', autoGenEnchantsForHost);
    $('btnPeClearAllEnchants')?.addEventListener('click', clearEnchantsForHost);

    // Tab 1 进阶内容工坊（野怪 / 模式 / 突变词条 / 汉化）子页签与按钮绑定
    document.querySelectorAll('.extra-subtab').forEach((btn) => {
      btn.addEventListener('click', () => {
        const target = btn.dataset.extra;
        document.querySelectorAll('.extra-subtab').forEach((b) => b.classList.toggle('active', b === btn));
        document.querySelectorAll('.extra-pane').forEach((p) => p.classList.toggle('active', p.id === `extra-pane-${target}`));
      });
    });
    $('btnAddMonster')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws) return;
      ws.monsters = ws.monsters || [];
      const seq = ws.monsters.length + 1;
      ws.monsters.push({
        id: `${ws.manifest.id}:monster_${seq}`,
        internalName: `Custom Monster ${seq}`,
        player: { attributes: { HealthMax: 200, Level: 3 }, hand: { unlockedSlots: 1023, items: [] }, skills: [] },
      });
      B().markDirty(true);
      B().updateCounts();
      renderExtraContentStudio();
    });
    $('btnAddMode')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws) return;
      ws.modes = ws.modes || [];
      const seq = ws.modes.length + 1;
      ws.modes.push({
        modeId: `${ws.manifest.id}:mode_${seq}`,
        nameZh: `自定义玩法模式 #${seq}`,
        descZh: '自定义规则模式',
        basePreset: 'PureOfficial',
        baseSpecialMode: 'Standard',
        mutatorIds: [],
        affectsGhostPool: false,
      });
      B().markDirty(true);
      B().updateCounts();
      renderExtraContentStudio();
    });
    $('btnAddMutator')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws) return;
      ws.mutators = ws.mutators || [];
      const seq = ws.mutators.length + 1;
      ws.mutators.push({
        id: `${ws.manifest.id}:mutator_${seq}`,
        nameZh: `自定义突变词条 #${seq}`,
        descriptionZh: '自定义肉鸽词条说明',
        category: 'challenge',
        difficultyDelta: 1,
        scoreMultiplierDelta: 0.15,
        isPositive: false,
        targetHook: 'RunEngine',
        enabledByDefault: false,
        rules: {},
      });
      B().markDirty(true);
      B().updateCounts();
      renderExtraContentStudio();
    });
    $('btnApplyLocJson')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws) return;
      try {
        const raw = $('locRawJson').value.trim();
        if (!raw) {
          ws.localization = null;
        } else {
          const parsed = JSON.parse(raw);
          parsed.schemaVersion = 1;
          parsed.locale = $('locLocale')?.value.trim() || parsed.locale || 'zh-CN';
          parsed.revision = $('locRevision')?.value.trim() || parsed.revision || '1.0.0';
          ws.localization = parsed;
        }
        B().markDirty(true);
        B().toast('已更新汉化覆盖词典！');
      } catch (ex) {
        B().toast(`汉化 JSON 语法错误：${ex.message}`, 'err');
      }
    });
    $('btnClearLocOverlay')?.addEventListener('click', () => {
      const ws = B().state.workspace;
      if (!ws) return;
      ws.localization = null;
      B().markDirty(true);
      renderExtraContentStudio();
      B().toast('已清空本 Mod 汉化覆盖词典。');
    });

    // Tab 6 纯中文对话式 AI 提示词生成器
    $('aiIdeaInput')?.addEventListener('input', () => updateAiPromptPreview('interview'));
    $('btnCopyAiInterview')?.addEventListener('click', async () => {
      const txt = buildAiPromptText('interview');
      if ($('aiGeneratedPromptPreview')) $('aiGeneratedPromptPreview').textContent = txt;
      try {
        await navigator.clipboard.writeText(txt);
        B().toast('已复制「纯中文对话访谈式造卡」指令！直接粘贴给本地 AI 即可开始一步步对话定制。');
      } catch {
        B().toast('请手动全选下方框内文本复制。', 'err');
      }
    });
    $('btnCopyAiDirect')?.addEventListener('click', async () => {
      const txt = buildAiPromptText('direct');
      if ($('aiGeneratedPromptPreview')) $('aiGeneratedPromptPreview').textContent = txt;
      try {
        await navigator.clipboard.writeText(txt);
        B().toast('已复制「按你的大白话想法直接落地」指令！粘贴给本地 AI 即可自动生成并过检。');
      } catch {
        B().toast('请手动全选下方框内文本复制。', 'err');
      }
    });
  }

  // ── 5. 纯中文一键点选按钮组（所属英雄 / 物品标签 / 效果分类 / 补丁标签 / 奇遇英雄） ──
  function renderPillGroup(containerId, items, selectedList, onToggle, options = {}) {
    const box = $(containerId);
    if (!box) return;
    const selectedSet = new Set(selectedList || []);
    const allItems = [...(items || [])];
    // 兼容自定义/罕见值，避免点选丢失
    selectedSet.forEach((v) => {
      if (v && !allItems.some((x) => x.id === v)) allItems.push({ id: v, nameZh: v });
    });

    box.innerHTML = allItems.map((item) => {
      const active = selectedSet.has(item.id) ? 'active' : '';
      const tipAttr = item.descZh ? ` title="${escapeHtml(item.descZh)}"` : '';
      return `<button type="button" class="pill-btn ${active}" data-pill-val="${escapeHtml(item.id)}"${tipAttr}>${escapeHtml(item.nameZh || item.id)}</button>`;
    }).join('');

    box.querySelectorAll('.pill-btn').forEach((btn) => {
      btn.addEventListener('click', () => {
        const val = btn.dataset.pillVal;
        let next = Array.from(selectedSet);
        if (options.exclusiveValue && val === options.exclusiveValue) {
          next = [options.exclusiveValue];
        } else {
          if (selectedSet.has(val)) {
            next = next.filter((x) => x !== val);
          } else {
            next = next.filter((x) => x !== options.exclusiveValue);
            next.push(val);
          }
          if (options.fallbackValue && next.length === 0) {
            next = [options.fallbackValue];
          }
        }
        onToggle(next);
      });
    });
  }

  const PLAYABLE_HERO_IDS = ['Vanessa', 'Dooley', 'Pygmalien', 'Mak', 'Karnok', 'Jules', 'Stelle', 'Hero8'];

  // 奇遇事件语境专用英雄目录：Common 独立显示为「✨ 全英雄通用 (Common)」，不复用物品中立商店文案，底层值仍为 Common
  function getEventHeroCatalog(cat) {
    if (Array.isArray(cat?.eventHeroes) && cat.eventHeroes.length > 0) {
      return cat.eventHeroes;
    }
    const src = Array.isArray(cat?.heroes) ? cat.heroes : [];
    if (!src.length) {
      return [{ id: 'Common', nameZh: '✨ 全英雄通用 (Common)', descZh: '任意英雄对局均可触发该奇遇（底层数据值仍保持 Common）' }];
    }
    return src.map((h) => (
      h && h.id === 'Common'
        ? { ...h, nameZh: '✨ 全英雄通用 (Common)', descZh: '任意英雄对局均可触发该奇遇（底层数据值仍保持 Common）' }
        : h
    ));
  }

  function renderHeroPillGroup(containerId, heroesCatalog, selectedList, onToggle) {
    const box = $(containerId);
    if (!box) return;
    const selectedSet = new Set(selectedList || []);
    const allPlayableSelected = PLAYABLE_HERO_IDS.every((h) => selectedSet.has(h)) && !selectedSet.has('Common');
    const playableItems = (heroesCatalog || []).filter((h) => h.id !== 'Common');
    const commonItem = (heroesCatalog || []).find((h) => h.id === 'Common')
      || { id: 'Common', nameZh: '中立/奇物店专属 (Common)' };

    let html = `<button type="button" class="pill-btn ${allPlayableSelected ? 'active' : ''}" data-hero-action="all-playable" title="自动勾选全部 8 位英雄，杰杰等常规商店均可按正常概率刷出">✨ 全 8 英雄常规店通用</button>`;
    html += playableItems.map((item) => {
      const active = selectedSet.has(item.id) ? 'active' : '';
      return `<button type="button" class="pill-btn ${active}" data-pill-val="${escapeHtml(item.id)}" title="${escapeHtml(item.descZh || '')}">${escapeHtml(item.nameZh || item.id)}</button>`;
    }).join('');
    html += `<button type="button" class="pill-btn ${selectedSet.has('Common') ? 'active' : ''}" data-pill-val="Common" title="仅在 Curio 等中立奇物商店刷出，不会出现在杰杰等常规英雄店">🧭 ${escapeHtml(commonItem.nameZh)}</button>`;
    box.innerHTML = html;

    box.querySelector('[data-hero-action="all-playable"]')?.addEventListener('click', () => {
      onToggle([...PLAYABLE_HERO_IDS]);
    });

    box.querySelectorAll('[data-pill-val]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const val = btn.dataset.pillVal;
        let next = Array.from(selectedSet);
        if (val === 'Common') {
          next = ['Common'];
        } else {
          if (selectedSet.has(val)) {
            next = next.filter((x) => x !== val);
          } else {
            next = next.filter((x) => x !== 'Common');
            next.push(val);
          }
          if (next.length === 0) next = [...PLAYABLE_HERO_IDS];
        }
        onToggle(next);
      });
    });
  }

  function renderAllPillPickers() {
    const cat = catalog();
    const heroes = cat.heroes || [];
    const tags = cat.tags || [];
    const htags = cat.hiddenTags || [];
    const st = B().state;

    // 1) 自定义新卡：英雄、物品标签、效果分类
    const card = st.workspace && st.selectedCardIdx >= 0 ? st.workspace.cards[st.selectedCardIdx] : null;
    if (card) {
      renderHeroPillGroup('ceHeroPills', heroes, card.heroes && card.heroes.length ? card.heroes : [...PLAYABLE_HERO_IDS], (next) => {
        card.heroes = next;
        if ($('ceHeroes')) $('ceHeroes').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
        B().updateLivePreview(card);
      });

      renderPillGroup('ceTagPills', tags, card.tags || [], (next) => {
        card.tags = next;
        if ($('ceTags')) $('ceTags').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
        B().updateLivePreview(card);
      });

      renderPillGroup('ceHiddenTagPills', htags, card.hiddenTags || [], (next) => {
        card.hiddenTags = next;
        if ($('ceHiddenTags')) $('ceHiddenTags').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
      });
    }

    // 2) 原位改原版补丁：覆盖英雄池与追加标签
    const patch = st.workspace && st.selectedPatchIdx >= 0 ? st.workspace.patches[st.selectedPatchIdx] : null;
    if (patch) {
      renderPillGroup('peHeroPills', heroes, patch.heroes || [], (next) => {
        patch.heroes = next.length ? next : null;
        if ($('peHeroes')) $('peHeroes').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
        B().updateLivePreviewFromPatch(patch);
      });

      renderPillGroup('peAddTagPills', tags, patch.addTags || [], (next) => {
        patch.addTags = next;
        if ($('peAddTags')) $('peAddTags').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
        B().updateLivePreviewFromPatch(patch);
      });
    }

    // 3) 奇遇事件：适用英雄（使用奇遇独立语境，Common 展示为「✨ 全英雄通用 (Common)」）
    const ev = st.workspace && st.selectedEventIdx >= 0 ? st.workspace.events[st.selectedEventIdx] : null;
    if (ev) {
      const eventHeroes = getEventHeroCatalog(cat);
      renderPillGroup('evHeroPills', eventHeroes, ev.heroes || ['Common'], (next) => {
        ev.heroes = next;
        if ($('evHeroes')) $('evHeroes').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
      }, { exclusiveValue: 'Common', fallbackValue: 'Common' });
    }

    // 4) Manifest 前置依赖 Mod 多选药丸
    if (st.workspace?.manifest) {
      const selfId = st.workspace.manifest.id;
      const otherMods = (st.mods || [])
        .filter((m) => m.id && m.id !== selfId)
        .map((m) => ({ id: m.id, nameZh: `${m.name || m.id} (${m.id})` }));
      renderPillGroup('mfDepsPills', otherMods, st.workspace.manifest.dependencies || [], (next) => {
        st.workspace.manifest.dependencies = next;
        if ($('mfDependencies')) $('mfDependencies').value = next.join(', ');
        B().markDirty(true);
        renderAllPillPickers();
      });
    }
  }

  // ── 5b. 卡牌/补丁双宿主 counterDisplays 多徽章可视化编辑器 ─────────────────────
  function renderHostCounterDisplaysEditor() {
    const info = getActiveTargetHost();
    if (!info) return;
    const listEl = $(info.host === 'card' ? 'ceCounterDisplaysList' : 'peCounterDisplaysList');
    if (!listEl) return;
    const list = info.host === 'card' ? (info.obj.counterDisplays || []) : (info.obj.counterDisplaysOverride || []);
    if (!list.length) {
      listEl.innerHTML = '<div class="muted">暂无卡面实时计数角标（点击右上角「+ 添加计数角标」可配置最多 4 槽位 3D 角标）。</div>';
      return;
    }

    const ms = catalog().mechanicSchema || {};
    const cdAttrs = ms.counterDisplayAttributes || ['Custom_0', 'Custom_1', 'Custom_2', 'Custom_3', 'Custom_4', 'Custom_5', 'Custom_6', 'Custom_7', 'Custom_8', 'Counter', 'Ammo', 'AmmoMax', 'DamageAmount', 'ShieldApplyAmount', 'BurnApplyAmount', 'PoisonApplyAmount', 'HealAmount', 'RegenApplyAmount', 'CritChance', 'Multicast', 'CooldownMax'];
    const styles = ms.counterDisplayStyles || [{ id: 'auto', nameZh: '自动 (auto)' }, { id: 'pips', nameZh: '格点 (pips)' }, { id: 'bar', nameZh: '进度条 (bar)' }, { id: 'gem', nameZh: '宝石 (gem)' }, { id: 'pill', nameZh: '胶囊 (pill)' }];
    const vts = ms.counterDisplayValueTypes || [{ id: 'count', nameZh: '计数 (count)' }, { id: 'progress', nameZh: '进度比 (progress)' }, { id: 'percent', nameZh: '百分比 (percent)' }, { id: 'seconds', nameZh: '秒数 (seconds)' }, { id: 'multiplier', nameZh: '倍率 (multiplier)' }, { id: 'delta', nameZh: '增减 (delta)' }];
    const poses = ms.counterDisplayPositions || [{ id: 'bottom', nameZh: '底部 (bottom)' }, { id: 'bottomRight', nameZh: '右下 (bottomRight)' }, { id: 'topLeft', nameZh: '左上 (topLeft)' }, { id: 'topRight', nameZh: '右上 (topRight)' }];

    listEl.innerHTML = list.map((cd, i) => `
      <div class="counter-item-card">
        <div class="panel-header compact">
          <strong>角标 #${i + 1}：${escapeHtml(cd.labelZh || cd.syncAttribute || 'Custom_3')}</strong>
          <button type="button" class="btn btn-danger btn-xs" data-rm-cd="${i}">删除</button>
        </div>
        <div class="form-grid">
          <label class="field"><span>中文短名</span><input type="text" data-cd-i="${i}" data-cd-f="labelZh" value="${escapeHtml(cd.labelZh || '')}" /></label>
          <label class="field"><span>同步属性</span><select data-cd-i="${i}" data-cd-f="syncAttribute">${cdAttrs.map((a) => `<option value="${escapeHtml(a)}" ${a === (cd.syncAttribute || 'Custom_3') ? 'selected' : ''}>${escapeHtml(a)}</option>`).join('')}</select></label>
          <label class="field"><span>固定上限 maxConst</span><input type="number" data-cd-i="${i}" data-cd-f="maxConst" value="${escapeHtml(safeNumAttr(cd.maxConst))}" placeholder="可选" /></label>
          <label class="field"><span>动态上限属性 maxAttr</span><select data-cd-i="${i}" data-cd-f="maxAttr"><option value="">无</option>${cdAttrs.map((a) => `<option value="${escapeHtml(a)}" ${a === (cd.maxAttr || '') ? 'selected' : ''}>${escapeHtml(a)}</option>`).join('')}</select></label>
          <label class="field"><span>样式 style</span><select data-cd-i="${i}" data-cd-f="style">${styles.map((s) => `<option value="${escapeHtml(s.id)}" ${s.id === (cd.style || 'auto') ? 'selected' : ''}>${escapeHtml(s.nameZh)}</option>`).join('')}</select></label>
          <label class="field"><span>值型 valueType</span><select data-cd-i="${i}" data-cd-f="valueType">${vts.map((v) => `<option value="${escapeHtml(v.id)}" ${v.id === (cd.valueType || 'count') ? 'selected' : ''}>${escapeHtml(v.nameZh)}</option>`).join('')}</select></label>
          <label class="field"><span>锚点 position</span><select data-cd-i="${i}" data-cd-f="position">${poses.map((p) => `<option value="${escapeHtml(p.id)}" ${p.id === (cd.position || 'bottom') ? 'selected' : ''}>${escapeHtml(p.nameZh)}</option>`).join('')}</select></label>
          <label class="field"><span>主题色 / 单位</span>
            <div class="inline-pair">
              <input type="text" data-cd-i="${i}" data-cd-f="colorHex" value="${escapeHtml(cd.colorHex || '#F97316')}" placeholder="#F97316" />
              <input type="text" data-cd-i="${i}" data-cd-f="unit" value="${escapeHtml(cd.unit || '')}" placeholder="单位" style="width:76px;" />
            </div>
          </label>
          <label class="field span-2">
            <label class="inline-check"><input type="checkbox" data-cd-i="${i}" data-cd-f="hideWhenZero" ${cd.hideWhenZero ? 'checked' : ''} /> 数值为 0 时隐藏角标（hideWhenZero）</label>
          </label>
        </div>
      </div>
    `).join('');

    listEl.querySelectorAll('[data-cd-f]').forEach((el) => {
      el.addEventListener('change', () => {
        const i = parseInt(el.dataset.cdI, 10);
        const f = el.dataset.cdF;
        const item = list[i];
        if (!item) return;
        if (f === 'hideWhenZero') item[f] = !!el.checked;
        else if (f === 'maxConst') item[f] = el.value === '' ? undefined : parseInt(el.value, 10);
        else if (f === 'maxAttr' || f === 'unit' || f === 'colorHex') item[f] = el.value.trim() || undefined;
        else item[f] = el.value.trim();
        B().markDirty(true);
        if (info.host === 'card') B().updateLivePreview(info.obj);
        else B().updateLivePreviewFromPatch(info.obj);
      });
    });
    listEl.querySelectorAll('[data-rm-cd]').forEach((btn) => {
      btn.addEventListener('click', () => {
        list.splice(parseInt(btn.dataset.rmCd, 10), 1);
        B().markDirty(true);
        renderHostCounterDisplaysEditor();
        if (info.host === 'card') B().updateLivePreview(info.obj);
        else B().updateLivePreviewFromPatch(info.obj);
      });
    });
  }

  // ── 5c. Tab 1 进阶内容工坊：Monsters / Modes / Mutators / Localization ─────────
  function renderExtraContentStudio() {
    const ws = B().state.workspace;
    if (!ws) return;

    // Monsters
    const mBox = $('monstersStudioList');
    if (mBox) {
      const monsters = ws.monsters || [];
      mBox.innerHTML = monsters.length
        ? monsters.map((m, i) => `
          <div class="counter-item-card">
            <div class="panel-header compact">
              <strong>野怪 #${i + 1}：${escapeHtml(m.internalName || m.id)}</strong>
              <button type="button" class="btn btn-danger btn-xs" data-rm-monster="${i}">删除</button>
            </div>
            <div class="form-grid">
              <label class="field"><span>野怪 ID</span><input type="text" data-mf-i="${i}" data-mf-f="id" value="${escapeHtml(m.id || '')}" /></label>
              <label class="field"><span>名称（internalName）</span><input type="text" data-mf-i="${i}" data-mf-f="internalName" value="${escapeHtml(m.internalName || '')}" /></label>
              <label class="field"><span>生命上限（HealthMax）</span><input type="number" data-mf-i="${i}" data-mf-f="healthMax" value="${escapeHtml(safeNumAttr(m.player?.attributes?.HealthMax ?? 200))}" /></label>
              <label class="field"><span>等级（Level）</span><input type="number" data-mf-i="${i}" data-mf-f="level" value="${escapeHtml(safeNumAttr(m.player?.attributes?.Level ?? 1))}" /></label>
              <label class="field span-2"><span>完整野怪 JSON（手牌 items / 技能 skills）</span><textarea rows="4" spellcheck="false" data-mf-json="${i}">${escapeHtml(JSON.stringify(m, null, 2))}</textarea></label>
            </div>
          </div>
        `).join('')
        : '<div class="empty-hint">暂无自定义野怪，点击「+ 新增野怪模板」添加。</div>';
      mBox.querySelectorAll('[data-mf-f]').forEach((el) => {
        el.addEventListener('change', () => {
          const i = parseInt(el.dataset.mfI, 10);
          const f = el.dataset.mfF;
          const m = monsters[i];
          if (!m) return;
          m.player = m.player || { attributes: {}, hand: { unlockedSlots: 1023, items: [] }, skills: [] };
          m.player.attributes = m.player.attributes || {};
          if (f === 'healthMax') m.player.attributes.HealthMax = parseInt(el.value, 10) || 100;
          else if (f === 'level') m.player.attributes.Level = parseInt(el.value, 10) || 1;
          else m[f] = el.value.trim();
          B().markDirty(true);
          renderExtraContentStudio();
        });
      });
      mBox.querySelectorAll('[data-mf-json]').forEach((ta) => {
        ta.addEventListener('change', () => {
          const i = parseInt(ta.dataset.mfJson, 10);
          try {
            monsters[i] = JSON.parse(ta.value);
            B().markDirty(true);
            renderExtraContentStudio();
          } catch (ex) { B().toast(`野怪 JSON 语法错误：${ex.message}`, 'err'); }
        });
      });
      mBox.querySelectorAll('[data-rm-monster]').forEach((btn) => {
        btn.addEventListener('click', () => {
          monsters.splice(parseInt(btn.dataset.rmMonster, 10), 1);
          B().markDirty(true);
          B().updateCounts();
          renderExtraContentStudio();
        });
      });
    }

    // Modes
    const mdBox = $('modesStudioList');
    if (mdBox) {
      const modes = ws.modes || [];
      mdBox.innerHTML = modes.length
        ? modes.map((md, i) => `
          <div class="counter-item-card">
            <div class="panel-header compact">
              <strong>玩法模式 #${i + 1}：${escapeHtml(md.nameZh || md.modeId)}</strong>
              <button type="button" class="btn btn-danger btn-xs" data-rm-mode="${i}">删除</button>
            </div>
            <div class="form-grid">
              <label class="field"><span>模式 ID（modeId）</span><input type="text" data-md-i="${i}" data-md-f="modeId" value="${escapeHtml(md.modeId || '')}" /></label>
              <label class="field"><span>中文名称</span><input type="text" data-md-i="${i}" data-md-f="nameZh" value="${escapeHtml(md.nameZh || '')}" /></label>
              <label class="field span-2"><span>模式简介</span><input type="text" data-md-i="${i}" data-md-f="descZh" value="${escapeHtml(md.descZh || '')}" /></label>
              <label class="field span-2"><span>完整模式 JSON（mutatorIds / ruleOverrides）</span><textarea rows="4" spellcheck="false" data-md-json="${i}">${escapeHtml(JSON.stringify(md, null, 2))}</textarea></label>
            </div>
          </div>
        `).join('')
        : '<div class="empty-hint">暂无自定义玩法模式，点击「+ 新增玩法模式」添加。</div>';
      mdBox.querySelectorAll('[data-md-f]').forEach((el) => {
        el.addEventListener('change', () => {
          const i = parseInt(el.dataset.mdI, 10);
          modes[i][el.dataset.mdF] = el.value.trim();
          B().markDirty(true);
          renderExtraContentStudio();
        });
      });
      mdBox.querySelectorAll('[data-md-json]').forEach((ta) => {
        ta.addEventListener('change', () => {
          const i = parseInt(ta.dataset.mdJson, 10);
          try {
            modes[i] = JSON.parse(ta.value);
            B().markDirty(true);
            renderExtraContentStudio();
          } catch (ex) { B().toast(`模式 JSON 语法错误：${ex.message}`, 'err'); }
        });
      });
      mdBox.querySelectorAll('[data-rm-mode]').forEach((btn) => {
        btn.addEventListener('click', () => {
          modes.splice(parseInt(btn.dataset.rmMode, 10), 1);
          B().markDirty(true);
          B().updateCounts();
          renderExtraContentStudio();
        });
      });
    }

    // Mutators
    const mutBox = $('mutatorsStudioList');
    if (mutBox) {
      const mutators = ws.mutators || [];
      mutBox.innerHTML = mutators.length
        ? mutators.map((mt, i) => `
          <div class="counter-item-card">
            <div class="panel-header compact">
              <strong>突变词条 #${i + 1}：${escapeHtml(mt.nameZh || mt.id)}</strong>
              <button type="button" class="btn btn-danger btn-xs" data-rm-mutator="${i}">删除</button>
            </div>
            <div class="form-grid">
              <label class="field"><span>词条 ID</span><input type="text" data-mt-i="${i}" data-mt-f="id" value="${escapeHtml(mt.id || '')}" /></label>
              <label class="field"><span>中文名称</span><input type="text" data-mt-i="${i}" data-mt-f="nameZh" value="${escapeHtml(mt.nameZh || '')}" /></label>
              <label class="field"><span>目标钩子 targetHook</span><select data-mt-i="${i}" data-mt-f="targetHook"><option value="RunEngine" ${(mt.targetHook || 'RunEngine') === 'RunEngine' ? 'selected' : ''}>RunEngine</option><option value="CombatSimulator" ${mt.targetHook === 'CombatSimulator' ? 'selected' : ''}>CombatSimulator</option></select></label>
              <label class="field"><span>难度变化 difficultyDelta</span><input type="number" step="0.5" data-mt-i="${i}" data-mt-f="difficultyDelta" value="${escapeHtml(safeNumAttr(mt.difficultyDelta ?? 0))}" /></label>
              <label class="field span-2"><span>说明（descriptionZh）</span><input type="text" data-mt-i="${i}" data-mt-f="descriptionZh" value="${escapeHtml(mt.descriptionZh || mt.descZh || '')}" /></label>
              <label class="field span-2"><span>完整词条 JSON（含 rules 键值表）</span><textarea rows="4" spellcheck="false" data-mt-json="${i}">${escapeHtml(JSON.stringify(mt, null, 2))}</textarea></label>
            </div>
          </div>
        `).join('')
        : '<div class="empty-hint">暂无自定义肉鸽突变词条，点击「+ 新增肉鸽突变词条」添加。</div>';
      mutBox.querySelectorAll('[data-mt-f]').forEach((el) => {
        el.addEventListener('change', () => {
          const i = parseInt(el.dataset.mtI, 10);
          const f = el.dataset.mtF;
          mutators[i][f] = f === 'difficultyDelta' ? (parseFloat(el.value) || 0) : el.value.trim();
          B().markDirty(true);
          renderExtraContentStudio();
        });
      });
      mutBox.querySelectorAll('[data-mt-json]').forEach((ta) => {
        ta.addEventListener('change', () => {
          const i = parseInt(ta.dataset.mtJson, 10);
          try {
            mutators[i] = JSON.parse(ta.value);
            B().markDirty(true);
            renderExtraContentStudio();
          } catch (ex) { B().toast(`突变词条 JSON 语法错误：${ex.message}`, 'err'); }
        });
      });
      mutBox.querySelectorAll('[data-rm-mutator]').forEach((btn) => {
        btn.addEventListener('click', () => {
          mutators.splice(parseInt(btn.dataset.rmMutator, 10), 1);
          B().markDirty(true);
          B().updateCounts();
          renderExtraContentStudio();
        });
      });
    }

    // Localization
    const loc = ws.localization;
    if ($('locLocale')) $('locLocale').value = loc?.locale || 'zh-CN';
    if ($('locRevision')) $('locRevision').value = loc?.revision || '1.0.0';
    if ($('locRawJson')) $('locRawJson').value = loc ? JSON.stringify(loc, null, 2) : '';
  }

  // ── 6. 13 种官方附魔可视化编辑器 & 一键智能生成 ──────────────────────────────
  function buildSmartEnchantmentPreset(enchKey, card) {
    const bronzeAttrs = card?.tiers?.Bronze?.attributes || card?.attributes || {};
    const hasDmg = bronzeAttrs.DamageAmount !== undefined && Number(bronzeAttrs.DamageAmount) > 0;
    const dmgValNode = hasDmg
      ? { $type: 'TReferenceValueCardAttribute', attributeType: 'DamageAmount', target: { $type: 'TTargetCardSelf' } }
      : { $type: 'TFixedValue', value: 20 };

    switch (enchKey) {
      case 'Fiery':
        return {
          tooltipZh: '触发时额外对敌方施加 6 层灼烧',
          data: {
            attributes: { BurnApplyAmount: 6 },
            hiddenTags: ['Burn'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionPlayerBurnApply', target: { $type: 'TTargetPlayerRelative', targetMode: 'Opponent' }, value: { $type: 'TFixedValue', value: 6 } },
              },
            },
          },
        };
      case 'Toxic':
        return {
          tooltipZh: '触发时额外对敌方施加 4 层剧毒',
          data: {
            attributes: { PoisonApplyAmount: 4 },
            hiddenTags: ['Poison'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionPlayerPoisonApply', target: { $type: 'TTargetPlayerRelative', targetMode: 'Opponent' }, value: { $type: 'TFixedValue', value: 4 } },
              },
            },
          },
        };
      case 'Shielded':
        return {
          tooltipZh: hasDmg ? '触发时额外获得等同于本卡伤害（{attr.DamageAmount}）的护盾' : '触发时额外获得 20 点护盾',
          data: {
            hiddenTags: ['Shield'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionPlayerShieldApply', target: { $type: 'TTargetPlayerRelative', targetMode: 'Self' }, value: dmgValNode },
              },
            },
          },
        };
      case 'Restorative':
        return {
          tooltipZh: hasDmg ? '触发时额外回复等同于本卡伤害（{attr.DamageAmount}）的生命并净化灼烧/剧毒' : '触发时额外回复 20 点生命并净化灼烧/剧毒',
          data: {
            hiddenTags: ['Heal'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionPlayerHeal', target: { $type: 'TTargetPlayerRelative', targetMode: 'Self' }, value: dmgValNode },
              },
            },
          },
        };
      case 'Mossy':
        return {
          tooltipZh: '触发时额外获得 5 层再生',
          data: {
            hiddenTags: ['Regen'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionPlayerRegenApply', target: { $type: 'TTargetPlayerRelative', targetMode: 'Self' }, value: { $type: 'TFixedValue', value: 5 } },
              },
            },
          },
        };
      case 'Heavy':
        return {
          tooltipZh: '触发时使随机一件敌方物品减速 2 秒',
          data: {
            hiddenTags: ['Slow'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionCardSlow', target: { $type: 'TTargetCardRandom', targetSection: 'OpponentHand' }, value: { $type: 'TFixedValue', value: 2000 } },
              },
            },
          },
        };
      case 'Icy':
        return {
          tooltipZh: '触发时冻结随机一件敌方物品 1.5 秒',
          data: {
            hiddenTags: ['Freeze'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionCardFreeze', target: { $type: 'TTargetCardRandom', targetSection: 'OpponentHand' }, value: { $type: 'TFixedValue', value: 1500 } },
              },
            },
          },
        };
      case 'Turbo':
        return {
          tooltipZh: '触发时额外使左右相邻物品各加速 1.5 秒',
          data: {
            hiddenTags: ['Haste'],
            hasAbilities: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: { $type: 'TActionCardHaste', target: { $type: 'TTargetCardPositional', origin: 'Self', targetMode: 'Neighbor' }, value: { $type: 'TFixedValue', value: 1500 } },
              },
            },
          },
        };
      case 'Deadly':
        return {
          tooltipZh: '本卡永久获得 +50% 暴击率',
          data: {
            hiddenTags: ['Crit'],
            attributes: { CritChance: 50 },
          },
        };
      case 'Obsidian':
        return hasDmg
          ? {
              tooltipZh: '本卡基础伤害翻倍（×2）',
              data: {
                hiddenTags: ['Damage'],
                hasAuras: true,
                auras: {
                  ea0: {
                    id: 'ea0', activeIn: 'HandOnly',
                    action: {
                      $type: 'TAuraActionCardModifyAttribute',
                      target: { $type: 'TTargetCardSelf' },
                      attributeType: 'DamageAmount',
                      operation: 'Multiply',
                      value: { $type: 'TFixedValue', value: 2 },
                    },
                  },
                },
              },
            }
          : {
              tooltipZh: '触发时额外对敌方造成 25 点伤害',
              data: {
                hiddenTags: ['Damage'],
                hasAbilities: true,
                abilities: {
                  e0: {
                    id: 'e0', activeIn: 'HandOnly',
                    trigger: { $type: 'TTriggerOnCardFired' },
                    action: { $type: 'TActionPlayerDamage', target: { $type: 'TTargetPlayerRelative', targetMode: 'Opponent' }, value: { $type: 'TFixedValue', value: 25 } },
                  },
                },
              },
            };
      case 'Shiny':
        return {
          tooltipZh: '获得 +1 多重施放（每次冷却连发两轮）',
          data: {
            attributes: {},
            abilities: {},
            hasAuras: true,
            auras: {
              ea0: {
                id: 'ea0',
                activeIn: 'HandAndStash',
                action: {
                  $type: 'TAuraActionCardModifyAttribute',
                  target: { $type: 'TTargetCardSelf' },
                  attributeType: 'Multicast',
                  operation: 'Add',
                  value: { $type: 'TFixedValue', value: 1 },
                },
              },
            },
          },
        };
      case 'Radiant':
        return {
          tooltipZh: '免疫冻结、减速与摧毁',
          data: {
            tags: ['Radiant'],
            attributes: { PercentFreezeReduction: 100, PercentSlowReduction: 100, DestroyImmunity: 1 },
          },
        };
      case 'Golden':
      default:
        return {
          tooltipZh: '出售价值翻倍，且每次触发获得 2 金币',
          data: {
            hiddenTags: ['EconomyReference'],
            hasAbilities: true,
            hasAuras: true,
            abilities: {
              e0: {
                id: 'e0', activeIn: 'HandOnly',
                trigger: { $type: 'TTriggerOnCardFired' },
                action: {
                  $type: 'TActionPlayerModifyAttribute',
                  target: { $type: 'TTargetPlayerAbsolute', targetMode: 'Player' },
                  attributeType: 'Gold',
                  operation: 'Add',
                  value: { $type: 'TFixedValue', value: 2 },
                },
              },
            },
            auras: {
              ea0: {
                id: 'ea0', activeIn: 'HandAndStash',
                action: {
                  $type: 'TAuraActionCardModifyAttribute',
                  target: { $type: 'TTargetCardSelf' },
                  attributeType: 'SellPrice',
                  operation: 'Multiply',
                  value: { $type: 'TFixedValue', value: 2 },
                },
              },
            },
          },
        };
    }
  }

  function autoGenerateAll13Enchantments(card) {
    const enchList = catalog().enchantments || [];
    card.enchantments = card.enchantments || {};
    card.enchantTooltipsZh = card.enchantTooltipsZh || {};
    enchList.forEach((e) => {
      const preset = buildSmartEnchantmentPreset(e.id, card);
      card.enchantments[e.id] = preset.data;
      card.enchantTooltipsZh[e.id] = [preset.tooltipZh];
    });
  }

  function autoGenerateAll13EnchantmentsForPatch(patch, baseCard) {
    const enchList = catalog().enchantments || [];
    patch.enchantmentOverrides = patch.enchantmentOverrides || {};
    patch.enchantTooltipsZh = patch.enchantTooltipsZh || {};
    const refCard = baseCard || { tiers: { Bronze: { attributes: patch.tierAttributeOverrides?.Bronze || {} } } };
    enchList.forEach((e) => {
      const preset = buildSmartEnchantmentPreset(e.id, refCard);
      patch.enchantmentOverrides[e.id] = preset.data;
      patch.enchantTooltipsZh[e.id] = [preset.tooltipZh];
    });
  }

  function renderEnchantmentsEditor() {
    const info = getActiveTargetHost();
    const cePills = $('ceEnchantPills');
    const cePane = $('ceEnchantEditorPane');
    const pePills = $('peEnchantPills');
    const pePane = $('peEnchantEditorPane');
    if (!info) {
      if (cePills) cePills.innerHTML = '';
      if (cePane) cePane.classList.add('hidden');
      if (pePills) pePills.innerHTML = '';
      if (pePane) pePane.classList.add('hidden');
      return;
    }

    const isCard = info.host === 'card';
    const pillsBox = isCard ? cePills : pePills;
    const pane = isCard ? cePane : pePane;
    if (!pillsBox || !pane) return;

    const st = B().state;
    const targetObj = info.obj;
    const enchMapKey = isCard ? 'enchantments' : 'enchantmentOverrides';
    const enchList = catalog().enchantments || [];
    if (!enchList.length) return;
    if (!enchList.some((e) => e.id === v14.selectedEnchantKey)) v14.selectedEnchantKey = enchList[0].id;

    const getMap = () => targetObj[enchMapKey] || {};
    const configuredCount = enchList.filter((e) => Boolean(getMap()[e.id] || targetObj.enchantTooltipsZh?.[e.id]?.length)).length;

    pillsBox.innerHTML = enchList.map((e) => {
      const hasData = Boolean(getMap()[e.id] || targetObj.enchantTooltipsZh?.[e.id]?.length);
      const isAct = e.id === v14.selectedEnchantKey;
      return `<button type="button" class="pill-btn ${hasData ? 'has-data' : ''} ${isAct ? 'active' : ''}" data-ench-key="${escapeHtml(e.id)}">${hasData ? '✓ ' : ''}${escapeHtml(e.nameZh)}</button>`;
    }).join('');

    const refreshPreview = () => {
      if (isCard) B().updateLivePreview(targetObj);
      else B().updateLivePreviewFromPatch(targetObj);
    };

    pillsBox.querySelectorAll('[data-ench-key]').forEach((btn) => {
      btn.addEventListener('click', () => {
        v14.selectedEnchantKey = btn.dataset.enchKey;
        st.previewEnchant = v14.selectedEnchantKey;
        if ($('previewEnchantSelect')) $('previewEnchantSelect').value = v14.selectedEnchantKey;
        renderEnchantmentsEditor();
        refreshPreview();
      });
    });

    const curKey = v14.selectedEnchantKey;
    const curMeta = enchList.find((e) => e.id === curKey) || { id: curKey, nameZh: curKey };
    const curData = getMap()[curKey];
    const curTips = targetObj.enchantTooltipsZh?.[curKey] || [];
    const summaryParts = [];
    if (curData?.attributes) {
      Object.entries(curData.attributes).forEach(([k, v]) => summaryParts.push(`属性 ${k} +${v}`));
    }
    if (curData?.abilities) {
      Object.values(curData.abilities).forEach((ab) => summaryParts.push(summarizeAffixZh('ability', ab)));
    }
    if (curData?.auras) {
      Object.values(curData.auras).forEach((au) => summaryParts.push(summarizeAffixZh('aura', au)));
    }

    const idPfx = isCard ? 'ce' : 'pe';
    pane.classList.remove('hidden');
    pane.innerHTML = `
      <div class="panel-header compact">
        <div>
          <span class="badge ${curData ? 'badge-green' : 'badge-yellow'}">${curData ? '已配置' : '未配置'}</span>
          <strong style="margin-left:6px;">${escapeHtml(curMeta.nameZh)}</strong>
          <small class="muted" style="margin-left:8px;">已配置 ${configuredCount} / ${enchList.length} 种附魔</small>
        </div>
        <div class="btn-row">
          <button type="button" class="btn btn-outline btn-xs" id="btn${idPfx}EnchAutoSingle">✨ 生成此附魔标准效果</button>
          ${curData ? `<button type="button" class="btn btn-danger btn-xs" id="btn${idPfx}EnchRemoveSingle">移除此附魔</button>` : ''}
        </div>
      </div>
      <div class="form-grid">
        <label class="field span-2">
          <span>附魔中文说明（局内获得该附魔后自动显示在卡面，支持 <code>{attr.DamageAmount}</code> 等变量）</span>
          <input type="text" id="${idPfx}EnchTipInput" value="${escapeHtml(curTips.join('；'))}" placeholder="例如：触发时额外施加 6 层灼烧" />
        </label>
      </div>
      <div class="muted" style="margin-top:6px;">实战生效规则：${escapeHtml(summaryParts.length ? summaryParts.join('；') : '尚未生成战斗效果，点击右上角「✨ 生成此附魔标准效果」即可立即生效！')}</div>
      <details class="advanced-json" style="margin-top:6px;">
        <summary>开发者代码视图（本附魔 JSON 规则）</summary>
        <textarea id="${idPfx}EnchRawJson" rows="5" spellcheck="false">${escapeHtml(JSON.stringify(curData || {}, null, 2))}</textarea>
        <button type="button" class="btn btn-outline btn-xs" id="btn${idPfx}EnchApplyJson" style="margin-top:4px;">应用附魔代码</button>
      </details>
    `;

    $(`${idPfx}EnchTipInput`)?.addEventListener('input', (e) => {
      targetObj.enchantTooltipsZh = targetObj.enchantTooltipsZh || {};
      const lines = e.target.value.split(/[；\n]/).map((s) => s.trim()).filter(Boolean);
      if (lines.length) targetObj.enchantTooltipsZh[curKey] = lines;
      else delete targetObj.enchantTooltipsZh[curKey];
      B().markDirty(true);
      refreshPreview();
    });
    $(`btn${idPfx}EnchAutoSingle`)?.addEventListener('click', () => {
      targetObj[enchMapKey] = targetObj[enchMapKey] || {};
      targetObj.enchantTooltipsZh = targetObj.enchantTooltipsZh || {};
      const refCard = isCard ? targetObj : (st.vanillaBlueprintCache?.[targetObj.targetTemplateId]?.card || { tiers: { Bronze: { attributes: targetObj.tierAttributeOverrides?.Bronze || {} } } });
      const preset = buildSmartEnchantmentPreset(curKey, refCard);
      targetObj[enchMapKey][curKey] = preset.data;
      targetObj.enchantTooltipsZh[curKey] = [preset.tooltipZh];
      B().markDirty(true);
      renderEnchantmentsEditor();
      refreshPreview();
      B().toast(`已生成「${curMeta.nameZh}」附魔规则与中文说明！`);
    });
    $(`btn${idPfx}EnchRemoveSingle`)?.addEventListener('click', () => {
      if (targetObj[enchMapKey]) delete targetObj[enchMapKey][curKey];
      if (targetObj.enchantTooltipsZh) delete targetObj.enchantTooltipsZh[curKey];
      B().markDirty(true);
      renderEnchantmentsEditor();
      refreshPreview();
    });
    $(`btn${idPfx}EnchApplyJson`)?.addEventListener('click', () => {
      try {
        const parsed = JSON.parse($(`${idPfx}EnchRawJson`).value);
        targetObj[enchMapKey] = targetObj[enchMapKey] || {};
        targetObj[enchMapKey][curKey] = parsed;
        B().markDirty(true);
        renderEnchantmentsEditor();
        refreshPreview();
        B().toast(`已应用「${curMeta.nameZh}」附魔规则 JSON！`);
      } catch (ex) {
        B().toast(`附魔 JSON 语法错误：${ex.message}`, 'err');
      }
    });
  }

  // ── 7. Tab 6：纯中文自然语言对话访谈 AI 造卡提示词生成器 ──────────────────────
  function buildAiPromptText(mode = 'interview') {
    const ws = B()?.state?.workspace;
    const modId = ws?.manifest?.id || 'my-custom-pack';
    const modName = ws?.manifest?.name || '我的原创扩展包';
    const idea = ($('aiIdeaInput')?.value || '').trim();

    if (mode === 'direct') {
      return `请激活 bazaar-mod-creator 技能，用纯中文大白话与我沟通（不要甩英文代码术语），帮我把下面这个想法直接落地到《The Bazaar（大巴扎）》扩展包「${modName}」（目录 mods/${modId}）里：

【我的想法】
${idea || '（请在此补充你想做的卡牌、奇遇联动或新机制想法）'}

【请按以下标准自动完成】
1. 自动从原版白名单库挑选同类型、同尺寸的安全外观底板，预生成带随机防撞后缀的唯一编号；
2. 除非我指定特殊价格，否则买卖价自动遵循官方标准定价；
3. 如果某张卡需要“仅在特定奇遇或某张卡效果里获得”，自动设为 GuidOnly 并同步建好联动奇遇/发卡效果；
4. 如果涉及原版没有的全新机制，使用 V14 开放沙盒机制（mechanics/*.json）独立定义并挂载，严禁改动引擎硬编码；
5. 全部完成后自动执行 10 项安全检测与 50 场真实模拟战斗（validate --strict --offline-sim），确认 0 报错 0 异常后再用简明中文向我汇报成品效果！`;
    }

    return `请激活 bazaar-mod-creator 技能，作为我的《The Bazaar（大巴扎）》纯中文卡牌策划助手，通过【一问一答的对话访谈】帮我把脑海里的想法细化并落地到扩展包「${modName}」（mods/${modId}）中。

【沟通规则（务必遵守）】
- 全程用清晰简洁的纯中文大白话提问和总结，不要抛出 TTrigger / JSON / GUID 等生僻代码术语；
- 按以下 5 个简单步骤依次引导我确认（每次只问 1~2 个轻松好答的问题）：
  1. 定位：想做新卡牌、改原版卡，还是做奇遇联动？适合哪个英雄、占几格？
  2. 核心玩法：平时多久触发一次、触发后做什么、有没有和相邻物品/特定标签的联动？
  3. 特殊获取：是在普通商店正常刷新，还是只能在特定奇遇/其他卡牌效果里拿到？
  4. 新机制与外观：有没有原版做不到的独特新机制（如特殊自爆、限次格挡碎裂等），需不需要定制卡图？
  5. 确认方案：你用大白话整理一张完整清单给我点头确认后，再由你自动生成防撞编号、套用官方标准买卖价、写出合规配置与沙盒机制，并跑完 10 项安全检测 + 50 场模拟战斗确认零崩溃后直接交付！
${idea ? `\n【我目前的初步想法】\n${idea}\n\n请基于这个初步想法，先用大白话帮我梳理要点，再提出第 1 个需要我确认的问题。` : '\n我现在还没完全想好细节，请直接提出第 1 个简单问题引导我开始吧！'}`;
  }

  function updateAiPromptPreview(mode = 'interview') {
    const pre = $('aiGeneratedPromptPreview');
    if (pre) pre.textContent = buildAiPromptText(mode);
  }

  window.WorkshopV14 = {
    summarizeAffixZh,
    readVfxOverrideKey,
    openAffixEditorForActiveTarget: (kind, key) => openAffixBuilder(kind, key),
    openVanillaAffixExtractor,
    onReady: () => {
      bindV14Events();
      loadServerMechanics();
      renderMechanicLists();
      renderMechanicEditor();
      renderPatchAbilitiesSummary();
      renderCardAttachedMechanics();
      renderHostCounterDisplaysEditor();
      renderAllPillPickers();
      renderEnchantmentsEditor();
      renderExtraContentStudio();
      updateAiPromptPreview('interview');
    },
    onWorkspaceLoaded: () => {
      const ws = B().state.workspace;
      v14.selectedMechIdx = (ws?.mechanics?.length || 0) > 0 ? 0 : -1;
      loadServerMechanics();
      renderMechanicLists();
      renderMechanicEditor();
      renderPatchAbilitiesSummary();
      renderCardAttachedMechanics();
      renderHostCounterDisplaysEditor();
      renderAllPillPickers();
      renderEnchantmentsEditor();
      renderExtraContentStudio();
      updateAiPromptPreview('interview');
    },
    onModsListUpdated: () => {
      renderAllPillPickers();
    },
    onCardOrPatchRendered: () => {
      renderPatchAbilitiesSummary();
      renderCardAttachedMechanics();
      renderHostCounterDisplaysEditor();
      renderAllPillPickers();
      renderEnchantmentsEditor();
    },
    onEventEditorRendered: () => {
      renderAllPillPickers();
    },
    onTabSwitched: (tab) => {
      if (tab === 'mechanics') {
        loadServerMechanics();
        renderMechanicLists();
        renderMechanicEditor();
      } else if (tab === 'guide') {
        updateAiPromptPreview('interview');
      } else if (tab === 'events' || tab === 'overview') {
        renderAllPillPickers();
        if (tab === 'overview') renderExtraContentStudio();
      }
    },
  };
})();
