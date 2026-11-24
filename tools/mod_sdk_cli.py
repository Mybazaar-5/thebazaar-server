#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TheBazaar V15.4 标准化工业级 Mod 命令行 SDK（tools/mod_sdk_cli.py）

零第三方强依赖（纯 Python 3.10+ 标准库即可执行全部离线检索、静态 8 闸门校验、安全打包/解包与在线发布；
通过 --server 或内置离线 BazaarCli 桥接可无缝执行完整 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 压测）。

统一退出码：
  0 = 全部执行/校验通过（0 Error）
  1 = 参数错误、文件不存在或外部依赖不可达
  2 = 静态防 Bug 铁闸（#1~#8）未通过
  3 = 动态 50 场战斗压力体检（#9）或隔离回归（#10）未通过
  4 = .bazaarmod.zip 安全检疫失败（Zip Slip / Zip Bomb / 符号链接 / 可执行文件）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import stat
import struct
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import uuid
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
DRIVE_PREFIX_RE = re.compile(r"^[A-Za-z]:")
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

# 严格对齐 BazaarCore/Mods/MechanicModels.cs V15.4 单一事实源闭集
ALLOWED_MECHANIC_HOOKS = {
    "OnFightStart", "OnCardFired", "BeforeDamage", "BeforeStatusApply",
    "BeforeCardStateChange", "BeforeCardFire", "AfterCardFire",
    "OnCustomEvent", "OnTriggerEvent", "PostCombatCommit",
}
ALLOWED_MECHANIC_OPS = {
    "If", "Repeat", "ScheduleDelayed", "CancelHook", "ModifyHookAmount", "Stop",
    "SetFlag", "ClearFlag", "ModifyCounter", "DealDamage", "ApplyStatus",
    "ModifyCardAttribute", "ModifyPlayerAttribute", "ChangeCardState",
    "ForceUseCard", "EmitCustomEvent", "QueuePostCombatRemoveCard", "QueuePostCombatModifyAttr",
}
ALLOWED_MECHANIC_SCOPES = {
    "SelfCard", "OwnerBoard", "EnemyBoard", "OwnerPlayer", "EnemyPlayer", "Global", "Any",
}
ALLOWED_MECHANIC_PARAM_TYPES = {
    "int", "float", "string", "bool", "tag", "attribute", "targetMode", "vfxKey",
}
ALLOWED_MECHANIC_TARGET_MODES = {
    "Self", "HookTarget", "HookSource", "AllAllies", "OtherAllies", "AllEnemies",
    "Adjacent", "Left", "Right", "LeftMostAlly", "RightMostAlly", "RandomAllies", "RandomEnemies",
}
ALLOWED_MECHANIC_STATUSES = {
    "Shield", "Burn", "Poison", "Heal", "Regen", "Haste", "Slow", "Freeze", "Charge",
}
ALLOWED_SYNC_ATTRIBUTES = {f"Custom_{i}" for i in range(9)}  # Custom_0..Custom_8
ALLOWED_PLAYER_ATTRIBUTES = {
    "Health", "HealthMax", "Shield", "Burn", "Poison",
    "HealthRegen", "Regen", "Rage", "RageMax", "Tempo", "Joy",
    "Gold", "Income", "PercentDamageReduction",
}
ALLOWED_COUNTER_DISPLAY_ATTRIBUTES = ALLOWED_SYNC_ATTRIBUTES | {
    "Counter", "Ammo", "AmmoMax",
    "DamageAmount", "ShieldApplyAmount", "BurnApplyAmount", "PoisonApplyAmount",
    "HealAmount", "RegenApplyAmount", "CritChance", "Multicast", "CooldownMax",
}
ALLOWED_CD_STYLES = {"auto", "pips", "bar", "gem", "pill"}
ALLOWED_CD_VALUE_TYPES = {"count", "progress", "percent", "seconds", "multiplier", "delta"}
ALLOWED_CD_POSITIONS = {"bottom", "bottomRight", "topLeft", "topRight"}

MAX_ASSET_BYTES = 2 * 1024 * 1024
MAX_TOTAL_ASSETS_BYTES = 32 * 1024 * 1024
MAX_ZIP_BYTES = 25 * 1024 * 1024
MAX_ZIP_UNCOMPRESSED = 64 * 1024 * 1024
MAX_ZIP_ENTRIES = 200
STREAM_CHUNK_BYTES = 65536


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
    if "\x00" in rel:
        return False
    norm = rel.replace("\\", "/").strip()
    if norm.startswith("/") or DRIVE_PREFIX_RE.match(norm) or ":" in norm:
        return False
    parts = [p for p in norm.split("/") if p]
    return bool(parts) and all(p not in (".", "..") for p in parts)


def _parse_jpeg_dimensions(data: bytes) -> tuple[int, int] | None:
    """解析标准 JPEG SOF0/SOF1/SOF2/SOF3 段的真实宽高。"""
    if len(data) < 4 or data[0:2] != b"\xff\xd8":
        return None
    idx = 2
    n = len(data)
    while idx + 3 < n:
        if data[idx] != 0xFF:
            idx += 1
            continue
        while idx < n and data[idx] == 0xFF:
            idx += 1
        if idx >= n:
            break
        marker = data[idx]
        idx += 1
        if marker in (0xD8, 0xD9, 0x01) or (0xD0 <= marker <= 0xD7):
            continue
        if idx + 2 > n:
            break
        seg_len = struct.unpack(">H", data[idx:idx + 2])[0]
        if seg_len < 2 or idx + seg_len > n:
            break
        if marker in (0xC0, 0xC1, 0xC2, 0xC3):
            if seg_len >= 7:
                h, w = struct.unpack(">HH", data[idx + 3:idx + 7])
                if w > 0 and h > 0:
                    return w, h
            return None
        idx += seg_len
    return None


def inspect_image_header(data: bytes) -> tuple[bool, str, int, int]:
    """方案 A 图像头校验：放行标准 PNG 与 JPEG，显式识别并拒收 Unity 不支持的 WebP。"""
    if len(data) >= 24 and data[:8] == b"\x89PNG\r\n\x1a\n" and data[12:16] == b"IHDR":
        w, h = struct.unpack(">II", data[16:24])
        return (w > 0 and h > 0), "image/png", w, h
    if len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return False, "image/webp", 0, 0
    if len(data) >= 4 and data[:3] == b"\xff\xd8\xff":
        dims = _parse_jpeg_dimensions(data)
        if dims is not None:
            return True, "image/jpeg", dims[0], dims[1]
        return False, "image/jpeg", 0, 0
    return False, "application/octet-stream", 0, 0


# 向后兼容旧辅助名
inspect_png_header = inspect_image_header


def _has_symlink_in_chain(base_root: Path, target_path: Path) -> bool:
    """检查从 base_root 到 target_path 路径链路中是否存在符号链接。"""
    cur = target_path
    base_resolved = base_root.resolve()
    while True:
        if cur.is_symlink():
            return True
        try:
            if cur.resolve() == base_resolved:
                break
        except Exception:
            return True
        if cur.parent == cur:
            break
        cur = cur.parent
    return False


def _atomic_swap_directory(stage_dir: Path, target_dir: Path, parent_root: Path, mod_id: str) -> None:
    """同卷隐藏目录原子切换事务（使用 .bak_ 前缀兼容 ModLoader 跳过规则，异常自动回滚）。"""
    if not target_dir.exists():
        os.replace(stage_dir, target_dir)
        return
    backup_dir = parent_root / f".bak_{mod_id}_{uuid.uuid4().hex[:12]}"
    os.replace(target_dir, backup_dir)
    try:
        os.replace(stage_dir, target_dir)
    except Exception:
        try:
            if not target_dir.exists() and backup_dir.exists():
                os.replace(backup_dir, target_dir)
        except Exception:
            pass
        raise
    shutil.rmtree(backup_dir, ignore_errors=True)


# ════════════════════════════════════════════════════════════════════════════
# 子命令 1：new — 初始化标准 V15.4 Mod 骨架
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

    for sub in ["cards", "patches", "mechanics", "events", "monsters", "modes", "localization", "assets"]:
        (target / sub).mkdir(parents=True, exist_ok=True)

    manifest = {
        "$schema": "../../docs/schemas/bazaar-mod.schema.json#/$defs/ModManifest",
        "schemaVersion": 1,
        "id": mod_id,
        "name": args.name or mod_id,
        "version": args.version or "1.0.0",
        "author": args.author or "社区创作者",
        "description": args.description or f"{args.name or mod_id}：基于 BazaarModKit V15.4 标准创建的扩展包。",
        "scope": args.scope,
        "affectsGhostPool": False,
        "enabledByDefault": False,
        "loadOrder": 100,
        "dependencies": [],
        "contentRoots": {
            "cards": "cards/custom_cards.json",
            "patches": "patches/vanilla_patches.json",
            "mechanics": "mechanics/custom_mechanics.json",
            "events": "events/custom_events.json",
            "modes": "modes/*.json",
            "localization": "localization/zh-CN.json",
            "assets": "assets/*",
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
    write_json_file(target / "mechanics" / "custom_mechanics.json", {
        "$schema": "../../../docs/schemas/bazaar-mod.schema.json#/$defs/MechanicFile",
        "schemaVersion": 1,
        "modId": mod_id,
        "mechanics": [],
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
        f"- 规范版本：BazaarModKit V15.4\n"
        f"- 校验命令：`python tools/mod_sdk_cli.py validate mods/{mod_id} --strict --offline-sim`\n",
        encoding="utf-8",
    )

    print(f"✅ 已初始化标准 V15.4 Mod 骨架：{target}")
    print("下一步：python tools/mod_sdk_cli.py clone-card --search \"Kukri\" --type Item")
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

    # clone 模式：完整保留 Enchantments、Quests、Tooltips 与全部五阶
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

    active_tips: list[str] = []
    passive_tips: list[str] = []
    loc_block = matched.get("Localization") or {}
    for tip in (loc_block.get("Tooltips") or []):
        if not isinstance(tip, dict):
            continue
        txt = ((tip.get("Content") or {}).get("Text") or "").strip()
        if not txt:
            continue
        if str(tip.get("TooltipType") or "").lower() == "passive":
            passive_tips.append(txt)
        else:
            active_tips.append(txt)

    desc_en = ((loc_block.get("Description") or {}).get("Text") or matched.get("InternalDescription") or "").strip()

    cloned_card: dict[str, Any] = {
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
        "descEn": desc_en or None,
        "activeTooltipsZh": active_tips,
        "passiveTooltipsZh": passive_tips,
        "tiers": tiers_out if tiers_out else None,
        "attributes": matched.get("Attributes"),
        "enchantments": matched.get("Enchantments"),
        "abilities": abilities,
        "auras": auras,
        "quests": matched.get("Quests"),
    }
    cloned_card = {k: v for k, v in cloned_card.items() if v is not None}

    card_file = mod_dir / "cards" / "custom_cards.json"
    doc = read_json_file(card_file) if card_file.is_file() else {"schemaVersion": 1, "modId": mod_id, "cards": []}
    doc["cards"] = [c for c in doc.get("cards", []) if c.get("customId", "").lower() != custom_id.lower()]
    doc["cards"].append(cloned_card)
    write_json_file(card_file, doc)
    print(f"✅ 已克隆原版白名单卡 {cid} ({sm['titleEn']}) → {custom_id}（保留 Enchantments/Quests），写入 {card_file}")
    return 0


# ════════════════════════════════════════════════════════════════════════════
# 纯 Python 静态 8 闸门离线校验引擎（与 C# ModValidator V15.4 保持错误码一致）
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

    try:
        manifest = read_json_file(manifest_path)
    except Exception as ex:
        return 2, {
            "ok": True,
            "passed": False,
            "modId": mod_dir.name,
            "errorCount": 1,
            "warningCount": 0,
            "summaryZh": f"解析 mod.json 失败：{ex}",
            "gates": [{"gateId": 1, "key": "schema_structure", "nameZh": "闸门 1：Schema 结构与完整性", "status": "FAIL",
                       "issues": [{"severity": "Error", "code": "JSON_PARSE_ERROR", "message": f"解析 mod.json 失败：{ex}"}]}],
        }

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

    # 加载全部内容目录（含 mechanics / monsters / modes / mutators）
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
    mechanics = collect_list("mechanics", "mechanics")
    monsters = collect_list("monsters", "monsters")
    modes = collect_list("modes", "modes")
    mutators = collect_list("modes", "mutators")

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

    # Gate 2: ID 命名空间与冲突（覆盖 cards/events/steps/mechanics/monsters/modes/mutators）
    if not MOD_ID_RE.match(mod_id):
        add_issue(2, "Error", "INVALID_MOD_ID", f"非法 Mod ID '{mod_id}'")
    prefix = f"{mod_id}:"
    local_ids: set[str] = set()
    referenced_assets: list[tuple[str, str]] = []

    def record_art_ref(owner_id: str, rel: Any):
        if isinstance(rel, str) and rel.strip():
            norm = rel.replace("\\", "/").strip().lstrip("/")
            referenced_assets.append((owner_id, norm))

    all_card_objs: list[dict[str, Any]] = []
    for c in cards:
        all_card_objs.append(c)
        record_art_ref(str(c.get("customId") or ""), c.get("customArtFile"))
        record_art_ref(str(c.get("customId") or ""), c.get("customBgArtFile"))
    for ev in events:
        eid = str(ev.get("eventId") or ev.get("customId") or "").strip()
        check_legacy(ev, eid)
        record_art_ref(eid, ev.get("customArtFile"))
        record_art_ref(eid, ev.get("customBgArtFile"))
        all_card_objs.append({
            "customId": eid,
            "baseVisualTemplateId": ev.get("baseVisualTemplateId") or "",
            "type": ev.get("eventType") or ev.get("type") or "EventEncounter",
            "size": "Medium",
            "startingTier": ev.get("startingTier") or "Bronze",
            "titleZh": ev.get("titleZh") or "",
            "descZh": ev.get("descZh") or "",
            "customArtFile": ev.get("customArtFile"),
        })
        for st in (ev.get("steps") or []):
            if isinstance(st, dict):
                all_card_objs.append(st)
                record_art_ref(str(st.get("customId") or ""), st.get("customArtFile"))
                record_art_ref(str(st.get("customId") or ""), st.get("customBgArtFile"))

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
        record_art_ref(tid, p.get("customArtFile"))
        record_art_ref(tid, p.get("customBgArtFile"))

    mech_ids: set[str] = set()
    for m in mechanics:
        mid = str(m.get("id") or "").strip()
        if not mid:
            add_issue(2, "Error", "MECHANIC_MISSING_ID", "自定义机制缺少 id。")
            continue
        if mod_id and not mid.lower().startswith(prefix.lower()):
            add_issue(2, "Error", "ID_NAMESPACE_VIOLATION", f"机制 ID '{mid}' 必须以 '{prefix}' 为前缀。", mid)
        if mid.lower() in mech_ids:
            add_issue(2, "Error", "DUPLICATE_MECHANIC_ID", f"机制 ID '{mid}' 重复。", mid)
        mech_ids.add(mid.lower())

    monster_ids: set[str] = set()
    for mon in monsters:
        mon_id = str(mon.get("id") or mon.get("Id") or "").strip()
        if not mon_id:
            add_issue(2, "Error", "MONSTER_MISSING_ID", "自定义野怪缺少 id。")
            continue
        if mod_id and not mon_id.lower().startswith(prefix.lower()):
            add_issue(2, "Error", "ID_NAMESPACE_VIOLATION", f"野怪 ID '{mon_id}' 必须以 '{prefix}' 为前缀。", mon_id)
        if mon_id.lower() in monster_ids:
            add_issue(2, "Error", "DUPLICATE_MONSTER_ID", f"野怪 ID '{mon_id}' 重复。", mon_id)
        monster_ids.add(mon_id.lower())
        player = mon.get("player") or mon.get("Player") or {}
        if not isinstance(player, dict):
            add_issue(1, "Error", "MONSTER_INVALID_PLAYER", f"野怪 '{mon_id}' 缺少合法 player 对象。", mon_id)
        else:
            attrs = player.get("attributes") or player.get("Attributes") or {}
            hp = attrs.get("HealthMax")
            if hp is not None and (not isinstance(hp, (int, float)) or hp <= 0):
                add_issue(5, "Error", "MONSTER_INVALID_HEALTH", f"野怪 '{mon_id}' HealthMax 必须 > 0。", mon_id)

    mode_ids: set[str] = set()
    for md in modes:
        md_id = str(md.get("modeId") or "").strip()
        if not md_id:
            add_issue(2, "Error", "MODE_MISSING_ID", "自定义模式缺少 modeId。")
            continue
        if mod_id and not md_id.lower().startswith(prefix.lower()):
            add_issue(2, "Error", "ID_NAMESPACE_VIOLATION", f"模式 ID '{md_id}' 必须以 '{prefix}' 为前缀。", md_id)
        if md_id.lower() in mode_ids:
            add_issue(2, "Error", "DUPLICATE_MODE_ID", f"模式 ID '{md_id}' 重复。", md_id)
        mode_ids.add(md_id.lower())

    mut_ids: set[str] = set()
    for mt in mutators:
        mt_id = str(mt.get("id") or "").strip()
        if not mt_id:
            add_issue(2, "Error", "MUTATOR_MISSING_ID", "自定义突变词条缺少 id。")
            continue
        if mod_id and not mt_id.lower().startswith(prefix.lower()):
            add_issue(2, "Error", "ID_NAMESPACE_VIOLATION", f"突变词条 ID '{mt_id}' 必须以 '{prefix}' 为前缀。", mt_id)
        if mt_id.lower() in mut_ids:
            add_issue(2, "Error", "DUPLICATE_MUTATOR_ID", f"突变词条 ID '{mt_id}' 重复。", mt_id)
        mut_ids.add(mt_id.lower())

    # Gate 4: 递归 $type / Prerequisite + 机制沙盒静态校验 + counterDisplays 校验
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

    def validate_counter_display(cd: Any, owner_id: str, param_keys: set[str] | None = None):
        if not isinstance(cd, dict):
            return
        sa = cd.get("syncAttribute")
        if sa and sa not in ALLOWED_COUNTER_DISPLAY_ATTRIBUTES:
            add_issue(4, "Error", "MECHANIC_INVALID_COUNTER_ATTR",
                      f"'{owner_id}' counterDisplay.syncAttribute='{sa}' 不在合法 21 项属性白名单中。", owner_id)
        ma = cd.get("maxAttr")
        if ma and ma not in ALLOWED_COUNTER_DISPLAY_ATTRIBUTES:
            add_issue(4, "Error", "MECHANIC_INVALID_COUNTER_ATTR",
                      f"'{owner_id}' counterDisplay.maxAttr='{ma}' 不在合法 21 项属性白名单中。", owner_id)
        st_val = cd.get("style")
        if st_val and st_val not in ALLOWED_CD_STYLES:
            add_issue(4, "Error", "MECHANIC_INVALID_COUNTER_STYLE", f"'{owner_id}' counterDisplay.style='{st_val}' 非法。", owner_id)
        vt_val = cd.get("valueType")
        if vt_val and vt_val not in ALLOWED_CD_VALUE_TYPES:
            add_issue(4, "Error", "MECHANIC_INVALID_COUNTER_VALUETYPE", f"'{owner_id}' counterDisplay.valueType='{vt_val}' 非法。", owner_id)
        pos_val = cd.get("position")
        if pos_val and pos_val not in ALLOWED_CD_POSITIONS:
            add_issue(4, "Error", "MECHANIC_INVALID_COUNTER_POSITION", f"'{owner_id}' counterDisplay.position='{pos_val}' 非法。", owner_id)
        mp = cd.get("maxParam")
        if param_keys is not None and mp and mp not in param_keys:
            add_issue(4, "Error", "MECHANIC_UNKNOWN_PARAM_REF", f"'{owner_id}' counterDisplay.maxParam='{mp}' 未在 parameters 中声明。", owner_id)

    for c in all_card_objs:
        cid = str(c.get("customId") or "")
        walk_rules(c.get("abilities"), cid)
        walk_rules(c.get("auras"), cid)
        walk_rules(c.get("enchantments"), cid)
        cds = c.get("counterDisplays")
        if isinstance(cds, list):
            if len(cds) > 4:
                add_issue(4, "Error", "COUNTER_DISPLAYS_TOO_MANY", f"'{cid}' counterDisplays 数量 {len(cds)} 超过上限 4。", cid)
            for cd in cds:
                validate_counter_display(cd, cid)

    for p in patches:
        tid = str(p.get("targetTemplateId") or "")
        walk_rules(p.get("abilitiesOverride"), tid)
        walk_rules(p.get("aurasOverride"), tid)
        walk_rules(p.get("enchantmentOverrides"), tid)
        cds = p.get("counterDisplaysOverride")
        if isinstance(cds, list):
            if len(cds) > 4:
                add_issue(4, "Error", "COUNTER_DISPLAYS_TOO_MANY", f"Patch '{tid}' counterDisplaysOverride 数量超过上限 4。", tid)
            for cd in cds:
                validate_counter_display(cd, tid)

    # 校验 mechanics 静态规则（对齐 MechanicSandboxInterpreter.ValidateStatic）
    def walk_mech_steps(steps: Any, mid: str, param_keys: set[str]):
        if not isinstance(steps, list):
            return
        for st in steps:
            if not isinstance(st, dict):
                continue
            op = str(st.get("op") or "").strip()
            if op not in ALLOWED_MECHANIC_OPS:
                add_issue(4, "Error", "MECHANIC_UNKNOWN_OP", f"机制 '{mid}' 使用了未知步骤 op='{op}'。", mid)
            sa = st.get("syncAttribute")
            if sa and sa not in ALLOWED_SYNC_ATTRIBUTES:
                add_issue(4, "Error", "MECHANIC_INVALID_SYNC_ATTR", f"机制 '{mid}' syncAttribute='{sa}' 必须在 Custom_0..Custom_8 内。", mid)
            status = st.get("status")
            if op == "ApplyStatus" and status not in ALLOWED_MECHANIC_STATUSES:
                add_issue(4, "Error", "MECHANIC_UNKNOWN_STATUS", f"机制 '{mid}' ApplyStatus.status='{status}' 非法。", mid)
            if op == "ModifyPlayerAttribute":
                pa = st.get("attribute")
                if pa and pa not in ALLOWED_PLAYER_ATTRIBUTES:
                    add_issue(4, "Error", "MECHANIC_INVALID_PLAYER_ATTR", f"机制 '{mid}' ModifyPlayerAttribute.attribute='{pa}' 不在 14 项玩家属性白名单中。", mid)
            ts = st.get("targetSpec")
            if isinstance(ts, dict):
                tm = ts.get("mode")
                if tm and tm not in ALLOWED_MECHANIC_TARGET_MODES:
                    add_issue(4, "Error", "MECHANIC_UNKNOWN_TARGET_MODE", f"机制 '{mid}' targetSpec.mode='{tm}' 非法。", mid)
                for pk_field in ("modeParam", "countParam", "requireTagParam"):
                    ref = ts.get(pk_field)
                    if ref and ref not in param_keys:
                        add_issue(4, "Error", "MECHANIC_UNKNOWN_PARAM_REF", f"机制 '{mid}' 引用了未声明参数 {pk_field}='{ref}'。", mid)
            for pk_field in ("vfxKeyParam", "attributeParam"):
                ref = st.get(pk_field)
                if ref and ref not in param_keys:
                    add_issue(4, "Error", "MECHANIC_UNKNOWN_PARAM_REF", f"机制 '{mid}' 引用了未声明参数 {pk_field}='{ref}'。", mid)
            for expr_field in ("value", "amount", "count", "delayMs", "intervalMs", "initialDelayMs", "payloadValue"):
                expr = st.get(expr_field)
                if isinstance(expr, dict) and expr.get("param") and expr["param"] not in param_keys:
                    add_issue(4, "Error", "MECHANIC_UNKNOWN_PARAM_REF", f"机制 '{mid}' 表达式引用了未声明参数 '{expr['param']}'。", mid)
            walk_mech_steps(st.get("then"), mid, param_keys)
            walk_mech_steps(st.get("else"), mid, param_keys)
            walk_mech_steps(st.get("steps"), mid, param_keys)

    for m in mechanics:
        mid = str(m.get("id") or "<unknown>").strip()
        params = m.get("parameters") or []
        param_keys: set[str] = set()
        for pr in params:
            if not isinstance(pr, dict):
                continue
            pk = str(pr.get("key") or "").strip()
            pt = str(pr.get("type") or "").strip()
            if pk:
                param_keys.add(pk)
            if pt not in ALLOWED_MECHANIC_PARAM_TYPES:
                add_issue(4, "Error", "MECHANIC_UNKNOWN_PARAM_TYPE", f"机制 '{mid}' 参数 '{pk}' 类型 '{pt}' 非法。", mid)
        if m.get("counterDisplay"):
            validate_counter_display(m["counterDisplay"], mid, param_keys)
        if isinstance(m.get("counterDisplays"), list):
            if len(m["counterDisplays"]) > 4:
                add_issue(4, "Error", "COUNTER_DISPLAYS_TOO_MANY", f"机制 '{mid}' counterDisplays 超过上限 4。", mid)
            for cd in m["counterDisplays"]:
                validate_counter_display(cd, mid, param_keys)
        hooks = m.get("hooks") or []
        if not hooks:
            add_issue(4, "Error", "MECHANIC_NO_HOOKS", f"机制 '{mid}' 至少需要声明 1 个管线钩子。", mid)
        for hk in hooks:
            if not isinstance(hk, dict):
                continue
            hname = str(hk.get("hook") or "").strip()
            if hname not in ALLOWED_MECHANIC_HOOKS:
                add_issue(4, "Error", "MECHANIC_UNKNOWN_HOOK", f"机制 '{mid}' 使用了非法钩子 '{hname}'。", mid)
            scope = str(hk.get("scope") or "SelfCard").strip()
            if scope not in ALLOWED_MECHANIC_SCOPES:
                add_issue(4, "Error", "MECHANIC_UNKNOWN_SCOPE", f"机制 '{mid}' 使用了非法作用域 '{scope}'。", mid)
            walk_mech_steps(hk.get("steps"), mid, param_keys)

    # Gate 5: 冷却毫秒 / 数值上界 / 光环倍率 / 自充能防死循环（覆盖 cards 与 patches）
    def check_aura_multipliers(auras_map: Any, owner_id: str):
        if not isinstance(auras_map, dict):
            return
        for au in auras_map.values():
            if not isinstance(au, dict):
                continue
            act = au.get("action")
            if isinstance(act, dict) and act.get("operation") == "Multiply":
                val = act.get("value")
                num = val if isinstance(val, (int, float)) else (val.get("value") if isinstance(val, dict) and val.get("$type") == "TFixedValue" else None)
                if isinstance(num, (int, float)) and not (0.05 <= num <= 10.0):
                    add_issue(5, "Error", "AURA_MULTIPLIER_OUT_OF_RANGE",
                              f"'{owner_id}' 光环 Multiply 倍率 {num} 超出安全范围 [0.05, 10.0]。", owner_id)

    def check_self_charge_loop(abilities_map: Any, min_cd: float | None, owner_id: str):
        if not isinstance(abilities_map, dict):
            return
        for ab in abilities_map.values():
            if not isinstance(ab, dict):
                continue
            if (ab.get("trigger") or {}).get("$type") != "TTriggerOnCardFired":
                continue
            act = ab.get("action")
            stack = [act] if isinstance(act, dict) else []
            while stack:
                cur = stack.pop()
                if not isinstance(cur, dict):
                    continue
                if cur.get("$type") == "TActionAnd" and isinstance(cur.get("actions"), list):
                    stack.extend(cur["actions"])
                    continue
                tgt_type = (cur.get("target") or {}).get("$type")
                if cur.get("$type") == "TActionCardForceUse" and tgt_type == "TTargetCardSelf":
                    add_issue(5, "Error", "SELF_LOOP_DETECTED", f"'{owner_id}' 存在 OnCardFired -> ForceUse(Self) 死循环。", owner_id)
                if cur.get("$type") == "TActionCardCharge" and tgt_type == "TTargetCardSelf" and min_cd:
                    val = cur.get("value")
                    chg = val if isinstance(val, (int, float)) else (val.get("value") if isinstance(val, dict) and val.get("$type") == "TFixedValue" else None)
                    if isinstance(chg, (int, float)) and chg >= min_cd * 0.9:
                        add_issue(5, "Error", "SELF_CHARGE_LOOP_RISK",
                                  f"'{owner_id}' 存在自开火自充能 {chg}ms >= 90% 冷却({min_cd}ms) 死循环风险。", owner_id)

    for c in cards:
        cid = str(c.get("customId") or "")
        abilities = c.get("abilities") or {}
        has_fired = any((ab.get("trigger") or {}).get("$type") == "TTriggerOnCardFired" for ab in abilities.values() if isinstance(ab, dict))
        cds: list[float] = []
        for bag in [c.get("attributes") or {}] + [td.get("attributes") or {} for td in (c.get("tiers") or {}).values() if isinstance(td, dict)]:
            for ak, av in bag.items():
                if isinstance(av, (int, float)) and (av > 100000 or av < -10000):
                    add_issue(5, "Error", "NUMERIC_OUT_OF_SAFE_BOUNDS", f"'{cid}' 属性 {ak}={av} 超出安全上界。", cid)
            if "CooldownMax" in bag and isinstance(bag["CooldownMax"], (int, float)):
                cds.append(float(bag["CooldownMax"]))
        if c.get("type") == "Item" and has_fired:
            if not cds:
                add_issue(5, "Error", "COOLDOWN_MISSING", f"主动开火物品 '{cid}' 缺少 CooldownMax。", cid)
            for cd in cds:
                if cd < 1000 or cd > 60000:
                    add_issue(5, "Error", "COOLDOWN_TOO_LOW_OR_UNIT_ERROR",
                              f"物品 '{cid}' CooldownMax={cd} 超出合法区间 [1000, 60000]ms（疑似将秒误填为毫秒）。", cid)
        check_aura_multipliers(c.get("auras"), cid)
        check_self_charge_loop(abilities, min(cds) if cds else None, cid)

    for p in patches:
        tid = str(p.get("targetTemplateId") or "")
        p_cds: list[float] = []
        bags = [p.get("attributeOverrides") or {}] + [
            bag for bag in (p.get("tierAttributeOverrides") or {}).values() if isinstance(bag, dict)
        ]
        for bag in bags:
            for ak, av in bag.items():
                if isinstance(av, (int, float)) and (av > 100000 or av < -10000):
                    add_issue(5, "Error", "NUMERIC_OUT_OF_SAFE_BOUNDS", f"Patch '{tid}' 属性 {ak}={av} 超出安全上界。", tid)
            if "CooldownMax" in bag and isinstance(bag["CooldownMax"], (int, float)):
                cd = float(bag["CooldownMax"])
                p_cds.append(cd)
                if cd < 1000 or cd > 60000:
                    add_issue(5, "Error", "COOLDOWN_TOO_LOW_OR_UNIT_ERROR",
                              f"Patch '{tid}' CooldownMax={cd} 超出 [1000, 60000]ms。", tid)
        check_aura_multipliers(p.get("aurasOverride"), tid)
        check_self_charge_loop(p.get("abilitiesOverride"), min(p_cds) if p_cds else None, tid)

    # Gate 6: 奇遇 DAG 三色 DFS + 调度合法性与空选项防软锁
    graph: dict[str, list[str]] = {}
    for ev in events:
        eid = str(ev.get("eventId") or ev.get("customId") or "").strip()
        ev_type = str(ev.get("eventType") or ev.get("type") or "EventEncounter")
        min_d = ev.get("minDay", 1)
        max_d = ev.get("maxDay", 99)
        if isinstance(min_d, (int, float)) and isinstance(max_d, (int, float)) and min_d > max_d:
            add_issue(6, "Error", "INVALID_EVENT_DAY_RANGE", f"奇遇 '{eid}' minDay({min_d}) > maxDay({max_d})。", eid)
        wt = ev.get("weight", 1.0)
        if isinstance(wt, (int, float)) and wt < 0:
            add_issue(6, "Error", "INVALID_EVENT_WEIGHT", f"奇遇 '{eid}' weight({wt}) 不能为负数。", eid)
        steps = ev.get("steps") or []
        if ev_type not in ("CombatEncounter", "PedestalEncounter", "EncounterStep") and len(steps) == 0:
            add_issue(6, "Error", "EVENT_EMPTY_STEPS", f"奇遇 '{eid}' 未配置任何选项 steps，玩家进入将软锁。", eid)
        graph.setdefault(eid, [])
        for nid in (ev.get("nextEncounterIds") or []):
            nid_s = str(nid or "").strip()
            if nid_s:
                graph[eid].append(nid_s)
                if nid_s.lower() not in local_ids and nid_s.lower() not in base_cards:
                    add_issue(6, "Error", "ENCOUNTER_DANGLING_TARGET", f"奇遇 '{eid}' 跳转目标 '{nid_s}' 不存在。", eid)
        for st in steps:
            if not isinstance(st, dict):
                continue
            sid = str(st.get("customId") or "").strip()
            graph[eid].append(sid)
            graph.setdefault(sid, [])
            for opt in (st.get("nextEncounterOnSelection") or []):
                tid = str((opt or {}).get("id") or "").strip()
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
            if isinstance(tip, str) and tip.count("{") != tip.count("}"):
                add_issue(7, "Error", "BROKEN_PLACEHOLDER_SYNTAX", f"'{cid}' 提示文本花括号未闭合：{tip}", cid)

    # Gate 8: 方案 A PNG/JPEG 资产魔数、尺寸、体积、SHA-256 与引用存在性核验
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
            ok_img, ct, w, h = inspect_image_header(raw)
            if ct == "image/webp" or f.suffix.lower() == ".webp":
                add_issue(8, "Error", "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY", f"资产 '{rel}' 为 WebP，Unity 原生不解码 WebP，请转为标准 PNG/JPEG。", rel)
                continue
            if not ok_img or ct not in ("image/png", "image/jpeg"):
                add_issue(8, "Error", "ASSET_MAGIC_INVALID", f"资产 '{rel}' 非合法标准 PNG/JPEG 魔数头。", rel)
                continue
            if len(raw) > MAX_ASSET_BYTES:
                add_issue(8, "Error", "ASSET_SIZE_EXCEEDED", f"资产 '{rel}' 超出 2MB 上限。", rel)
            if not (64 <= w <= 1024 and 64 <= h <= 1024):
                add_issue(8, "Error", "ASSET_DIMENSION_OUT_OF_RANGE", f"资产 '{rel}' 尺寸 {w}x{h} 超出 [64, 1024]。", rel)
            sha = hashlib.sha256(raw).hexdigest().lower()
            if rel.lower() in expected_sha and expected_sha[rel.lower()] != sha:
                add_issue(8, "Error", "ASSET_SHA256_MISMATCH", f"资产 '{rel}' SHA-256 与 art_manifest.json 不一致。", rel)

    # 消费 found_assets：核对卡牌/补丁/事件声明的全部 customArtFile / customBgArtFile 是否真实落盘
    for owner_id, art_rel in referenced_assets:
        if not is_safe_rel_path(art_rel):
            add_issue(8, "Error", "ASSET_PATH_TRAVERSAL", f"'{owner_id}' 引用的资产路径 '{art_rel}' 非法。", owner_id)
        elif art_rel.lower() not in found_assets and not (mod_dir / art_rel).is_file():
            add_issue(8, "Error", "MISSING_ASSET_FILE",
                      f"'{owner_id}' 引用的贴图资产 '{art_rel}' 在 mods/{mod_id or mod_dir.name}/ 中不存在。", owner_id,
                      "请将对应 PNG/JPEG 文件放入 assets/ 目录或修正 customArtFile 路径。")

    if total_bytes > MAX_TOTAL_ASSETS_BYTES:
        add_issue(8, "Error", "ASSET_SIZE_EXCEEDED", "总资产超出 32MB 限制。")

    names = {
        1: ("schema_structure", "闸门 1：Schema 结构、废字段与五阶绑定完整性"),
        2: ("id_namespace_and_deps", "闸门 2：ID 全局命名空间防撞与依赖"),
        3: ("visual_whitelist_and_size", "闸门 3：白名单视觉载体与同 Type/Size 防白块穿模"),
        4: ("rule_type_whitelist", "闸门 4：规则树与开放沙盒机制静态白名单"),
        5: ("numeric_and_loop_safety", "闸门 5：冷却毫秒 / 数值上界 / 光环倍率与防死循环"),
        6: ("encounter_dag_integrity", "闸门 6：奇遇事件链三色 DFS 防环与调度/软锁检查"),
        7: ("localization_and_placeholders", "闸门 7：中文描述与占位符校验"),
        8: ("png_asset_integrity", "闸门 8：PNG/JPEG 魔数 / 尺寸 / SHA-256 与引用完整性"),
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


def call_server_validate(
    server_url: str,
    mod_id: str,
    matches: int,
    seed: int,
    token: str | None = None,
) -> tuple[int, dict[str, Any]]:
    url = f"{server_url.rstrip('/')}/api/workshop/mods/{urllib.parse.quote(mod_id)}/validate"
    headers = {"Content-Type": "application/json; charset=utf-8"}
    effective_token = token or os.environ.get("BAZAAR_WORKSHOP_TOKEN")
    if effective_token:
        headers["X-Workshop-Token"] = effective_token.strip()
    req = urllib.request.Request(
        url,
        data=json.dumps({"matches": matches, "seed": seed}).encode("utf-8"),
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=45) as resp:
            rep = json.loads(resp.read().decode("utf-8"))
            if rep.get("passed"):
                return 0, rep
            static_fail = any((g.get("gateId") or 0) <= 8 and g.get("status") == "FAIL" for g in (rep.get("gates") or []))
            return (2 if static_fail else 3), rep
    except urllib.error.HTTPError as ex:
        raw_body = ex.read().decode("utf-8", errors="replace")
        return 1, {"ok": False, "passed": False, "error": f"服务端返回 HTTP {ex.code}：{raw_body[:300]}"}
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
        code, rep = call_server_validate(args.server, mod_id, args.matches, args.seed, token=args.token)
        print_validation_report(rep, args.json)
        return code

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
        code, rep = call_server_validate(args.server, mod_id, args.matches, args.seed, token=args.token)
        print_validation_report(rep, args.json)
        return code

    code, rep = call_offline_cli("mod-sim-test", mod_dir, args.matches, args.seed, static_only=False, data_root=data_root)
    print_validation_report(rep, args.json)
    return code


# ════════════════════════════════════════════════════════════════════════════
# 子命令 5 & 6：pack / unpack（标准 .bazaarmod.zip 军工级安检）
# ════════════════════════════════════════════════════════════════════════════

def _pack_mod_dir_to_zip(mod_dir: Path, out_path: Path, rep: dict[str, Any]) -> tuple[str, str]:
    manifest = read_json_file(mod_dir / "mod.json")
    mod_id = manifest["id"]
    version = manifest.get("version", "1.0.0")
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
    return mod_id, version


def cmd_pack(args: argparse.Namespace) -> int:
    mod_dir = Path(args.mod_dir).resolve()
    if not (mod_dir / "mod.json").is_file():
        print(f"[错误] 缺少 mod.json：{mod_dir}", file=sys.stderr)
        return 1
    data_root = locate_data_root(args.data_root)

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
    _pack_mod_dir_to_zip(mod_dir, out_path, rep)
    print(f"✅ 校验全绿并成功打包：{out_path} ({out_path.stat().st_size:,} bytes)")
    return 0


def cmd_unpack(args: argparse.Namespace) -> int:
    zip_path = Path(args.zip_file).resolve()
    if not zip_path.is_file():
        print(f"[错误] 压缩包不存在：{zip_path}", file=sys.stderr)
        return 1
    if zip_path.stat().st_size > MAX_ZIP_BYTES:
        print("[安全拦截] 压缩包超出 25 MB 上限。", file=sys.stderr)
        return 4

    with tempfile.TemporaryDirectory(prefix="bazaar_unpack_") as tmp:
        tmp_root = Path(tmp).resolve()
        try:
            with zipfile.ZipFile(zip_path, "r") as zf:
                infos = zf.infolist()
                if len(infos) == 0 or len(infos) > MAX_ZIP_ENTRIES:
                    print(f"[安全拦截] 压缩包条目数异常（{len(infos)}，上限 {MAX_ZIP_ENTRIES}）。", file=sys.stderr)
                    return 4

                actual_total_bytes = 0
                for info in infos:
                    # 1. 先跨平台归一化反斜杠再判断目录（防止 Linux 下带反斜杠目录/路径绕过）
                    rel = info.filename.replace("\\", "/")
                    if rel.endswith("/") or info.is_dir():
                        continue
                    # 2. 严格拦截 ZIP 符号链接条目（Unix S_IFLNK = 0o120000）
                    unix_mode = (info.external_attr >> 16) & 0xFFFF
                    if stat.S_ISLNK(unix_mode):
                        print(f"[安全拦截] 禁止解包符号链接条目：{info.filename}", file=sys.stderr)
                        return 4
                    # 3. 拦截路径穿越、绝对路径与 Windows 驱动器盘符
                    if not is_safe_rel_path(rel):
                        print(f"[安全拦截] 检测到路径穿越/非法路径（Zip Slip）：{info.filename}", file=sys.stderr)
                        return 4
                    if Path(rel).suffix.lower() in FORBIDDEN_ZIP_EXTS:
                        print(f"[安全拦截] 禁止导入可执行/脚本文件：{rel}", file=sys.stderr)
                        return 4
                    # 4. 严格基于 pathlib.is_relative_to 防前缀碰撞（如 /tmp/dir2 命中 /tmp/dir）
                    dest = (tmp_root / rel).resolve()
                    if not dest.is_relative_to(tmp_root):
                        print(f"[安全拦截] 目标路径越界：{rel}", file=sys.stderr)
                        return 4
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    if _has_symlink_in_chain(tmp_root, dest):
                        print(f"[安全拦截] 检测到父级符号链接越界风险：{rel}", file=sys.stderr)
                        return 4
                    # 5. 跨条目真实流式字节累计防伪造 Zip Bomb（绝不轻信 header.file_size）
                    with zf.open(info, "r") as src, open(dest, "wb") as dst:
                        while True:
                            chunk = src.read(STREAM_CHUNK_BYTES)
                            if not chunk:
                                break
                            actual_total_bytes += len(chunk)
                            if actual_total_bytes > MAX_ZIP_UNCOMPRESSED:
                                print("[安全拦截] 实际解压总字节超出 64 MB（真实流式 Zip Bomb 防护）。", file=sys.stderr)
                                return 4
                            dst.write(chunk)
        except zipfile.BadZipFile:
            print("[安全拦截] 不是有效的 ZIP 文件。", file=sys.stderr)
            return 4

        actual_dir = tmp_root
        if not (actual_dir / "mod.json").is_file():
            subs = [p for p in tmp_root.iterdir() if p.is_dir() and not p.is_symlink()]
            if len(subs) == 1 and (subs[0] / "mod.json").is_file():
                actual_dir = subs[0]
            else:
                print("[安全拦截] 压缩包缺少 mod.json。", file=sys.stderr)
                return 4

        # 优先桥接 BazaarCli 10 闸门权威校验；仅在显式 --allow-static-only 或 bz 未编译时回退纯 Python 8 闸门
        data_root = locate_data_root(args.data_root)
        if not args.allow_static_only and locate_bazaar_cli() is not None:
            code, rep = call_offline_cli("mod-validate", actual_dir, matches=20, seed=2026131, static_only=False, data_root=data_root)
        else:
            code, rep = run_python_static_8_gates(actual_dir, data_root)
        if code != 0:
            print_validation_report(rep, as_json=False)
            print("[阻断] 压缩包未通过防 Bug 铁闸，已拒绝解包。", file=sys.stderr)
            return code

        mod_id = str(read_json_file(actual_dir / "mod.json").get("id") or "").strip()
        if not MOD_ID_RE.match(mod_id):
            print(f"[安全拦截] 压缩包 mod.json 中的 Mod ID 非法：{mod_id}", file=sys.stderr)
            return 4

        if args.validate_only:
            print(f"✅ 检疫通过（--validate-only 未落盘）：{mod_id}")
            return 0

        into_root = Path(args.into).resolve()
        into_root.mkdir(parents=True, exist_ok=True)
        dest_mod = (into_root / mod_id).resolve()
        if not dest_mod.is_relative_to(into_root) or dest_mod == into_root:
            print(f"[安全拦截] 解包落盘目录越界：{dest_mod}", file=sys.stderr)
            return 4

        stage_dir = into_root / f".tmp_{mod_id}_{uuid.uuid4().hex[:12]}"
        try:
            shutil.copytree(actual_dir, stage_dir)
            _atomic_swap_directory(stage_dir, dest_mod, into_root, mod_id)
        finally:
            if stage_dir.exists():
                shutil.rmtree(stage_dir, ignore_errors=True)

        print(f"✅ 安全检疫通过并已原子解包至：{dest_mod}")
        return 0


# ════════════════════════════════════════════════════════════════════════════
# 子命令 7：publish — 一键发布至服务端工坊或社区库（支持 --token & --uploader-secret）
# ════════════════════════════════════════════════════════════════════════════

def _build_multipart_body(field_name: str, filename: str, payload: bytes, content_type: str = "application/zip") -> tuple[bytes, str]:
    boundary = f"----BazaarModSdk{uuid.uuid4().hex}"
    safe_fn = re.sub(r'["\r\n\\/:*?<>|]', "_", filename)
    parts = [
        f"--{boundary}\r\n".encode("utf-8"),
        f'Content-Disposition: form-data; name="{field_name}"; filename="{safe_fn}"\r\n'.encode("utf-8"),
        f"Content-Type: {content_type}\r\n\r\n".encode("utf-8"),
        payload,
        f"\r\n--{boundary}--\r\n".encode("utf-8"),
    ]
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def cmd_publish(args: argparse.Namespace) -> int:
    source = Path(args.mod_path).resolve()
    if not source.exists():
        print(f"[错误] 目标路径不存在：{source}", file=sys.stderr)
        return 1

    server_url = (args.server or "http://127.0.0.1:8080").rstrip("/")
    token = args.token or os.environ.get("BAZAAR_WORKSHOP_TOKEN")
    uploader_secret = args.uploader_secret or os.environ.get("BAZAAR_UPLOADER_SECRET")
    player_name = args.player_name or os.environ.get("BAZAAR_PLAYER_NAME") or "CLI作者"

    temp_dir_obj: tempfile.TemporaryDirectory[str] | None = None
    try:
        if source.is_file() and source.suffix.lower() == ".zip":
            zip_path = source
            mod_id_hint = source.stem.replace(".bazaarmod", "")
        elif source.is_dir():
            if not (source / "mod.json").is_file():
                print(f"[错误] Mod 目录缺少 mod.json：{source}", file=sys.stderr)
                return 1
            data_root = locate_data_root(args.data_root)
            if not args.skip_pack:
                if args.allow_static_only or locate_bazaar_cli() is None:
                    code, rep = run_python_static_8_gates(source, data_root)
                else:
                    code, rep = call_offline_cli("mod-validate", source, matches=50, seed=2026131, static_only=False, data_root=data_root)
                if code != 0:
                    print_validation_report(rep, as_json=False)
                    print("[阻断] 本地预检未通过，已中止发布。", file=sys.stderr)
                    return code
            else:
                rep = {"errorCount": 0, "warningCount": 0, "summaryZh": "skip-pack"}
            temp_dir_obj = tempfile.TemporaryDirectory(prefix="bazaar_pub_")
            mf = read_json_file(source / "mod.json")
            mod_id_hint = mf["id"]
            zip_path = Path(temp_dir_obj.name) / f"{mod_id_hint}-{mf.get('version', '1.0.0')}.bazaarmod.zip"
            _pack_mod_dir_to_zip(source, zip_path, rep)
        else:
            print(f"[错误] 不支持的发布路径（请传入 Mod 目录或 .bazaarmod.zip 文件）：{source}", file=sys.stderr)
            return 1

        zip_bytes = zip_path.read_bytes()
        body, ct_header = _build_multipart_body("file", zip_path.name, zip_bytes)

        if args.target == "community":
            url = f"{server_url}/api/community/mods/upload"
            headers = {
                "Content-Type": ct_header,
                "X-Player-Name": urllib.parse.quote(player_name),
            }
            if token:
                headers["X-Workshop-Token"] = token.strip()
            if uploader_secret:
                headers["X-Uploader-Secret"] = uploader_secret.strip()
            req = urllib.request.Request(url, data=body, headers=headers, method="POST")
            try:
                with urllib.request.urlopen(req, timeout=90) as resp:
                    data = json.loads(resp.read().decode("utf-8"))
                    issued = data.get("uploaderSecret") or resp.headers.get("X-Uploader-Secret")
                    print(f"✅ 已发布至社区 Mod 库：{data.get('messageZh') or mod_id_hint}")
                    if issued:
                        print(f"🔑 服务端签发 X-Uploader-Secret（后续更新/删除请保管并通过 --uploader-secret 传入）：{issued}")
                    return 0
            except urllib.error.HTTPError as ex:
                raw = ex.read().decode("utf-8", errors="replace")
                print(f"[失败] 社区上传被拒绝（HTTP {ex.code}）：{raw[:500]}", file=sys.stderr)
                return 2 if ex.code == 422 else 1
            except urllib.error.URLError as ex:
                print(f"[错误] 无法连接社区服务端 {url}：{ex}", file=sys.stderr)
                return 1

        # workshop 目标：导入检疫 + 可选 publish-reload
        import_url = f"{server_url}/api/workshop/mods/import"
        headers = {"Content-Type": ct_header}
        if token:
            headers["X-Workshop-Token"] = token.strip()
        req = urllib.request.Request(import_url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=90) as resp:
                imp_data = json.loads(resp.read().decode("utf-8"))
                imported_id = imp_data.get("modId") or mod_id_hint
                print(f"✅ 服务端检疫导入成功：{imported_id}")
        except urllib.error.HTTPError as ex:
            raw = ex.read().decode("utf-8", errors="replace")
            print(f"[失败] 工坊导入被拒绝（HTTP {ex.code}）：{raw[:500]}", file=sys.stderr)
            return 2 if ex.code == 422 else 1
        except urllib.error.URLError as ex:
            print(f"[错误] 无法连接工坊服务端 {import_url}：{ex}", file=sys.stderr)
            return 1

        if args.no_reload:
            return 0

        reload_url = f"{server_url}/api/workshop/mods/{urllib.parse.quote(imported_id)}/publish-reload"
        r_headers = {"Content-Type": "application/json"}
        if token:
            r_headers["X-Workshop-Token"] = token.strip()
        r_req = urllib.request.Request(reload_url, data=b"{}", headers=r_headers, method="POST")
        try:
            with urllib.request.urlopen(r_req, timeout=90) as resp:
                rel_data = json.loads(resp.read().decode("utf-8"))
                print(f"🚀 已发布并热重载进服务端：{rel_data.get('messageZh') or imported_id}")
                return 0
        except urllib.error.HTTPError as ex:
            raw = ex.read().decode("utf-8", errors="replace")
            print(f"[失败] 发布热重载被拦截（HTTP {ex.code}）：{raw[:500]}", file=sys.stderr)
            return 2 if ex.code == 422 else 1
        except urllib.error.URLError as ex:
            print(f"[错误] 无法调用热重载接口 {reload_url}：{ex}", file=sys.stderr)
            return 1
    finally:
        if temp_dir_obj is not None:
            temp_dir_obj.cleanup()


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="TheBazaar V15.4 标准化工业级 Mod SDK 命令行工具")
    sub = p.add_subparsers(dest="command", required=True)

    p_new = sub.add_parser("new", help="初始化标准 V15.4 Mod 骨架目录（含 mechanics/）")
    p_new.add_argument("mod_id", help="唯一 Mod ID（小写字母/数字/连字符）")
    p_new.add_argument("--name", default="", help="中文展示名")
    p_new.add_argument("--author", default="社区创作者", help="作者署名")
    p_new.add_argument("--version", default="1.0.0", help="初始版本号")
    p_new.add_argument("--description", default="", help="中文简介")
    p_new.add_argument("--scope", default="multiplayer-safe", choices=sorted(VALID_SCOPES))
    p_new.add_argument("--mods-dir", default=None)
    p_new.add_argument("--force", action="store_true")
    p_new.set_defaults(func=cmd_new)

    p_clone = sub.add_parser("clone-card", help="检索 3,368 原版白名单卡并一键克隆（保留附魔/任务）或生成原位补丁")
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
    p_val.add_argument("--token", default=None, help="公网写/管理口令（或环境变量 BAZAAR_WORKSHOP_TOKEN）")
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
    p_sim.add_argument("--token", default=None, help="公网管理口令（或环境变量 BAZAAR_WORKSHOP_TOKEN）")
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

    p_unpack = sub.add_parser("unpack", help="军工级安检并原子解包社区 .bazaarmod.zip")
    p_unpack.add_argument("zip_file", help=".bazaarmod.zip 路径")
    p_unpack.add_argument("--into", default=str(REPO_ROOT / "mods"))
    p_unpack.add_argument("--validate-only", action="store_true")
    p_unpack.add_argument("--allow-static-only", action="store_true", help="跳过 BazaarCli 10 闸门，仅跑纯 Python 静态 8 闸门")
    p_unpack.add_argument("--data-root", default=None)
    p_unpack.set_defaults(func=cmd_unpack)

    p_pub = sub.add_parser("publish", help="一键打包并发布至服务端工坊（热重载）或社区 Mod 库")
    p_pub.add_argument("mod_path", help="Mod 目录或已打包的 .bazaarmod.zip 文件路径")
    p_pub.add_argument("--server", default="http://127.0.0.1:8080", help="目标服务端地址")
    p_pub.add_argument("--target", choices=["workshop", "community"], default="workshop", help="发布目标：workshop 或 community")
    p_pub.add_argument("--token", default=None, help="X-Workshop-Token（默认读环境变量 BAZAAR_WORKSHOP_TOKEN）")
    p_pub.add_argument("--uploader-secret", default=None, help="社区作者管理凭证 X-Uploader-Secret（或环境变量 BAZAAR_UPLOADER_SECRET）")
    p_pub.add_argument("--player-name", default=None, help="社区上传作者展示名 X-Player-Name")
    p_pub.add_argument("--skip-pack", action="store_true", help="跳过本地预检直接打包上传（仍受服务端 10 闸门硬拦）")
    p_pub.add_argument("--allow-static-only", action="store_true")
    p_pub.add_argument("--no-reload", action="store_true", help="仅导入工坊草稿，不执行 publish-reload 热重载")
    p_pub.add_argument("--data-root", default=None)
    p_pub.set_defaults(func=cmd_publish)

    return p


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())
