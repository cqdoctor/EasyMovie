# Project Rules

## Build & Run
- 每次代码修改后，必须编译并运行验证效果
- 编译命令: `dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false`
- 运行命令: `dotnet run --project EasyMovie.Client/EasyMovie.Client.csproj`
- 编译前先终止旧进程: `taskkill /F /IM EasyMovie.Client.exe`
- 如果编译失败出现 csc.exe 崩溃(退出码 268435659)，清理 obj 目录后重试: `Remove-Item -Recurse -Force EasyMovie.Client\obj; dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false`
- **一键脚本**: `.\build-and-run.ps1` 自动终止旧进程、清理、编译、运行（`-NoRun` 只编译不运行，`-NoClean` 跳过清理）

## 窗口图标控制
- **任务栏图标**: 由 `csproj` 中的 `<ApplicationIcon>app.ico</ApplicationIcon>` 控制，这是 EXE 级别的图标，不影响标题栏
- **标题栏图标**: XAML 中 `Icon="pack://application:,,,/transparent.ico"`（透明 1x1 像素），标题栏图标不可见
- **对话框窗口**: 使用 `WS_EX_DLGMODALFRAME` + `SetWindowPos(SWP_FRAMECHANGED)` 去掉标题栏图标（参考 `SettingsView.xaml.cs` 中的 `RemoveIcon` 方法）
- **原理**: WPF 的 `Window.Icon` 只控制标题栏图标；`ApplicationIcon` 独立控制任务栏/EXE 图标，两者互不影响
- **注意**: 不要使用 `<UseWindowsForms>true</UseWindowsForms>`，会导致 `System.Windows.Forms` 和 `System.Windows` 命名空间冲突（大量 CS0104 错误）
- **相关文件**: `MainWindow.xaml`, `EasyMovie.Client.csproj`, `SettingsView.xaml.cs`（`RemoveIcon` 方法）
