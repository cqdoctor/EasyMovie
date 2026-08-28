#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
EasyMovie 统计服务审计脚本（永久固化 / 离线可跑 / 零第三方依赖）

用途：守护 StatisticsService 的性能契约，防止优化成果被后续改动悄悄吃掉。

检查项：
  A. 数据面：真实 DB 里 PosterData 的占比 —— 量化「全实体加载」的代价
  B. 代码面：静态扫描 StatisticsService.cs，拦截会重新引入全量加载的写法
  C. 规模面：按当前库规模推算统计页所处档位，提示是否接近性能拐点

退出码：
  0 = PASS  全部通过
  1 = FAIL  发现回归风险（须修复）
  2 = SKIP  环境不满足（DB 不存在 / 源码缺失），非失败

用法：
  python tools/audit/audit_statistics.py
  python tools/audit/audit_statistics.py --db <path> --src <path>
"""
import argparse
import os
import re
import shutil
import sqlite3
import sys

DEFAULT_DB = r"C:\Users\10638\AppData\Local\EasyMovie\EasyMovie.db"
DEFAULT_SRC = "EasyMovie.Data/StatisticsService.cs"

# ── 实测基线（2026-08-28，同机 xUnit 基准，见 StatisticsBenchmarkTests.cs）──
# 规模 -> (优化前冷启动 ms, 优化后冷启动 ms)
BASELINE_MS = {290: (409, 13), 1000: (1299, 64), 2000: (2878, 72)}

# 代码面：这些写法会把整行（含 PosterData，实测占库 99.4%）读进内存，
# 或产生 JOIN 笛卡尔积。统计场景一律禁止。
FORBIDDEN_PATTERNS = [
    (r"_context\.Movies\s*\.\s*ToListAsync\s*\(",
     "Movies 全实体加载（会把 PosterData 一起读进内存）"),
    (r"_context\.Movies\s*\.\s*Include\s*\(",
     "Movies 直接 Include（会产生 JOIN 笛卡尔积 + 实体 fixup）"),
    (r"_context\.WatchLogs\s*\.\s*ToListAsync\s*\(",
     "WatchLogs 全实体加载（应改用窄投影）"),
]

# 期望存在的正向写法（缺失则提醒，不计入失败）
EXPECTED_PATTERNS = [
    (r"AsNoTracking\s*\(", "AsNoTracking（统计只读，不应产生变更跟踪开销）"),
    (r"MovieStatRow", "窄投影 MovieStatRow（避免加载 PosterData）"),
]


def hr(title=""):
    print("\n" + "=" * 62)
    if title:
        print(title)
        print("=" * 62)


def audit_data(db_path):
    """A. 数据面：量化全实体加载的代价。"""
    hr("A. 数据面 —— PosterData 占比")
    if not os.path.exists(db_path):
        print(f"  SKIP: 数据库不存在: {db_path}")
        return 2, None

    size_mb = os.path.getsize(db_path) / 1024 / 1024
    print(f"  DB 文件: {db_path}")
    print(f"  DB 体积: {size_mb:.2f} MB")

    try:
        con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        cur = con.cursor()
        cur.execute("SELECT COUNT(*) FROM Movies")
        movies = cur.fetchone()[0]
        cur.execute(
            "SELECT COUNT(*), SUM(LENGTH(PosterData)) FROM Movies WHERE PosterData IS NOT NULL"
        )
        posters, poster_bytes = cur.fetchone()
        poster_bytes = poster_bytes or 0
        cur.execute("SELECT COUNT(*) FROM WatchLogs")
        logs = cur.fetchone()[0]
        con.close()
    except sqlite3.Error as e:
        print(f"  SKIP: 读取失败（数据库可能被占用或结构不符）: {e}")
        return 2, None

    poster_mb = poster_bytes / 1024 / 1024
    # 口径说明：两种占比含义不同，都要给，否则容易被误读
    #   - 内容占比 = PosterData / 所有列字节总和（衡量「每行里有多大一块是海报」）
    #   - 文件占比 = PosterData / DB 文件体积（差异来自页开销与空闲页）
    try:
        con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        cur = con.cursor()
        cur.execute("PRAGMA table_info(Movies)")
        cols = [r[1] for r in cur.fetchall()]
        total_col_bytes = 0
        for c in cols:
            cur.execute(f'SELECT SUM(LENGTH("{c}")) FROM Movies')
            total_col_bytes += cur.fetchone()[0] or 0
        con.close()
    except sqlite3.Error:
        total_col_bytes = 0

    content_ratio = poster_bytes / total_col_bytes * 100 if total_col_bytes else 0
    file_ratio = poster_bytes / (size_mb * 1024 * 1024) * 100 if size_mb else 0
    print(f"  电影数            : {movies}")
    print(f"  观影记录数        : {logs}")
    print(f"  海报数            : {posters}（{poster_mb:.2f} MB）")
    print(f"  内容占比          : {content_ratio:.1f}%（PosterData / 所有列字节总和）")
    print(f"  文件占比          : {file_ratio:.1f}%（PosterData / DB 文件体积，含页开销）")
    if movies:
        print(f"  平均每部海报      : {poster_bytes / movies / 1024:.1f} KB")
        print(f"  → 全实体加载代价  : 约 {poster_mb:.1f} MB 进入托管堆（统计页并不需要）")

    print("  判定              : PASS（数据面为只读观测，不阻断）")
    return 0, movies


def audit_code(src_path):
    """B. 代码面：静态拦截会重新引入全量加载的写法。"""
    hr("B. 代码面 —— 全量加载回归护栏")
    if not os.path.exists(src_path):
        print(f"  SKIP: 源码不存在: {src_path}")
        return 2

    with open(src_path, "r", encoding="utf-8-sig") as f:
        src = f.read()

    # 去掉注释，避免文档里提到的反例被误判
    code = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    code = re.sub(r"^\s*//.*$", "", code, flags=re.M)

    failed = False
    for pattern, desc in FORBIDDEN_PATTERNS:
        hits = re.findall(pattern, code)
        if hits:
            failed = True
            print(f"  [FAIL] 检测到 {len(hits)} 处：{desc}")
            for m in re.finditer(pattern, code):
                line = code[: m.start()].count("\n") + 1
                print(f"         -> 第 {line} 行: {m.group(0)}")
        else:
            print(f"  [PASS] 未发现：{desc}")

    for pattern, desc in EXPECTED_PATTERNS:
        if re.search(pattern, code):
            print(f"  [PASS] 存在正向写法：{desc}")
        else:
            print(f"  [WARN] 缺少正向写法：{desc}（请确认是否另有等价优化）")

    return 1 if failed else 0


def audit_scale(movies):
    """C. 规模面：按当前库规模给出档位参考。"""
    hr("C. 规模面 —— 当前库所处档位")
    if not movies:
        print("  SKIP: 未获取到电影数")
        return 2

    print(f"  当前 {movies} 部，对照实测基线（冷启动耗时）：")
    print(f"  {'规模':>8} | {'优化前':>10} | {'优化后':>10} | {'提速':>8}")
    print("  " + "-" * 46)
    for n, (before, after) in sorted(BASELINE_MS.items()):
        mark = "  <== 当前" if abs(n - movies) == min(abs(k - movies) for k in BASELINE_MS) else ""
        print(f"  {n:>8} | {before:>8} ms | {after:>8} ms | {before / after:>7.1f}x{mark}")

    # 线性外推（优化后已呈线性，实测 1.3~2.0 KB/部、约 0.04 ms/部）
    est = 13 * max(movies, 1) / 290
    print(f"\n  按当前规模线性外推，优化后冷启动约 {est:.0f} ms")
    print("  注：优化前为超线性（O(n·g)），外推不适用。")
    return 0


# 自检用违规样本：模拟有人把统计查询改回「全实体加载」
SELF_TEST_VIOLATION = """
    // 由护栏自检临时注入（验证后自动移除）
    public async Task<int> __GuardrailProbe()
    {
        var all = await _context.Movies.ToListAsync();
        var logs = await _context.WatchLogs.ToListAsync();
        return all.Count + logs.Count;
    }
"""


def self_test(src_path):
    """自检：确认静态护栏真的能拦住回归，而不是形同虚设。
    流程：备份 -> 注入违规 -> 断言 FAIL -> 还原 -> 断言 PASS -> 校验逐字节还原。
    """
    hr("自检 —— 验证静态护栏有效性（会临时改写源码，结束后自动还原）")
    if not os.path.exists(src_path):
        print(f"  SKIP: 源码不存在: {src_path}")
        return 2

    bak = src_path + ".guardrail.bak"
    shutil.copy2(src_path, bak)
    ok = True
    try:
        with open(src_path, "r", encoding="utf-8-sig") as f:
            original = f.read()

        idx = original.rstrip().rfind("}")
        injected = original[:idx] + SELF_TEST_VIOLATION + original[idx:]

        # 1) 注入违规后必须判定为 FAIL
        with open(src_path, "w", encoding="utf-8") as f:
            f.write(injected)
        if audit_code(src_path) != 1:
            print("  [FAIL] 注入违规代码后护栏未拦截 —— 护栏失效！")
            ok = False
        else:
            print("  [PASS] 护栏成功拦截注入的违规代码")

        # 2) 还原后必须重新判定为 PASS
        with open(src_path, "w", encoding="utf-8") as f:
            f.write(original)
        if audit_code(src_path) != 0:
            print("  [FAIL] 还原后护栏仍报失败 —— 规则误伤正常代码！")
            ok = False
        else:
            print("  [PASS] 还原后护栏恢复通过，无误伤")

        # 3) 源码必须逐字节还原
        with open(src_path, "r", encoding="utf-8-sig") as f:
            restored = f.read()
        if restored != original:
            print("  [FAIL] 源码未完整还原！")
            ok = False
        else:
            print("  [PASS] 源码已逐字节还原")
    finally:
        if os.path.exists(bak):
            try:
                os.remove(bak)
            except OSError:
                pass

    print("\n  自检结论:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description="EasyMovie 统计服务性能契约审计")
    ap.add_argument("--db", default=DEFAULT_DB, help="EasyMovie.db 路径")
    ap.add_argument("--src", default=DEFAULT_SRC, help="StatisticsService.cs 路径")
    ap.add_argument("--self-test", action="store_true",
                    help="验证静态护栏本身是否仍能拦住回归（会临时改写并还原源码）")
    args = ap.parse_args()

    # 允许从任意目录调用：若相对路径找不到，则回退到仓库根目录
    src = args.src
    if not os.path.exists(src):
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        alt = os.path.join(root, args.src)
        if os.path.exists(alt):
            src = alt

    print("EasyMovie 统计服务审计（离线 / 只读 / 无网络依赖）")

    if args.self_test:
        return self_test(src)

    code_data, movies = audit_data(args.db)
    code_ret = audit_code(src)
    scale_ret = audit_scale(movies)

    hr("审计结论")
    # 数据面 SKIP 不阻断（DB 可能被应用占用）；代码面 FAIL 必须修复
    if code_ret == 1:
        print("  FAIL —— 统计服务存在全量加载回归风险，请修复后再提交。")
        return 1
    if code_ret == 2:
        print("  SKIP —— 未找到源码，跳过代码面检查。")
        return 2
    print("  PASS —— 未发现全量加载回归。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
