// 统计大文件的方法构成，找出可继续拆分的区块
import fs from "node:fs";
import path from "node:path";

const ROOT = "D:\\project\\EasyMovie";
const targets = [
  "EasyMovie.Client/Views/MovieListView.xaml.cs",
  "EasyMovie.Client/Views/VideoPlayerHost.xaml.cs",
  "EasyMovie.Client/MainWindow.xaml.cs",
  "EasyMovie.Client/Views/SettingsView.xaml.cs",
  "EasyMovie.Tools/MovieApi/DoubanApiClient.cs",
  "EasyMovie.Client/DbHelper.cs",
];

for (const t of targets) {
  const p = path.join(ROOT, t);
  const lines = fs.readFileSync(p, "utf8").split(/\r?\n/);
  const methods = [];
  let region = null;
  let cur = null;
  lines.forEach((l, i) => {
    const rm = l.match(/#region\s+(.*)/);
    if (rm) { region = rm[1].trim(); return; }
    if (/#endregion/.test(l)) { region = null; return; }
    const mm = l.match(/^\s{4,8}(private|public|internal|protected|static)[\s\w<>?\[\],]*\s+(\w+)\s*\(/);
    if (mm) {
      cur = { name: mm[2], line: i + 1, region, start: i };
      methods.push(cur);
    }
  });
  // 计算每个方法长度
  methods.forEach((m, idx) => {
    const next = methods[idx + 1];
    const endIdx = idx === methods.length - 1 ? -1 : next.start;
    // 简单用下一个方法起点作为终点
    m.len = endIdx > 0 ? endIdx - m.start : lines.length - m.start;
  });
  console.log(`\n=== ${t}  共 ${lines.length} 行 / ${methods.length} 个方法 ===`);
  const big = methods.filter((m) => m.len > 60).sort((a, b) => b.len - a.len);
  console.log(`  -- 长度 >60 行的胖方法 ${big.length} 个 --`);
  for (const m of big.slice(0, 12)) {
    console.log(`   ${String(m.len).padStart(4)} 行  L${String(m.line).padStart(5)}  ${m.name}()   [${m.region || "-"}]`);
  }
  // 找重复：同名 / 高相似的方法体
  const byName = {};
  for (const m of methods) byName[m.name] = (byName[m.name] || 0) + 1;
  const dup = Object.entries(byName).filter(([, n]) => n > 1);
  if (dup.length) console.log("  -- 同名重载 --", dup.map(([n, c]) => `${n}x${c}`).join(", "));
}
