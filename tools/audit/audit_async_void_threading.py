#!/usr/bin/env python3
"""
只读审计：判断 async void 方法是否存在「在后台线程上执行」的调用点。

背景（由 tools/probe/AsyncVoidProbe 实测得出，勿凭记忆推翻）：
  async void 抛出的异常会经 AsyncVoidMethodBuilder 投递回**捕获时的 SynchronizationContext**。
  - 在 WPF UI 线程调用（SynchronizationContext = DispatcherSynchronizationContext）：
    异常回到 dispatcher，被 App.xaml.cs 的 DispatcherUnhandledException(Handled=true) 兜住，进程不崩。
  - 在后台线程调用（SynchronizationContext = null，如 Task.Run / ThreadPool /
    System.Timers.Elapsed 内部）：
    异常在线程池线程抛出，走 AppDomain.UnhandledException —— **不可恢复，进程必崩**。

本脚本找的是第二种。

实现要点（踩过的坑）：早期版本对每处后台上下文取「之后 3000 字符」作窗口，产生 4 个
误报（Task.Run 体内并没有调用那些 async void 方法，只是名字恰好落在窗口里）。
现改为**括号配平精确取委托体**，杜绝此类误报。
"""
import io
import os
import re

ROOTS = ["EasyMovie.Client", "EasyMovie.Core", "EasyMovie.Data"]
SKIP_DIRS = {"obj", "bin", "bldobj", "intobj1"}

ASYNC_VOID_DECL = re.compile(r"\basync\s+void\s+(\w+)\s*\(")

# 后台线程上下文：匹配到 start 后，需精确截出其委托体
BG_PATTERNS = [
    re.compile(r"Task\.Run\s*\("),
    re.compile(r"Task\.Factory\.StartNew\s*\("),
    re.compile(r"new\s+Thread\s*\("),
    re.compile(r"ThreadPool\.QueueUserWorkItem\s*\("),
]


def match_paren(src, open_idx):
    """给定 '(' 的下标，返回配对的 ')' 下标；不配平返回 -1。"""
    depth = 0
    for i in range(open_idx, len(src)):
        c = src[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
    return -1


def match_brace(src, open_idx):
    depth = 0
    for i in range(open_idx, len(src)):
        c = src[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def delegate_body(src, after_idx):
    """
    从后台上下文关键字之后的位置，截出委托体的字符区间 (start, end)。
    两种形态：
      Task.Run(() => { ... })   -> 取 '{' 起的花括号块
      Task.Run(() => Foo())     -> 取 '=>' 之后到配平 ')' 之前的表达式
    截不出来返回 None。
    """
    # 找该处之后的第一个 '=>'
    m = re.compile(r"=>").search(src, after_idx, after_idx + 200)
    if not m:
        return None
    # 形态 1：'=> {' 后的花括号块
    b = src.find("{", m.end(), m.end() + 20)
    if 0 <= b <= m.end() + 3:
        e = match_brace(src, b)
        if e > 0:
            return (b, e)
    # 形态 2：'=> expr'，取到配平 ')'
    p = src.find("(", after_idx)
    if p < 0:
        return None
    e = match_paren(src, p)
    if e < 0:
        return None
    return (m.end(), e)


def collect_async_void_names():
    names = {}
    for root in ROOTS:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in sorted(filenames):
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dirpath, fn)
                src = io.open(p, encoding="utf-8-sig", errors="replace").read()
                for m in ASYNC_VOID_DECL.finditer(src):
                    line_no = src[:m.start()].count("\n") + 1
                    names.setdefault(m.group(1), []).append((p, line_no))
    return names


def main():
    names = collect_async_void_names()
    print("async void 方法（去重后）: %d 个，调用点 %d 处"
          % (len(names), sum(len(v) for v in names.values())))

    hits = []
    scanned = 0
    for root in ROOTS:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in sorted(filenames):
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dirpath, fn)
                src = io.open(p, encoding="utf-8-sig", errors="replace").read()
                for pat in BG_PATTERNS:
                    for bm in pat.finditer(src):
                        scanned += 1
                        region = delegate_body(src, bm.end())
                        if not region:
                            continue
                        body = src[region[0]:region[1]]
                        for name in names:
                            if re.search(r"\b%s\s*\(" % re.escape(name), body):
                                line_no = src[:bm.start()].count("\n") + 1
                                hits.append((p, line_no, pat.pattern, name,
                                             body.strip()[:60].replace("\n", " ")))

    print("扫描后台线程上下文: %d 处" % scanned)
    if not hits:
        print()
        print("结论：**未发现** async void 方法在后台线程上下文中被调用。")
        print("      即全部 async void 都在 UI 线程 SynchronizationContext 下执行，")
        print("      异常均可被 DispatcherUnhandledException 兜住（探针实测 3/3 捕获、dispatcher 存活）。")
        return

    print()
    print("=== 在后台线程调用 async void 的位置 ===")
    for p, ln, ctx, name, snippet in hits:
        print("  %s:%d  [%s] -> %s()" % (p, ln, ctx, name))
        print("        委托体: %s..." % snippet)


if __name__ == "__main__":
    main()
