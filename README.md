# 🎬 MovieManager — 电影管理系统

Windows 桌面电影管理应用，WPF + Material Design + SQLite。

## 功能

- 🎬 **电影库** — 表格/卡片双视图，搜索/筛选/排序/分页
- 📁 **分类管理** — 多级分类树，无限嵌套
- 🏷️ **标签系统** — 自定义颜色标签，多对多关联
- ⭐ **评分记录** — 1-10 评分 + 观看状态 + 笔记 + 收藏
- 📊 **统计面板** — 4 种图表（饼图/柱状图/折线图）
- 📦 **导入导出** — CSV/JSON 导入导出 + 全量备份还原
- 🌐 **在线搜索** — 豆瓣/TMDB API 集成，一键添加

## 技术栈

| 层 | 技术 |
|---|---|
| UI | WPF + MaterialDesignInXamlToolkit |
| MVVM | CommunityToolkit.Mvvm |
| 图表 | LiveCharts2 |
| 数据库 | SQLite + EF Core 9 |
| CSV | CsvHelper |
| 日志 | Serilog |
| 测试 | xUnit + Moq + FluentAssertions |

## 项目结构

```
MovieManager/
├── MovieManager.Client/     # WPF 桌面客户端
│   ├── Views/               # 7 个视图
│   ├── Converters/          # 值转换器
│   └── App.xaml             # 主题 & 全局配置
├── MovieManager.Core/       # 核心业务
│   ├── Models/              # Movie, Category, Tag
│   ├── Interfaces/          # 6 个服务接口
│   └── Enums/               # WatchStatus
├── MovieManager.Data/       # 数据访问层
│   ├── Repositories/        # 3 个仓储
│   └── Configurations/      # Fluent API 配置
├── MovieManager.Tools/      # 工具 & API
│   ├── ImportExport/        # 导入导出服务
│   └── MovieApi/            # 豆瓣/TMDB 客户端
└── MovieManager.Tests/      # 201+ 单元测试
```

## 构建

```bash
# 使用 VS MSBuild（推荐，WPF XAML 编译）
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" MovieManager.slnx /t:Build /p:Configuration=Debug

# 运行测试
dotnet test MovieManager.Tests/MovieManager.Tests.csproj -c Debug
```

## 开发进度

| Phase | 内容 | 状态 |
|---|---|---|
| 1 | 项目骨架 + 数据层 | ✅ |
| 2 | 电影库 UI + 搜索筛选 | ✅ |
| 3 | 分类/标签 + 评分收藏 | ✅ |
| 4 | 统计面板 4 图表 | ✅ |
| 5 | 导入导出 + 备份还原 | ✅ |
| 6 | 豆瓣/TMDB API | ✅ |
| 7 | 主题/设置/日志/打包 | ✅ |
