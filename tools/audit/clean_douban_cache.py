#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
清理 cache.db 中的高疑似误匹配脏数据（离线 / 零第三方依赖）。

背景：DoubanApiClient.PickBestMatch 的双向包含判定曾导致短片名截胡长片名
（搜「杀死比尔」命中「杀」），把无关影片写进缓存。该 bug 已于 2026-08-28 修复，
但历史脏数据仍在库里。

清理条件（三条全中才删，刻意从严）：
  1. Title 长度 <= --max-len（**默认 1**，即只删单字/单字母）
  2. 无评分（Rating 为空或 0）
  3. 无导演（Director 为空）

**为什么默认只删单字（重要，2026-08-29 实测修正）**：
最初把阈值设为 3，预览清单时发现问题——680 条缓存里有 39 条符合
「长度<=3 且无评分无导演」，但其中大量是**合法影视作品**，例如：
  「上海」(1935)、「潜伏」、「神探」(1998)、「蚁人」(2015, src=seed)、
  「驱魔人」、「小人物」、「人生路」、「神探」
中文影视片名 2~3 字非常常见，它们只是恰好没补全到评分/导演，并非误匹配产物。
删掉这些会导致下次导入时白白消耗豆瓣配额重新查询（每日上限 50）。

因此默认阈值收紧到 1：单字/单字母片名（如「爱」「杀」「我」「B」「S」）
几乎不可能是完整片名，是误匹配的最典型特征，且重建成本极低。
如需扩大范围，可用 --max-len 2 或 3 自行权衡（风险自负）。

**为什么不删其他短标题**：680 条中有 177 条 Title 长度 <= 3，其中 138 条
有评分/导演，是合法短片名（如「731」「哨兵」「指尖」），绝不能删。

安全设计：
  - 默认 dry-run，只预览不删除
  - --apply 才真正删除，且删除前自动备份到 backups/
  - 备份成功才继续；备份失败即刻中止

用法：
  python tools/audit/clean_douban_cache.py                # 预览（默认只含单字）
  python tools/audit/clean_douban_cache.py --max-len 3    # 预览更大范围
  python tools/audit/clean_douban_cache.py --apply        # 备份并删除
"""
import argparse
import os
import shutil
import sqlite3
import sys
from datetime import datetime

DB = r"C:\Users\10638\AppData\Local\EasyMovie\cache.db"
BACKUP_DIR = r"C:\Users\10638\AppData\Local\EasyMovie\backups"

# 默认 1：只删单字/单字母。中文 2~3 字片名大量合法（「潜伏」「神探」「蚁人」），
# 放大阈值会误删真实缓存，导致下次重新联网查询、白耗豆瓣配额。
DEFAULT_MAX_LEN = 1


def dirty_condition(max_len):
    return (f'LENGTH(Title) <= {max_len} '
            'AND COALESCE("Rating",0) = 0 '
            'AND COALESCE("Director",\'\') = \'\'')


def hr(t=""):
    print("\n" + "=" * 62)
    if t:
        print(t)
        print("=" * 62)


def main():
    ap = argparse.ArgumentParser(description="清理 cache.db 高疑似误匹配脏数据")
    ap.add_argument("--apply", action="store_true",
                    help="真正执行删除（默认只预览）。会先自动备份。")
    ap.add_argument("--max-len", type=int, default=DEFAULT_MAX_LEN,
                    help=f"标题长度阈值，默认 {DEFAULT_MAX_LEN}（只删单字）。"
                         "调大会误删合法的中文短片名，风险自负。")
    ap.add_argument("--db", default=DB)
    args = ap.parse_args()

    if not os.path.exists(args.db):
        print(f"SKIP: cache.db 不存在: {args.db}")
        return 2

    where_dirty = dirty_condition(args.max_len)
    con = sqlite3.connect(args.db)
    cur = con.cursor()

    cur.execute("SELECT COUNT(*) FROM CachedMovies")
    total_before = cur.fetchone()[0]

    cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE {where_dirty}")
    dirty = cur.fetchone()[0]

    cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE LENGTH(Title) <= {args.max_len}")
    short_total = cur.fetchone()[0]

    hr("A. 现状")
    print(f"  标题长度阈值              : <= {args.max_len}")
    print(f"  缓存总条数                : {total_before}")
    print(f"  Title 长度 <= {args.max_len} 的条数      : {short_total}")
    print(f"  其中高疑似脏数据（待清理）: {dirty}")
    print(f"  保留（有评分/导演，视为合法片名）: {short_total - dirty}")

    if dirty == 0:
        print("\n  没有需要清理的脏数据。")
        con.close()
        return 0

    hr("B. 待清理清单")
    cur.execute(f"SELECT Id, Title, Year, Source FROM CachedMovies WHERE {where_dirty} "
                "ORDER BY LENGTH(Title), Title")
    rows = cur.fetchall()
    for r in rows:
        print(f"    Id={r[0]:<5d} Title={r[1]!r:<8s} y={r[2]} src={r[3]}")
    print(f"\n  共 {len(rows)} 条。")

    if not args.apply:
        hr("预览模式")
        print("  以上为预览，未做任何修改。")
        print("  确认无误后加 --apply 执行（会自动备份到 backups/）。")
        con.close()
        return 0

    # ── 备份优先：备份失败即刻中止 ──
    hr("C. 备份")
    os.makedirs(BACKUP_DIR, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_path = os.path.join(BACKUP_DIR, f"cache.db.bak_{stamp}")
    try:
        shutil.copy2(args.db, backup_path)
        # 校验备份完整性（字节数一致）
        if os.path.getsize(backup_path) != os.path.getsize(args.db):
            print("  [FAIL] 备份大小不一致，中止删除。")
            con.close()
            return 1
        print(f"  已备份: {backup_path}  ({os.path.getsize(backup_path)} 字节)")
    except Exception as e:
        print(f"  [FAIL] 备份失败，中止删除: {e}")
        con.close()
        return 1

    # ── 删除 ──
    hr("D. 删除")
    try:
        cur.execute(f"DELETE FROM CachedMovies WHERE {where_dirty}")
        deleted = cur.rowcount
        con.commit()
        print(f"  已删除 {deleted} 条。")
    except Exception as e:
        con.rollback()
        print(f"  [FAIL] 删除失败，已回滚: {e}")
        con.close()
        return 1

    # ── 验证 ──
    hr("E. 验证")
    cur.execute("SELECT COUNT(*) FROM CachedMovies")
    total_after = cur.fetchone()[0]
    cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE {where_dirty}")
    remain = cur.fetchone()[0]
    print(f"  删除前: {total_before} 条")
    print(f"  删除后: {total_after} 条（减少 {total_before - total_after}）")
    print(f"  残留脏数据: {remain}")

    con.close()

    if remain != 0 or total_after != total_before - deleted:
        print("\n  FAIL —— 验证未通过，请从备份恢复。")
        return 1

    print("\n  PASS —— 清理完成且验证通过。")
    print(f"  如需回滚：把 {backup_path} 复制回 {args.db}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
