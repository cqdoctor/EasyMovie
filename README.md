# 🎬 EasyMovie — 轻松管理你的电影库

一款 Windows 桌面电影收藏管理应用，WPF + Material Design + SQLite，简洁易用。

[English](#-easymovie--manage-your-movie-collection-with-ease) | 中文

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![WPF](https://img.shields.io/badge/WPF-Material_Design-68217A)
![SQLite](https://img.shields.io/badge/SQLite-EF_Core_9-003B57?logo=sqlite)
![License](https://img.shields.io/badge/License-MIT-green)

## ✨ 功能特性

### 🎬 电影库管理
- **多视图模式** — 表格 / 卡片 / 海报墙 / 合集，四种视图自由切换
- **搜索筛选** — 全文搜索 + 高级筛选（分类/年份/评分/状态/收藏/标签 AND/OR 组合）
- **拼音搜索** — 支持中文拼音全拼、首字母快速检索
- **批量编辑** — 多选电影 + 批量修改分类/状态/评分/收藏
- **排序分页** — 多字段排序 + 分页浏览

### 📁 分类与标签
- **分类管理** — 多级分类树，无限嵌套，按国家/地区自动归类
- **标签系统** — 自定义颜色标签，多对多关联
- **自动分类** — 获取信息后自动根据国家分配分类，分类变更时自动更新

### 🎞️ 电影合集
- **合集管理** — 创建/编辑/删除合集，如"漫威宇宙"、"哈利波特系列"
- **海报展示** — 合集内电影以海报墙形式展示，支持播放和详情查看
- **灵活管理** — 添加/移除电影，搜索支持拼音首字母

### ⭐ 评分与记录
- **评分系统** — 1-10 评分 + 观看状态 + 笔记 + 收藏
- **重复检测** — 自动检测重复电影，一键清理

### 📊 统计面板
- **多种图表** — 饼图/柱状图/折线图等多种图表
- **数据概览** — 总数/评分分布/年份分布/分类占比

### 📦 导入导出
- **多格式** — CSV/JSON 导入导出 + 全量备份还原
- **文件夹导入** — 自动扫描本地电影文件并匹配信息

### 🌐 多源搜索
- **在线搜索** — 豆瓣 / TMDB / 1905 / 猫眼，一键获取电影信息
- **封面下载** — 自动下载高清海报
- **信息补全** — 标题/导演/演员/年份/国家/简介等一键填充

### ⌨️ 键盘快捷键
- **可配置** — 设置页自定义快捷键，点击录制，冲突检测
- **持久化** — 自定义快捷键保存到本地，重启不丢失
- **默认快捷键** — Ctrl+F 搜索 / Ctrl+N 添加 / Delete 删除 / F5 刷新 / F3 切换视图 等

### 🎨 界面与体验
- **主题切换** — 浅色/深色/随系统自动切换
- **国际化** — 中文/英文界面切换
- **性能优化** — 页面预加载 + 批量数据库操作 + 虚拟化列表

## 🛠️ 技术栈

| 层 | 技术 |
|---|---|
| UI | WPF + MaterialDesignInXamlToolkit 5.x |
| MVVM | CommunityToolkit.Mvvm |
| 图表 | LiveCharts2 (SkiaSharp) |
| 数据库 | SQLite + EF Core 9 |
| CSV | CsvHelper |
| 日志 | Serilog |
| 测试 | xUnit + Moq + FluentAssertions |

## 📁 项目结构

```
EasyMovie/
├── EasyMovie.Client/     # WPF 桌面客户端
│   ├── Views/            # 视图（电影库/分类标签/统计/设置/合集/快捷键等）
│   ├── Strings/          # 多语言资源（中/英）
│   ├── Converters/       # 值转换器
│   └── App.xaml          # 主题 & 全局配置
├── EasyMovie.Core/       # 核心业务层
│   ├── Models/           # Movie, Category, Tag, MovieCollection
│   ├── Interfaces/       # 服务接口
│   └── Services/         # 业务逻辑
├── EasyMovie.Data/       # 数据访问层
│   ├── Repositories/     # 仓储实现
│   ├── Configurations/   # Fluent API 配置
│   └── CollectionService # 合集服务
├── EasyMovie.Tools/      # 工具 & API
│   ├── ImportExport/     # 导入导出服务
│   └── MovieApi/         # 豆瓣/TMDB/1905/猫眼客户端
└── EasyMovie.Tests/      # 单元测试
```

## 🚀 构建与运行

```bash
# 还原依赖
dotnet restore EasyMovie.sln

# 构建（推荐关闭并行编译避免 csc 崩溃）
dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false

# 运行
dotnet run --project EasyMovie.Client/EasyMovie.Client.csproj
```

### 环境要求

- .NET 10 SDK
- Windows 10/11

## 📋 开发进度

| Phase | 内容 | 状态 |
|---|---|---|
| 1 | 项目骨架 + 数据层 | ✅ |
| 2 | 电影库 UI + 搜索筛选 | ✅ |
| 3 | 分类/标签 + 评分收藏 | ✅ |
| 4 | 统计面板多图表 | ✅ |
| 5 | 导入导出 + 备份还原 | ✅ |
| 6 | 多源 API 集成 | ✅ |
| 7 | 主题/设置/日志/打包 | ✅ |
| 8 | 项目重命名 EasyMovie | ✅ |
| 9 | 国际化（中/英） | ✅ |
| 10 | 高级筛选 + 保存筛选条件 | ✅ |
| 11 | 批量编辑 + 重复检测 | ✅ |
| 12 | 可配置键盘快捷键 | ✅ |
| 13 | 电影合集管理 | ✅ |
| 14 | 观影日记 / 愿望单 | 🔜 |

## 📄 License

MIT License

---

# 🎬 EasyMovie — Manage Your Movie Collection with Ease

A Windows desktop movie collection manager built with WPF + Material Design + SQLite. Simple and intuitive.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![WPF](https://img.shields.io/badge/WPF-Material_Design-68217A)
![SQLite](https://img.shields.io/badge/SQLite-EF_Core_9-003B57?logo=sqlite)
![License](https://img.shields.io/badge/License-MIT-green)

## ✨ Features

### 🎬 Movie Library
- **Multi-View** — Table / Card / Poster Wall / Collections, four view modes
- **Search & Filter** — Full-text search + Advanced filter (Category/Year/Rating/Status/Favorite/Tags AND/OR)
- **Pinyin Search** — Quick search by Chinese pinyin full spelling or initials
- **Batch Edit** — Multi-select + batch modify category/status/rating/favorite
- **Sort & Paginate** — Multi-field sorting + paginated browsing

### 📁 Categories & Tags
- **Categories** — Multi-level category tree, unlimited nesting, auto-categorize by country
- **Tags** — Custom color tags with many-to-many association
- **Auto-Categorize** — Auto-assign category based on country when fetching info

### 🎞️ Movie Collections
- **Collection Management** — Create/Edit/Delete collections, e.g. "Marvel Universe", "Harry Potter Series"
- **Poster Display** — Movies in collection shown as poster wall with play & detail buttons
- **Flexible Management** — Add/Remove movies, search with pinyin support

### ⭐ Ratings & Records
- **Rating System** — 1-10 rating + watch status + notes + favorites
- **Duplicate Detection** — Auto-detect duplicate movies, one-click cleanup

### 📊 Statistics
- **Multiple Charts** — Pie/Bar/Line charts and more
- **Data Overview** — Total count/Rating distribution/Year distribution/Category breakdown

### 📦 Import & Export
- **Multi-Format** — CSV/JSON import/export + full backup & restore
- **Folder Import** — Auto-scan local movie files and match info

### 🌐 Multi-Source Search
- **Online Search** — Douban / TMDB / 1905 / Maoyan, one-click movie info
- **Cover Download** — Auto-download HD posters
- **Info Completion** — Title/Director/Actors/Year/Country/Synopsis auto-fill

### ⌨️ Keyboard Shortcuts
- **Configurable** — Customize shortcuts in Settings, click-to-record, conflict detection
- **Persistent** — Custom shortcuts saved locally, survive restarts
- **Defaults** — Ctrl+F Search / Ctrl+N Add / Delete Remove / F5 Refresh / F3 Cycle View, etc.

### 🎨 UI & Experience
- **Theme** — Light/Dark/Follow system
- **i18n** — Chinese/English interface switching
- **Performance** — Page pre-loading + batch DB operations + virtualized lists

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| UI | WPF + MaterialDesignInXamlToolkit 5.x |
| MVVM | CommunityToolkit.Mvvm |
| Charts | LiveCharts2 (SkiaSharp) |
| Database | SQLite + EF Core 9 |
| CSV | CsvHelper |
| Logging | Serilog |
| Testing | xUnit + Moq + FluentAssertions |

## 📁 Project Structure

```
EasyMovie/
├── EasyMovie.Client/     # WPF desktop client
│   ├── Views/            # Views (Library/Categories/Stats/Settings/Collections/Shortcuts)
│   ├── Strings/          # i18n resources (zh-CN/en-US)
│   ├── Converters/       # Value converters
│   └── App.xaml          # Theme & global config
├── EasyMovie.Core/       # Core business layer
│   ├── Models/           # Movie, Category, Tag, MovieCollection
│   ├── Interfaces/       # Service interfaces
│   └── Services/         # Business logic
├── EasyMovie.Data/       # Data access layer
│   ├── Repositories/     # Repository implementations
│   ├── Configurations/   # Fluent API configs
│   └── CollectionService # Collection service
├── EasyMovie.Tools/      # Tools & APIs
│   ├── ImportExport/     # Import/export services
│   └── MovieApi/         # Douban/TMDB/1905/Maoyan clients
└── EasyMovie.Tests/      # Unit tests
```

## 🚀 Build & Run

```bash
# Restore dependencies
dotnet restore EasyMovie.sln

# Build (recommended to disable parallel build to avoid csc crash)
dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false

# Run
dotnet run --project EasyMovie.Client/EasyMovie.Client.csproj
```

### Requirements

- .NET 10 SDK
- Windows 10/11

## 📋 Development Progress

| Phase | Content | Status |
|---|---|---|
| 1 | Project skeleton + Data layer | ✅ |
| 2 | Movie library UI + Search & filter | ✅ |
| 3 | Categories/Tags + Rating & favorites | ✅ |
| 4 | Statistics panel with multiple charts | ✅ |
| 5 | Import/Export + Backup & restore | ✅ |
| 6 | Multi-source API integration | ✅ |
| 7 | Theme/Settings/Logging/Packaging | ✅ |
| 8 | Project rename to EasyMovie | ✅ |
| 9 | i18n (Chinese/English) | ✅ |
| 10 | Advanced filter + Save filter presets | ✅ |
| 11 | Batch edit + Duplicate detection | ✅ |
| 12 | Configurable keyboard shortcuts | ✅ |
| 13 | Movie collection management | ✅ |
| 14 | Watch diary / Wishlist | 🔜 |

## 📄 License

MIT License
