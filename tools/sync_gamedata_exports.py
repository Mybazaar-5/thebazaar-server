#!/usr/bin/env python3
"""
权威 GameData.db 数据同步导出工具（tools/sync_gamedata_exports.py）。

功能说明：
1. 从权威 SQLite 数据库 `dist/data/GameData.db`（若当前工作树不存在，自动回退查找
   `e:\\thebazaar\\dist\\data\\GameData.db`）只读提取表数据；
2. 完整同步导出并校验以下核心 JSON（消除过期文件与权威 DB 的数据分裂）：
   - `dist/data/gamemodes.json`（顶层 `{"<version>": [...]}`，修复 StandardPrices Legendary 24/48 及补全 12 个缺失字段）
   - `dist/data/levelups.json`（顶层 `{"<version>": [...]}`，修复 Lv11+ 恒定 800 HP 并完整导出 Rewards / TutorialRewards）
   - `dist/data/monsters.json`（顶层数组 `[...]`，全量同步 179 条野怪模板及全部字段）
   - 可选通过 `--all` 一并同步 `seasons.json`、`challenges.json`、`tooltips.json`、`cards.json`；
3. 自动校验关键游戏数值不变量（StandardPrices、Lv11~Lv30 HP、monsters 数量与字段一致性），
   并在 `manifest.json` 存在时按内容 MD5 刷新对应条目的 ETag；
4. 支持 `--check` 纯只读校验模式，方便 CI / 发包前确认零漂移。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import sqlite3
import sys
from typing import Any

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_LOCAL_DB = REPO_ROOT / "dist" / "data" / "GameData.db"
FALLBACK_MAIN_DB = pathlib.Path(r"./dist\data\GameData.db")
DEFAULT_OUT_DIR = REPO_ROOT / "dist" / "data"

# 各导出目标的定义：(SQLite 表名, 输出文件名, 包装方式, 排序SQL, manifest 键名)
EXPORT_SPECS: dict[str, dict[str, Any]] = {
    "gamemodes": {
        "table": "game_modes",
        "filename": "gamemodes.json",
        "wrap": "version_dict",
        "order_by": "Id",
        "manifest_key": "gamemodes",
    },
    "levelups": {
        "table": "level_ups",
        "filename": "levelups.json",
        "wrap": "version_dict",
        "order_by": "Id",
        "manifest_key": "levelups",
    },
    "monsters": {
        "table": "monsters",
        "filename": "monsters.json",
        "wrap": "list",
        "order_by": "Id",
        "manifest_key": "monsters",
    },
    "seasons": {
        "table": "seasons",
        "filename": "seasons.json",
        "wrap": "version_dict",
        "order_by": "Id",
        "manifest_key": "seasons",
    },
    "challenges": {
        "table": "challenges",
        "filename": "challenges.json",
        "wrap": "version_dict",
        "order_by": "Id",
        "manifest_key": "challenges",
    },
    "tooltips": {
        "table": "tooltips",
        "filename": "tooltips.json",
        "wrap": "tooltips_version_dict",
        "order_by": "Id",
        "manifest_key": "tooltips",
    },
    "cards": {
        "table": "cards",
        "filename": "cards.json",
        "wrap": "version_dict",
        "order_by": "Id",
        "manifest_key": "cards",
    },
}

DEFAULT_TARGETS = ("gamemodes", "levelups", "monsters")


def resolve_db_path(explicit: pathlib.Path | None) -> pathlib.Path:
    """按优先级定位权威 GameData.db。"""
    candidates: list[pathlib.Path] = []
    if explicit is not None:
        candidates.append(explicit)
    else:
        candidates.append(DEFAULT_LOCAL_DB)
        candidates.append(FALLBACK_MAIN_DB)

    for cand in candidates:
        if cand.is_file() and cand.stat().st_size > 0:
            return cand

    searched = ", ".join(str(p) for p in candidates)
    raise FileNotFoundError(f"未找到有效权威 GameData.db（已尝试：{searched}）")


def load_table_payload(conn: sqlite3.Connection, spec: dict[str, Any]) -> tuple[Any, int, str]:
    """从 SQLite 表只读提取并组装目标 JSON 结构。"""
    table = spec["table"]
    order_by = spec["order_by"]
    wrap = spec["wrap"]

    rows = conn.execute(f'SELECT Id, Data FROM "{table}" ORDER BY "{order_by}"').fetchall()
    parsed_items: list[dict[str, Any]] = []
    version = "5.0.0"

    if wrap == "tooltips_version_dict":
        for _, blob in rows:
            obj = json.loads(blob.decode("utf-8"))
            if isinstance(obj, dict):
                for val in obj.values():
                    if isinstance(val, dict):
                        if val.get("Version"):
                            version = str(val["Version"])
                        parsed_items.append(val)
        return {version: parsed_items}, len(parsed_items), version

    for _, blob in rows:
        obj = json.loads(blob.decode("utf-8"))
        if isinstance(obj, dict) and obj.get("Version"):
            version = str(obj["Version"])
        parsed_items.append(obj)

    if wrap == "version_dict":
        return {version: parsed_items}, len(parsed_items), version
    if wrap == "list":
        return parsed_items, len(parsed_items), version
    raise ValueError(f"未知包装格式：{wrap}")


def validate_exported_payload(target: str, payload: Any) -> list[str]:
    """校验导出数据的关键权威不变量，返回错误列表（空列表表示通过）。"""
    errors: list[str] = []

    if target == "gamemodes":
        if not isinstance(payload, dict) or not payload:
            return ["gamemodes.json 顶层必须是非空版本字典"]
        items = next(iter(payload.values()))
        if not isinstance(items, list) or len(items) < 1:
            return ["gamemodes.json 版本数组为空"]
        gm = items[0]
        required_keys = (
            "BurnDecrementByAsPercentage",
            "CombatSimEffectDelay",
            "CritPercentMultiplier",
            "EncounterSpawnTierPercentages",
            "EncounterTierTable",
            "ExclusionSet",
            "Fates",
            "InternalName",
            "ItemSkillSpawnTierPercantagesByDay",
            "ItemSkillTierTablesByDay",
            "RewardSpawnTierPercentages",
            "Sandstorm",
            "StandardPrices",
        )
        missing = [k for k in required_keys if k not in gm]
        if missing:
            errors.append(f"gamemodes.json 缺失关键字段：{', '.join(missing)}")

        sp = gm.get("StandardPrices") or {}
        buy = sp.get("ItemBuyPrices") or {}
        sell = sp.get("ItemSellPrices") or {}
        expected_buy = {"Small": 24, "Medium": 48, "Large": 64}
        expected_sell = {"Small": 12, "Medium": 24, "Large": 32}
        for size, exp_v in expected_buy.items():
            got = (buy.get(size) or {}).get("Legendary")
            if got != exp_v:
                errors.append(f"StandardPrices.ItemBuyPrices.{size}.Legendary={got}，期望 {exp_v}")
        for size, exp_v in expected_sell.items():
            got = (sell.get(size) or {}).get("Legendary")
            if got != exp_v:
                errors.append(f"StandardPrices.ItemSellPrices.{size}.Legendary={got}，期望 {exp_v}")

    elif target == "levelups":
        if not isinstance(payload, dict) or not payload:
            return ["levelups.json 顶层必须是非空版本字典"]
        items = next(iter(payload.values()))
        if not isinstance(items, list) or len(items) < 30:
            return [f"levelups.json 条目数异常：{len(items) if isinstance(items, list) else 'N/A'}"]
        by_lv = {int(x.get("Level", -1)): x for x in items if isinstance(x, dict)}
        for lv in range(11, 31):
            entry = by_lv.get(lv)
            if not entry:
                errors.append(f"levelups.json 缺失 Lv{lv}")
                continue
            if entry.get("HealthIncrease") != 800:
                errors.append(f"levelups.json Lv{lv} HealthIncrease={entry.get('HealthIncrease')}，期望 800")
        missing_rewards = [lv for lv in range(1, 31) if lv in by_lv and not by_lv[lv].get("Rewards")]
        if missing_rewards:
            errors.append(f"levelups.json 以下等级缺失 Rewards：{missing_rewards}")

    elif target == "monsters":
        if not isinstance(payload, list):
            return ["monsters.json 顶层必须是 JSON 数组"]
        if len(payload) < 179:
            errors.append(f"monsters.json 条目数为 {len(payload)}，低于权威基线 179")
        for idx, m in enumerate(payload):
            if not isinstance(m, dict):
                errors.append(f"monsters[{idx}] 不是对象")
                break
            for req in ("Id", "InternalName", "Player"):
                if req not in m:
                    errors.append(f"monsters[{idx}] 缺失字段 {req}")
                    break

    return errors


def atomic_write_bytes(path: pathlib.Path, content: bytes) -> None:
    """先写临时文件再原子替换目标文件，防止半写入破坏数据。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    tmp_path.write_bytes(content)
    os.replace(tmp_path, path)


def update_manifest_etags(out_dir: pathlib.Path, written_etags: dict[str, str]) -> bool:
    """若 out_dir/manifest.json 存在，刷新已同步条目的 MD5 ETag。"""
    manifest_path = out_dir / "manifest.json"
    if not manifest_path.is_file() or not written_etags:
        return False

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception:
        return False

    entries = manifest.get("Entries")
    if not isinstance(entries, dict):
        return False

    changed = False
    for key, md5_hex in written_etags.items():
        new_etag = f'"{md5_hex}"'
        cur = entries.get(key)
        if not isinstance(cur, dict) or cur.get("ETag") != new_etag:
            entries[key] = {"ETag": new_etag}
            changed = True

    if changed:
        raw = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        atomic_write_bytes(manifest_path, raw)
    return changed


def parse_targets(targets_arg: str, sync_all: bool) -> list[str]:
    if sync_all:
        return list(EXPORT_SPECS.keys())
    selected: list[str] = []
    for item in targets_arg.split(","):
        name = item.strip().lower()
        if not name:
            continue
        if name not in EXPORT_SPECS:
            raise ValueError(f"不支持的同步目标：{name}（可选：{', '.join(EXPORT_SPECS.keys())}）")
        if name not in selected:
            selected.append(name)
    return selected or list(DEFAULT_TARGETS)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="从权威 GameData.db 同步导出并校验 dist/data/*.json。"
    )
    parser.add_argument(
        "--db",
        type=pathlib.Path,
        default=None,
        help="权威 GameData.db 路径（默认依次查找 dist/data/GameData.db 与 e:\\thebazaar\\dist\\data\\GameData.db）",
    )
    parser.add_argument(
        "--out-dir",
        type=pathlib.Path,
        default=DEFAULT_OUT_DIR,
        help=f"导出输出目录（默认：{DEFAULT_OUT_DIR}）",
    )
    parser.add_argument(
        "--targets",
        type=str,
        default=",".join(DEFAULT_TARGETS),
        help="逗号分隔的同步目标（默认：gamemodes,levelups,monsters）",
    )
    parser.add_argument(
        "--all",
        action="store_true",
        help="同步全部 7 张 JSON（gamemodes,levelups,monsters,seasons,challenges,tooltips,cards）",
    )
    parser.add_argument(
        "--pretty",
        action="store_true",
        help="以缩进美化格式输出 JSON（默认保持与官方一致的紧凑格式）",
    )
    parser.add_argument(
        "--no-manifest",
        action="store_true",
        help="不同步刷新 manifest.json 中的 ETag",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="只读校验模式：比对现有导出 JSON 与权威 GameData.db 是否完全一致（不写入文件）",
    )
    args = parser.parse_args()

    try:
        db_path = resolve_db_path(args.db)
        targets = parse_targets(args.targets, args.all)
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 2

    print(f"[sync_gamedata_exports] 权威数据库：{db_path} ({db_path.stat().st_size:,} 字节)")
    print(f"[sync_gamedata_exports] 目标目录  ：{args.out_dir}")
    print(f"[sync_gamedata_exports] 同步集合  ：{', '.join(targets)}")

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    written_etags: dict[str, str] = {}
    has_failure = False

    try:
        for target in targets:
            spec = EXPORT_SPECS[target]
            payload, count, version = load_table_payload(conn, spec)
            validation_errors = validate_exported_payload(target, payload)
            if validation_errors:
                has_failure = True
                print(f"  [失败] {spec['filename']} 权威校验未通过：", file=sys.stderr)
                for err in validation_errors:
                    print(f"         - {err}", file=sys.stderr)
                continue

            out_path = args.out_dir / spec["filename"]

            if args.check:
                if not out_path.is_file():
                    has_failure = True
                    print(f"  [缺失] {out_path} 不存在", file=sys.stderr)
                    continue
                existing = json.loads(out_path.read_text(encoding="utf-8"))
                if existing != payload:
                    has_failure = True
                    print(f"  [漂移] {spec['filename']} 与 GameData.db 不一致！", file=sys.stderr)
                else:
                    print(f"  [一致] {spec['filename']} (count={count}, version={version})")
                continue

            if args.pretty:
                raw_text = json.dumps(payload, ensure_ascii=False, indent=2)
            else:
                raw_text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
            raw_bytes = raw_text.encode("utf-8")
            md5_hex = hashlib.md5(raw_bytes).hexdigest()

            old_size = out_path.stat().st_size if out_path.is_file() else 0
            atomic_write_bytes(out_path, raw_bytes)
            written_etags[spec["manifest_key"]] = md5_hex
            print(
                f"  [同步] {spec['filename']}: rows={count}, version={version}, "
                f"size={old_size:,} -> {len(raw_bytes):,} B, md5={md5_hex[:12]}"
            )
    finally:
        conn.close()

    if not args.check and not args.no_manifest and written_etags:
        updated = update_manifest_etags(args.out_dir, written_etags)
        print(f"  [manifest] ETag {'已刷新' if updated else '无变化'}")

    if has_failure:
        print("[sync_gamedata_exports] 校验存在失败项。", file=sys.stderr)
        return 1

    print("[sync_gamedata_exports] 全部目标同步与权威校验通过！")
    return 0


if __name__ == "__main__":
    sys.exit(main())
