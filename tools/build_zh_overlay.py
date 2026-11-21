#!/usr/bin/env python3
"""
全链路外挂简体中文词典构建与自检工具（tools/build_zh_overlay.py）。

设计职责：
1. 读取受 Git 追踪的人类可读翻译源表 `tools/data/zh_translation_catalog.json`；
2. 结合权威 `GameData.db`、`dist/data/translations/zh-CN.bytes`、`dist/data/valid_card_ids.json`，
   编译生成四层结构的外挂汉化词典：
   - 层 1 `hashOverrides`：按官方 `LocalizationUtility.GetHash` 规则计算 UTF-8 小写 32 位 MD5，
     覆盖数据库缺失文本（含 2026-11-06 新卡、送货包裹、技能/附魔/Tooltip/挑战/英雄枚举）
     及全部运行时 `LocalizableText`；
   - 层 2 `cardOverrides`：按完整 `TemplateId` 提供标题/描述/Tooltip/附魔精确覆盖与 DB 存在性校验；
   - 层 3 `uiExactOverrides`、`uiContextOverrides` 与 `uiRegexOverrides`：覆盖官方运行时 UI
     未汉化 `LocalizableText`、绕过本地化服务的硬编码英文 UI、4 处字符串插值模板及上下文消歧；
   - 层 4 `serverMessageOverrides`（`exact` + `regex`）：覆盖 `BazaarServer` 全部暴露给客户端的
     英文报错/状态文本与 `BazaarLauncher` 英文残余提示；
3. 执行全量自动化质量自检（缺一不可，任一失败返回非 0 退出码）：
   - JSON 语法与字段类型合法性；
   - 零空译文 / 零空白译文；
   - 零破坏占位符（`{ability.*}`, `{aura.*}`, `{completionRequirement}`, `{0}`, `{1}`, `{Code}` 等）
     与零破坏富文本标签（`<size=...>`, `<font=...>`, `<material=...>`, `<color=...>` 等）；
   - 全部正则可编译且替换串 `$1/$2/...` 捕获组索引合法；
   - `cardOverrides` 全部 TemplateId 在权威 `GameData.db` 中真实存在；
4. 自动计算内容 `sha256`、`generatedAt`、`entryCount`，原子写入：
   - `tools/data/custom_zh_overlay.json`（受 Git 追踪的权威词典制品）
   - `dist/data/translations/custom_zh_overlay.json`（服务端/客户端直接消费路径）。
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import sqlite3
import sys
from collections import Counter
from typing import Any

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_CATALOG = REPO_ROOT / "tools" / "data" / "zh_translation_catalog.json"
DEFAULT_TRACKED_OUT = REPO_ROOT / "tools" / "data" / "custom_zh_overlay.json"
DEFAULT_DIST_OUT = REPO_ROOT / "dist" / "data" / "translations" / "custom_zh_overlay.json"
DEFAULT_ZH_BYTES = REPO_ROOT / "dist" / "data" / "translations" / "zh-CN.bytes"
DEFAULT_WHITELIST = REPO_ROOT / "dist" / "data" / "valid_card_ids.json"
DEFAULT_LOCAL_DB = REPO_ROOT / "dist" / "data" / "GameData.db"
FALLBACK_MAIN_DB = pathlib.Path(r"e:\thebazaar\dist\data\GameData.db")

SCHEMA_URL = "https://bazaar-local.schema/custom-zh-overlay/v1"

# 提取各类游戏占位符与 TMP 富文本标签，用于严格的保真校验
PLACEHOLDER_RE = re.compile(r"\{[^{}]+\}")
RICHTEXT_TAG_RE = re.compile(r"</?(?:size|font|material|color|b|i|style|sprite|voffset|line-height)(?:=[^>]*)?>", re.I)
GROUP_REF_RE = re.compile(r"\$(\d+)")


def md5_utf8(text: str) -> str:
    """与官方 LocalizationUtility.GetHash 完全一致的 UTF-8 小写 MD5。"""
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def resolve_optional_db(explicit: pathlib.Path | None) -> pathlib.Path | None:
    candidates = [explicit] if explicit is not None else [DEFAULT_LOCAL_DB, FALLBACK_MAIN_DB]
    for p in candidates:
        if p and p.is_file() and p.stat().st_size > 0:
            return p
    return None


def atomic_write_text(path: pathlib.Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8", newline="\n")
    os.replace(tmp, path)


def validate_pair_fidelity(en: str, zh: str, label: str, errors: list[str]) -> None:
    """校验单条英中翻译的空值、占位符与富文本标签 100% 保真。"""
    if not isinstance(zh, str) or not zh.strip():
        errors.append(f"[空译文] {label}: 原文={en!r}")
        return

    en_ph = Counter(PLACEHOLDER_RE.findall(en))
    zh_ph = Counter(PLACEHOLDER_RE.findall(zh))
    if en_ph != zh_ph:
        errors.append(
            f"[占位符损坏] {label}: 原文占位符={dict(en_ph)} != 译文占位符={dict(zh_ph)} | 原文={en!r} | 译文={zh!r}"
        )

    en_tags = Counter(RICHTEXT_TAG_RE.findall(en))
    zh_tags = Counter(RICHTEXT_TAG_RE.findall(zh))
    if en_tags != zh_tags:
        errors.append(
            f"[富文本标签损坏] {label}: 原文标签={dict(en_tags)} != 译文标签={dict(zh_tags)} | 原文={en!r} | 译文={zh!r}"
        )


def validate_regex_rules(rules: list[dict[str, Any]], section: str, errors: list[str]) -> None:
    """校验正则条目可编译、无空替换、捕获组引用不越界且 id 唯一。"""
    seen_ids: set[str] = set()
    for idx, rule in enumerate(rules):
        rid = rule.get("id")
        pat = rule.get("pattern")
        rep = rule.get("replacement")
        if not isinstance(rid, str) or not rid.strip():
            errors.append(f"[{section}#{idx}] 缺少有效 id")
            continue
        if rid in seen_ids:
            errors.append(f"[{section}] 重复规则 id: {rid}")
        seen_ids.add(rid)

        if not isinstance(pat, str) or not pat.strip():
            errors.append(f"[{section}:{rid}] pattern 为空")
            continue
        if not isinstance(rep, str) or not rep.strip():
            errors.append(f"[{section}:{rid}] replacement 为空")
            continue

        try:
            compiled = re.compile(pat)
        except re.error as exc:
            errors.append(f"[{section}:{rid}] 正则语法非法: {exc} ({pat!r})")
            continue

        for m in GROUP_REF_RE.finditer(rep):
            gnum = int(m.group(1))
            if gnum < 1 or gnum > compiled.groups:
                errors.append(
                    f"[{section}:{rid}] 替换串引用越界捕获组 ${gnum}（pattern 仅含 {compiled.groups} 个捕获组）"
                )


def load_zh_bytes_hashes(zh_bytes_path: pathlib.Path) -> set[str]:
    if not zh_bytes_path.is_file():
        return set()
    conn = sqlite3.connect(f"file:{zh_bytes_path}?mode=ro", uri=True)
    try:
        return {row[0] for row in conn.execute("SELECT hash FROM translation")}
    finally:
        conn.close()


def collect_db_coverage(
    db_path: pathlib.Path,
    zh_hashes: set[str],
    whitelist_path: pathlib.Path,
    overlay_hashes: dict[str, str],
    card_overrides: dict[str, Any],
    errors: list[str],
) -> dict[str, Any]:
    """核验 cardOverrides GUID 合法性，并统计对 GameData.db 缺失 MD5 的精准覆盖率。"""
    wl_ids: set[str] = set()
    if whitelist_path.is_file():
        wl_ids = set(json.loads(whitelist_path.read_text(encoding="utf-8")).get("valid_ids", []))

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        cards: dict[str, dict[str, Any]] = {}
        for cid, blob in conn.execute("SELECT Id, Data FROM cards"):
            cards[cid] = json.loads(blob.decode("utf-8"))

        # 校验 cardOverrides 中全部 TemplateId 存在性
        for tid, override in card_overrides.items():
            if tid not in cards:
                errors.append(f"[cardOverrides] TemplateId 不存在于 GameData.db: {tid}")
                continue
            if not isinstance(override, dict):
                errors.append(f"[cardOverrides] {tid} 必须是对象")
                continue
            for field in ("title", "description"):
                if field in override and (not isinstance(override[field], str) or not override[field].strip()):
                    errors.append(f"[cardOverrides] {tid}.{field} 不可为空")
            if "tooltips" in override:
                if not isinstance(override["tooltips"], dict):
                    errors.append(f"[cardOverrides] {tid}.tooltips 必须是字典")
                else:
                    for tk, tv in override["tooltips"].items():
                        if not isinstance(tv, str) or not tv.strip():
                            errors.append(f"[cardOverrides] {tid}.tooltips[{tk}] 不可为空")
            if "enchantments" in override:
                if not isinstance(override["enchantments"], dict):
                    errors.append(f"[cardOverrides] {tid}.enchantments 必须是字典")
                else:
                    for ek, ev in override["enchantments"].items():
                        if not isinstance(ev, list) or not all(isinstance(x, str) and x.strip() for x in ev):
                            errors.append(f"[cardOverrides] {tid}.enchantments[{ek}] 必须是非空字符串数组")

        # 计算穿透活跃引用 GUID
        guid_re = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.I)
        referenced_ids: set[str] = set()
        for _, blob in conn.execute("SELECT Id, Data FROM monsters"):
            for gid in guid_re.findall(blob.decode("utf-8", errors="ignore")):
                referenced_ids.add(gid.lower())
        for _, blob in conn.execute("SELECT Id, Data FROM level_ups"):
            for gid in guid_re.findall(blob.decode("utf-8", errors="ignore")):
                referenced_ids.add(gid.lower())
        for cid, c in cards.items():
            if c.get("SpawningEligibility", "Never") != "Never":
                raw = json.dumps(c)
                for gid in guid_re.findall(raw):
                    if gid.lower() != cid.lower():
                        referenced_ids.add(gid.lower())

        all_missing: set[str] = set()
        wl_missing: set[str] = set()
        active_missing: set[str] = set()

        def record_text(text: Any, in_wl: bool, is_active: bool) -> None:
            if not isinstance(text, str) or not text.strip():
                return
            h = md5_utf8(text)
            if h in zh_hashes:
                return
            all_missing.add(h)
            if in_wl:
                wl_missing.add(h)
            if is_active:
                active_missing.add(h)

        for cid, c in cards.items():
            in_wl = cid in wl_ids
            is_active = (c.get("SpawningEligibility", "Never") != "Never") or (cid.lower() in referenced_ids)
            loc = c.get("Localization") or {}
            if isinstance(loc.get("Title"), dict):
                record_text(loc["Title"].get("Text"), in_wl, is_active)
            if isinstance(loc.get("Description"), dict):
                record_text(loc["Description"].get("Text"), in_wl, is_active)
            for tt in loc.get("Tooltips") or []:
                if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                    record_text(tt["Content"].get("Text"), in_wl, is_active)
            for ev in (c.get("Enchantments") or {}).values():
                if isinstance(ev, dict):
                    for tt in (ev.get("Localization") or {}).get("Tooltips") or []:
                        if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                            record_text(tt["Content"].get("Text"), in_wl, is_active)
            for q in c.get("Quests") or []:
                if isinstance(q, dict):
                    for entry in q.get("Entries") or []:
                        if isinstance(entry, dict):
                            for tt in entry.get("Tooltips") or []:
                                if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                                    record_text(tt["Content"].get("Text"), in_wl, is_active)
                            for tt in (entry.get("Localization") or {}).get("Tooltips") or []:
                                if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                                    record_text(tt["Content"].get("Text"), in_wl, is_active)
                    if isinstance(q.get("Reward"), dict):
                        for tt in q["Reward"].get("Tooltips") or []:
                            if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                                record_text(tt["Content"].get("Text"), in_wl, is_active)

        for _, blob in conn.execute("SELECT Id, Data FROM challenges"):
            ch = json.loads(blob.decode("utf-8"))
            loc = ch.get("Localization") or {}
            is_act = bool(ch.get("IsActive"))
            if isinstance(loc.get("Title"), dict):
                record_text(loc["Title"].get("Text"), True, is_act)
            if isinstance(loc.get("Description"), dict):
                record_text(loc["Description"].get("Text"), True, is_act)

        for hero in ("Common", "Hero8", "The Dragons"):
            record_text(hero, True, True)

        return {
            "dbTotalMissing": len(all_missing),
            "dbTotalCovered": sum(1 for h in all_missing if h in overlay_hashes),
            "whitelistAndChallengesMissing": len(wl_missing),
            "whitelistAndChallengesCovered": sum(1 for h in wl_missing if h in overlay_hashes),
            "activeVisibleMissing": len(active_missing),
            "activeVisibleCovered": sum(1 for h in active_missing if h in overlay_hashes),
            "remainingWhitelistUncovered": sorted(h for h in wl_missing if h not in overlay_hashes),
        }
    finally:
        conn.close()


def build_overlay(
    catalog: dict[str, Any],
    zh_hashes: set[str],
    db_path: pathlib.Path | None,
    whitelist_path: pathlib.Path,
    revision: str | None = None,
) -> tuple[dict[str, Any], dict[str, Any], list[str]]:
    """根据翻译源表编译最终四层外挂词典，并执行全量质量自检。"""
    errors: list[str] = []

    db_texts: dict[str, str] = catalog.get("dbTexts") or {}
    ui_exact: dict[str, str] = catalog.get("uiExactOverrides") or {}
    ui_contexts: dict[str, dict[str, str]] = catalog.get("uiContextOverrides") or {}
    ui_regex: list[dict[str, Any]] = catalog.get("uiRegexOverrides") or []
    card_overrides: dict[str, Any] = catalog.get("cardOverrides") or {}
    srv_exact: dict[str, str] = (catalog.get("serverMessageOverrides") or {}).get("exact") or {}
    srv_regex: list[dict[str, Any]] = (catalog.get("serverMessageOverrides") or {}).get("regex") or []

    # 1. 校验并构建层 1 hashOverrides（涵盖全部 dbTexts + 全部 uiExactOverrides 中缺失于官方 zh-CN.bytes 的文本）
    hash_overrides: dict[str, str] = {}
    hash_sources: dict[str, str] = {}

    for en, zh in sorted(db_texts.items()):
        validate_pair_fidelity(en, zh, f"dbTexts[{en[:40]}]", errors)
        h = md5_utf8(en)
        hash_overrides[h] = zh
        hash_sources[h] = en

    for en, zh in sorted(ui_exact.items()):
        validate_pair_fidelity(en, zh, f"uiExact[{en[:40]}]", errors)
        h = md5_utf8(en)
        # 若该 UI 文本不在官方 zh-CN.bytes 中（或属于已知需要覆盖的英文残余），同步写入 hashOverrides
        # 以便 LocalizationService.TryGetText(hash, ...) O(1) 极速命中
        if h not in zh_hashes and h not in hash_overrides:
            hash_overrides[h] = zh
            hash_sources[h] = en

    # 2. 校验上下文消歧字典
    for ctx_name, mapping in sorted(ui_contexts.items()):
        if not isinstance(mapping, dict) or not mapping:
            errors.append(f"[uiContextOverrides] {ctx_name} 必须是非空字典")
            continue
        for en, zh in mapping.items():
            validate_pair_fidelity(en, zh, f"uiContext[{ctx_name}:{en}]", errors)

    # 3. 校验全部正则规则
    validate_regex_rules(ui_regex, "uiRegexOverrides", errors)
    validate_regex_rules(srv_regex, "serverMessageOverrides.regex", errors)

    # 4. 校验服务端精确报错词典
    for en, zh in sorted(srv_exact.items()):
        validate_pair_fidelity(en, zh, f"serverExact[{en[:40]}]", errors)

    # 5. 排序保证确定性输出
    sorted_hash_overrides = dict(sorted(hash_overrides.items()))
    sorted_card_overrides = dict(sorted(card_overrides.items()))
    sorted_ui_exact = dict(sorted(ui_exact.items()))
    sorted_ui_contexts = {k: dict(sorted(v.items())) for k, v in sorted(ui_contexts.items())}
    sorted_srv_exact = dict(sorted(srv_exact.items()))

    # 6. 数据库覆盖率与 cardOverrides GUID 核验
    db_cov: dict[str, Any] = {}
    if db_path is not None:
        db_cov = collect_db_coverage(
            db_path=db_path,
            zh_hashes=zh_hashes,
            whitelist_path=whitelist_path,
            overlay_hashes=sorted_hash_overrides,
            card_overrides=sorted_card_overrides,
            errors=errors,
        )

    ctx_entry_total = sum(len(v) for v in sorted_ui_contexts.values())
    entry_counts = {
        "total": (
            len(sorted_hash_overrides)
            + len(sorted_card_overrides)
            + len(sorted_ui_exact)
            + ctx_entry_total
            + len(ui_regex)
            + len(sorted_srv_exact)
            + len(srv_regex)
        ),
        "hashOverrides": len(sorted_hash_overrides),
        "cardOverrides": len(sorted_card_overrides),
        "uiExactOverrides": len(sorted_ui_exact),
        "uiContextOverrides": ctx_entry_total,
        "uiRegexOverrides": len(ui_regex),
        "serverMessageExact": len(sorted_srv_exact),
        "serverMessageRegex": len(srv_regex),
    }

    # 先构建核心内容体并计算确定性 sha256（不含易变时间戳字段，便于 ETag 与增量比对）
    content_body = {
        "hashOverrides": sorted_hash_overrides,
        "cardOverrides": sorted_card_overrides,
        "uiExactOverrides": sorted_ui_exact,
        "uiContextOverrides": sorted_ui_contexts,
        "uiRegexOverrides": ui_regex,
        "serverMessageOverrides": {
            "exact": sorted_srv_exact,
            "regex": srv_regex,
        },
    }
    canonical_bytes = json.dumps(content_body, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
    content_sha256 = hashlib.sha256(canonical_bytes).hexdigest()

    now_utc = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    rev = revision or catalog.get("revision") or dt.datetime.now(dt.timezone.utc).strftime("%Y.%m.%d-01")

    overlay: dict[str, Any] = {
        "$schema": SCHEMA_URL,
        "schemaVersion": int(catalog.get("schemaVersion", 1)),
        "locale": str(catalog.get("locale", "zh-CN")),
        "revision": rev,
        "generatedAt": now_utc,
        "sha256": content_sha256,
        "minGameDataVersion": str(catalog.get("minGameDataVersion", "5.0.0")),
        "priority": str(catalog.get("priority", "override")),
        "entryCount": entry_counts,
        **content_body,
    }

    return overlay, db_cov, errors


def verify_existing_overlay(path: pathlib.Path) -> list[str]:
    """对已生成的 custom_zh_overlay.json 执行独立完整性校验。"""
    errors: list[str] = []
    if not path.is_file():
        return [f"文件不存在: {path}"]
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        return [f"JSON 解析失败: {exc}"]

    for req in (
        "schemaVersion",
        "locale",
        "generatedAt",
        "sha256",
        "entryCount",
        "hashOverrides",
        "cardOverrides",
        "uiExactOverrides",
        "uiRegexOverrides",
        "serverMessageOverrides",
    ):
        if req not in data:
            errors.append(f"缺少必填顶层字段: {req}")

    if errors:
        return errors

    # 校验 hashOverrides 键均为 32 位小写十六进制 MD5 且值非空
    md5_re = re.compile(r"^[0-9a-f]{32}$")
    for k, v in data["hashOverrides"].items():
        if not md5_re.match(k):
            errors.append(f"hashOverrides 非法 MD5 键: {k!r}")
        if not isinstance(v, str) or not v.strip():
            errors.append(f"hashOverrides 空译文: {k}")

    # 校验 sha256 与内容体严格匹配
    content_body = {
        "hashOverrides": data["hashOverrides"],
        "cardOverrides": data["cardOverrides"],
        "uiExactOverrides": data["uiExactOverrides"],
        "uiContextOverrides": data.get("uiContextOverrides", {}),
        "uiRegexOverrides": data["uiRegexOverrides"],
        "serverMessageOverrides": data["serverMessageOverrides"],
    }
    canonical_bytes = json.dumps(content_body, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
    actual_sha = hashlib.sha256(canonical_bytes).hexdigest()
    if actual_sha != data["sha256"]:
        errors.append(f"sha256 校验不匹配: 声明={data['sha256']} 实算={actual_sha}")

    validate_regex_rules(data["uiRegexOverrides"], "uiRegexOverrides", errors)
    validate_regex_rules((data["serverMessageOverrides"] or {}).get("regex", []), "serverMessageOverrides.regex", errors)
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description="构建并自检大巴扎全链路外挂简体中文补齐词典。")
    parser.add_argument("--catalog", type=pathlib.Path, default=DEFAULT_CATALOG, help="源翻译表 JSON 路径")
    parser.add_argument("--tracked-out", type=pathlib.Path, default=DEFAULT_TRACKED_OUT, help="受 Git 追踪的词典输出路径")
    parser.add_argument("--dist-out", type=pathlib.Path, default=DEFAULT_DIST_OUT, help="运行时 dist/data 同步输出路径")
    parser.add_argument("--db", type=pathlib.Path, default=None, help="权威 GameData.db 路径（可选覆盖）")
    parser.add_argument("--zh-bytes", type=pathlib.Path, default=DEFAULT_ZH_BYTES, help="官方 zh-CN.bytes 路径")
    parser.add_argument("--whitelist", type=pathlib.Path, default=DEFAULT_WHITELIST, help="valid_card_ids.json 路径")
    parser.add_argument("--revision", type=str, default=None, help="自定义版本号（如 2026.11.21-01）")
    parser.add_argument("--no-dist", action="store_true", help="仅生成受 Git 追踪的词典，不同步写 dist/data")
    parser.add_argument("--verify-only", action="store_true", help="仅自检现有输出词典与源表，不写文件")
    args = parser.parse_args()

    if not args.catalog.is_file():
        print(f"[错误] 未找到源翻译表: {args.catalog}", file=sys.stderr)
        return 2

    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    zh_hashes = load_zh_bytes_hashes(args.zh_bytes)
    db_path = resolve_optional_db(args.db)

    overlay, db_cov, errors = build_overlay(
        catalog=catalog,
        zh_hashes=zh_hashes,
        db_path=db_path,
        whitelist_path=args.whitelist,
        revision=args.revision,
    )

    if errors:
        print(f"[build_zh_overlay] 自检发现 {len(errors)} 处错误：", file=sys.stderr)
        for err in errors[:50]:
            print(f"  - {err}", file=sys.stderr)
        if len(errors) > 50:
            print(f"  ... 其余 {len(errors) - 50} 处错误已省略", file=sys.stderr)
        return 1

    if not args.verify_only:
        formatted = json.dumps(overlay, ensure_ascii=False, indent=2) + "\n"
        atomic_write_text(args.tracked_out, formatted)
        print(f"[build_zh_overlay] 已写入受追踪词典: {args.tracked_out} ({len(formatted.encode('utf-8')):,} B)")
        if not args.no_dist:
            atomic_write_text(args.dist_out, formatted)
            print(f"[build_zh_overlay] 已同步运行词典  : {args.dist_out}")

    # 回读核验
    verify_targets = [args.tracked_out]
    if not args.no_dist and not args.verify_only:
        verify_targets.append(args.dist_out)
    for vt in verify_targets:
        v_errs = verify_existing_overlay(vt)
        if v_errs:
            print(f"[build_zh_overlay] 回读校验失败 ({vt}):", file=sys.stderr)
            for e in v_errs:
                print(f"  - {e}", file=sys.stderr)
            return 1

    ec = overlay["entryCount"]
    print(f"[build_zh_overlay] 自检 100% 通过！sha256={overlay['sha256'][:16]}...")
    print(
        f"  词典规模: 总计={ec['total']} | hashOverrides={ec['hashOverrides']} | "
        f"cardOverrides={ec['cardOverrides']} | uiExact={ec['uiExactOverrides']} | "
        f"uiContexts={ec['uiContextOverrides']} | uiRegex={ec['uiRegexOverrides']} | "
        f"serverExact={ec['serverMessageExact']} | serverRegex={ec['serverMessageRegex']}"
    )
    if db_cov:
        act_pct = (100.0 * db_cov["activeVisibleCovered"] / db_cov["activeVisibleMissing"]) if db_cov["activeVisibleMissing"] else 100.0
        wl_pct = (100.0 * db_cov["whitelistAndChallengesCovered"] / db_cov["whitelistAndChallengesMissing"]) if db_cov["whitelistAndChallengesMissing"] else 100.0
        all_pct = (100.0 * db_cov["dbTotalCovered"] / db_cov["dbTotalMissing"]) if db_cov["dbTotalMissing"] else 100.0
        print(
            f"  DB 缺口覆盖: 活跃可见={db_cov['activeVisibleCovered']}/{db_cov['activeVisibleMissing']} ({act_pct:.1f}%) | "
            f"白名单+挑战={db_cov['whitelistAndChallengesCovered']}/{db_cov['whitelistAndChallengesMissing']} ({wl_pct:.1f}%) | "
            f"全库总计={db_cov['dbTotalCovered']}/{db_cov['dbTotalMissing']} ({all_pct:.1f}%)"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
