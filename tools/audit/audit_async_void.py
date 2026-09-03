#!/usr/bin/env python3
"""
只读审计：统计 async void 方法中「方法体内没有 try/catch」的比例。

背景：async void 里的异常无法被调用方捕获，会直接抛到 SynchronizationContext，
在 WPF 上即进程级崩溃。XAML 事件处理器必须是 async void（签名所限），
因此正确的缓解手段不是改成 async Task，而是**确保每个 async void 体内有 try/catch**。

本脚本只做静态扫描，不修改任何文件。
"""
import io
import os
import re
import sys

ROOTS = ["EasyMovie.Client", "EasyMovie.Core", "EasyMovie.Data"]
DECL = re.compile(r"\basync\s+void\b")


def find_bodies(src):
    """返回 [(line_no, signature, has_catch, body_len)]"""
    out = []
    for m in DECL.finditer(src):
        # 从声明处向后找第一个 '{'（方法体起点）；跳过形参列表里的 brace
        i = src.find("{", m.end())
        if i < 0:
            continue
        depth = 0
        j = i
        while j < len(src):
            if src[j] == "{":
                depth += 1
            elif src[j] == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        body = src[i:j + 1]
        # 只看本层是否有 catch（粗略：async void 体内极少嵌套 try，够用）
        has_catch = re.search(r"\bcatch\b", body) is not None
        sig_line = src[m.start():src.find("\n", m.start())].strip()
        line_no = src[:m.start()].count("\n") + 1
        out.append((line_no, sig_line, has_catch, body.count("\n") + 1))
    return out


def main():
    total = 0
    nocatch = []
    per_file = []
    for root in ROOTS:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames
                           if d not in ("obj", "bin", "bldobj", "intobj1")]
            for fn in sorted(filenames):
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dirpath, fn)
                src = io.open(p, encoding="utf-8-sig", errors="replace").read()
                items = find_bodies(src)
                if not items:
                    continue
                total += len(items)
                bad = [x for x in items if not x[2]]
                per_file.append((len(items), len(bad), p))
                for x in bad:
                    nocatch.append((p, x[0], x[1], x[3]))

    print("async void 总数: %d" % total)
    print("其中方法体内无 try/catch: %d (%.1f%%)"
          % (len(nocatch), 100.0 * len(nocatch) / total if total else 0))
    print()
    print("=== 按文件（总数 / 无保护） ===")
    for n, bad, p in sorted(per_file, key=lambda t: (-t[1], -t[0])):
        print("  %3d / %3d  %s" % (bad, n, p))
    print()
    print("=== 无 try/catch 的 async void 明细（按方法体行数降序，长的风险更高） ===")
    for p, ln, sig, blen in sorted(nocatch, key=lambda t: -t[3]):
        print("  %-58s L%-5d body=%-4d %s" % (p, ln, blen, sig[:70]))


if __name__ == "__main__":
    main()
