# EasyMovie 项目规则

## 构建与运行

- 编译: `dotnet build EasyMovie.Client\EasyMovie.Client.csproj`
- 运行: `dotnet run --project EasyMovie.Client\EasyMovie.Client.csproj`
- 仅编译主项目（跳过测试项目）: `dotnet build EasyMovie.Client\EasyMovie.Client.csproj`
- 全量编译: `dotnet build EasyMovie.sln`（测试项目可能有旧代码错误，优先用主项目编译）

## 项目结构

- `EasyMovie.Core` - 核心模型、接口、服务（不依赖 EF Core）
- `EasyMovie.Data` - 数据层（EF Core、仓储实现）
- `EasyMovie.Tools` - 工具层（API客户端、导入导出）
- `EasyMovie.Client` - WPF 客户端（UI、ViewModel）

## 代码规范

- 多语言资源: 修改 `EasyMovie.Client/Strings/Strings.zh-CN.xaml` 和 `Strings.en-US.xaml`
- 新增服务: Core 层定义接口，Data 层实现仓储，Client 层调用
- Core 层不引用 EF Core，数据库操作放在 Data 或 Client 层
- 提交信息格式: `feat: / fix: / refactor: 描述`
