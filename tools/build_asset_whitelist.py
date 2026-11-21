#!/usr/bin/env python3
"""
构建客户端 Addressables 资源安全白名单 (`valid_card_ids.json`)。

只读解析原版客户端 `StreamingAssets/aa/catalog.bin`（Addressables v2 二进制目录）
并结合 `./re/strings`（可选参考）及 `cards.json` / `GameData.db` 全部卡牌模板，
校验以下 6 类卡牌是否具备客户端本地可加载资产：
  Item, Skill, EventEncounter, EncounterStep, PedestalEncounter, CombatEncounter

默认输出至 `./dist/data/valid_card_ids.json`，供 `CardPool.Load()` 自动加载过滤，
确保服务端发给客户端的卡牌 100% 拥有本地资产、0 InvalidKeyException、0 白板。
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import sqlite3
import struct
import sys
from typing import Any

TARGET_TYPES = (
    "Item",
    "Skill",
    "EventEncounter",
    "EncounterStep",
    "PedestalEncounter",
    "CombatEncounter",
)

CATALOG_MAGIC_V2 = 0x0DE38942

DEFAULT_CATALOG = pathlib.Path(
    os.environ.get("BAZAAR_CATALOG_BIN", r"./catalog.bin")
)
DEFAULT_CARDS_JSON = pathlib.Path(r"./dist/data/cards.json")
DEFAULT_DB = pathlib.Path(r"./dist/data/GameData.db")
DEFAULT_RE_STRINGS = pathlib.Path(r"./re/strings")
DEFAULT_OUT = pathlib.Path(r"./dist/data/valid_card_ids.json")


def parse_catalog_bin_keys(catalog_path: pathlib.Path) -> tuple[bytes, int, set[str]]:
    """
    只读解析 Unity Addressables binary catalog.bin。
    返回: (原始字节, 数据区起始偏移, 提取的显式 ASCII/UTF-8 键集合)。
    """
    raw = catalog_path.read_bytes()
    data_start = 0
    if len(raw) >= 64:
        hdr = struct.unpack_from("<16I", raw, 0)
        if hdr[0] == CATALOG_MAGIC_V2 and hdr[1] == 2 and hdr[7] < len(raw):
            data_start = hdr[7]

    # 从头部关键索引表提取标准长度前缀字符串（作为辅助核验集）
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


def catalog_contains_key(raw: bytes, data_start: int, extracted: set[str], key: str) -> bool:
    """检查给定 Addressables key 是否精确存在于 catalog.bin。"""
    if not key or key.lower() in ("invalid", "placeholder", "none"):
        return False
    if key in extracted:
        return True
    encoded = key.encode("utf-8")
    needle = struct.pack("<I", len(encoded)) + encoded
    pos = raw.find(needle, data_start)
    if pos >= 0:
        return True
    # 极少数情况 key 可能位于 header 后早期字符串段
    return raw.find(needle) >= 0


def load_re_strings(re_dir: pathlib.Path) -> set[str]:
    """只读收集 ./re/strings/*.txt 中可用资产名字符串作为交叉参考信息。"""
    found: set[str] = set()
    if not re_dir.is_dir():
        return found
    for txt in re_dir.glob("*.txt"):
        try:
            for line in txt.read_text(encoding="utf-8", errors="ignore").splitlines():
                s = line.strip()
                if s and len(s) <= 128 and ("Icon_" in s or "Reward_" in s or "Event_" in s):
                    found.add(s)
        except OSError:
            pass
    return found


def load_cards_merged(
    cards_json_path: pathlib.Path, db_path: pathlib.Path
) -> tuple[dict[str, dict[str, Any]], dict[str, int]]:
    """
    合并加载 cards.json 与 GameData.db 中的全部卡牌，以 GameData.db（运行时权威表）优先覆盖同 ID。
    """
    merged: dict[str, dict[str, Any]] = {}
    stats = {"cards_json": 0, "game_data_db": 0}

    if cards_json_path.is_file():
        payload = json.loads(cards_json_path.read_text(encoding="utf-8"))
        if isinstance(payload, dict):
            card_list = next(iter(payload.values()), [])
        elif isinstance(payload, list):
            card_list = payload
        else:
            card_list = []
        for card in card_list:
            if isinstance(card, dict) and card.get("Id"):
                merged[card["Id"]] = card
                stats["cards_json"] += 1

    if db_path.is_file():
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        try:
            for cid, blob in conn.execute("SELECT Id, Data FROM cards"):
                try:
                    card = json.loads(blob.decode("utf-8"))
                except Exception:
                    continue
                if isinstance(card, dict):
                    card.setdefault("Id", cid)
                    merged[card["Id"]] = card
                    stats["game_data_db"] += 1
        finally:
            conn.close()

    return merged, stats


def validate_card(
    card: dict[str, Any],
    cat_raw: bytes,
    data_start: int,
    extracted: set[str],
) -> tuple[bool, str]:
    """
    按客户端 AssetLoader.cs 真实加载路径验证该卡牌资产是否完备：
      - Item / Skill / EncounterStep / EventEncounter / PedestalEncounter / CombatEncounter:
        均依赖有效的 ArtKey（不可为空 / Invalid / Placeholder，且必须精确命中 catalog.bin）。
      - 若遭遇卡定义了 BoardOverride / Presentation，也必须命中 catalog.bin。
    """
    art_key = (card.get("ArtKey") or "").strip()
    if not art_key:
        return False, "empty_art_key"
    if art_key.lower() in ("invalid", "placeholder"):
        return False, f"invalid_art_key:{art_key}"
    if not catalog_contains_key(cat_raw, data_start, extracted, art_key):
        return False, f"art_key_missing_in_catalog:{art_key}"

    for extra_field in ("BoardOverride", "Presentation"):
        val = (card.get(extra_field) or "").strip()
        if val and not catalog_contains_key(cat_raw, data_start, extracted, val):
            return False, f"{extra_field}_missing_in_catalog:{val}"

    return True, "ok"


def build_whitelist(
    catalog_path: pathlib.Path,
    cards_json_path: pathlib.Path,
    db_path: pathlib.Path,
    re_strings_dir: pathlib.Path,
    out_path: pathlib.Path,
) -> dict[str, Any]:
    if not catalog_path.is_file():
        raise FileNotFoundError(f"未找到客户端 Addressables 目录文件: {catalog_path}")

    cat_raw, data_start, extracted = parse_catalog_bin_keys(catalog_path)
    re_refs = load_re_strings(re_strings_dir)
    cards_by_id, source_stats = load_cards_merged(cards_json_path, db_path)

    valid_ids: list[str] = []
    valid_by_type: dict[str, list[str]] = {t: [] for t in TARGET_TYPES}
    invalid_by_type: dict[str, int] = {t: 0 for t in TARGET_TYPES}
    invalid_details: list[dict[str, Any]] = []

    for cid in sorted(cards_by_id.keys()):
        card = cards_by_id[cid]
        ctype = card.get("Type") or ""
        if ctype not in TARGET_TYPES:
            continue

        ok, reason = validate_card(card, cat_raw, data_start, extracted)
        if ok:
            valid_ids.append(cid)
            valid_by_type[ctype].append(cid)
        else:
            invalid_by_type[ctype] += 1
            invalid_details.append(
                {
                    "id": cid,
                    "type": ctype,
                    "internal_name": card.get("InternalName") or "",
                    "spawning_eligibility": card.get("SpawningEligibility") or "",
                    "art_key": card.get("ArtKey") or "",
                    "reason": reason,
                }
            )

    by_type_valid_counts = {t: len(valid_by_type[t]) for t in TARGET_TYPES}
    result = {
        "version": "1.0",
        "generated_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "catalog_path": str(catalog_path),
        "catalog_bytes": len(cat_raw),
        "catalog_extracted_keys": len(extracted),
        "re_string_hints": len(re_refs),
        "sources": source_stats,
        "target_types": list(TARGET_TYPES),
        "total_checked": len(valid_ids) + len(invalid_details),
        "total_valid": len(valid_ids),
        "total_invalid": len(invalid_details),
        "by_type_valid": by_type_valid_counts,
        "by_type_invalid": invalid_by_type,
        "valid_ids": valid_ids,
        "valid_ids_by_type": valid_by_type,
        "invalid_cards": invalid_details,
    }

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Addressables card asset whitelist.")
    parser.add_argument("--catalog", type=pathlib.Path, default=DEFAULT_CATALOG)
    parser.add_argument("--cards-json", type=pathlib.Path, default=DEFAULT_CARDS_JSON)
    parser.add_argument("--db", type=pathlib.Path, default=DEFAULT_DB)
    parser.add_argument("--re-strings", type=pathlib.Path, default=DEFAULT_RE_STRINGS)
    parser.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT)
    args = parser.parse_args()

    res = build_whitelist(args.catalog, args.cards_json, args.db, args.re_strings, args.out)
    print(
        f"[build_asset_whitelist] checked={res['total_checked']} valid={res['total_valid']} "
        f"invalid={res['total_invalid']} -> {args.out}"
    )
    print(f"  by_type_valid  : {res['by_type_valid']}")
    print(f"  by_type_invalid: {res['by_type_invalid']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
