// 项目静态体检：文件规模、潜在缺陷模式、分层违规
import fs from "node:fs";
import path from "node:path";

const ROOT = "D:\\project\\EasyMovie";
const SRC = ["EasyMovie.Client", "EasyMovie.Core", "EasyMovie.Data", "EasyMovie.Tools",
  "EasyMovie.BackfillRunner", "EasyMovie.Tests", "MovieListHeadlessTest"];

function walk(dir, acc = []) {
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return acc; }
  for (const e of entries) {
    if (e.name === "bin" || e.name === "obj" || e.name === ".git" || e.name === ".workbuddy") continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, acc);
    else if (e.name.endsWith(".cs") || e.name.endsWith(".xaml")) acc.push(p);
  }
  return acc;
}

const files = SRC.flatMap((s) => walk(path.join(ROOT, s)));
console.log("源文件总数 =", files.length);

// 1) 规模分布
const sized = files
  .map((f) => ({ f: path.relative(ROOT, f), lines: fs.readFileSync(f, "utf8").split(/\r?\n/).length }))
  .sort((a, b) => b.lines - a.lines);
console.log("\n=== TOP 20 最大文件 ===");
for (const s of sized.slice(0, 20)) console.log(String(s.lines).padStart(6) + "  " + s.f);

const total = sized.reduce((a, b) => a + b.lines, 0);
console.log("代码总行数 =", total);

// 2) 缺陷模式扫描
const patterns = {
  "async void": /\basync\s+void\b/g,
  "空 catch 块": /catch\s*(\([^)]*\))?\s*\{\s*\}/g,
  "catch 后只 Log 不处理_粗略": /catch[^{]*\{\s*Log\.(Error|Warning)\([^;]*\);\s*\}/g,
  "SQL 字符串拼接": /(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlInterpolated|\$\s*"[^"]*(SELECT|UPDATE|DELETE|INSERT)[^"]*")/g,
  "硬编码密钥": /(api[_-]?key|apikey|secret|token|password)\s*=\s*"[A-Za-z0-9_\-]{12,}"/gi,
  "Thread.Sleep": /\bThread\.Sleep\(/g,
  ".Result / .Wait()": /\.(Result|Wait)\(\)/g,
  "new Random()": /new\s+Random\(\)/g,
  "未 await 的 Task": /^\s*(?![^/]*await)[^/\n]*\.(SaveChangesAsync|ToListAsync|CountAsync|FirstOrDefaultAsync)\([^)]*\);/gm,
  "ConfigureAwait(false)": /ConfigureAwait\(false\)/g,
  "MessageBox 在后台线程风险": /await\s+Task\.Run\([^)]*\)\s*;?[\s\S]{0,200}?MessageBox\./g,
};

console.log("\n=== 缺陷模式统计 ===");
const perFile = {};
for (const f of files) {
  const txt = fs.readFileSync(f, "utf8");
  for (const [name, re] of Object.entries(patterns)) {
    const m = txt.match(re);
    if (m && m.length) {
      perFile[name] = perFile[name] || [];
      perFile[name].push([path.relative(ROOT, f), m.length]);
    }
  }
}
for (const [name, list] of Object.entries(perFile)) {
  const sum = list.reduce((a, b) => a + b[1], 0);
  console.log(`\n[${name}] 合计 ${sum} 处，分布在 ${list.length} 个文件`);
  list.sort((a, b) => b[1] - a[1]).slice(0, 8).forEach(([f, n]) => console.log("   " + String(n).padStart(4) + "  " + f));
}

// 3) 分层违规：Data/Core 不应引用 Client 或 WPF
console.log("\n=== 分层违规检查 ===");
for (const layer of ["EasyMovie.Core", "EasyMovie.Data", "EasyMovie.Tools"]) {
  for (const f of walk(path.join(ROOT, layer))) {
    const txt = fs.readFileSync(f, "utf8");
    if (/System\.Windows\b|using\s+EasyMovie\.Client\b|System\.Windows\.Controls/.test(txt)) {
      console.log(`  ${layer} 出现 WPF/Client 依赖: ${path.relative(ROOT, f)}`);
    }
  }
}
console.log("  (以上为空则无违规)");
