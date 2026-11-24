#!/usr/bin/env python3
"""
大巴扎官方 Steam 更新五阶段离线兼容体检工具（tools/compat_doctor.py）。

基于 Wave 1 审计规范（docs/audit/wave1_l10n_mod_compat.md 第 3.2 节）实现，零依赖 Unity 运行时，
可在官方 Steam 客户端任意小补丁发布后 10 秒内定位全部断裂点与增量缺口：

  - Stage 1：GameData.db（--db 对比 --baseline-db）8 表 Schema、$type 集合、英雄枚举，
             以及卡牌/技能/野怪/升级/模式/赛季/挑战/藏品行级差异审计；
  - Stage 2：解析 Steam Addressables catalog.bin，核验 valid_card_ids.json 与
             valid_collectible_ids.json，自动报出新增/失效资产与硬编码 Key 存活率；
  - Stage 3：扫描 GameData.db vs zh-CN.bytes + custom_zh_overlay.json 剩余未汉化缺口，
             按 P0（活跃可见）/ P1（白名单+挑战）/ P2（非白名单）分级并导出待翻 JSON 模板；
  - Stage 4：自动静态扫描 src/BazaarLauncher/** 全部 Harmony 钩子与反射私有字段，调用本机
             PowerShell + Mono.Cecil 直读 live Steam Managed/*.dll 核验钩子存活率、抓出
             死补丁与多重载签名漂移，并比对 ext/ 与 live Steam 的 MessagePack DTO 契约；
  - Stage 5：聚合输出 0-100 健康评分、红/黄/绿状态、结构化 JSON 报告与简体中文 Markdown 报告。
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
import struct
import subprocess
import sys
import tempfile
from collections import Counter, defaultdict
from typing import Any

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
MAIN_REPO_FALLBACK = pathlib.Path(r".")
DEFAULT_STEAM_DIR = pathlib.Path(r"<THE_BAZAAR_GAME_DIR>")

CATALOG_MAGIC_V2 = 0x0DE38942
TARGET_CARD_TYPES = (
    "Item",
    "Skill",
    "EventEncounter",
    "EncounterStep",
    "PedestalEncounter",
    "CombatEncounter",
)
CRITICAL_ADDRESSABLES_KEYS = ("Hero_DRA", "Skin_DRA_01/A")
GUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.I)

# 核心 P0 模块（断裂直接阻断启动/登录/战斗/关键状态机）
P0_TYPE_KEYWORDS = (
    "TheBazaar.Config",
    "SteamModule",
    "SteamAuthService",
    "CollectionManager",
    "HeroManager",
    "CombatState",
    "CombatSimHandler",
    "GameSimHandler",
    "BoardManager",
    "BoardStatePresentation",
    "PedestalState",
    "EncounterController",
    "TTargetCardXMost",
)


def first_existing(*paths: pathlib.Path | None) -> pathlib.Path | None:
    for p in paths:
        if p and p.exists():
            return p
    return None


def md5_utf8(text: str) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def atomic_write_text(path: pathlib.Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(content, encoding="utf-8", newline="\n")
    os.replace(tmp, path)


# ============================================================================
# Stage 1: GameData.db 表结构、$type、卡牌/技能/野怪/升级/模式差异审计
# ============================================================================

def read_db_snapshot(db_path: pathlib.Path) -> dict[str, Any]:
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        tables: dict[str, dict[str, Any]] = {}
        for name, sql in conn.execute("SELECT name, sql FROM sqlite_master WHERE type='table' ORDER BY name"):
            cols = [
                {"name": row[1], "type": row[2], "notnull": bool(row[3]), "pk": bool(row[5])}
                for row in conn.execute(f'PRAGMA table_info("{name}")')
            ]
            cnt = conn.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
            tables[name] = {"sql": (sql or "").strip(), "columns": cols, "count": cnt}

        def load_kv(table_name: str) -> dict[str, bytes]:
            if table_name not in tables:
                return {}
            return {str(r[0]): r[1] for r in conn.execute(f'SELECT Id, Data FROM "{table_name}"')}

        return {
            "path": str(db_path),
            "size": db_path.stat().st_size,
            "tables": tables,
            "cards": load_kv("cards"),
            "monsters": load_kv("monsters"),
            "level_ups": load_kv("level_ups"),
            "game_modes": load_kv("game_modes"),
            "seasons": load_kv("seasons"),
            "challenges": load_kv("challenges"),
            "collectibles": load_kv("collectibles"),
            "tooltips": load_kv("tooltips"),
        }
    finally:
        conn.close()


def collect_dollar_types(obj: Any, out: set[str]) -> None:
    if isinstance(obj, dict):
        t = obj.get("$type")
        if isinstance(t, str) and t:
            out.add(t)
        for v in obj.values():
            if isinstance(v, (dict, list)):
                collect_dollar_types(v, out)
    elif isinstance(obj, list):
        for item in obj:
            if isinstance(item, (dict, list)):
                collect_dollar_types(item, out)


def run_stage1_db_diff(db_path: pathlib.Path, baseline_db_path: pathlib.Path | None) -> dict[str, Any]:
    cur = read_db_snapshot(db_path)
    cur_cards_parsed = {cid: json.loads(b.decode("utf-8")) for cid, b in cur["cards"].items()}
    cur_types_by_card = Counter(c.get("Type") or "Unknown" for c in cur_cards_parsed.values())

    cur_dollar_types: set[str] = set()
    cur_heroes: set[str] = set()
    for c in cur_cards_parsed.values():
        collect_dollar_types(c, cur_dollar_types)
        for h in c.get("Heroes") or []:
            if isinstance(h, str) and h:
                cur_heroes.add(h)
    for tbl in ("game_modes", "level_ups", "seasons"):
        for b in cur[tbl].values():
            collect_dollar_types(json.loads(b.decode("utf-8")), cur_dollar_types)

    # 权威基础健壮性断言（无 baseline 时亦能独立发现数据异常）
    cur_gm = json.loads(next(iter(cur["game_modes"].values())).decode("utf-8")) if cur["game_modes"] else {}
    sp_buy = ((cur_gm.get("StandardPrices") or {}).get("ItemBuyPrices") or {})
    lv_hps = {}
    for lid, b in cur["level_ups"].items():
        obj = json.loads(b.decode("utf-8"))
        lv_hps[int(obj.get("Level", lid))] = obj.get("HealthIncrease")

    result: dict[str, Any] = {
        "dbPath": str(db_path),
        "dbSizeBytes": cur["size"],
        "baselineDbPath": str(baseline_db_path) if baseline_db_path else None,
        "tableCounts": {k: v["count"] for k, v in cur["tables"].items()},
        "cardTypeCounts": dict(sorted(cur_types_by_card.items())),
        "dollarTypeCount": len(cur_dollar_types),
        "heroes": sorted(cur_heroes),
        "standardPricesLegendary": {
            "SmallBuy": (sp_buy.get("Small") or {}).get("Legendary"),
            "MediumBuy": (sp_buy.get("Medium") or {}).get("Legendary"),
            "LargeBuy": (sp_buy.get("Large") or {}).get("Legendary"),
        },
        "levelUpLv11To30Uniform800": all(lv_hps.get(lv) == 800 for lv in range(11, 31)),
        "schemaDrift": [],
        "newDollarTypes": [],
        "removedDollarTypes": [],
        "newHeroes": [],
        "tableDiffs": {},
        "cardDiffsByType": {},
        "monsterHpChanges": [],
        "levelUpChanges": [],
        "gameModeChangedFields": [],
        "status": "GREEN",
    }

    if baseline_db_path is None:
        result["note"] = "未提供 --baseline-db，仅执行当前数据库结构与完整性快照核验。"
        return result

    base = read_db_snapshot(baseline_db_path)

    # 1) Schema 对比
    all_tables = sorted(set(cur["tables"].keys()) | set(base["tables"].keys()))
    for tname in all_tables:
        if tname not in base["tables"]:
            result["schemaDrift"].append(f"新增表: {tname}")
        elif tname not in cur["tables"]:
            result["schemaDrift"].append(f"删除表: {tname}")
        else:
            if cur["tables"][tname]["columns"] != base["tables"][tname]["columns"]:
                result["schemaDrift"].append(f"表列定义变更: {tname}")

    # 2) $type 与 Hero 对比
    base_cards_parsed = {cid: json.loads(b.decode("utf-8")) for cid, b in base["cards"].items()}
    base_dollar_types: set[str] = set()
    base_heroes: set[str] = set()
    for c in base_cards_parsed.values():
        collect_dollar_types(c, base_dollar_types)
        for h in c.get("Heroes") or []:
            if isinstance(h, str) and h:
                base_heroes.add(h)
    for tbl in ("game_modes", "level_ups", "seasons"):
        for b in base[tbl].values():
            collect_dollar_types(json.loads(b.decode("utf-8")), base_dollar_types)

    result["newDollarTypes"] = sorted(cur_dollar_types - base_dollar_types)
    result["removedDollarTypes"] = sorted(base_dollar_types - cur_dollar_types)
    result["newHeroes"] = sorted(cur_heroes - base_heroes)

    # 交叉检查新增的 $type 是否已在 src/BazaarCore/ 中实现，区分“真正未覆盖的新 $type”与“已跟进实现的基线增量”
    core_dir = REPO_ROOT / "src" / "BazaarCore"
    core_corpus = ""
    if core_dir.is_dir():
        core_corpus = "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in core_dir.rglob("*.cs"))
    result["unimplementedNewDollarTypes"] = [
        t for t in result["newDollarTypes"] if core_corpus and (f'"{t}"' not in core_corpus and t not in core_corpus)
    ]

    # 3) 8 表行级增删改统计
    for tbl in ("cards", "monsters", "level_ups", "game_modes", "seasons", "challenges", "collectibles", "tooltips"):
        c_map, b_map = cur[tbl], base[tbl]
        added = sorted(set(c_map.keys()) - set(b_map.keys()))
        removed = sorted(set(b_map.keys()) - set(c_map.keys()))
        changed = sorted(k for k in (set(c_map.keys()) & set(b_map.keys())) if c_map[k] != b_map[k])
        result["tableDiffs"][tbl] = {
            "baseline": len(b_map),
            "current": len(c_map),
            "added": len(added),
            "removed": len(removed),
            "changed": len(changed),
            "addedSample": added[:10],
            "removedSample": removed[:10],
        }

    # 4) 卡牌按 Type 细分差异
    by_type = defaultdict(lambda: {"added": 0, "removed": 0, "changed": 0})
    for cid in set(cur_cards_parsed.keys()) - set(base_cards_parsed.keys()):
        by_type[cur_cards_parsed[cid].get("Type") or "Unknown"]["added"] += 1
    for cid in set(base_cards_parsed.keys()) - set(cur_cards_parsed.keys()):
        by_type[base_cards_parsed[cid].get("Type") or "Unknown"]["removed"] += 1
    for cid in set(cur_cards_parsed.keys()) & set(base_cards_parsed.keys()):
        if cur["cards"][cid] != base["cards"][cid]:
            by_type[cur_cards_parsed[cid].get("Type") or "Unknown"]["changed"] += 1
    result["cardDiffsByType"] = dict(sorted(by_type.items()))

    # 5) 野怪血量变化采样
    for mid in sorted(set(cur["monsters"].keys()) & set(base["monsters"].keys())):
        if cur["monsters"][mid] != base["monsters"][mid]:
            cm = json.loads(cur["monsters"][mid].decode("utf-8"))
            bm = json.loads(base["monsters"][mid].decode("utf-8"))
            chp = ((cm.get("Player") or {}).get("Attributes") or {}).get("HealthMax")
            bhp = ((bm.get("Player") or {}).get("Attributes") or {}).get("HealthMax")
            if chp != bhp:
                result["monsterHpChanges"].append({
                    "id": mid,
                    "name": cm.get("InternalName", ""),
                    "baselineHp": bhp,
                    "currentHp": chp,
                })

    # 6) 升级表差异
    for lid in sorted(set(cur["level_ups"].keys()) & set(base["level_ups"].keys()), key=lambda x: int(x)):
        if cur["level_ups"][lid] != base["level_ups"][lid]:
            cl = json.loads(cur["level_ups"][lid].decode("utf-8"))
            bl = json.loads(base["level_ups"][lid].decode("utf-8"))
            result["levelUpChanges"].append({
                "level": int(cl.get("Level", lid)),
                "hpChanged": cl.get("HealthIncrease") != bl.get("HealthIncrease"),
                "rewardsChanged": cl.get("Rewards") != bl.get("Rewards"),
            })

    # 7) game_modes 顶层字段差异
    if cur["game_modes"] and base["game_modes"]:
        base_gm = json.loads(next(iter(base["game_modes"].values())).decode("utf-8"))
        for key in sorted(set(cur_gm.keys()) | set(base_gm.keys())):
            if cur_gm.get(key) != base_gm.get(key):
                result["gameModeChangedFields"].append(key)

    if result["schemaDrift"] or result["unimplementedNewDollarTypes"]:
        result["status"] = "RED"
    elif result["newDollarTypes"] or result["newHeroes"] or result["tableDiffs"]["cards"]["added"] > 0 or result["tableDiffs"]["monsters"]["added"] > 0:
        result["status"] = "YELLOW"
    return result


# ============================================================================
# Stage 2: Steam Addressables catalog.bin vs valid_card_ids / valid_collectible_ids
# ============================================================================

def parse_catalog_bin(catalog_path: pathlib.Path) -> tuple[bytes, int, set[str]]:
    raw = catalog_path.read_bytes()
    data_start = 0
    if len(raw) >= 64:
        hdr = struct.unpack_from("<16I", raw, 0)
        if hdr[0] == CATALOG_MAGIC_V2 and hdr[1] == 2 and hdr[7] < len(raw):
            data_start = hdr[7]

    extracted: set[str] = set()
    if data_start > 36:
        hdr = struct.unpack_from("<16I", raw, 0)
        table_start, table_end = hdr[2], min(hdr[8], len(raw))
        for off in range(table_start, table_end, 4):
            val_off = struct.unpack_from("<I", raw, off)[0]
            if val_off + 8 <= len(raw):
                _, slen = struct.unpack_from("<2I", raw, val_off)
                if 1 <= slen <= 256 and val_off + 8 + slen <= len(raw):
                    chunk = raw[val_off + 8 : val_off + 8 + slen]
                    try:
                        s = chunk.decode("utf-8")
                        if all(32 <= ord(ch) < 127 for ch in s):
                            extracted.add(s)
                    except UnicodeDecodeError:
                        pass
    return raw, data_start, extracted


def catalog_has_key(raw: bytes, data_start: int, extracted: set[str], key: str) -> bool:
    if not key or key.lower() in ("invalid", "placeholder", "none"):
        return False
    if key in extracted:
        return True
    enc = key.encode("utf-8")
    needle = struct.pack("<I", len(enc)) + enc
    if raw.find(needle, data_start) >= 0:
        return True
    return raw.find(needle) >= 0


def run_stage2_asset_audit(
    db_path: pathlib.Path,
    catalog_path: pathlib.Path | None,
    valid_cards_path: pathlib.Path | None,
    valid_collectibles_path: pathlib.Path | None,
) -> dict[str, Any]:
    if not catalog_path or not catalog_path.is_file():
        return {
            "status": "YELLOW",
            "skipped": True,
            "reason": f"未找到 Steam Addressables catalog.bin: {catalog_path}",
        }

    raw, data_start, extracted = parse_catalog_bin(catalog_path)
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        db_cards = {cid: json.loads(b.decode("utf-8")) for cid, b in conn.execute("SELECT Id, Data FROM cards")}
        db_collectibles: dict[str, dict[str, Any]] = {}
        for ctype_key, blob in conn.execute("SELECT Id, Data FROM collectibles"):
            arr = json.loads(blob.decode("utf-8"))
            if isinstance(arr, list):
                for item in arr:
                    if isinstance(item, dict) and item.get("Id"):
                        item["_tableType"] = ctype_key
                        db_collectibles[item["Id"]] = item
    finally:
        conn.close()

    live_valid_cards: set[str] = set()
    live_invalid_cards: list[dict[str, str]] = []
    for cid, card in db_cards.items():
        ctype = card.get("Type") or ""
        if ctype not in TARGET_CARD_TYPES:
            continue
        art_key = (card.get("ArtKey") or "").strip()
        ok = catalog_has_key(raw, data_start, extracted, art_key)
        reason = "ok" if ok else (f"missing_art_key:{art_key}" if art_key else "empty_art_key")
        if ok:
            for extra in ("BoardOverride", "Presentation"):
                ev = (card.get(extra) or "").strip()
                if ev and not catalog_has_key(raw, data_start, extracted, ev):
                    ok = False
                    reason = f"missing_{extra}:{ev}"
                    break
        if ok:
            live_valid_cards.add(cid)
        else:
            live_invalid_cards.append({
                "id": cid,
                "type": ctype,
                "internalName": card.get("InternalName") or "",
                "spawning": card.get("SpawningEligibility") or "",
                "reason": reason,
            })

    wl_card_ids: set[str] = set()
    if valid_cards_path and valid_cards_path.is_file():
        wl_card_ids = set(json.loads(valid_cards_path.read_text(encoding="utf-8")).get("valid_ids", []))

    new_valid_not_in_wl = sorted(live_valid_cards - wl_card_ids)
    stale_wl_ids_missing_in_db = sorted(wl_card_ids - set(db_cards.keys()))
    invalid_in_wl = sorted((wl_card_ids & set(db_cards.keys())) - live_valid_cards)

    # 关键硬编码 Addressables Key 存活核验
    critical_keys = {k: catalog_has_key(raw, data_start, extracted, k) for k in CRITICAL_ADDRESSABLES_KEYS}

    # 藏品白名单校验
    wl_coll_ids: set[str] = set()
    if valid_collectibles_path and valid_collectibles_path.is_file():
        coll_json = json.loads(valid_collectibles_path.read_text(encoding="utf-8"))
        if isinstance(coll_json, list):
            wl_coll_ids = {str(x["id"]) for x in coll_json if isinstance(x, dict) and x.get("id")}
        elif isinstance(coll_json, dict):
            wl_coll_ids = set(coll_json.get("valid_ids", []))

    coll_in_db_not_wl = sorted(set(db_collectibles.keys()) - wl_coll_ids)
    coll_stale_in_wl = sorted(wl_coll_ids - set(db_collectibles.keys()))

    status = "GREEN"
    if not all(critical_keys.values()) or invalid_in_wl:
        status = "RED"
    elif new_valid_not_in_wl or stale_wl_ids_missing_in_db or coll_stale_in_wl or coll_in_db_not_wl:
        status = "YELLOW"

    return {
        "status": status,
        "skipped": False,
        "catalogPath": str(catalog_path),
        "catalogSizeBytes": len(raw),
        "catalogExtractedKeys": len(extracted),
        "criticalKeys": critical_keys,
        "cards": {
            "dbChecked": len(live_valid_cards) + len(live_invalid_cards),
            "liveValid": len(live_valid_cards),
            "liveMissingAssets": len(live_invalid_cards),
            "whitelistTotal": len(wl_card_ids),
            "newValidNotInWhitelistCount": len(new_valid_not_in_wl),
            "newValidNotInWhitelistSample": new_valid_not_in_wl[:15],
            "staleWhitelistNotInDbCount": len(stale_wl_ids_missing_in_db),
            "staleWhitelistNotInDbSample": stale_wl_ids_missing_in_db[:15],
            "whitelistBrokenInCatalogCount": len(invalid_in_wl),
            "whitelistBrokenInCatalogSample": invalid_in_wl[:15],
            "activeMissingAssets": [x for x in live_invalid_cards if x["spawning"] != "Never"][:15],
        },
        "collectibles": {
            "dbTotal": len(db_collectibles),
            "whitelistTotal": len(wl_coll_ids),
            "dbNotInWhitelistCount": len(coll_in_db_not_wl),
            "dbNotInWhitelistSample": coll_in_db_not_wl[:10],
            "staleWhitelistNotInDbCount": len(coll_stale_in_wl),
            "staleWhitelistNotInDbSample": coll_stale_in_wl[:10],
        },
    }


# ============================================================================
# Stage 3: GameData.db vs zh-CN.bytes + custom_zh_overlay.json 未汉化缺口审计
# ============================================================================

def run_stage3_l10n_diff(
    db_path: pathlib.Path,
    zh_bytes_path: pathlib.Path | None,
    overlay_path: pathlib.Path | None,
    valid_cards_path: pathlib.Path | None,
    todo_out_path: pathlib.Path,
) -> dict[str, Any]:
    zh_hashes: set[str] = set()
    if zh_bytes_path and zh_bytes_path.is_file():
        zconn = sqlite3.connect(f"file:{zh_bytes_path}?mode=ro", uri=True)
        try:
            zh_hashes = {row[0] for row in zconn.execute("SELECT hash FROM translation")}
        finally:
            zconn.close()

    overlay_hashes: set[str] = set()
    overlay_meta: dict[str, Any] = {}
    if overlay_path and overlay_path.is_file():
        ov = json.loads(overlay_path.read_text(encoding="utf-8"))
        overlay_hashes = set((ov.get("hashOverrides") or {}).keys())
        overlay_meta = {
            "path": str(overlay_path),
            "revision": ov.get("revision"),
            "sha256": ov.get("sha256"),
            "entryCount": ov.get("entryCount"),
        }

    wl_ids: set[str] = set()
    if valid_cards_path and valid_cards_path.is_file():
        wl_ids = set(json.loads(valid_cards_path.read_text(encoding="utf-8")).get("valid_ids", []))

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        cards = {cid: json.loads(b.decode("utf-8")) for cid, b in conn.execute("SELECT Id, Data FROM cards")}
        referenced_ids: set[str] = set()
        for _, b in conn.execute("SELECT Id, Data FROM monsters"):
            for g in GUID_RE.findall(b.decode("utf-8", errors="ignore")):
                referenced_ids.add(g.lower())
        for _, b in conn.execute("SELECT Id, Data FROM level_ups"):
            for g in GUID_RE.findall(b.decode("utf-8", errors="ignore")):
                referenced_ids.add(g.lower())
        for cid, c in cards.items():
            if c.get("SpawningEligibility", "Never") != "Never":
                for g in GUID_RE.findall(json.dumps(c)):
                    if g.lower() != cid.lower():
                        referenced_ids.add(g.lower())

        missing_official: dict[str, dict[str, Any]] = {}

        def record(text: Any, kind: str, cid: str, ctype: str, in_wl: bool, is_active: bool) -> None:
            if not isinstance(text, str) or not text.strip():
                return
            h = md5_utf8(text)
            if h in zh_hashes:
                return
            rec = missing_official.get(h)
            if rec is None:
                rec = {
                    "hash": h,
                    "text": text,
                    "kinds": set(),
                    "types": set(),
                    "sampleCardIds": [],
                    "inWhitelist": False,
                    "activeVisible": False,
                }
                missing_official[h] = rec
            rec["kinds"].add(kind)
            if ctype:
                rec["types"].add(ctype)
            if cid and len(rec["sampleCardIds"]) < 3 and cid not in rec["sampleCardIds"]:
                rec["sampleCardIds"].append(cid)
            if in_wl:
                rec["inWhitelist"] = True
            if is_active:
                rec["activeVisible"] = True

        for cid, c in cards.items():
            ctype = c.get("Type") or ""
            in_wl = cid in wl_ids
            is_act = (c.get("SpawningEligibility", "Never") != "Never") or (cid.lower() in referenced_ids)
            loc = c.get("Localization") or {}
            if isinstance(loc.get("Title"), dict):
                record(loc["Title"].get("Text"), "title", cid, ctype, in_wl, is_act)
            if isinstance(loc.get("Description"), dict):
                record(loc["Description"].get("Text"), "description", cid, ctype, in_wl, is_act)
            for tt in loc.get("Tooltips") or []:
                if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                    record(tt["Content"].get("Text"), "tooltip", cid, ctype, in_wl, is_act)
            for ek, ev in (c.get("Enchantments") or {}).items():
                if isinstance(ev, dict):
                    for tt in (ev.get("Localization") or {}).get("Tooltips") or []:
                        if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                            record(tt["Content"].get("Text"), f"enchant:{ek}", cid, ctype, in_wl, is_act)
            for q in c.get("Quests") or []:
                if isinstance(q, dict):
                    for entry in q.get("Entries") or []:
                        if isinstance(entry, dict):
                            for tt in (entry.get("Tooltips") or []) + ((entry.get("Localization") or {}).get("Tooltips") or []):
                                if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                                    record(tt["Content"].get("Text"), "quest", cid, ctype, in_wl, is_act)
                    if isinstance(q.get("Reward"), dict):
                        for tt in q["Reward"].get("Tooltips") or []:
                            if isinstance(tt, dict) and isinstance(tt.get("Content"), dict):
                                record(tt["Content"].get("Text"), "quest_reward", cid, ctype, in_wl, is_act)

        for chid, b in conn.execute("SELECT Id, Data FROM challenges"):
            ch = json.loads(b.decode("utf-8"))
            loc = ch.get("Localization") or {}
            is_act = bool(ch.get("IsActive"))
            if isinstance(loc.get("Title"), dict):
                record(loc["Title"].get("Text"), "challenge_title", chid, "Challenge", True, is_act)
            if isinstance(loc.get("Description"), dict):
                record(loc["Description"].get("Text"), "challenge_desc", chid, "Challenge", True, is_act)

        for hero in ("Common", "Hero8", "The Dragons"):
            record(hero, "hero_enum", "", "Hero", True, True)
    finally:
        conn.close()

    rem_p0, rem_p1, rem_p2 = [], [], []
    for h, rec in sorted(missing_official.items(), key=lambda kv: kv[1]["text"]):
        if h in overlay_hashes:
            continue
        item = {
            "hash": h,
            "en": rec["text"],
            "zh": "",
            "types": sorted(rec["types"]),
            "kinds": sorted(rec["kinds"]),
            "sampleCardIds": rec["sampleCardIds"],
        }
        if rec["activeVisible"]:
            rem_p0.append(item)
        elif rec["inWhitelist"]:
            rem_p1.append(item)
        else:
            rem_p2.append(item)

    todo_payload = {
        "generatedAt": dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "dbPath": str(db_path),
        "remainingCount": len(rem_p0) + len(rem_p1) + len(rem_p2),
        "P0_activeVisible": rem_p0,
        "P1_whitelistAndChallenges": rem_p1,
        "P2_nonWhitelist": rem_p2,
    }
    atomic_write_text(todo_out_path, json.dumps(todo_payload, ensure_ascii=False, indent=2) + "\n")

    base_total = len(missing_official)
    base_active = sum(1 for r in missing_official.values() if r["activeVisible"])
    base_wl = sum(1 for r in missing_official.values() if r["inWhitelist"])
    rem_total = len(rem_p0) + len(rem_p1) + len(rem_p2)

    status = "GREEN"
    if rem_p0:
        status = "RED"
    elif rem_p1 or rem_p2:
        status = "YELLOW"

    return {
        "status": status,
        "zhBytesPath": str(zh_bytes_path) if zh_bytes_path else None,
        "zhBytesEntries": len(zh_hashes),
        "overlay": overlay_meta,
        "todoTemplatePath": str(todo_out_path),
        "beforeOverlay": {
            "totalMissingMd5": base_total,
            "whitelistAndChallengesMissing": base_wl,
            "activeVisibleMissing": base_active,
        },
        "afterOverlay": {
            "remainingTotal": rem_total,
            "remainingP0Active": len(rem_p0),
            "remainingP1Whitelist": len(rem_p1),
            "remainingP2NonWhitelist": len(rem_p2),
            "activeCoveragePct": round(100.0 * (base_active - len(rem_p0)) / base_active, 2) if base_active else 100.0,
            "whitelistCoveragePct": round(100.0 * (base_wl - len(rem_p0) - len(rem_p1)) / base_wl, 2) if base_wl else 100.0,
            "totalCoveragePct": round(100.0 * (base_total - rem_total) / base_total, 2) if base_total else 100.0,
        },
    }


# ============================================================================
# Stage 4: 静态扫描 BazaarLauncher Harmony 钩子 + Mono.Cecil 实机 DLL 符号与 DTO 核验
# ============================================================================

def classify_hook_risk(type_name: str, file_name: str, is_prefix_shortcircuit: bool) -> str:
    if any(k in type_name for k in P0_TYPE_KEYWORDS):
        return "P0"
    if file_name in ("BazaarLauncherPlugin.cs", "NoSteam.cs", "NameAuth.cs"):
        return "P0" if is_prefix_shortcircuit else "P1"
    if file_name in ("CombatPatch.cs", "CompatAuditPatch.cs", "Unlocker.cs"):
        return "P1"
    return "P2"


def scan_launcher_hooks_and_fields(launcher_dir: pathlib.Path) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """静态扫描 src/BazaarLauncher/**/*.cs，自动提取全部 Harmony 钩子与关键反射字段。"""
    hooks: list[dict[str, Any]] = []
    fields: list[dict[str, Any]] = []
    seen_hooks: set[tuple[str, str, str, str]] = set()
    seen_fields: set[tuple[str, str, str]] = set()

    # 匹配常见模式
    re_type_assign = re.compile(r'(?:var|Type)\s+(\w+)\s*=\s*AccessTools\.TypeByName\(\s*"([^"]+)"\s*\)')
    re_typeof_assign = re.compile(r'(?:var|Type)\s+(\w+)\s*=\s*typeof\(\s*([A-Za-z0-9_.]+)\s*\)')
    re_method_call = re.compile(
        r'AccessTools\.(Method|PropertyGetter|PropertySetter)\(\s*(\w+)\s*,\s*"([^"]+)"(?:\s*,\s*new\[\]\s*\{([^}]+)\})?'
    )
    re_safe_patch = re.compile(
        r'(?:SafePatch(?:Postfix|Prefix|Finalizer)|Patch)\(\s*\w+\s*,\s*(\w+)\s*,\s*"([^"]+)"'
    )
    re_foreach_arr = re.compile(r'foreach\s*\(\s*(?:var|string)\s+(\w+)\s+in\s+new(?:\s+string)?\[\]\s*\{([^}]+)\}', re.S)
    re_method_var = re.compile(r'AccessTools\.Method\(\s*(\w+)\s*,\s*(\w+)\s*\)')
    re_field_call = re.compile(r'AccessTools\.Field\(\s*(\w+)\s*,\s*"([^"]+)"\s*\)')
    re_getfield_call = re.compile(r'(\w+)\??\.GetField\(\s*"([^"]+)"')
    re_backing_tpl = re.compile(r'"<"\s*\+\s*\w+\s*\+\s*">k__BackingField"')
    re_harmony_attr = re.compile(r'\[HarmonyPatch\(\s*(?:typeof\(([^)]+)\)|"([^"]+)")\s*,\s*"([^"]+)"\s*\)\]')

    def add_hook(fname: str, lineno: int, tname: str, mname: str, kind: str, param_raw: str | None) -> None:
        if not tname or not mname or tname.startswith("BazaarLauncher.") or tname == fname.replace(".cs", ""):
            return
        # 跳过自身内部回调方法
        if tname in ("CombatPatch", "CompatAuditPatch", "Unlocker", "NoSteam", "NameAuth", "CloudGuard", "FrameRateGuard", "TrafficDump", "TournamentLocalizationPatch", "BazaarLauncherPlugin"):
            return
        if kind == "PropertyGetter" and not mname.startswith("get_"):
            mname = "get_" + mname
        elif kind == "PropertySetter" and not mname.startswith("set_"):
            mname = "set_" + mname
        key = (fname, tname, mname, param_raw or "")
        if key in seen_hooks:
            return
        seen_hooks.add(key)
        hooks.append({
            "file": fname,
            "line": lineno,
            "typeName": tname,
            "methodName": mname,
            "kind": kind,
            "hasParamSignature": bool(param_raw),
            "paramSignatureRaw": (param_raw or "").strip(),
            "risk": classify_hook_risk(tname, fname, False),
        })

    def add_field(fname: str, lineno: int, tname: str, field_name: str) -> None:
        if not tname or not field_name:
            return
        key = (fname, tname, field_name)
        if key in seen_fields:
            return
        seen_fields.add(key)
        fields.append({
            "file": fname,
            "line": lineno,
            "typeName": tname,
            "fieldName": field_name,
            "isBackingField": "k__BackingField" in field_name,
        })

    for cs_file in sorted(launcher_dir.rglob("*.cs")):
        fname = cs_file.name
        text = cs_file.read_text(encoding="utf-8", errors="ignore")
        lines = text.splitlines()

        # 维护当前方法内的变量 -> 类型映射
        var_types: dict[str, str] = {}
        loop_arrays: dict[str, tuple[str, list[str]]] = {}  # loopVar -> (typeVarOrEmpty, items)
        recent_type_name = ""

        # 处理特殊已知静态数组辅助（如 BazaarLauncherPlugin.cs 的 5 个 Config URL 属性）
        if fname == "BazaarLauncherPlugin.cs":
            for prop in ("NetURL", "SocketURL", "TournamentURL", "DataURL", "MaintenanceDataURL"):
                if f'"{prop}"' in text:
                    add_field(fname, 145, "TheBazaar.Config", f"<{prop}>k__BackingField")

        # 处理 Unlocker.cs 显式 GetMethods 循环过滤（如 TryGetHeroSO / InitializeAsync / OnOwnedHeroesChanged）
        if fname == "Unlocker.cs":
            for idx, ln in enumerate(lines, 1):
                if 'm.Name == "TryGetHeroSO"' in ln:
                    add_hook(fname, idx, "TheBazaar.HeroManager", "TryGetHeroSO", "Method", None)
                elif 'm.Name == "InitializeAsync"' in ln:
                    add_hook(fname, idx, "TheBazaar.HeroManager", "InitializeAsync", "Method", None)
                elif 'm.Name == "OnOwnedHeroesChanged"' in ln:
                    add_hook(fname, idx, "TheBazaar.HeroManager", "OnOwnedHeroesChanged", "Method", None)

        for idx, line in enumerate(lines, 1):
            stripped = line.strip()
            # 新方法声明时重置局部 var_types，避免跨方法误绑定同名变量 t
            if re.match(r"^(?:private|internal|public)\s+(?:static\s+)?(?:void|bool|int|string|object|Task)\s+\w+\s*\(", stripped):
                var_types.clear()
                loop_arrays.clear()

            # [HarmonyPatch]
            for m in re_harmony_attr.finditer(line):
                tname = m.group(1) or m.group(2)
                add_hook(fname, idx, tname, m.group(3), "HarmonyPatchAttr", None)

            # var x = AccessTools.TypeByName("...")
            for m in re_type_assign.finditer(line):
                var_types[m.group(1)] = m.group(2)
                recent_type_name = m.group(2)
            for m in re_typeof_assign.finditer(line):
                var_types[m.group(1)] = m.group(2)

            # foreach (var x in new[] { "A", "B" }) —— 支持跨多行数组字面量
            if "foreach" in line and "new" in line:
                chunk_for = "\n".join(lines[idx - 1 : min(len(lines), idx + 6)])
                m_for = re_foreach_arr.search(chunk_for)
                if m_for:
                    lvar = m_for.group(1)
                    items = re.findall(r'"([^"]+)"', m_for.group(2))
                    loop_arrays[lvar] = (recent_type_name, items)

            # AccessTools.Method(typeVar, "MethodName", ...) —— 拼合下一行以捕获跨行 new[] { ... }
            ctx_two_lines = line + " " + (lines[idx] if idx < len(lines) else "")
            for m in re_method_call.finditer(ctx_two_lines):
                kind, tvar, mname, params = m.group(1), m.group(2), m.group(3), m.group(4)
                tname = var_types.get(tvar, "")
                # 只记录真正用于 harmony.Patch 或在补丁注册函数里的调用
                lookahead = "\n".join(lines[idx - 1 : min(len(lines), idx + 12)])
                if tname and ("harmony.Patch" in lookahead or "_harmony.Patch" in lookahead or "Patch(" in lookahead):
                    add_hook(fname, idx, tname, mname, kind, params)

            # SafePatchPostfix(harmony, t, "Method", ...) 与 Patch(harmony, t, "Method", ...)
            for m in re_safe_patch.finditer(line):
                tvar, mname = m.group(1), m.group(2)
                tname = var_types.get(tvar, "")
                if tname:
                    add_hook(fname, idx, tname, mname, "SafePatch", None)

            # AccessTools.Method(tVar, loopVar)
            for m in re_method_var.finditer(line):
                tvar, mvar = m.group(1), m.group(2)
                tname = var_types.get(tvar, "")
                if tname and mvar in loop_arrays:
                    lookahead = "\n".join(lines[idx - 1 : min(len(lines), idx + 10)])
                    if "harmony.Patch" in lookahead or "_harmony.Patch" in lookahead:
                        for mname in loop_arrays[mvar][1]:
                            add_hook(fname, idx, tname, mname, "LoopMethod", None)

            # 反射字段提取（若同行或下一行有 AccessTools.Property 回退，则跳过纯字段强断言）
            field_ctx = line + " " + (lines[idx] if idx < len(lines) else "")
            for m in re_field_call.finditer(line):
                tname = var_types.get(m.group(1), "")
                if tname and f'Property({m.group(1)}, "{m.group(2)}")' not in field_ctx:
                    add_field(fname, idx, tname, m.group(2))
            for m in re_getfield_call.finditer(line):
                tname = var_types.get(m.group(1), "")
                if tname:
                    add_field(fname, idx, tname, m.group(2))

        # 单独处理 PatchMethod(harmony, "TypeName", "MethodName", ...) 类辅助函数
        for idx, line in enumerate(lines, 1):
            for m in re.finditer(r'(?:PatchMethod|PatchPrefix|PatchPostfix|PatchGetter|TryPatch)\(\s*harmony\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"', line):
                add_hook(fname, idx, m.group(1), m.group(2), "HelperPatch", None)
            # NoSteam.cs / Unlocker.cs 直接传类型与方法名的帮助调用，如 PatchReturnTrue(harmony, "Type", "Method")
            for m in re.finditer(r'Patch\w*\(\s*harmony\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"', line):
                add_hook(fname, idx, m.group(1), m.group(2), "HelperPatch", None)

    return hooks, fields


CECIL_PS1_SCRIPT = r"""
param(
    [string]$CecilDll,
    [string]$ManagedDir,
    [string]$InputJson,
    [string]$OutputJson,
    [string]$ExtSharedDll
)

$ErrorActionPreference = "Stop"
Add-Type -Path $CecilDll

$spec = Get-Content -Raw -Encoding UTF8 $InputJson | ConvertFrom-Json

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($ManagedDir)
$readerParams = New-Object Mono.Cecil.ReaderParameters
$readerParams.AssemblyResolver = $resolver
$readerParams.ReadSymbols = $false

# 优先加载游戏与 Unity 核心程序集
$targetDllNames = @(
    "TheBazaarRuntime.dll",
    "BazaarGameShared.dll",
    "BazaarGameClient.dll",
    "BazaarBattleService.dll",
    "Assembly-CSharp.dll",
    "com.rlabrecque.steamworks.net.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.UnityWebRequestModule.dll",
    "Sentry.dll",
    "Sentry.Unity.dll"
)

$typeMap = @{}
$loadedAssemblies = @()
$dllFiles = Get-ChildItem -Path $ManagedDir -Filter "*.dll"

foreach ($f in $dllFiles) {
    if ($targetDllNames -contains $f.Name -or $f.Name -like "Bazaar*" -or $f.Name -like "TheBazaar*" -or $f.Name -like "Tempo*") {
        try {
            $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $readerParams)
            $loadedAssemblies += $f.Name
            foreach ($t in $asm.MainModule.GetTypes()) {
                $full = $t.FullName
                if (-not $typeMap.ContainsKey($full)) {
                    $typeMap[$full] = @{ Type = $t; Assembly = $f.Name }
                }
                if (-not $typeMap.ContainsKey($t.Name)) {
                    $typeMap[$t.Name] = @{ Type = $t; Assembly = $f.Name }
                }
            }
        } catch {
            # 跳过无法读取的原生/非托管 DLL
        }
    }
}

function Find-MethodInHierarchy($typeDef, [string]$methodName) {
    $matched = @()
    $cur = $typeDef
    $depth = 0
    while ($null -ne $cur -and $depth -lt 12) {
        foreach ($m in $cur.Methods) {
            if ($m.Name -eq $methodName) {
                $matched += $m
            }
        }
        if ($matched.Count -gt 0) { break }
        if ($null -eq $cur.BaseType) { break }
        $baseKey = $cur.BaseType.FullName
        if ($typeMap.ContainsKey($baseKey)) {
            $cur = $typeMap[$baseKey].Type
        } else {
            try {
                $cur = $cur.BaseType.Resolve()
            } catch {
                break
            }
        }
        $depth++
    }
    return $matched
}

function Find-FieldInHierarchy($typeDef, [string]$fieldName) {
    $cur = $typeDef
    $depth = 0
    while ($null -ne $cur -and $depth -lt 12) {
        foreach ($fld in $cur.Fields) {
            if ($fld.Name -eq $fieldName) {
                return $fld
            }
        }
        if ($null -eq $cur.BaseType) { break }
        $baseKey = $cur.BaseType.FullName
        if ($typeMap.ContainsKey($baseKey)) {
            $cur = $typeMap[$baseKey].Type
        } else {
            try {
                $cur = $cur.BaseType.Resolve()
            } catch {
                break
            }
        }
        $depth++
    }
    return $null
}

$hookResults = @()
foreach ($h in $spec.hooks) {
    $tname = $h.typeName
    $mname = $h.methodName
    $isBclOrOptional = ($tname -like "System.*" -or $tname -like "Sentry*")

    $entry = $null
    if ($typeMap.ContainsKey($tname)) {
        $entry = $typeMap[$tname]
    }

    if ($null -eq $entry) {
        $hookResults += [PSCustomObject]@{
            file = $h.file
            line = $h.line
            typeName = $tname
            methodName = $mname
            risk = $h.risk
            hasParamSignature = $h.hasParamSignature
            status = $(if ($isBclOrOptional) { "OPTIONAL_OR_BCL" } else { "MISSING_TYPE" })
            assembly = ""
            overloadCount = 0
            visibility = ""
            returnType = ""
            signatures = @()
        }
        continue
    }

    $td = $entry.Type
    $methods = @(Find-MethodInHierarchy $td $mname)
    if ($methods.Count -eq 0) {
        $hookResults += [PSCustomObject]@{
            file = $h.file
            line = $h.line
            typeName = $tname
            methodName = $mname
            risk = $h.risk
            hasParamSignature = $h.hasParamSignature
            status = "DEAD_PATCH"
            assembly = $entry.Assembly
            overloadCount = 0
            visibility = ""
            returnType = ""
            signatures = @()
        }
        continue
    }

    $sigs = @()
    $anyNonPublic = $false
    foreach ($m in $methods) {
        if (-not $m.IsPublic) { $anyNonPublic = $true }
        $pStr = ($m.Parameters | ForEach-Object { $_.ParameterType.Name + " " + $_.Name }) -join ", "
        $sigs += ($m.ReturnType.Name + " " + $m.Name + "(" + $pStr + ")")
    }

    $st = "ALIVE"
    if ($methods.Count -gt 1 -and (-not $h.hasParamSignature)) {
        $st = "AMBIGUOUS_OVERLOAD"
    }

    $hookResults += [PSCustomObject]@{
        file = $h.file
        line = $h.line
        typeName = $tname
        methodName = $mname
        risk = $h.risk
        hasParamSignature = $h.hasParamSignature
        status = $st
        assembly = $entry.Assembly
        overloadCount = $methods.Count
        visibility = $(if ($anyNonPublic) { "NonPublic" } else { "Public" })
        returnType = $methods[0].ReturnType.FullName
        signatures = $sigs
    }
}

$fieldResults = @()
foreach ($f in $spec.fields) {
    $tname = $f.typeName
    $fname = $f.fieldName
    if (-not $typeMap.ContainsKey($tname)) {
        $fieldResults += [PSCustomObject]@{
            file = $f.file; line = $f.line; typeName = $tname; fieldName = $fname;
            isBackingField = $f.isBackingField; status = "MISSING_TYPE"; fieldType = ""
        }
        continue
    }
    $fld = Find-FieldInHierarchy $typeMap[$tname].Type $fname
    if ($null -eq $fld) {
        # 部分调用点先试 Field 再回退 Property（如 Hint.Card），顺带检查是否存在同名 Property
        $hasProp = $false
        $curP = $typeMap[$tname].Type
        $dP = 0
        while ($null -ne $curP -and $dP -lt 12) {
            foreach ($pr in $curP.Properties) {
                if ($pr.Name -eq $fname) { $hasProp = $true; break }
            }
            if ($hasProp -or $null -eq $curP.BaseType) { break }
            if ($typeMap.ContainsKey($curP.BaseType.FullName)) { $curP = $typeMap[$curP.BaseType.FullName].Type } else { break }
            $dP++
        }
        $fieldResults += [PSCustomObject]@{
            file = $f.file; line = $f.line; typeName = $tname; fieldName = $fname;
            isBackingField = $f.isBackingField; status = $(if ($hasProp) { "ALIVE" } else { "MISSING_FIELD" }); fieldType = $(if ($hasProp) { "(Property)" } else { "" })
        }
    } else {
        $fieldResults += [PSCustomObject]@{
            file = $f.file; line = $f.line; typeName = $tname; fieldName = $fname;
            isBackingField = $f.isBackingField; status = "ALIVE"; fieldType = $fld.FieldType.FullName
        }
    }
}

# 额外：比对 ext/BazaarGameShared.dll 与 live Steam Managed/BazaarGameShared.dll 的 MessagePack [Key(n)] 契约
function Get-MsgPackSchema($dllPath) {
    $map = @{}
    if (-not (Test-Path $dllPath)) { return $map }
    $r = New-Object Mono.Cecil.DefaultAssemblyResolver
    $r.AddSearchDirectory((Split-Path $dllPath -Parent))
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $r
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllPath, $rp)
    foreach ($t in $asm.MainModule.GetTypes()) {
        $hasMp = $false
        foreach ($ca in $t.CustomAttributes) {
            if ($ca.AttributeType.Name -eq "MessagePackObjectAttribute") { $hasMp = $true; break }
        }
        if (-not $hasMp) { continue }
        $keys = @{}
        foreach ($p in $t.Properties) {
            foreach ($ca in $p.CustomAttributes) {
                if ($ca.AttributeType.Name -eq "KeyAttribute" -and $ca.ConstructorArguments.Count -gt 0) {
                    $kVal = [string]$ca.ConstructorArguments[0].Value
                    $keys[$kVal] = ($p.Name + ":" + $p.PropertyType.FullName)
                }
            }
        }
        foreach ($fl in $t.Fields) {
            foreach ($ca in $fl.CustomAttributes) {
                if ($ca.AttributeType.Name -eq "KeyAttribute" -and $ca.ConstructorArguments.Count -gt 0) {
                    $kVal = [string]$ca.ConstructorArguments[0].Value
                    $keys[$kVal] = ($fl.Name + ":" + $fl.FieldType.FullName)
                }
            }
        }
        $map[$t.FullName] = $keys
    }
    return $map
}

$mpDrifts = @()
$mpCheckedTypes = 0
$liveShared = Join-Path $ManagedDir "BazaarGameShared.dll"
if ($ExtSharedDll -and (Test-Path $ExtSharedDll) -and (Test-Path $liveShared)) {
    $extSchema = Get-MsgPackSchema $ExtSharedDll
    $liveSchema = Get-MsgPackSchema $liveShared
    $mpCheckedTypes = $extSchema.Count
    foreach ($tKey in $extSchema.Keys) {
        if (-not $liveSchema.ContainsKey($tKey)) {
            $mpDrifts += [PSCustomObject]@{ type = $tKey; detail = "DTO 类型在 live Steam DLL 中已消失" }
            continue
        }
        $eKeys = $extSchema[$tKey]
        $lKeys = $liveSchema[$tKey]
        foreach ($k in $eKeys.Keys) {
            if (-not $lKeys.ContainsKey($k)) {
                $mpDrifts += [PSCustomObject]@{ type = $tKey; detail = "Key($k) 已移除 (原: $($eKeys[$k]))" }
            } elseif ($lKeys[$k] -ne $eKeys[$k]) {
                $mpDrifts += [PSCustomObject]@{ type = $tKey; detail = "Key($k) 签名变更: $($eKeys[$k]) -> $($lKeys[$k])" }
            }
        }
        foreach ($k in $lKeys.Keys) {
            if (-not $eKeys.ContainsKey($k)) {
                $mpDrifts += [PSCustomObject]@{ type = $tKey; detail = "新增 Key($k): $($lKeys[$k])" }
            }
        }
    }
}

$outObj = [PSCustomObject]@{
    loadedAssemblies = $loadedAssemblies
    indexedTypeCount = $typeMap.Count
    hooks = $hookResults
    fields = $fieldResults
    messagePackCheckedTypes = $mpCheckedTypes
    messagePackDrifts = $mpDrifts
}

$outObj | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputJson -Encoding UTF8
"""


def run_stage4_harmony_check(
    launcher_dir: pathlib.Path,
    managed_dir: pathlib.Path | None,
    cecil_dll: pathlib.Path | None,
    ext_shared_dll: pathlib.Path | None,
) -> dict[str, Any]:
    hooks, fields = scan_launcher_hooks_and_fields(launcher_dir)

    if not managed_dir or not managed_dir.is_dir():
        return {
            "status": "YELLOW",
            "skipped": True,
            "reason": f"未找到 Steam Managed DLL 目录: {managed_dir}",
            "scannedHooks": len(hooks),
            "scannedFields": len(fields),
        }

    if not cecil_dll or not cecil_dll.is_file():
        return {
            "status": "YELLOW",
            "skipped": True,
            "reason": "未找到 Mono.Cecil.dll，无法离线解析 Managed DLL 元数据。",
            "scannedHooks": len(hooks),
            "scannedFields": len(fields),
        }

    with tempfile.TemporaryDirectory(prefix="compat_doctor_") as tmpdir:
        tmp_path = pathlib.Path(tmpdir)
        ps1_path = tmp_path / "verify_cecil.ps1"
        in_json = tmp_path / "input.json"
        out_json = tmp_path / "output.json"

        ps1_path.write_text(CECIL_PS1_SCRIPT, encoding="utf-8")
        in_json.write_text(json.dumps({"hooks": hooks, "fields": fields}, ensure_ascii=False), encoding="utf-8")

        cmd = [
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(ps1_path),
            "-CecilDll",
            str(cecil_dll),
            "-ManagedDir",
            str(managed_dir),
            "-InputJson",
            str(in_json),
            "-OutputJson",
            str(out_json),
            "-ExtSharedDll",
            str(ext_shared_dll) if (ext_shared_dll and ext_shared_dll.is_file()) else "",
        ]
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90)
        if proc.returncode != 0 or not out_json.is_file():
            return {
                "status": "RED",
                "skipped": False,
                "error": f"Mono.Cecil 核验进程失败 (exit={proc.returncode}): {proc.stderr.strip() or proc.stdout.strip()}",
                "scannedHooks": len(hooks),
                "scannedFields": len(fields),
            }

        cecil_data = json.loads(out_json.read_text(encoding="utf-8-sig"))

    hook_rows = cecil_data.get("hooks") or []
    if isinstance(hook_rows, dict):
        hook_rows = [hook_rows]
    field_rows = cecil_data.get("fields") or []
    if isinstance(field_rows, dict):
        field_rows = [field_rows]
    mp_drifts = cecil_data.get("messagePackDrifts") or []
    if isinstance(mp_drifts, dict):
        mp_drifts = [mp_drifts]

    game_hooks = [h for h in hook_rows if h["status"] != "OPTIONAL_OR_BCL"]
    alive_hooks = [h for h in game_hooks if h["status"] in ("ALIVE", "AMBIGUOUS_OVERLOAD")]
    dead_patches = [h for h in game_hooks if h["status"] in ("DEAD_PATCH", "MISSING_TYPE")]
    ambiguous = [h for h in game_hooks if h["status"] == "AMBIGUOUS_OVERLOAD"]
    non_public_hooks = [h for h in alive_hooks if h.get("visibility") == "NonPublic"]

    alive_fields = [f for f in field_rows if f["status"] == "ALIVE"]
    missing_fields = [f for f in field_rows if f["status"] != "ALIVE"]
    backing_fields = [f for f in field_rows if f.get("isBackingField")]

    p0_broken = [h for h in dead_patches if h.get("risk") == "P0"]
    survival_pct = round(100.0 * len(alive_hooks) / len(game_hooks), 2) if game_hooks else 100.0

    status = "GREEN"
    if mp_drifts or missing_fields:
        status = "RED"
    elif dead_patches or ambiguous:
        # 若为已知低危死补丁（非 P0）给 YELLOW；若是 P0 死补丁仍给 YELLOW/RED 按严重度区分
        status = "RED" if any(d["methodName"] not in ("Awake", "Start") for d in p0_broken) else "YELLOW"

    return {
        "status": status,
        "skipped": False,
        "managedDir": str(managed_dir),
        "cecilDll": str(cecil_dll),
        "loadedAssemblies": cecil_data.get("loadedAssemblies", []),
        "indexedTypeCount": cecil_data.get("indexedTypeCount", 0),
        "summary": {
            "totalScannedHooks": len(hook_rows),
            "gameDllHooks": len(game_hooks),
            "aliveHooks": len(alive_hooks),
            "deadPatchCount": len(dead_patches),
            "ambiguousOverloadCount": len(ambiguous),
            "nonPublicHookCount": len(non_public_hooks),
            "hookSurvivalPct": survival_pct,
            "totalScannedFields": len(field_rows),
            "aliveFields": len(alive_fields),
            "missingFields": len(missing_fields),
            "backingFieldsCount": len(backing_fields),
            "messagePackCheckedTypes": cecil_data.get("messagePackCheckedTypes", 0),
            "messagePackDriftCount": len(mp_drifts),
        },
        "deadPatches": dead_patches,
        "ambiguousOverloads": ambiguous,
        "missingFields": missing_fields,
        "messagePackDrifts": mp_drifts,
    }


# ============================================================================
# Stage 5: 健康评分与简体中文 Markdown / JSON 报告生成
# ============================================================================

def compute_health_score(s1: dict[str, Any], s2: dict[str, Any], s3: dict[str, Any], s4: dict[str, Any]) -> dict[str, Any]:
    score = 100
    deductions: list[str] = []

    # Stage 1 扣分
    if s1.get("schemaDrift"):
        score -= 25
        deductions.append(f"Stage 1: 检测到 {len(s1['schemaDrift'])} 处 GameData.db 表结构漂移 (-25)")
    unimpl_types = s1.get("unimplementedNewDollarTypes") or []
    if unimpl_types:
        score -= 15
        deductions.append(f"Stage 1: 检测到 {len(unimpl_types)} 种 BazaarCore 尚未实现的未知规则 $type (-15)")
    if not s1.get("levelUpLv11To30Uniform800", True):
        score -= 5
        deductions.append("Stage 1: level_ups Lv11-30 HP 非恒定 800 (-5)")

    # Stage 2 扣分
    if s2.get("skipped"):
        score -= 5
        deductions.append("Stage 2: 未找到 catalog.bin，跳过资产核验 (-5)")
    else:
        ck = s2.get("criticalKeys") or {}
        if not all(ck.values()):
            score -= 20
            deductions.append("Stage 2: 关键硬编码 Addressables Key 失效 (-20)")
        broken_wl = (s2.get("cards") or {}).get("whitelistBrokenInCatalogCount", 0)
        if broken_wl > 0:
            pen = min(20, broken_wl * 2)
            score -= pen
            deductions.append(f"Stage 2: 白名单中有 {broken_wl} 张卡在 catalog.bin 失效 (-{pen})")
        stale_wl = (s2.get("cards") or {}).get("staleWhitelistNotInDbCount", 0)
        if stale_wl > 0:
            score -= 3
            deductions.append(f"Stage 2: valid_card_ids.json 含 {stale_wl} 条 DB 已移除旧卡 (-3)")
        new_valid = (s2.get("cards") or {}).get("newValidNotInWhitelistCount", 0)
        if new_valid > 0:
            score -= 3
            deductions.append(f"Stage 2: 有 {new_valid} 张新有效资产卡尚未合入白名单 (-3)")

    # Stage 3 扣分
    after = s3.get("afterOverlay") or {}
    if after.get("remainingP0Active", 0) > 0:
        pen = min(20, after["remainingP0Active"] * 2)
        score -= pen
        deductions.append(f"Stage 3: 仍有 {after['remainingP0Active']} 条 P0 活跃可见文本未汉化 (-{pen})")
    if after.get("remainingP1Whitelist", 0) > 0:
        pen = min(10, max(2, after["remainingP1Whitelist"] // 10))
        score -= pen
        deductions.append(f"Stage 3: 仍有 {after['remainingP1Whitelist']} 条白名单/挑战文本未汉化 (-{pen})")

    # Stage 4 扣分
    if s4.get("skipped"):
        score -= 10
        deductions.append("Stage 4: 未执行 live Steam Managed DLL 钩子核验 (-10)")
    elif s4.get("error"):
        score -= 30
        deductions.append("Stage 4: Mono.Cecil 核验运行异常 (-30)")
    else:
        sm = s4.get("summary") or {}
        if sm.get("messagePackDriftCount", 0) > 0:
            score -= 35
            deductions.append(f"Stage 4: MessagePack DTO 发生 {sm['messagePackDriftCount']} 处契约漂移（高危阻断）(-35)")
        if sm.get("deadPatchCount", 0) > 0:
            pen = min(25, sm["deadPatchCount"] * 3)
            score -= pen
            deductions.append(f"Stage 4: 存在 {sm['deadPatchCount']} 个失效死补丁 (-{pen})")
        if sm.get("ambiguousOverloadCount", 0) > 0:
            pen = min(10, sm["ambiguousOverloadCount"] * 2)
            score -= pen
            deductions.append(f"Stage 4: 存在 {sm['ambiguousOverloadCount']} 处多重载未锁签名钩子 (-{pen})")
        if sm.get("missingFields", 0) > 0:
            pen = min(20, sm["missingFields"] * 5)
            score -= pen
            deductions.append(f"Stage 4: 存在 {sm['missingFields']} 个缺失反射字段 (-{pen})")

    score = max(0, min(100, score))
    if score >= 90 and not (s4.get("summary") or {}).get("messagePackDriftCount"):
        grade, badge = "GREEN", "🟢 绿灯（健康可发包）"
    elif score >= 70 and not (s4.get("summary") or {}).get("messagePackDriftCount"):
        grade, badge = "YELLOW", "🟡 黄灯（可用，建议处理告警项）"
    else:
        grade, badge = "RED", "🔴 红灯（存在阻断级兼容风险）"

    return {
        "score": score,
        "grade": grade,
        "badge": badge,
        "deductions": deductions,
    }


def render_markdown_report(report: dict[str, Any]) -> str:
    h = report["health"]
    s1 = report["stages"]["stage1_db_diff"]
    s2 = report["stages"]["stage2_asset_audit"]
    s3 = report["stages"]["stage3_l10n_diff"]
    s4 = report["stages"]["stage4_harmony_check"]

    lines: list[str] = [
        "# 大巴扎官方 Steam 更新兼容体检报告（compat_doctor）",
        "",
        f"> **体检时间（UTC）**：`{report['generatedAt']}`  ",
        f"> **健康总分**：**{h['score']} / 100** —— {h['badge']}  ",
        f"> **检查对象**：DB `{s1['dbPath']}` | Steam `{s4.get('managedDir', 'N/A')}`",
        "",
        "---",
        "",
        "## 0. 综合健康总览与扣分明细",
        "",
        "| 体检阶段 | 模块名称 | 状态 | 核心结论摘要 |",
        "| :--- | :--- | :---: | :--- |",
        f"| **Stage 1** | `GameData.db` 差异审计 | `{s1['status']}` | 表结构漂移 {len(s1['schemaDrift'])} 处，基线新增 `$type` {len(s1['newDollarTypes'])} 种（未实现 {len(s1.get('unimplementedNewDollarTypes', []))} 种），共 {s1['tableCounts'].get('cards', 0)} 卡 / {s1['tableCounts'].get('monsters', 0)} 怪 |",
        f"| **Stage 2** | Addressables 资产白名单审计 | `{s2['status']}` | "
        + (
            "已跳过（未定位 catalog.bin）"
            if s2.get("skipped")
            else f"有效卡 {s2['cards']['liveValid']}，白名单失效 {s2['cards']['whitelistBrokenInCatalogCount']}，旧卡残留 {s2['cards']['staleWhitelistNotInDbCount']}，新增可用未入库 {s2['cards']['newValidNotInWhitelistCount']}"
        )
        + " |",
        f"| **Stage 3** | 全链路汉化缺口扫描 | `{s3['status']}` | 叠加外挂词典后：P0 活跃可见剩余 **{s3['afterOverlay']['remainingP0Active']}**（覆盖率 {s3['afterOverlay']['activeCoveragePct']}%），白名单剩余 **{s3['afterOverlay']['remainingP1Whitelist']}**，全库总覆盖率 **{s3['afterOverlay']['totalCoveragePct']}%** |",
        f"| **Stage 4** | Harmony 钩子与 DTO 契约核验 | `{s4['status']}` | "
        + (
            f"异常/跳过：{s4.get('reason') or s4.get('error')}"
            if s4.get("skipped") or s4.get("error")
            else f"游戏 DLL 钩子存活 **{s4['summary']['aliveHooks']}/{s4['summary']['gameDllHooks']}（{s4['summary']['hookSurvivalPct']}%）**，死补丁 {s4['summary']['deadPatchCount']}，多重载告警 {s4['summary']['ambiguousOverloadCount']}，MessagePack DTO 漂移 {s4['summary']['messagePackDriftCount']}"
        )
        + " |",
        "",
    ]

    if h["deductions"]:
        lines.append("### 扣分与整改提示")
        for d in h["deductions"]:
            lines.append(f"- {d}")
        lines.append("")
    else:
        lines.append("> 所有五阶段检查项全部满分通过，无扣分项。\n")

    # Stage 1 详情
    lines.extend([
        "---",
        "",
        "## 1. Stage 1：`GameData.db` 表结构与行级增量审计",
        "",
        f"- **当前数据库**：`{s1['dbPath']}`（{s1['dbSizeBytes']:,} B）",
        f"- **基线数据库**：`{s1['baselineDbPath'] or '未指定'}`",
        f"- **规则 `$type` 种类**：{s1['dollarTypeCount']} 种（新增：{len(s1['newDollarTypes'])}，移除：{len(s1['removedDollarTypes'])}）",
        f"- **英雄枚举**：`{', '.join(s1['heroes'])}`（新增：{s1['newHeroes'] or '无'}）",
        f"- **权威常数校验**：Legendary 买价 Small/Medium/Large = `{s1['standardPricesLegendary']['SmallBuy']}/{s1['standardPricesLegendary']['MediumBuy']}/{s1['standardPricesLegendary']['LargeBuy']}`；Lv11–30 HP 恒定 800 = `{s1['levelUpLv11To30Uniform800']}`",
        "",
    ])
    if s1["tableDiffs"]:
        lines.extend([
            "| 数据表 | 基线行数 | 当前行数 | 新增 | 移除 | 内容变更 |",
            "| :--- | ---: | ---: | ---: | ---: | ---: |",
        ])
        for tbl, d in s1["tableDiffs"].items():
            lines.append(f"| `{tbl}` | {d['baseline']} | {d['current']} | +{d['added']} | -{d['removed']} | ~{d['changed']} |")
        lines.append("")

    if s1["cardDiffsByType"]:
        lines.extend([
            "### 卡牌（`cards`）按类型增删改统计",
            "",
            "| 卡牌类型 | 新增 | 移除 | 内容变更 |",
            "| :--- | ---: | ---: | ---: |",
        ])
        for ct, d in s1["cardDiffsByType"].items():
            lines.append(f"| `{ct}` | +{d['added']} | -{d['removed']} | ~{d['changed']} |")
        lines.append("")

    # Stage 2 详情
    lines.extend([
        "---",
        "",
        "## 2. Stage 2：Steam Addressables `catalog.bin` 与资产白名单审计",
        "",
    ])
    if s2.get("skipped"):
        lines.append(f"- 跳过原因：{s2['reason']}\n")
    else:
        c2 = s2["cards"]
        col2 = s2["collectibles"]
        lines.extend([
            f"- **catalog.bin**：`{s2['catalogPath']}`（{s2['catalogSizeBytes']:,} B，提取显式键 {s2['catalogExtractedKeys']:,} 条）",
            f"- **关键硬编码资产键存活**：`{json.dumps(s2['criticalKeys'], ensure_ascii=False)}`",
            f"- **卡牌白名单核验**：DB 核查 {c2['dbChecked']} 张 → 实测可用 **{c2['liveValid']}** 张 / 缺资产 {c2['liveMissingAssets']} 张；现有 `valid_card_ids.json` 共 {c2['whitelistTotal']} 张",
            f"  - 白名单内资产在 catalog.bin 失效：**{c2['whitelistBrokenInCatalogCount']}** 张",
            f"  - 白名单内含 DB 已移除旧卡：**{c2['staleWhitelistNotInDbCount']}** 张",
            f"  - DB 新增有效卡尚未入白名单：**{c2['newValidNotInWhitelistCount']}** 张",
            f"- **藏品白名单核验**：DB 共 {col2['dbTotal']} 项 vs `valid_collectible_ids.json` 共 {col2['whitelistTotal']} 项（DB 未入白名单 {col2['dbNotInWhitelistCount']}，白名单陈旧 {col2['staleWhitelistNotInDbCount']}）",
            "",
        ])

    # Stage 3 详情
    b3 = s3["beforeOverlay"]
    a3 = s3["afterOverlay"]
    lines.extend([
        "---",
        "",
        "## 3. Stage 3：全链路汉化覆盖率与增量待翻导出",
        "",
        f"- **官方 `zh-CN.bytes`**：{s3['zhBytesEntries']:,} 条",
        f"- **外挂词典 `custom_zh_overlay.json`**：版本 `{s3['overlay'].get('revision', '未加载')}`，`sha256={str(s3['overlay'].get('sha256', ''))[:16]}...`",
        f"- **待翻模板已导出至**：`{s3['todoTemplatePath']}`",
        "",
        "| 统计维度 | 仅官方 `zh-CN.bytes` 缺失 | 叠加 `custom_zh_overlay.json` 剩余 | 补齐覆盖率 |",
        "| :--- | ---: | ---: | ---: |",
        f"| **P0 活跃可见缺口** | {b3['activeVisibleMissing']} | **{a3['remainingP0Active']}** | **{a3['activeCoveragePct']}%** |",
        f"| **P1 白名单与挑战缺口** | {b3['whitelistAndChallengesMissing']} | **{a3['remainingP0Active'] + a3['remainingP1Whitelist']}** | **{a3['whitelistCoveragePct']}%** |",
        f"| **全库去重 MD5 总缺口** | {b3['totalMissingMd5']} | **{a3['remainingTotal']}** | **{a3['totalCoveragePct']}%** |",
        "",
    ])

    # Stage 4 详情
    lines.extend([
        "---",
        "",
        "## 4. Stage 4：`BazaarLauncher` Harmony 钩子与 MessagePack DTO 契约实机核验",
        "",
    ])
    if s4.get("skipped") or s4.get("error"):
        lines.append(f"- 未完成核验：{s4.get('reason') or s4.get('error')}\n")
    else:
        sm = s4["summary"]
        lines.extend([
            f"- **已扫描核心程序集**：{len(s4['loadedAssemblies'])} 个（索引类型总数 {s4['indexedTypeCount']:,}）",
            f"- **Harmony 钩子存活率**：**{sm['aliveHooks']} / {sm['gameDllHooks']}（{sm['hookSurvivalPct']}%）**（另含 {sm['totalScannedHooks'] - sm['gameDllHooks']} 个 BCL/可选钩子；NonPublic 侵入钩子 {sm['nonPublicHookCount']} 个）",
            f"- **关键反射字段存活率**：**{sm['aliveFields']} / {sm['totalScannedFields']}**（含自动属性背字段 `<Prop>k__BackingField` {sm['backingFieldsCount']} 处）",
            f"- **MessagePack DTO 契约核验**：共比对 `ext/BazaarGameShared.dll` vs live Steam DLL **{sm['messagePackCheckedTypes']}** 个 `[MessagePackObject]` 类型，漂移数 = **{sm['messagePackDriftCount']}**",
            "",
        ])
        if s4["deadPatches"]:
            lines.extend([
                "### 检出的失效死补丁（Dead Patches）",
                "",
                "| 来源文件:行号 | 目标类型 | 目标方法 | 风险级 | 状态 |",
                "| :--- | :--- | :--- | :---: | :---: |",
            ])
            for dp in s4["deadPatches"]:
                lines.append(f"| `{dp['file']}:{dp['line']}` | `{dp['typeName']}` | `{dp['methodName']}` | `{dp['risk']}` | `{dp['status']}` |")
            lines.append("")

        if s4["ambiguousOverloads"]:
            lines.extend([
                "### 检出的多重载未锁签名钩子（签名漂移风险）",
                "",
                "| 来源文件:行号 | 目标类型 | 目标方法 | 重载数 | 现有重载签名 |",
                "| :--- | :--- | :--- | ---: | :--- |",
            ])
            for ao in s4["ambiguousOverloads"]:
                sigs = " ; ".join(ao.get("signatures") or [])
                lines.append(f"| `{ao['file']}:{ao['line']}` | `{ao['typeName']}` | `{ao['methodName']}` | {ao['overloadCount']} | `{sigs}` |")
            lines.append("")

        if s4["messagePackDrifts"]:
            lines.append("### MessagePack DTO 契约漂移（阻断级告警）\n")
            for dr in s4["messagePackDrifts"][:30]:
                lines.append(f"- `{dr['type']}`：{dr['detail']}")
            lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="大巴扎官方 Steam 更新五阶段离线兼容体检 CLI。")
    parser.add_argument("--db", type=pathlib.Path, default=None, help="目标权威 GameData.db 路径")
    parser.add_argument("--baseline-db", type=pathlib.Path, default=None, help="对比基线 GameData.db 路径")
    parser.add_argument("--game-dir", type=pathlib.Path, default=DEFAULT_STEAM_DIR, help="Steam 游戏根目录")
    parser.add_argument("--zh-bytes", type=pathlib.Path, default=None, help="官方 zh-CN.bytes 路径")
    parser.add_argument("--overlay", type=pathlib.Path, default=None, help="外挂 custom_zh_overlay.json 路径")
    parser.add_argument("--valid-cards", type=pathlib.Path, default=None, help="valid_card_ids.json 路径")
    parser.add_argument("--valid-collectibles", type=pathlib.Path, default=None, help="valid_collectible_ids.json 路径")
    parser.add_argument("--launcher-dir", type=pathlib.Path, default=REPO_ROOT / "src" / "BazaarLauncher", help="BazaarLauncher 源码目录")
    parser.add_argument("--out-md", type=pathlib.Path, default=REPO_ROOT / "logs" / "compat_doctor_report.md", help="Markdown 报告输出路径")
    parser.add_argument("--out-json", type=pathlib.Path, default=REPO_ROOT / "logs" / "compat_doctor_report.json", help="JSON 报告输出路径")
    parser.add_argument("--l10n-todo", type=pathlib.Path, default=REPO_ROOT / "logs" / "l10n_todo.json", help="增量待翻模板输出路径")
    parser.add_argument("--strict", action="store_true", help="开启 CI 严格退出码（GREEN=0, YELLOW=1, RED=2）；默认报告生成成功即返回 0")
    args = parser.parse_args()

    db_path = first_existing(
        args.db,
        REPO_ROOT / "dist" / "data" / "GameData.db",
        MAIN_REPO_FALLBACK / "dist" / "data" / "GameData.db",
    )
    if not db_path:
        print("[compat_doctor] 错误：未找到权威 GameData.db", file=sys.stderr)
        return 2

    baseline_db_path = first_existing(
        args.baseline_db,
        REPO_ROOT / "docs" / "recon" / "gamedata" / "GameData.db",
        MAIN_REPO_FALLBACK / "docs" / "recon" / "gamedata" / "GameData.db",
    )
    if baseline_db_path and baseline_db_path.resolve() == db_path.resolve():
        baseline_db_path = None

    catalog_path = args.game_dir / "TheBazaar_Data" / "StreamingAssets" / "aa" / "catalog.bin"
    managed_dir = args.game_dir / "TheBazaar_Data" / "Managed"
    cecil_dll = first_existing(
        args.game_dir / "BepInEx" / "core" / "Mono.Cecil.dll",
        REPO_ROOT / "ext" / "bepinex" / "core" / "Mono.Cecil.dll",
        REPO_ROOT / "ext" / "Mono.Cecil.dll",
        *list((REPO_ROOT / "ext").rglob("Mono.Cecil.dll")) if (REPO_ROOT / "ext").is_dir() else [],
    )
    ext_shared_dll = first_existing(
        REPO_ROOT / "ext" / "BazaarGameShared.dll",
        *list((REPO_ROOT / "ext").rglob("BazaarGameShared.dll")) if (REPO_ROOT / "ext").is_dir() else [],
    )

    zh_bytes = first_existing(
        args.zh_bytes,
        REPO_ROOT / "dist" / "data" / "translations" / "zh-CN.bytes",
        MAIN_REPO_FALLBACK / "dist" / "data" / "translations" / "zh-CN.bytes",
    )
    overlay_path = first_existing(
        args.overlay,
        REPO_ROOT / "dist" / "data" / "translations" / "custom_zh_overlay.json",
        REPO_ROOT / "tools" / "data" / "custom_zh_overlay.json",
    )
    valid_cards = first_existing(
        args.valid_cards,
        REPO_ROOT / "dist" / "data" / "valid_card_ids.json",
        MAIN_REPO_FALLBACK / "dist" / "data" / "valid_card_ids.json",
    )
    valid_collectibles = first_existing(
        args.valid_collectibles,
        REPO_ROOT / "dist" / "data" / "valid_collectible_ids.json",
        MAIN_REPO_FALLBACK / "dist" / "data" / "valid_collectible_ids.json",
    )

    print("[compat_doctor] 开始执行官方 Steam 更新五阶段兼容体检...")
    s1 = run_stage1_db_diff(db_path, baseline_db_path)
    print(f"  [Stage 1] DB 差异审计完成: status={s1['status']}, cards={s1['tableCounts'].get('cards', 0)}, monsters={s1['tableCounts'].get('monsters', 0)}")

    s2 = run_stage2_asset_audit(db_path, catalog_path, valid_cards, valid_collectibles)
    print(f"  [Stage 2] Addressables 资产审计完成: status={s2['status']}")

    s3 = run_stage3_l10n_diff(db_path, zh_bytes, overlay_path, valid_cards, args.l10n_todo)
    print(
        f"  [Stage 3] 汉化缺口扫描完成: status={s3['status']}, "
        f"剩余 P0={s3['afterOverlay']['remainingP0Active']}, 总覆盖率={s3['afterOverlay']['totalCoveragePct']}%"
    )

    s4 = run_stage4_harmony_check(args.launcher_dir, managed_dir, cecil_dll, ext_shared_dll)
    if s4.get("summary"):
        sm = s4["summary"]
        print(
            f"  [Stage 4] Harmony 钩子核验完成: status={s4['status']}, "
            f"存活={sm['aliveHooks']}/{sm['gameDllHooks']} ({sm['hookSurvivalPct']}%), "
            f"死补丁={sm['deadPatchCount']}, 多重载={sm['ambiguousOverloadCount']}, DTO漂移={sm['messagePackDriftCount']}"
        )
    else:
        print(f"  [Stage 4] Harmony 钩子核验: status={s4['status']} ({s4.get('reason') or s4.get('error')})")

    health = compute_health_score(s1, s2, s3, s4)
    report = {
        "schemaVersion": 1,
        "generatedAt": dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "health": health,
        "stages": {
            "stage1_db_diff": s1,
            "stage2_asset_audit": s2,
            "stage3_l10n_diff": s3,
            "stage4_harmony_check": s4,
        },
    }

    atomic_write_text(args.out_json, json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    md_text = render_markdown_report(report)
    atomic_write_text(args.out_md, md_text)

    print(f"  [Stage 5] 体检报告已生成:")
    print(f"            健康评分 : {health['score']}/100 ({health['badge']})")
    print(f"            Markdown : {args.out_md}")
    print(f"            JSON     : {args.out_json}")
    print(f"            待翻模板 : {args.l10n_todo}")

    if args.strict:
        return {"GREEN": 0, "YELLOW": 1, "RED": 2}.get(health["grade"], 2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
