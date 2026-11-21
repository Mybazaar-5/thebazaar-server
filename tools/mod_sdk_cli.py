#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TheBazaar V13.1 标准化工业级 Mod 命令行 SDK（tools/mod_sdk_cli.py）

零第三方强依赖（纯 Python 3.10+ 标准库即可执行全部离线检索、静态 8 闸门校验与安全打包；
通过 --server 或内置离线 BazaarCli 桥接可无缝执行完整 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 压测）。

统一退出码：
  0 = 全部执行/校验通过（0 Error）
  1 = 参数错误、文件不存在或外部依赖不可达
  2 = 静态防 Bug 铁闸（#1~#8）未通过
  3 = 动态 50 场战斗压力体检（#9）或隔离回归（#10）未通过
  4 = .bazaarmod.zip 安全检疫失败（Zip Slip / Zip Bomb / 可执行文件）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

# 确保 Windows 终端 UTF-8 中文输出不乱码
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

REPO_ROOT = Path(__file__).resolve().parent.parent
MOD_ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$|^[a-z0-9]{2}$")
VALID_SCOPES = {"singleplayer-only", "multiplayer-safe", "cosmetic-only"}
VALID_SIZES = {"Small", "Medium", "Large"}
VALID_TIERS = {"Bronze", "Silver", "Gold", "Diamond", "Legendary"}
VALID_TYPES = {"Item", "Skill", "EventEncounter", "EncounterStep", "PedestalEncounter", "CombatEncounter"}
FORBIDDEN_LEGACY_FIELDS = {"effects", "costGold", "rewardGold", "rewardXp", "chainEvent", "grantCards"}
FORBIDDEN_ZIP_EXTS = {
    ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".js", ".mjs", ".cjs", ".cs", ".so", ".dylib", ".msi", ".com", ".scr"
}
SUPPORTED_PREREQ_TYPES = {
    "TPrerequisiteCardAttributeComparator",
    "TPrerequisiteCardCount",
    "TPrerequisitePlayer",
    "TPrerequisiteRun",
}
MAX_ASSET_BYTES = 2 * 1024 * 1024
MAX_TOTAL_ASSETS_BYTES = 32 * 1024 * 1024
MAX_ZIP_BYTES = 25 * 1024 * 1024
MAX_ZIP_UNCOMPRESSED = 64 * 1024 * 1024
MAX_ZIP_ENTRIES = 200


def locate_data_root(explicit: str | None = None) -> Path | None:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit))
    env_data = os.environ.get("BAZAAR_DATA")
    if env_data:
        candidates.append(Path(env_data))
    candidates.extend([
        REPO_ROOT / "dist" / "data",
        REPO_ROOT / "data",
        Path.cwd() / "dist" / "data",
        Path.cwd() / "data",
    ])
    for c in candidates:
        if (c / "GameData.db").is_file() or (c / "valid_card_ids.json").is_file():
            return c.resolve()
    return None


def locate_game_db_path(data_root: Path | None) -> Path | None:
    candidates: list[Path] = []
    env_db = os.environ.get("BAZAAR_GAME_DATA_DB")
    if env_db:
        candidates.append(Path(env_db))
    if data_root:
        candidates.append(data_root / "GameData.db")
    home = Path.home()
    candidates.extend([
        home / "AppData" / "LocalLow" / "Tempo Storm" / "The Bazaar" / "prod" / "cache" / "GameData.db",
        REPO_ROOT / "dist" / "data" / "GameData.db",
    ])
    for p in candidates:
        if p.is_file() and p.stat().st_size > 1024:
            return p.resolve()
    return None


def locate_bazaar_cli() -> list[str] | None:
    """定位已编译的 BazaarCli（bz.exe / bz.dll）供离线执行真实 C# 10 闸门 + 50 场 CombatSimulator。"""
    exe_candidates = [
        REPO_ROOT / "src" / "BazaarCli" / "bin" / "Release" / "net8.0" / "bz.exe",
        REPO_ROOT / "src" / "BazaarCli" / "bin" / "Debug" / "net8.0" / "bz.exe",
        REPO_ROOT / "src" / "BazaarCli" / "bin" / "Release" / "net8.0" / "bz",
    ]
    for p in exe_candidates:
        if p.is_file():
            return [str(p)]
    dll_candidates = [
        REPO_ROOT / "src" / "BazaarCli" / "bin" / "Release" / "net8.0" / "bz.dll",
        REPO_ROOT / "src" / "BazaarCli" / "bin" / "Debug" / "net8.0" / "bz.dll",
    ]
    for p in dll_candidates:
        if p.is_file():
            return ["dotnet", str(p)]
    return None


def load_whitelist(data_root: Path) -> set[str]:
    wl_path = data_root / "valid_card_ids.json"
    if not wl_path.is_file():
        return set()
    raw = json.loads(wl_path.read_text(encoding="utf-8"))
    arr = raw if isinstance(raw, list) else raw.get("valid_ids", [])
    return {str(x).strip().lower() for x in arr if isinstance(x, str) and x.strip()}


def load_zh_title_map(data_root: Path) -> dict[str, str]:
    """从外挂中文词典读取卡牌中文标题覆盖（零依赖加速检索）。"""
    out: dict[str, str] = {}
    for p in [
        data_root / "translations" / "custom_zh_overlay.json",
        REPO_ROOT / "tools" / "data" / "custom_zh_overlay.json",
    ]:
        if not p.is_file():
            continue
        try:
            doc = json.loads(p.read_text(encoding="utf-8"))
            for cid, ov in (doc.get("cardOverrides") or {}).items():
                if isinstance(ov, dict) and ov.get("title"):
                    out[cid.lower()] = str(ov["title"])
        except Exception:
            pass
    return out


def load_base_cards_from_db(data_root: Path | None) -> dict[str, dict[str, Any]]:
    db_path = locate_game_db_path(data_root)
    cards: dict[str, dict[str, Any]] = {}
    if db_path is not None:
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        try:
            for cid, blob in conn.execute("SELECT Id, Data FROM cards"):
                raw = blob.decode("utf-8") if isinstance(blob, (bytes, bytearray)) else str(blob)
                obj = json.loads(raw)
                key = str(obj.get("Id") or cid).strip()
                obj["Id"] = key
                cards[key.lower()] = obj
            return cards
        finally:
            conn.close()

    # 回退兼容：若无 .db 则读取 dist/data/cards.json
    if data_root and (data_root / "cards.json").is_file():
        raw = json.loads((data_root / "cards.json").read_text(encoding="utf-8-sig"))
        items = raw if isinstance(raw, list) else (raw.get("cards") or list(raw.values()))
        for obj in items:
            if isinstance(obj, dict) and obj.get("Id"):
                cards[str(obj["Id"]).strip().lower()] = obj
    return cards


def load_known_rule_types() -> set[str]:
    """直接从仓库权威源码 RuleModels.cs 提取全部 KnownTypes（确保与 C# 引擎 100% 同源）。"""
    rm_path = REPO_ROOT / "src" / "BazaarCore" / "Models" / "RuleModels.cs"
    if rm_path.is_file():
        txt = rm_path.read_text(encoding="utf-8")
        m = re.search(r"KnownTypes\s*=\s*new[^{]*\{(.*?)\};", txt, re.S)
        if m:
            return set(re.findall(r'"([A-Za-z0-9_]+)"', m.group(1)))
    return set()


def read_json_file(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json_file(path: Path, obj: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def is_safe_rel_path(rel: str) -> bool:
    if not rel or not rel.strip():
        return False
    norm = rel.replace("\\", "/").strip()
    if norm.startswith("/") or ":" in norm:
        return False
    parts = [p for p in norm.split("/") if p]
    return bool(parts) and all(p not in (".", "..") for p in parts)


def inspect_png_header(data: bytes) -> tuple[bool, str, int, int]:
    """校验字节头并返回 (ok, content_type, width, height)。识别 WebP 以便显式拦截。"""
    if len(data) >= 24 and data[:8] == b"\x89PNG\r\n\x1a\n" and data[12:16] == b"IHDR":
        w, h = struct.unpack(">II", data[16:24])
        return (w > 0 and h > 0), "image/png", w, h
    if len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return False, "image/webp", 0, 0
    return False, "application/octet-stream", 0, 0


# ════════════════════════════════════════════════════════════════════════════
# 子命令 1：new — 初始化标准 Mod 骨架
# ════════════════════════════════════════════════════════════════════════════

def cmd_new(args: argparse.Namespace) -> int:
    mod_id = args.mod_id.strip().lower()
    if not MOD_ID_RE.match(mod_id):
        print(f"[错误] 非法 Mod ID '{mod_id}'：必须为 2~64 位小写字母、数字或连字符。", file=sys.stderr)
        return 1
    if args.scope not in VALID_SCOPES:
        print(f"[错误] 非法 scope '{args.scope}'，仅允许：{', '.join(sorted(VALID_SCOPES))}", file=sys.stderr)
        return 1

    mods_dir = Path(args.mods_dir).resolve() if args.mods_dir else (REPO_ROOT / "mods")
    target = mods_dir / mod_id
    if target.exists() and not args.force:
        print(f"[错误] 目标目录已存在：{target}（如需覆盖请加 --force）", file=sys.stderr)
        return 1

    for sub in ["cards", "patches", "events", "monsters", "modes", "localization", "assets"]:
        (target / sub).mkdir(parents=True, exist_ok=True)

    manifest = {
        "$schema": "../../docs/schemas/bazaar-mod.schema.json#/$defs/ModManifest",
        "schemaVersion": 1,
        "id": mod_id,
        "name": args.name or mod_id,
        "version": args.version or "1.0.0",
        "author": args.author or "社区创作者",
        "description": args.description or f"{args.name or mod_id}：基于 BazaarModKit V13.1 标准创建的扩展包。",
        "scope": args.scope,
        "affectsGhostPool": False,
        "enabledByDefault": False,
        "loadOrder": 100,
        "dependencies": [],
        "contentRoots": {
            "cards": "cards/custom_cards.json",
            "patches": "patches/vanilla_patches.json",
            "events": "events/custom_events.json",
            "modes": "modes/*.json",
            "localization": "localization/zh-CN.json",
            "assets": "assets/*.png",
            "artManifest": "assets/art_manifest.json",
        },
    }
    write_json_file(target / "mod.json", manifest)
    write_json_file(target / "cards" / "custom_cards.json", {
        "$schema": "../../../docs/schemas/bazaar-mod.schema.json#/$defs/CustomCardFile",
        "schemaVersion": 1,
        "modId": mod_id,
        "cards": [],
    })
    write_json_file(target / "patches" / "vanilla_patches.json", {
        "$schema": "../../../docs/schemas/bazaar-mod.schema.json#/$defs/CardPatchFile",
        "schemaVersion": 1,
        "modId": mod_id,
        "patches": [],
    })
    write_json_file(target / "events" / "custom_events.json", {
        "$schema": "../../../docs/schemas/bazaar-mod.schema.json#/$defs/CustomEventFile",
        "schemaVersion": 1,
        "modId": mod_id,
        "events": [],
    })
    write_json_file(target / "assets" / "art_manifest.json", {
        "schemaVersion": 1,
        "modId": mod_id,
        "assets": [],
    })
    (target / "README.md").write_text(
        f"# {args.name or mod_id} (`{mod_id}`)\n\n"
        f"- 作者：{args.author or '社区创作者'}\n"
        f"- 校验命令：`python tools/mod_sdk_cli.py validate mods/{mod_id} --strict --offline-sim`\n",
        encoding="utf-8",
    )

    print(f"✅ 已初始化标准 Mod 骨架：{target}")
    print(f"下一步：python tools/mod_sdk_cli.py clone-card --search \"Kukri\" --type Item")
    return 0


# ════════════════════════════════════════════════════════════════════════════
# 子命令 2：clone-card — 离线检索 3,368 原版白名单卡并克隆 / 生成原位补丁
# ════════════════════════════════════════════════════════════════════════════

def cmd_clone_card(args: argparse.Namespace) -> int:
    data_root = locate_data_root(args.data_root)
    if not data_root:
        print("[错误] 未找到 dist/data/GameData.db，请用 --data-root 指定。", file=sys.stderr)
        return 1

    cards = load_base_cards_from_db(data_root)
    whitelist = load_whitelist(data_root)
    zh_titles = load_zh_title_map(data_root)

    def card_summary(c: dict[str, Any]) -> dict[str, Any]:
        cid = c["Id"]
        loc_title = ((c.get("Localization") or {}).get("Title") or {}).get("Text") or c.get("InternalName") or cid
        zh = zh_titles.get(cid.lower()) or loc_title
        return {
            "templateId": cid,
            "titleZh": zh,
            "titleEn": c.get("InternalName") or loc_title,
            "type": c.get("Type") or "",
            "size": c.get("Size") or "",
            "startingTier": c.get("StartingTier") or "Bronze",
            "heroes": c.get("Heroes") or ["Common"],
            "tags": c.get("Tags") or [],
            "isWhitelisted": cid.lower() in whitelist,
        }

    # 检索模式
    if args.search is not None:
        q = args.search.strip().lower()
        results = []
        for c in cards.values():
            cid = c["Id"]
            if not args.include_non_whitelist and whitelist and cid.lower() not in whitelist:
                continue
            if args.type and (c.get("Type") or "").lower() != args.type.lower():
                continue
            if args.size and (c.get("Size") or "").lower() != args.size.lower():
                continue
            if args.hero and not any(h.lower() == args.hero.lower() for h in (c.get("Heroes") or [])):
                continue
            sm = card_summary(c)
            hay = f"{sm['templateId']} {sm['titleZh']} {sm['titleEn']} {' '.join(sm['tags'])} {' '.join(sm['heroes'])}".lower()
            if q and q not in hay:
                continue
            results.append(sm)
        results.sort(key=lambda x: (x["type"], x["titleEn"]))
        shown = results[: max(1, args.limit)]
        if args.json:
            print(json.dumps({"total": len(results), "items": shown}, ensure_ascii=False, indent=2))
        else:
            print(f"命中 {len(results)} 张原版白名单卡（展示前 {len(shown)} 张）：")
            for r in shown:
                print(f"  {r['templateId']} | {r['type']:14s} | {r['size']:6s} | {r['titleEn']} ({r['titleZh']}) | {','.join(r['tags'][:4])}")
        return 0

    # 克隆或原位补丁模式
    if not args.query or not args.mod:
        print("[错误] 克隆/补丁模式必须提供原版卡 GUID 或名称以及 --mod <mod-id>。", file=sys.stderr)
        return 1

    key = args.query.strip().lower()
    matched = cards.get(key)
    if not matched:
        cands = [
            c for c in cards.values()
            if (not whitelist or c["Id"].lower() in whitelist)
            and (key in (c.get("InternalName") or "").lower() or key in zh_titles.get(c["Id"].lower(), "").lower())
        ]
        cands.sort(key=lambda c: (0 if (c.get("InternalName") or "").lower() == key else 1, len(c.get("InternalName") or "")))
        if not cands:
            print(f"[错误] 未在合法白名单中找到匹配原版卡：{args.query}", file=sys.stderr)
            return 1
        matched = cands[0]

    cid = matched["Id"]
    if whitelist and cid.lower() not in whitelist:
        print(f"[错误] 原版卡 '{cid}' 不在 valid_card_ids.json 白名单中，严禁作为视觉载体！", file=sys.stderr)
        return 2

    mod_id = args.mod.strip().lower()
    mods_dir = Path(args.mods_dir).resolve() if args.mods_dir else (REPO_ROOT / "mods")
    mod_dir = mods_dir / mod_id
    if not (mod_dir / "mod.json").is_file():
        print(f"[错误] Mod 目录 '{mod_dir}' 不存在，请先运行 python tools/mod_sdk_cli.py new {mod_id}", file=sys.stderr)
        return 1

    sm = card_summary(matched)
    if args.mode == "patch":
        patch_file = mod_dir / "patches" / "vanilla_patches.json"
        doc = read_json_file(patch_file) if patch_file.is_file() else {"schemaVersion": 1, "modId": mod_id, "patches": []}
        tier_overrides: dict[str, dict[str, Any]] = {}
        for tk, td in (matched.get("Tiers") or {}).items():
            tier_overrides[tk] = dict(td.get("Attributes") or {})
        patch_entry = {
            "targetTemplateId": cid,
            "note": f"原位调整原版卡：{sm['titleEn']} ({sm['titleZh']})",
            "startingTier": matched.get("StartingTier") or "Bronze",
            "addTags": [],
            "removeTags": [],
            "tierAttributeOverrides": tier_overrides,
        }
        doc["patches"] = [p for p in doc.get("patches", []) if p.get("targetTemplateId", "").lower() != cid.lower()]
        doc["patches"].append(patch_entry)
        write_json_file(patch_file, doc)
        print(f"✅ 已生成原位补丁写入 {patch_file}（目标：{cid} / {sm['titleEn']}）")
        return 0

    # clone 模式
    slug = (args.slug or re.sub(r"[^a-z0-9]+", "_", sm["titleEn"].lower()).strip("_") or "custom_card")
    custom_id = f"{mod_id}:{slug}"
    abilities = matched.get("Abilities") or {}
    auras = matched.get("Auras") or {}
    all_ab_ids = sorted(abilities.keys())
    all_au_ids = sorted(auras.keys())

    tiers_out: dict[str, Any] = {}
    for tk, td in (matched.get("Tiers") or {}).items():
        tiers_out[tk] = {
            "attributes": dict(td.get("Attributes") or {}),
            "abilityIds": list(td.get("AbilityIds") or all_ab_ids),
            "auraIds": list(td.get("AuraIds") or all_au_ids),
        }

    cloned_card = {
        "customId": custom_id,
        "baseVisualTemplateId": cid,
        "type": matched.get("Type") or "Item",
        "size": matched.get("Size") or "Medium",
        "startingTier": matched.get("StartingTier") or "Bronze",
        "spawningEligibility": matched.get("SpawningEligibility") or "Always",
        "heroes": matched.get("Heroes") or ["Common"],
        "tags": matched.get("Tags") or [],
        "hiddenTags": matched.get("HiddenTags") or [],
        "titleZh": f"{sm['titleZh']}（改）",
        "titleEn": f"{sm['titleEn']} Custom",
        "descZh": f"基于原版「{sm['titleZh']}」克隆改造的自定义卡牌。",
        "activeTooltipsZh": [],
        "passiveTooltipsZh": [],
        "tiers": tiers_out if tiers_out else None,
        "attributes": matched.get("Attributes"),
        "abilities": abilities,
        "auras": auras,
    }
    cloned_card = {k: v for k, v in cloned_card.items() if v is not None}

    card_file = mod_dir / "cards" / "custom_cards.json"
    doc = read_json_file(card_file) if card_file.is_file() else {"schemaVersion": 1, "modId": mod_id, "cards": []}
    doc["cards"] = [c for c in doc.get("cards", []) if c.get("customId", "").lower() != custom_id.lower()]
    doc["cards"].append(cloned_card)
    write_json_file(card_file, doc)
    print(f"✅ 已克隆原版白名单卡 {cid} ({sm['titleEn']}) → {custom_id}，写入 {card_file}")
    return 0


# ════════════════════════════════════════════════════════════════════════════
# 纯 Python 静态 8 闸门离线校验引擎（与 C# ModValidator 保持错误码一致）
# ════════════════════════════════════════════════════════════════════════════

def run_python_static_8_gates(mod_dir: Path, data_root: Path | None) -> tuple[int, dict[str, Any]]:
    manifest_path = mod_dir / "mod.json"
    if not manifest_path.is_file():
        return 2, {
            "ok": True,
            "passed": False,
            "modId": mod_dir.name,
            "errorCount": 1,
            "warningCount": 0,
            "summaryZh": f"缺少必需的 mod.json：{manifest_path}",
            "gates": [{"gateId": 1, "key": "schema_structure", "nameZh": "闸门 1：Schema 结构与完整性", "status": "FAIL",
                       "issues": [{"severity": "Error", "code": "MANIFEST_NOT_FOUND", "message": "缺少 mod.json"}]}],
        }

    manifest = read_json_file(manifest_path)
    mod_id = str(manifest.get("id") or "").strip()
    base_cards = load_base_cards_from_db(data_root) if data_root else {}
    whitelist = load_whitelist(data_root) if data_root else set()
    known_types = load_known_rule_types()

    gate_issues: dict[int, list[dict[str, Any]]] = {i: [] for i in range(1, 9)}

    def add_issue(gate: int, sev: str, code: str, msg: str, target: str | None = None, hint: str | None = None):
        gate_issues[gate].append({
            "severity": sev, "modId": mod_id or mod_dir.name, "code": code,
            "message": msg, "targetId": target, "gateId": gate, "fixHintZh": hint,
        })

    # 加载内容文件
    def collect_list(subdir: str, key: str) -> list[dict[str, Any]]:
        d = mod_dir / subdir
        out: list[dict[str, Any]] = []
        if not d.is_dir():
            return out
        for f in sorted(d.rglob("*.json")):
            try:
                raw = read_json_file(f)
                if isinstance(raw, list):
                    out.extend([x for x in raw if isinstance(x, dict)])
                elif isinstance(raw, dict) and isinstance(raw.get(key), list):
                    out.extend([x for x in raw[key] if isinstance(x, dict)])
            except Exception as ex:
                add_issue(1, "Error", "JSON_PARSE_ERROR", f"解析 {f.relative_to(mod_dir)} 失败：{ex}", str(f.name))
        return out

    cards = collect_list("cards", "cards")
    patches = collect_list("patches", "patches")
    events = collect_list("events", "events")

    # Gate 1: Schema & 废字段
    if manifest.get("schemaVersion") != 1:
        add_issue(1, "Error", "UNSUPPORTED_SCHEMA_VERSION", f"schemaVersion 必须为 1（当前：{manifest.get('schemaVersion')}）")
    if manifest.get("scope") not in VALID_SCOPES:
        add_issue(1, "Error", "INVALID_MOD_SCOPE", f"非法 scope：{manifest.get('scope')}")
    for rk, rv in (manifest.get("contentRoots") or {}).items():
        if isinstance(rv, str) and not is_safe_rel_path(rv.replace("*", "x")):
            add_issue(1, "Error", "SCHEMA_PATH_TRAVERSAL", f"contentRoots.{rk}='{rv}' 包含目录穿越。")

    def check_legacy(obj: dict[str, Any], tid: str):
        for k in obj.keys():
            if k in FORBIDDEN_LEGACY_FIELDS:
                add_issue(1, "Error", "UNSUPPORTED_LEGACY_FIELD",
                          f"'{tid}' 包含引擎不会执行的虚构字段 '{k}'。", tid,
                          "请改用标准 abilities (TTriggerOnCardSelected) 与 attributes.BuyPrice。")

    # Gate 2: ID 命名空间与冲突
    if not MOD_ID_RE.match(mod_id):
        add_issue(2, "Error", "INVALID_MOD_ID", f"非法 Mod ID '{mod_id}'")
    prefix = f"{mod_id}:"
    local_ids: set[str] = set()

    all_card_objs: list[dict[str, Any]] = []
    for c in cards:
        all_card_objs.append(c)
    for ev in events:
        eid = ev.get("eventId") or ev.get("customId") or ""
        check_legacy(ev, eid)
        all_card_objs.append({
            "customId": eid,
            "baseVisualTemplateId": ev.get("baseVisualTemplateId") or "",
            "type": ev.get("eventType") or ev.get("type") or "EventEncounter",
            "size": "Medium",
            "startingTier": "Bronze",
            "titleZh": ev.get("titleZh") or "",
            "descZh": ev.get("descZh") or "",
            "customArtFile": ev.get("customArtFile"),
        })
        for st in (ev.get("steps") or []):
            all_card_objs.append(st)

    for obj in all_card_objs:
        cid = str(obj.get("customId") or "").strip()
        check_legacy(obj, cid)
        if not cid:
            add_issue(2, "Error", "MISSING_CUSTOM_CARD_ID", "自定义条目缺少 customId")
            continue
        if mod_id and not cid.lower().startswith(prefix.lower()):
            add_issue(2, "Error", "ID_NAMESPACE_VIOLATION", f"ID '{cid}' 必须以 '{prefix}' 为前缀。", cid)
        if cid.lower() in base_cards or cid.lower() in local_ids:
            add_issue(2, "Error", "DUPLICATE_CARD_ID", f"ID '{cid}' 冲突。", cid)
        local_ids.add(cid.lower())

        # Gate 3: 白名单 + 同 Type + Item 同 Size
        bvid = str(obj.get("baseVisualTemplateId") or "").strip()
        if not bvid:
            add_issue(3, "Error", "MISSING_BASE_VISUAL_ID", f"'{cid}' 缺少 baseVisualTemplateId。", cid)
        else:
            if whitelist and bvid.lower() not in whitelist:
                add_issue(3, "Error", "BASE_VISUAL_NOT_IN_WHITELIST", f"'{cid}' 载体 '{bvid}' 不在 valid_card_ids.json 白名单中。", cid)
            btpl = base_cards.get(bvid.lower())
            if base_cards and not btpl:
                add_issue(3, "Error", "BASE_VISUAL_NOT_FOUND", f"'{cid}' 载体 '{bvid}' 在 GameData.db 中不存在。", cid)
            elif btpl:
                if btpl.get("Type") != obj.get("type"):
                    add_issue(3, "Error", "BASE_VISUAL_TYPE_MISMATCH",
                              f"'{cid}' (Type={obj.get('type')}) 与载体 Type={btpl.get('Type')} 不一致。", cid)
                if obj.get("type") == "Item" and str(btpl.get("Size")).lower() != str(obj.get("size")).lower():
                    add_issue(3, "Error", "BASE_VISUAL_SIZE_MISMATCH",
                              f"'{cid}' (Size={obj.get('size')}) 与载体 Size={btpl.get('Size')} 不一致。", cid)

    for p in patches:
        tid = str(p.get("targetTemplateId") or "").strip()
        if not tid or (base_cards and tid.lower() not in base_cards):
            add_issue(2, "Error", "PATCH_TARGET_NOT_FOUND", f"原位补丁目标 '{tid}' 在 GameData.db 中不存在。", tid)

    # Gate 4: 递归 $type 与 Prerequisite 检查
    def walk_rules(node: Any, cid: str, in_prereq: bool = False):
        if isinstance(node, list):
            for it in node:
                walk_rules(it, cid, in_prereq)
            return
        if not isinstance(node, dict):
            return
        t = node.get("$type")
        if isinstance(t, str):
            if in_prereq and t not in SUPPORTED_PREREQ_TYPES:
                add_issue(4, "Error", "UNSUPPORTED_PREREQUISITE_TYPE",
                          f"'{cid}' 使用了未实现前置条件 '$type={t}'。", cid,
                          "请改用 TPrerequisitePlayer + TTargetPlayerAbsolute + TPlayerConditionalAttribute。")
            elif known_types and t not in known_types and not t.startswith(("TActionMod_", "TTriggerMod_")):
                add_issue(4, "Error", "UNKNOWN_RULE_TYPE", f"'{cid}' 使用了非法 '$type={t}'。", cid)
        for k, v in node.items():
            walk_rules(v, cid, in_prereq=(k.lower() == "prerequisites"))

    for c in all_card_objs:
        cid = str(c.get("customId") or "")
        walk_rules(c.get("abilities"), cid)
        walk_rules(c.get("auras"), cid)
        walk_rules(c.get("enchantments"), cid)

    # Gate 5: 冷却毫秒 / 数值上界 / 自充能防死循环
    for c in cards:
        cid = str(c.get("customId") or "")
        abilities = c.get("abilities") or {}
        has_fired = any((ab.get("trigger") or {}).get("$type") == "TTriggerOnCardFired" for ab in abilities.values() if isinstance(ab, dict))
        cds: list[float] = []
        for bag in [c.get("attributes") or {}] + [td.get("attributes") or {} for td in (c.get("tiers") or {}).values() if isinstance(td, dict)]:
            for ak, av in bag.items():
                if isinstance(av, (int, float)) and (av > 100000 or av < -10000):
                    add_issue(5, "Error", "NUMERIC_OUT_OF_SAFE_BOUNDS", f"'{cid}' 属性 {ak}={av} 超出安全上界。", cid)
            if "CooldownMax" in bag:
                cds.append(float(bag["CooldownMax"]))
        if c.get("type") == "Item" and has_fired:
            if not cds:
                add_issue(5, "Error", "COOLDOWN_MISSING", f"主动开火物品 '{cid}' 缺少 CooldownMax。", cid)
            for cd in cds:
                if cd < 1000:
                    add_issue(5, "Error", "COOLDOWN_TOO_LOW_OR_UNIT_ERROR",
                              f"物品 '{cid}' CooldownMax={cd} 低于 1000ms（疑似将秒误填为毫秒）。", cid)

    # Gate 6: 奇遇 DAG 三色 DFS
    graph: dict[str, list[str]] = {}
    for ev in events:
        eid = str(ev.get("eventId") or ev.get("customId") or "").strip()
        graph.setdefault(eid, [])
        for st in (ev.get("steps") or []):
            sid = str(st.get("customId") or "").strip()
            graph[eid].append(sid)
            graph.setdefault(sid, [])
            for opt in (st.get("nextEncounterOnSelection") or []):
                tid = str(opt.get("id") or "").strip()
                if tid:
                    graph[sid].append(tid)
                    if tid.lower() not in local_ids and tid.lower() not in base_cards:
                        add_issue(6, "Error", "ENCOUNTER_DANGLING_TARGET", f"步骤 '{sid}' 跳转目标 '{tid}' 不存在。", sid)
    color: dict[str, int] = {}
    def dfs(u: str) -> bool:
        color[u] = 1
        for v in graph.get(u, []):
            if v == u or color.get(v) == 1:
                return True
            if v in graph and color.get(v, 0) == 0 and dfs(v):
                return True
        color[u] = 2
        return False
    if any(color.get(n, 0) == 0 and dfs(n) for n in graph):
        add_issue(6, "Error", "ENCOUNTER_CYCLE_DETECTED", "检测到奇遇跳转环路，请断开环边形成严格 DAG。")

    # Gate 7: 中文与占位符括号检查
    for c in all_card_objs:
        cid = str(c.get("customId") or "")
        title = c.get("titleZh") or c.get("nameZh") or ""
        if not str(title).strip():
            add_issue(7, "Error", "MISSING_ZH_TITLE", f"'{cid}' 缺少中文标题 titleZh。", cid)
        for tip in list(c.get("activeTooltipsZh") or []) + list(c.get("passiveTooltipsZh") or []):
            if tip.count("{") != tip.count("}"):
                add_issue(7, "Error", "BROKEN_PLACEHOLDER_SYNTAX", f"'{cid}' 提示文本花括号未闭合：{tip}", cid)

    # Gate 8: PNG 资产与 art_manifest.json SHA-256 校验
    assets_dir = mod_dir / "assets"
    expected_sha: dict[str, str] = {}
    am_path = assets_dir / "art_manifest.json"
    if am_path.is_file():
        try:
            am = read_json_file(am_path)
            for ent in (am.get("assets") or []):
                lp = (ent.get("logicalPath") or ("assets/" + ent.get("filename", ""))).replace("\\", "/").lstrip("/")
                if lp and ent.get("sha256"):
                    expected_sha[lp.lower()] = str(ent["sha256"]).lower()
        except Exception:
            pass

    total_bytes = 0
    found_assets: set[str] = set()
    if assets_dir.is_dir():
        for f in sorted(assets_dir.rglob("*")):
            if not f.is_file() or f.suffix.lower() not in {".png", ".webp", ".jpg", ".jpeg"}:
                continue
            rel = f.relative_to(mod_dir).as_posix()
            found_assets.add(rel.lower())
            raw = f.read_bytes()
            total_bytes += len(raw)
            ok_img, ct, w, h = inspect_png_header(raw)
            if ct == "image/webp" or f.suffix.lower() == ".webp":
                add_issue(8, "Error", "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY", f"资产 '{rel}' 为 WebP，Unity 原生不解码 WebP，请转 PNG。", rel)
                continue
            if not ok_img:
                add_issue(8, "Error", "ASSET_MAGIC_INVALID", f"资产 '{rel}' PNG 魔数头无效。", rel)
                continue
            if len(raw) > MAX_ASSET_BYTES:
                add_issue(8, "Error", "ASSET_SIZE_EXCEEDED", f"资产 '{rel}' 超出 2MB 上限。", rel)
            if not (64 <= w <= 1024 and 64 <= h <= 1024):
                add_issue(8, "Error", "ASSET_DIMENSION_OUT_OF_RANGE", f"资产 '{rel}' 尺寸 {w}x{h} 超出 [64, 1024]。", rel)
            sha = hashlib.sha256(raw).hexdigest().lower()
            if rel.lower() in expected_sha and expected_sha[rel.lower()] != sha:
                add_issue(8, "Error", "ASSET_SHA256_MISMATCH", f"资产 '{rel}' SHA-256 与 art_manifest.json 不一致。", rel)
    if total_bytes > MAX_TOTAL_ASSETS_BYTES:
        add_issue(8, "Error", "ASSET_SIZE_EXCEEDED", "总资产超出 32MB 限制。")

    names = {
        1: ("schema_structure", "闸门 1：Schema 结构、废字段与四阶绑定完整性"),
        2: ("id_namespace_and_deps", "闸门 2：ID 全局命名空间防撞与依赖"),
        3: ("visual_whitelist_and_size", "闸门 3：白名单视觉载体与同 Type/Size 防白块穿模"),
        4: ("rule_type_whitelist", "闸门 4：全量 $type / 触发器 / 前置条件递归白名单"),
        5: ("numeric_and_loop_safety", "闸门 5：冷却毫秒 / 数值上界 / 倍率与自充能防死循环"),
        6: ("encounter_dag_integrity", "闸门 6：奇遇事件链三色 DFS 防环与死胡同检查"),
        7: ("localization_and_placeholders", "闸门 7：中文描述与占位符校验"),
        8: ("png_asset_integrity", "闸门 8：PNG 魔数 / 分辨率 / 体积与 SHA-256 校验"),
    }
    gates = []
    err_cnt = 0
    warn_cnt = 0
    for gid in range(1, 9):
        iss = gate_issues[gid]
        e = sum(1 for x in iss if x["severity"] == "Error")
        w = sum(1 for x in iss if x["severity"] == "Warning")
        err_cnt += e
        warn_cnt += w
        st = "FAIL" if e > 0 else ("WARN" if w > 0 else "PASS")
        gates.append({"gateId": gid, "key": names[gid][0], "nameZh": names[gid][1], "status": st, "durationMs": 0, "issues": iss})

    passed = err_cnt == 0
    report = {
        "ok": True,
        "passed": passed,
        "modId": mod_id or mod_dir.name,
        "validatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "errorCount": err_cnt,
        "warningCount": warn_cnt,
        "summaryZh": f"离线静态 8 闸门{'全部通过' if passed else '未通过'}（{err_cnt} 错误，{warn_cnt} 建议）。",
        "gates": gates,
    }
    return (0 if passed else 2), report


def call_server_validate(server_url: str, mod_id: str, matches: int, seed: int) -> tuple[int, dict[str, Any]]:
    url = f"{server_url.rstrip('/')}/api/workshop/mods/{urllib.parse.quote(mod_id)}/validate"
    req = urllib.request.Request(
        url,
        data=json.dumps({"matches": matches, "seed": seed}).encode("utf-8"),
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            rep = json.loads(resp.read().decode("utf-8"))
            if rep.get("passed"):
                return 0, rep
            static_fail = any((g.get("gateId") or 0) <= 8 and g.get("status") == "FAIL" for g in (rep.get("gates") or []))
            return (2 if static_fail else 3), rep
    except urllib.error.URLError as ex:
        return 1, {"ok": False, "passed": False, "error": f"无法连接服务端 {url}：{ex}"}


def call_offline_cli(cmd_name: str, mod_dir: Path, matches: int, seed: int, static_only: bool, data_root: Path | None) -> tuple[int, dict[str, Any]]:
    cli_cmd = locate_bazaar_cli()
    if not cli_cmd:
        return 1, {"ok": False, "passed": False, "error": "未找到已编译的 BazaarCli（请先执行 dotnet build src/BazaarCli/BazaarCli.csproj -c Release）。"}
    full_cmd = cli_cmd + [cmd_name, str(mod_dir.resolve()), "--matches", str(matches), "--seed", str(seed), "--json"]
    if static_only:
        full_cmd.append("--static-only")
    if data_root:
        full_cmd.extend(["--data-root", str(data_root)])
    proc = subprocess.run(full_cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    raw_out = (proc.stdout or "").strip()
    if not raw_out:
        return (proc.returncode or 1), {"ok": False, "passed": False, "error": (proc.stderr or "BazaarCli 未输出 JSON 报告").strip()}
    try:
        # 兼容前方可能有日志行，取第一个 '{' 起的 JSON
        idx = raw_out.find("{")
        rep = json.loads(raw_out[idx:] if idx >= 0 else raw_out)
        return proc.returncode, rep
    except Exception as ex:
        return 1, {"ok": False, "passed": False, "error": f"解析 BazaarCli JSON 失败：{ex}\n原始输出：{raw_out[:400]}"}


def print_validation_report(rep: dict[str, Any], as_json: bool) -> None:
    if as_json:
        print(json.dumps(rep, ensure_ascii=False, indent=2))
        return
    passed = bool(rep.get("passed"))
    print(f"=== TheBazaar Mod 体检报告：{rep.get('modId', '?')} | {'✅ PASS 全绿通过' if passed else '❌ FAIL 存在错误'} ===")
    print(f"摘要：{rep.get('summaryZh') or rep.get('error') or ''}")
    for g in (rep.get("gates") or []):
        st = g.get("status", "?")
        icon = {"PASS": "🟢", "WARN": "🟡", "FAIL": "🔴", "SKIP": "⚪"}.get(st, "•")
        print(f"  {icon} [{st:4s}] {g.get('nameZh')} ({g.get('durationMs', 0)}ms)")
        for it in (g.get("issues") or []):
            print(f"       [GATE #{g.get('gateId')}] [{it.get('code')}] {it.get('targetId') or '-'}: {it.get('message')}")
            if it.get("fixHintZh"):
                print(f"       → 修复建议：{it['fixHintZh']}")
    sr = rep.get("combatStressReport")
    if isinstance(sr, dict):
        print(f"  ⚔️ 真实战斗压测：{sr.get('matchesRun')} 场 | 异常={sr.get('exceptions')} | 300s僵局={sr.get('maxDurationHits')} | >200ms={sr.get('slowMatchesOver200ms')} | 平均={sr.get('avgMatchMs')}ms 最大={sr.get('maxMatchMs')}ms | 胜率={sr.get('modSideWinRatePct')}%")
        print(f"     平衡提示：{sr.get('balanceHintZh')}")


# ════════════════════════════════════════════════════════════════════════════
# 子命令 3 & 4：validate / sim-test
# ════════════════════════════════════════════════════════════════════════════

def cmd_validate(args: argparse.Namespace) -> int:
    mod_dir = Path(args.mod_dir).resolve()
    if not mod_dir.is_dir():
        print(f"[错误] Mod 目录不存在：{mod_dir}", file=sys.stderr)
        return 1
    data_root = locate_data_root(args.data_root)

    if args.server:
        mod_id = mod_dir.name
        if (mod_dir / "mod.json").is_file():
            try:
                mod_id = read_json_file(mod_dir / "mod.json").get("id") or mod_id
            except Exception:
                pass
        code, rep = call_server_validate(args.server, mod_id, args.matches, args.seed)
        print_validation_report(rep, args.json)
        return code

    # 优先走真实 C# 引擎（10 闸门 + 50 场真实 CombatSimulator），除非显式 --static-only 或 bz 未编译
    cli_available = locate_bazaar_cli() is not None
    if args.offline_sim or (not args.static_only and cli_available):
        code, rep = call_offline_cli("mod-validate", mod_dir, args.matches, args.seed, static_only=args.static_only, data_root=data_root)
        print_validation_report(rep, args.json)
        return code

    code, rep = run_python_static_8_gates(mod_dir, data_root)
    print_validation_report(rep, args.json)
    return code


def cmd_sim_test(args: argparse.Namespace) -> int:
    mod_dir = Path(args.mod_dir).resolve()
    if not mod_dir.is_dir():
        print(f"[错误] Mod 目录不存在：{mod_dir}", file=sys.stderr)
        return 1
    data_root = locate_data_root(args.data_root)

    if args.server:
        mod_id = mod_dir.name
        if (mod_dir / "mod.json").is_file():
            try:
                mod_id = read_json_file(mod_dir / "mod.json").get("id") or mod_id
            except Exception:
                pass
        code, rep = call_server_validate(args.server, mod_id, args.matches, args.seed)
        print_validation_report(rep, args.json)
        return code

    code, rep = call_offline_cli("mod-sim-test", mod_dir, args.matches, args.seed, static_only=False, data_root=data_root)
    print_validation_report(rep, args.json)
    return code


# ════════════════════════════════════════════════════════════════════════════
# 子命令 5 & 6：pack / unpack（标准 .bazaarmod.zip 四重安检）
# ════════════════════════════════════════════════════════════════════════════

def cmd_pack(args: argparse.Namespace) -> int:
    mod_dir = Path(args.mod_dir).resolve()
    if not (mod_dir / "mod.json").is_file():
        print(f"[错误] 缺少 mod.json：{mod_dir}", file=sys.stderr)
        return 1
    data_root = locate_data_root(args.data_root)

    # 强制前置门禁：打包前必须全绿
    if args.allow_static_only or locate_bazaar_cli() is None:
        code, rep = run_python_static_8_gates(mod_dir, data_root)
    else:
        code, rep = call_offline_cli("mod-validate", mod_dir, matches=50, seed=2026131, static_only=False, data_root=data_root)

    if code != 0:
        print_validation_report(rep, as_json=False)
        print("[阻断] 校验未全绿通过，拒绝打包带病 .bazaarmod.zip！", file=sys.stderr)
        return code

    manifest = read_json_file(mod_dir / "mod.json")
    mod_id = manifest["id"]
    version = manifest.get("version", "1.0.0")
    out_path = Path(args.output).resolve() if args.output else (REPO_ROOT / "dist" / "mods" / f"{mod_id}-{version}.bazaarmod.zip")
    out_path.parent.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for f in sorted(mod_dir.rglob("*")):
            if not f.is_file():
                continue
            rel = f.relative_to(mod_dir).as_posix()
            if rel.startswith(".") or "/." in rel or "__pycache__" in rel or f.suffix.lower() in FORBIDDEN_ZIP_EXTS:
                continue
            zf.write(f, arcname=rel)
        stamp = {
            "modId": mod_id,
            "version": version,
            "packedAtUtc": datetime.now(timezone.utc).isoformat(),
            "validationPassed": True,
            "errorCount": rep.get("errorCount", 0),
            "warningCount": rep.get("warningCount", 0),
            "summaryZh": rep.get("summaryZh", ""),
        }
        zf.writestr("validation_stamp.json", json.dumps(stamp, ensure_ascii=False, indent=2))

    print(f"✅ 校验全绿并成功打包：{out_path} ({out_path.stat().st_size:,} bytes)")
    return 0


def cmd_unpack(args: argparse.Namespace) -> int:
    zip_path = Path(args.zip_file).resolve()
    if not zip_path.is_file():
        print(f"[错误] 压缩包不存在：{zip_path}", file=sys.stderr)
        return 1
    if zip_path.stat().st_size > MAX_ZIP_BYTES:
        print(f"[安全拦截] 压缩包超出 25 MB 上限。", file=sys.stderr)
        return 4

    with tempfile.TemporaryDirectory(prefix="bazaar_unpack_") as tmp:
        tmp_dir = Path(tmp)
        try:
            with zipfile.ZipFile(zip_path, "r") as zf:
                infos = zf.infolist()
                if len(infos) == 0 or len(infos) > MAX_ZIP_ENTRIES:
                    print(f"[安全拦截] 压缩包条目数异常（{len(infos)}，上限 {MAX_ZIP_ENTRIES}）。", file=sys.stderr)
                    return 4
                total_uncomp = 0
                for info in infos:
                    if info.is_dir():
                        continue
                    rel = info.filename.replace("\\", "/")
                    if not is_safe_rel_path(rel):
                        print(f"[安全拦截] 检测到路径穿越（Zip Slip）：{info.filename}", file=sys.stderr)
                        return 4
                    if Path(rel).suffix.lower() in FORBIDDEN_ZIP_EXTS:
                        print(f"[安全拦截] 禁止导入可执行/脚本文件：{rel}", file=sys.stderr)
                        return 4
                    total_uncomp += info.file_size
                    if total_uncomp > MAX_ZIP_UNCOMPRESSED:
                        print("[安全拦截] 解压总大小超过 64 MB（Zip Bomb 防护）。", file=sys.stderr)
                        return 4
                    dest = (tmp_dir / rel).resolve()
                    if not str(dest).startswith(str(tmp_dir.resolve())):
                        print(f"[安全拦截] 目标越界：{rel}", file=sys.stderr)
                        return 4
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    with zf.open(info, "r") as src, open(dest, "wb") as dst:
                        shutil.copyfileobj(src, dst)
        except zipfile.BadZipFile:
            print("[安全拦截] 不是有效的 ZIP 文件。", file=sys.stderr)
            return 4

        actual_dir = tmp_dir
        if not (actual_dir / "mod.json").is_file():
            subs = [p for p in tmp_dir.iterdir() if p.is_dir()]
            if len(subs) == 1 and (subs[0] / "mod.json").is_file():
                actual_dir = subs[0]
            else:
                print("[安全拦截] 压缩包缺少 mod.json。", file=sys.stderr)
                return 4

        data_root = locate_data_root(args.data_root)
        code, rep = run_python_static_8_gates(actual_dir, data_root)
        if code != 0:
            print_validation_report(rep, as_json=False)
            print("[阻断] 压缩包未通过静态防 Bug 铁闸，已拒绝解包。", file=sys.stderr)
            return code

        mod_id = read_json_file(actual_dir / "mod.json")["id"]
        if args.validate_only:
            print(f"✅ 检疫通过（--validate-only 未落盘）：{mod_id}")
            return 0

        into_root = Path(args.into).resolve()
        into_root.mkdir(parents=True, exist_ok=True)
        dest_mod = into_root / mod_id
        if dest_mod.exists():
            shutil.rmtree(dest_mod)
        shutil.copytree(actual_dir, dest_mod)
        print(f"✅ 安全检疫通过并已解包至：{dest_mod}")
        return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="TheBazaar V13.1 标准化工业级 Mod SDK 命令行工具")
    sub = p.add_subparsers(dest="command", required=True)

    p_new = sub.add_parser("new", help="初始化标准 Mod 骨架目录")
    p_new.add_argument("mod_id", help="唯一 Mod ID（小写字母/数字/连字符）")
    p_new.add_argument("--name", default="", help="中文展示名")
    p_new.add_argument("--author", default="社区创作者", help="作者署名")
    p_new.add_argument("--version", default="1.0.0", help="初始版本号")
    p_new.add_argument("--description", default="", help="中文简介")
    p_new.add_argument("--scope", default="multiplayer-safe", choices=sorted(VALID_SCOPES))
    p_new.add_argument("--mods-dir", default=None)
    p_new.add_argument("--force", action="store_true")
    p_new.set_defaults(func=cmd_new)

    p_clone = sub.add_parser("clone-card", help="检索 3,368 原版白名单卡并一键克隆或生成原位补丁")
    p_clone.add_argument("query", nargs="?", default=None, help="原版卡 GUID 或名称")
    p_clone.add_argument("--search", default=None, help="按关键词搜索原版白名单卡")
    p_clone.add_argument("--type", default=None)
    p_clone.add_argument("--size", default=None)
    p_clone.add_argument("--hero", default=None)
    p_clone.add_argument("--limit", type=int, default=15)
    p_clone.add_argument("--include-non-whitelist", action="store_true")
    p_clone.add_argument("--mod", default=None, help="目标 Mod ID")
    p_clone.add_argument("--as", dest="slug", default=None, help="克隆新卡的 slug 名称")
    p_clone.add_argument("--mode", choices=["clone", "patch"], default="clone")
    p_clone.add_argument("--mods-dir", default=None)
    p_clone.add_argument("--data-root", default=None)
    p_clone.add_argument("--json", action="store_true")
    p_clone.set_defaults(func=cmd_clone_card)

    p_val = sub.add_parser("validate", help="执行 10 道防 Bug 铁闸 + 50 场真实战斗体检")
    p_val.add_argument("mod_dir", help="Mod 目录路径")
    p_val.add_argument("--strict", action="store_true")
    p_val.add_argument("--server", default=None, help="运行中服务端地址（如 http://127.0.0.1:8080）")
    p_val.add_argument("--offline-sim", action="store_true", help="强制通过本地 BazaarCli 跑 10 道铁闸 + 50 场真实战斗")
    p_val.add_argument("--static-only", action="store_true", help="仅运行静态 8 闸门")
    p_val.add_argument("--matches", type=int, default=50)
    p_val.add_argument("--seed", type=int, default=2026131)
    p_val.add_argument("--data-root", default=None)
    p_val.add_argument("--json", action="store_true")
    p_val.set_defaults(func=cmd_validate)

    p_sim = sub.add_parser("sim-test", help="独立运行 50 场真实 CombatSimulator 压力体检")
    p_sim.add_argument("mod_dir", help="Mod 目录路径")
    p_sim.add_argument("--matches", type=int, default=50)
    p_sim.add_argument("--seed", type=int, default=2026131)
    p_sim.add_argument("--server", default=None)
    p_sim.add_argument("--offline-cli", action="store_true")
    p_sim.add_argument("--data-root", default=None)
    p_sim.add_argument("--json", action="store_true")
    p_sim.set_defaults(func=cmd_sim_test)

    p_pack = sub.add_parser("pack", help="校验全绿后打包标准 .bazaarmod.zip")
    p_pack.add_argument("mod_dir", help="Mod 目录路径")
    p_pack.add_argument("-o", "--output", default=None)
    p_pack.add_argument("--allow-static-only", action="store_true")
    p_pack.add_argument("--data-root", default=None)
    p_pack.set_defaults(func=cmd_pack)

    p_unpack = sub.add_parser("unpack", help="四重安检并解包社区 .bazaarmod.zip")
    p_unpack.add_argument("zip_file", help=".bazaarmod.zip 路径")
    p_unpack.add_argument("--into", default=str(REPO_ROOT / "mods"))
    p_unpack.add_argument("--validate-only", action="store_true")
    p_unpack.add_argument("--data-root", default=None)
    p_unpack.set_defaults(func=cmd_unpack)

    return p


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())
