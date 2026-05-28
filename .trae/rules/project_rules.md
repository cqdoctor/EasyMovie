# Project Rules

## Build & Run
- 每次代码修改后，必须编译并运行验证效果
- 编译命令: `dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false`
- 运行命令: `dotnet run --project EasyMovie.Client/EasyMovie.Client.csproj`
- 编译前先终止旧进程: `taskkill /F /IM EasyMovie.Client.exe`
- 如果编译失败出现 csc.exe 崩溃(退出码 268435659)，清理 obj 目录后重试: `Remove-Item -Recurse -Force EasyMovie.Client\obj; dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false`
