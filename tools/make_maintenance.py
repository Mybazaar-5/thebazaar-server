#!/usr/bin/env python3
"""生成自建服的 maintenance.json（版本门放行 + 三子系统可用 + 清掉维护窗口）。

用法: python make_maintenance.py <output_dir> [source_maintenance.json]
"""
import json
import os
import sys


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    out_dir = sys.argv[1]
    src = sys.argv[2] if len(sys.argv) > 2 else os.path.expandvars(
        r"%USERPROFILE%\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\maintenance.json"
    )

    doc = {}
    if os.path.exists(src):
        with open(src, encoding="utf-8") as fh:
            doc = json.load(fh)

    status = doc.setdefault("status", {})
    systems = status.setdefault("systems", {})
    for key in ("temponet", "tempoidentity", "tempoplatform"):
        systems[key] = {"isAvailable": True}

    # 空版本表 → ServersHealthService.IsUpdateRequired() 恒 false
    status["versions"] = {}
    status["breakingChange"] = False

    # 维护窗口必须清掉，否则 IsOnMaintenance() 可能命中
    status.pop("maintenance", None)
    status.pop("announcement", None)

    status["httpGameClientTimeouts"] = {
        "defaultRequestSeconds": 60,
        "inRunCommandSeconds": 60,
        "deleteSessionSeconds": 60,
    }

    doc["breakingChange"] = False
    doc["httpGameClientTimeouts"] = status["httpGameClientTimeouts"]

    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, "maintenance.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2)

    print(f"wrote {out} ({os.path.getsize(out)} bytes)")
    print("systems:", json.dumps(systems))
    print("versions:", status["versions"], "breakingChange:", status["breakingChange"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
