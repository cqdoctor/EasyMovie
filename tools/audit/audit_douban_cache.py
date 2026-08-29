#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
EasyMovie 离线元数据缓存（cache.db）数据质量审计 —— 永久固化 / 离线可跑 / 零第三方依赖。

背景（2026-08-28 实证）：
  DoubanApiClient.PickBestMatch 的 TitleContains 曾是双向包含判定，
  搜「杀死比尔」会命中单字片「杀」，进而把无关影片写进 cache.db。
  观测到 680 条缓存中 177 条 Title 长度 <= 3，样本为「爱」「杀」「我」「B」「S」「K」「法」「冷」「暗」。
  该 bug 已修（反向包含需满足 子串长度>=3 且 长度比>=0.5），但历史脏数据仍在库里。

本脚本持续监控脏数据比例，防止问题复发或继续恶化。

用法：
  python tools/audit/audit_douban_cache.py
  python tools/audit/audit_douban_cache.py --list-dirty 20   # 列出脏数据样本
  python tools/audit/audit_douban_cache.py --db <path>

退出码：
  0 = PASS  脏数据比例在阈值内
  1 = FAIL  脏数据比例超阈值（说明误匹配在继续产生）
  2 = SKIP  环境不满足（cache.db 不存在等）

注意：本脚本只读，绝不删除/修改任何数据。清理脏数据需人工确认后另行处理。
"""
import argparse
import os
import sqlite3
import sys

DEFAULT_DB = r"C:\Users\10638\AppData\Local\EasyMovie\cache.db"

# 短标题占比阈值（%）。超过即判定误匹配仍在继续产生。
DIRTY_RATIO_LIMIT = 30.0
# 判定为「疑似脏数据」的标题长度上限
SHORT_TITLE_LEN = 3


def hr(t=""):
    print("\n" + "=" * 62)
    if t:
        print(t)
        print("=" * 62)


def main():
    ap = argparse.ArgumentParser(description="EasyMovie 离线元数据缓存数据质量审计")
    ap.add_argument("--db", default=DEFAULT_DB, help="cache.db 路径")
    ap.add_argument("--list-dirty", type=int, metavar="N", default=0,
                    help="列出 N 条疑似脏数据样本")
    args = ap.parse_args()

    print("EasyMovie 缓存数据质量审计（离线 / 只读）")

    if not os.path.exists(args.db):
        print(f"\nSKIP: cache.db 不存在: {args.db}")
        return 2

    con = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
    cur = con.cursor()

    hr("A. 缓存规模与填充率")
    cur.execute('SELECT COUNT(*) FROM CachedMovies')
    total = cur.fetchone()[0]
    print(f"  总条数: {total}")
    if total == 0:
        print("  缓存为空，无需审计")
        con.close()
        return 0

    for label, cond in [
        ("有评分", 'COALESCE("Rating",0) > 0'),
        ("有导演", 'COALESCE("Director",\'\') <> \'\''),
        ("有主演", 'COALESCE("Cast",\'\') <> \'\''),
        ("有海报", 'COALESCE("PosterUrl",\'\') <> \'\''),
    ]:
        cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE {cond}")
        n = cur.fetchone()[0]
        print(f"  {label:8s}: {n:>5d} / {total}  ({n / total * 100:5.1f}%)")

    hr("B. 短标题脏数据检测")
    cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE LENGTH(Title) <= {SHORT_TITLE_LEN}")
    short_n = cur.fetchone()[0]
    ratio = short_n / total * 100
    print(f"  Title 长度 <= {SHORT_TITLE_LEN} 的记录: {short_n} / {total}  ({ratio:.1f}%)")
    print(f"  阈值: {DIRTY_RATIO_LIMIT:.0f}%")

    # 高疑似误匹配：单字 + 无评分 + 无导演。
    # 阈值取 1 而非 3 —— 中文 2~3 字片名大量合法（「潜伏」「神探」「蚁人」「驱魔人」），
    # 把它们算作脏数据会误导后续清理，甚至误删真实缓存。
    # 判定标准与 tools/audit/clean_douban_cache.py 保持一致。
    cur.execute('SELECT COUNT(*) FROM CachedMovies WHERE LENGTH(Title) <= 1 '
                'AND COALESCE("Rating",0) = 0 AND COALESCE("Director",\'\') = \'\'')
    junk = cur.fetchone()[0]
    print(f"  其中「单字 + 评分导演皆空」（高疑似误匹配）: {junk}")

    cur.execute(f"SELECT COUNT(*) FROM CachedMovies WHERE LENGTH(Title) BETWEEN 2 AND {SHORT_TITLE_LEN} "
                'AND COALESCE("Rating",0) = 0 AND COALESCE("Director",\'\') = \'\'')
    ambiguous = cur.fetchone()[0]
    print(f"  「2~3 字 + 评分导演皆空」（多为合法中文短片名，不建议删）: {ambiguous}")
    print(f"  「短标题但有评分/导演」（合法短片名，必须保留）: {short_n - junk - ambiguous}")

    if args.list_dirty > 0:
        print(f"\n  --- 疑似脏数据样本（前 {args.list_dirty} 条）---")
        cur.execute('SELECT Title, Year, Source FROM CachedMovies '
                    f'WHERE LENGTH(Title) <= {SHORT_TITLE_LEN} ORDER BY LENGTH(Title), Title '
                    f'LIMIT {args.list_dirty}')
        for r in cur.fetchall():
            print(f"    Title={r[0]!r:8s} y={r[1]} src={r[2]}")

    con.close()

    hr("审计结论")
    if ratio > DIRTY_RATIO_LIMIT:
        print(f"  FAIL —— 短标题占比 {ratio:.1f}% 超过阈值 {DIRTY_RATIO_LIMIT:.0f}%，"
              "疑似误匹配仍在继续产生脏数据。")
        print("       检查 PickBestMatch.TitleContains 的反向包含门槛是否被改回去了。")
        return 1
    print(f"  PASS —— 短标题占比 {ratio:.1f}% 在阈值内。")
    if junk > 0:
        print(f"  提示：仍有 {junk} 条单字脏数据可清理（本脚本只读，不删数据）。")
        print("       执行：python tools/audit/clean_douban_cache.py --apply")
    if ambiguous > 0:
        print(f"  提示：{ambiguous} 条 2~3 字记录虽无评分/导演，但多为合法中文短片名"
              "（如「潜伏」「神探」「蚁人」），建议保留——删掉会白耗豆瓣配额重新查询。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
