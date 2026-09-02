#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
EasyMovie 主库（EasyMovie.db）文本字段质量审计 —— 永久固化 / 离线可跑 / 零第三方依赖。

背景（2026-08-31 实证，290 部真实库）：
  1. **简介残留 HTML 标签 116/290**（另有 25 部仅含多余空白/实体，合计 141 部经
     StripHtml 后内容会变化 —— 两个数字口径不同，勿混用）：
     `DbHelper.CleanHtmlInExistingData()` 是一次性迁移，
     标志文件一存在就永远跳过；v2 迁移之后由文件夹监控自动入库 / 定时同步等
     **未清洗的写入路径**又把带 <p> 标签的简介写了进去。UI 侧
     `MainWindow.xaml.cs` 直接 `DetailSynopsis.Text = movie.Synopsis`，
     导致约 40% 的影片把原始标签渲染给用户。
  2. **导演字段整值是职位标签**：Inventory #207「幽灵 Phantom AC3」的 Director = "编剧"。
     根因是 `InvalidPersonLabels` 旧版只有 9 项、不含"编剧"，且数据汇合点
     `MovieInfoFetcher` 只剥 HTML、从不过黑名单。
  3. **Year = 1000 共 6 部**（片名均含 "EAC3 Atmos"），属越界值。

本脚本持续监控这些指标，防止写入路径回退后问题复发或继续恶化。

用法：
  python tools/audit/audit_text_fields.py
  python tools/audit/audit_text_fields.py --list-dirty 20   # 列出脏数据样本
  python tools/audit/audit_text_fields.py --db <path>       # 指定库文件

退出码：
  0 = PASS  全部检查项均为 0
  1 = FAIL  存在脏数据（脚本会分项列出数量与样本）
  2 = SKIP  环境不满足（库文件不存在等）

注意：本脚本**只读**。它会把库复制到临时目录再打开，
绝不修改、删除或回写用户的真实数据库。清理脏数据需人工确认后另行处理。
"""
import argparse
import datetime
import os
import re
import shutil
import sqlite3
import sys
import tempfile

# 与 Core/Helpers/MovieCreditCleaner.cs 的 InvalidPersonLabels 保持一致（整串匹配）
INVALID_PERSON_LABELS = [
    "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多",
    "编剧", "原著", "角色", "制片人", "制片", "摄影", "剪辑", "视觉效果", "艺术指导", "服装设计",
]

HTML_TAG_RE = re.compile(r"<[^>]+>")

# 受检文本字段。Cast 是 SQL 关键字，查询时必须加引号。
TEXT_FIELDS = ["Synopsis", "Director", "Cast", "Country", "Notes", "OriginalTitle"]

# 离线元数据缓存库 cache.db 的受检字段（该库无 Synopsis / Notes 列）
CACHE_FIELDS = ["Title", "Director", "Cast", "Country", "Language", "OriginalTitle"]

MIN_YEAR = 1888  # 电影诞生于 1888 年（《朗德海花园场景》）
YEAR_HEADROOM = 2  # 允许"未来 2 年"的待上映片


def default_db_path():
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return None
    return os.path.join(local, "EasyMovie", "EasyMovie.db")


def default_cache_db_path():
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return None
    return os.path.join(local, "EasyMovie", "cache.db")


def open_readonly_copy(db_path):
    """把库（含 WAL/SHM）复制到临时目录后只读打开，避免对用户库产生任何写入或锁竞争。"""
    tmpdir = tempfile.mkdtemp(prefix="easymovie_audit_")
    dst = os.path.join(tmpdir, "audit.db")
    shutil.copy2(db_path, dst)
    for suffix in ("-wal", "-shm"):
        side = db_path + suffix
        if os.path.exists(side):
            try:
                shutil.copy2(side, dst + suffix)
            except OSError:
                pass  # 边车文件读不到不影响主库快照
    con = sqlite3.connect("file:{}?mode=ro".format(dst.replace("\\", "/")), uri=True)
    return con, tmpdir


def table_exists(con, name):
    row = con.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)
    ).fetchone()
    return row is not None


def check_html(con):
    """检查各文本字段里残留的 HTML 标签。"""
    cols = ", ".join('"{}"'.format(c) for c in TEXT_FIELDS)
    rows = con.execute(
        "SELECT Id, Title, {} FROM Movies".format(cols)
    ).fetchall()
    findings = {c: [] for c in TEXT_FIELDS}
    for row in rows:
        mid, title = row[0], row[1]
        for idx, col in enumerate(TEXT_FIELDS, start=2):
            val = row[idx]
            if not val:
                continue
            m = HTML_TAG_RE.search(val)
            if m:
                findings[col].append((mid, title, m.group(0), val))
    return findings


def check_director_label(con):
    """导演字段整值是否就是职位标签（如"编剧"）。"""
    rows = con.execute('SELECT Id, Title, Director FROM Movies').fetchall()
    bad = []
    for mid, title, director in rows:
        if not director:
            continue
        if director.strip() in INVALID_PERSON_LABELS:
            bad.append((mid, title, director))
    return bad


def check_year(con):
    """年份是否越界。"""
    max_year = datetime.date.today().year + YEAR_HEADROOM
    rows = con.execute(
        "SELECT Id, Title, Year FROM Movies WHERE Year < ? OR Year > ?",
        (MIN_YEAR, max_year),
    ).fetchall()
    return [(r[0], r[1], r[2]) for r in rows]


def check_duplicates(con):
    """重复文件路径 / 重复 片名+年份。"""
    dup_path = con.execute(
        "SELECT FilePath, COUNT(*) c FROM Movies WHERE FilePath IS NOT NULL AND FilePath != '' "
        "GROUP BY FilePath HAVING c > 1"
    ).fetchall()
    dup_title = con.execute(
        "SELECT Title, Year, COUNT(*) c FROM Movies GROUP BY Title, Year HAVING c > 1"
    ).fetchall()
    return dup_path, dup_title


def check_cache(con):
    """离线元数据缓存库 cache.db 的文本字段质量。

    为什么也要查它：cache.db 是 MovieInfoFetcher 的**离线命中源**，命中时会在汇合点之前
    提前 return，脏缓存会直灌个人库。写入方（SeedImporter 只 Trim、DoubanBackfillService
    写豆瓣原始结果）都不走 MovieInfoFetcher 的清洗，因此这里是最后一道可观测防线。
    """
    if not table_exists(con, "CachedMovies"):
        return None
    total = con.execute("SELECT COUNT(*) FROM CachedMovies").fetchone()[0]
    html = {}
    for col in CACHE_FIELDS:
        try:
            rows = con.execute(
                'SELECT Id, "{c}" FROM CachedMovies WHERE "{c}" IS NOT NULL'.format(c=col)
            ).fetchall()
        except sqlite3.OperationalError:
            continue  # 该列在旧版库中不存在，跳过
        hits = [(r[0], r[1]) for r in rows if HTML_TAG_RE.search(r[1] or "")]
        html[col] = (hits, total)
    try:
        rows = con.execute("SELECT Id, Director FROM CachedMovies WHERE Director IS NOT NULL").fetchall()
    except sqlite3.OperationalError:
        rows = []
    labels = [(r[0], r[1]) for r in rows if (r[1] or "").strip() in INVALID_PERSON_LABELS]
    return html, labels, total


def check_missing(con):
    """关键字段缺失率（不作失败判据，只作健康度参考）。"""
    total = con.execute("SELECT COUNT(*) FROM Movies").fetchone()[0]
    out = {}
    for col in ["PosterData", "Director", "Country", "Language", "Year", "Runtime", "Synopsis"]:
        q = 'SELECT COUNT(*) FROM Movies WHERE "{c}" IS NULL'.format(c=col)
        if col in ("Year", "Runtime"):
            q = 'SELECT COUNT(*) FROM Movies WHERE "{c}" IS NULL OR "{c}" = 0'.format(c=col)
        if col in ("Director", "Country", "Language", "Synopsis"):
            q = 'SELECT COUNT(*) FROM Movies WHERE "{c}" IS NULL OR TRIM("{c}") = \'\''.format(c=col)
        n = con.execute(q).fetchone()[0]
        out[col] = (n, total)
    return out


def main():
    ap = argparse.ArgumentParser(description="EasyMovie 主库文本字段质量审计（只读）")
    ap.add_argument("--db", default=None, help="主库路径，默认 %%LOCALAPPDATA%%\\EasyMovie\\EasyMovie.db")
    ap.add_argument("--cache-db", default=None,
                    help="离线缓存库路径，默认 %%LOCALAPPDATA%%\\EasyMovie\\cache.db（不存在则跳过该项）")
    ap.add_argument("--list-dirty", type=int, default=5, metavar="N",
                    help="每项检查列出的脏数据样本数（默认 5）")
    args = ap.parse_args()

    db_path = args.db or default_db_path()
    if not db_path or not os.path.exists(db_path):
        print("SKIP: 找不到数据库：{}".format(db_path))
        return 2

    con, tmpdir = open_readonly_copy(db_path)
    try:
        if not table_exists(con, "Movies"):
            print("SKIP: 库中无 Movies 表：{}".format(db_path))
            return 2

        total = con.execute("SELECT COUNT(*) FROM Movies").fetchone()[0]
        print("=" * 72)
        print("EasyMovie 文本字段质量审计（只读快照）")
        print("  库文件 : {}".format(db_path))
        print("  影片数 : {}".format(total))
        print("=" * 72)

        failed = False

        # 1) HTML 残留
        print("\n[1] 文本字段残留 HTML 标签")
        html = check_html(con)
        html_total = 0
        for col in TEXT_FIELDS:
            items = html[col]
            html_total += len(items)
            flag = "OK  " if not items else "FAIL"
            print("  {:<14} {:<4} {}/{}".format(col, flag, len(items), total))
            for mid, title, tag, val in items[: args.list_dirty]:
                snippet = val.replace("\n", " ")[:60]
                print("        #{} {}  命中 {} | {}".format(mid, title, tag, snippet))
            if len(items) > args.list_dirty:
                print("        ... 另有 {} 条".format(len(items) - args.list_dirty))
        if html_total:
            failed = True

        # 2) 导演整值是职位标签
        print("\n[2] 导演字段整值为职位标签（如「编剧」）")
        bad_dir = check_director_label(con)
        print("  Director      {:<4} {}/{}".format(
            "OK  " if not bad_dir else "FAIL", len(bad_dir), total))
        for mid, title, val in bad_dir[: args.list_dirty]:
            print("        #{} {}  Director = {!r}".format(mid, title, val))
        if bad_dir:
            failed = True

        # 3) 年份越界
        print("\n[3] 年份越界（< {} 或 > {}）".format(
            MIN_YEAR, datetime.date.today().year + YEAR_HEADROOM))
        bad_year = check_year(con)
        print("  Year          {:<4} {}/{}".format(
            "OK  " if not bad_year else "FAIL", len(bad_year), total))
        for mid, title, year in bad_year[: args.list_dirty]:
            print("        #{} {}  Year = {}".format(mid, title, year))
        if bad_year:
            failed = True

        # 4) 重复
        print("\n[4] 重复记录")
        dup_path, dup_title = check_duplicates(con)
        print("  重复文件路径  {:<4} {}".format("OK  " if not dup_path else "FAIL", len(dup_path)))
        for p, c in dup_path[: args.list_dirty]:
            print("        x{} {}".format(c, p))
        print("  重复片名+年份 {:<4} {}".format("OK  " if not dup_title else "FAIL", len(dup_title)))
        for t, y, c in dup_title[: args.list_dirty]:
            print("        x{} {} ({})".format(c, t, y))
        if dup_path or dup_title:
            failed = True

        # 5) 缺失率（仅参考，不判失败）
        print("\n[5] 关键字段缺失率（参考项，不参与判定）")
        miss = check_missing(con)
        for col, (n, tot) in miss.items():
            pct = (n * 100.0 / tot) if tot else 0.0
            print("  {:<14} {}/{} ({:.1f}%)".format(col, n, tot, pct))

        # 6) 离线缓存库 cache.db（源脏 → 命中时直灌个人库）
        print("\n[6] 离线元数据缓存 cache.db 文本字段（源脏会在命中时直灌个人库）")
        cache_path = args.cache_db or default_cache_db_path()
        cache_con = None
        cache_tmp = None
        if cache_path and os.path.exists(cache_path):
            try:
                cache_con, cache_tmp = open_readonly_copy(cache_path)
            except (OSError, sqlite3.Error) as exc:
                print("  SKIP  无法打开缓存库：{}".format(exc))
        else:
            print("  SKIP  找不到缓存库：{}".format(cache_path))
        if cache_con is not None:
            try:
                res = check_cache(cache_con)
                if res is None:
                    print("  SKIP  库中无 CachedMovies 表")
                else:
                    html, labels, ctotal = res
                    dirty = 0
                    for col, (hits, tot) in html.items():
                        dirty += len(hits)
                        print("  {:<14} {:<4} {}/{}".format(
                            col, "OK  " if not hits else "FAIL", len(hits), tot))
                        for cid, val in hits[: args.list_dirty]:
                            print("        #{} {}".format(cid, (val or "").replace("\n", " ")[:60]))
                    print("  {:<14} {:<4} {}/{}".format(
                        "Director=职位标签", "OK  " if not labels else "FAIL", len(labels), ctotal))
                    for cid, val in labels[: args.list_dirty]:
                        print("        #{} Director = {!r}".format(cid, val))
                    if dirty or labels:
                        failed = True
            finally:
                cache_con.close()
                if cache_tmp:
                    shutil.rmtree(cache_tmp, ignore_errors=True)

        print("\n" + "=" * 72)
        if failed:
            print("FAIL: 存在脏数据。若为 HTML 残留，请先启动一次应用让迁移清理存量，"
                  "再重跑本脚本确认归零。")
        else:
            print("PASS: 全部检查项均为 0。")
        print("=" * 72)
        return 1 if failed else 0
    finally:
        con.close()
        shutil.rmtree(tmpdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
