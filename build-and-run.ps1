# 一键清理+编译+运行脚本
param(
    [switch]$NoRun,
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"

# 终止旧进程
Write-Host ">> 终止旧进程..." -ForegroundColor Cyan
taskkill /F /IM EasyMovie.Client.exe 2>$null

if (-not $NoClean) {
    Write-Host ">> 清理 obj/bin..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force EasyMovie.Client\obj -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force EasyMovie.Client\bin -ErrorAction SilentlyContinue
}

# 编译
Write-Host ">> 编译中..." -ForegroundColor Cyan
$result = dotnet build EasyMovie.Client/EasyMovie.Client.csproj -p:BuildInParallel=false 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败!" -ForegroundColor Red
    Write-Host $result
    exit $LASTEXITCODE
}
Write-Host "编译成功!" -ForegroundColor Green

# 运行
if (-not $NoRun) {
    Write-Host ">> 启动程序..." -ForegroundColor Cyan
    dotnet run --project EasyMovie.Client/EasyMovie.Client.csproj
}