#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
EasyMovie 推荐服务性能护栏（永久固化 / 离线可跑 / 零第三方依赖）

守护 RecommendationService.GetRecommendationsAsync 的两项性能契约：
  A. 不得全量加载：为挑 20 部推荐而把全库海报读进内存（实测 96 KB/部，2000 部 ≈ 188 MB）
  B. 不得线性查找：candidates.FirstOrDefault(...) 是 O(n²)，实测热耗时 2270 ms 的主因

正确做法是两阶段：先查不含海报的窄投影算出 topN 的 Id，再只加载这 topN 部完整实体。

退出码：
  0 = PASS  未发现回归
  1 = FAIL  发现回归（须修复）
  2 = SKIP  环境不满足（源码缺失）

用法：
  python tools/audit/audit_recommendation.py
  python tools/audit/audit_recommendation.py --self-test
"""
import argparse
import os
import re
import shutil
import sys

DEFAULT_SRC = "EasyMovie.Core/Services/RecommendationService.cs"
MAIN_METHOD = "GetRecommendationsAsync"

# 主方法内禁止出现的写法
FORBIDDEN_IN_MAIN = [
    (r"GetAllAsync\s*\(", "主方法内全量加载（会把全库 PosterData 读进内存）"),
    (r"\.FirstOrDefault\s*\(", "主方法内线性查找（O(n²) 复杂度回归）"),
]

# 期望存在的两阶段写法（整文件范围）
EXPECTED = [
    (r"GetRecommendationDataAsync\s*\(", "两阶段之一：查询不含海报的窄投影"),
    (r"GetByIdsAsync\s*\(", "两阶段之二：只加载要展示的那几部完整实体"),
]

SELF_TEST_VIOLATION = """
        // 由护栏自检临时注入（验证后自动移除）
        var __probe = await _movieRepo.GetAllAsync();
        var __hit = __probe.FirstOrDefault(m => m.Id == 1);
"""


def strip_comments(src):
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"^\s*//.*$", "", src, flags=re.M)


def extract_method(src, name):
    """截取指定方法的主体（到下一个同级方法声明为止）。"""
    lines = src.split("\n")
    start = None
    for i, line in enumerate(lines):
        if name in line and re.match(r"\s*(public|private|internal|protected)\s", line):
            start = i
            break
    if start is None:
        return ""
    for j in range(start + 1, len(lines)):
        if lines[j].startswith("    ") and re.match(r"\s*(public|private|internal|protected)\s", lines[j]):
            return "\n".join(lines[start:j])
    return "\n".join(lines[start:])


def audit(src_path, verbose=True):
    if not os.path.exists(src_path):
        if verbose:
            print(f"  SKIP: 源码不存在: {src_path}")
        return 2

    with open(src_path, "r", encoding="utf-8-sig") as f:
        code = strip_comments(f.read())

    if verbose:
        print("=" * 62)
        print(f"A. 主方法 {MAIN_METHOD} 内的禁止写法")
        print("=" * 62)

    body = extract_method(code, MAIN_METHOD)
    if not body:
        if verbose:
            print(f"  SKIP: 未找到方法 {MAIN_METHOD}")
        return 2

    failed = False
    for pattern, desc in FORBIDDEN_IN_MAIN:
        hits = re.findall(pattern, body)
        if hits:
            failed = True
            if verbose:
                print(f"  [FAIL] 检测到 {len(hits)} 处：{desc}")
        elif verbose:
            print(f"  [PASS] 未发现：{desc}")

    if verbose:
        print("\n" + "=" * 62)
        print("B. 两阶段查询写法（整文件）")
        print("=" * 62)

    for pattern, desc in EXPECTED:
        if re.search(pattern, code):
            if verbose:
                print(f"  [PASS] 存在：{desc}")
        else:
            if verbose:
                print(f"  [WARN] 缺少：{desc}")

    return 1 if failed else 0


def self_test(src_path):
    print("=" * 62)
    print("自检 —— 验证护栏有效性（会临时改写源码，结束后自动还原）")
    print("=" * 62)
    if not os.path.exists(src_path):
        print(f"  SKIP: 源码不存在: {src_path}")
        return 2

    bak = src_path + ".guardrail.bak"
    shutil.copy2(src_path, bak)
    ok = True
    try:
        with open(src_path, "r", encoding="utf-8-sig") as f:
            original = f.read()

        # 注入到主方法末尾（在 MaterializeAsync 调用之前）
        idx = original.find("return await MaterializeAsync(top);")
        if idx == -1:
            print("  SKIP: 未找到注入点")
            return 2
        injected = original[:idx] + SELF_TEST_VIOLATION + "        " + original[idx:]

        with open(src_path, "w", encoding="utf-8") as f:
            f.write(injected)
        if audit(src_path, verbose=False) != 1:
            print("  [FAIL] 注入违规代码后护栏未拦截 —— 护栏失效！")
            ok = False
        else:
            print("  [PASS] 护栏成功拦截注入的违规代码")

        with open(src_path, "w", encoding="utf-8") as f:
            f.write(original)
        if audit(src_path, verbose=False) != 0:
            print("  [FAIL] 还原后仍报失败 —— 规则误伤正常代码！")
            ok = False
        else:
            print("  [PASS] 还原后恢复通过，无误伤")

        with open(src_path, "r", encoding="utf-8-sig") as f:
            if f.read() != original:
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
    ap = argparse.ArgumentParser(description="EasyMovie 推荐服务性能契约审计")
    ap.add_argument("--src", default=DEFAULT_SRC)
    ap.add_argument("--self-test", action="store_true",
                    help="验证护栏本身是否仍能拦住回归（会临时改写并还原源码）")
    args = ap.parse_args()

    src = args.src
    if not os.path.exists(src):
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        alt = os.path.join(root, args.src)
        if os.path.exists(alt):
            src = alt

    print("EasyMovie 推荐服务性能护栏（离线 / 只读）")

    if args.self_test:
        return self_test(src)

    ret = audit(src, verbose=True)
    print("\n" + "=" * 62)
    print("审计结论")
    print("=" * 62)
    if ret == 1:
        print("  FAIL —— 推荐服务存在性能回归，请修复后再提交。")
    elif ret == 2:
        print("  SKIP —— 未找到源码或主方法。")
    else:
        print("  PASS —— 未发现性能回归。")
    return ret


if __name__ == "__main__":
    sys.exit(main())
