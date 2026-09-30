<#
    AyuTranslate 构建脚本（PowerShell）

    用法：
        pwsh -File build.ps1                 # 编译 + 发布单文件 exe 到 dist\
        pwsh -File build.ps1 -SelfTest       # 还要跑一次自检
        pwsh -File build.ps1 -FrameworkDependent:$false   # 自带运行时（体积大但不依赖 .NET）
        pwsh -File build.ps1 -Zip            # 额外打包 zip
#>
[CmdletBinding()]
param(
    [switch] $SelfTest,
    [switch] $Zip,
    [switch] $FrameworkDependent = $true,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\AyuTranslate.csproj'
$out  = Join-Path $root 'dist'

function Write-Step($n, $text) {
    Write-Host ''
    Write-Host "[$n] $text" -ForegroundColor Cyan
}

Write-Host '============================================' -ForegroundColor DarkCyan
Write-Host '  AyuTranslate 构建' -ForegroundColor White
Write-Host '============================================' -ForegroundColor DarkCyan

# ---------- 环境检查 ----------
Write-Step '1/4' '检查 .NET SDK'
try {
    $ver = & dotnet --version 2>&1
    Write-Host "    dotnet $ver"
} catch {
    throw '找不到 dotnet。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download'
}

if (-not (Test-Path $proj)) { throw "找不到项目文件：$proj" }

# ---------- 编译 ----------
Write-Step '2/4' "编译（$Configuration / win-x64）"
& dotnet build $proj -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }

# ---------- 发布 ----------
Write-Step '3/4' '发布到 dist\'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$publishArgs = @(
    'publish', $proj,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--nologo',
    '-o', $out,
    '-v', 'minimal',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none'
)

if ($FrameworkDependent) {
    # 依赖已安装的 .NET 8 运行时，体积小；单文件模式下不能开启压缩
    $publishArgs += '--self-contained:false'
} else {
    # 自带运行时，无需安装 .NET；可以开启压缩
    $publishArgs += '--self-contained:true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
    $publishArgs += '-p:PublishTrimmed=false'
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

$exe = Join-Path $out 'AyuTranslate.exe'
if (-not (Test-Path $exe)) { throw "没有生成 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host ''
Write-Host "    生成：$exe ($size MB)" -ForegroundColor Green

# ---------- 自检 ----------
Write-Step '4/4' '验证'
if ($SelfTest) {
    Write-Host '    运行自检（需要 AyuGram 正在运行）…'
    & $exe --selftest --dump
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "自检返回 $LASTEXITCODE，请查看上面的输出。"
    }
} else {
    Write-Host '    跳过自检（加 -SelfTest 参数可启用）'
}

if ($Zip) {
    $zipPath = Join-Path $root 'AyuTranslate-win-x64.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath
    Write-Host "    打包：$zipPath" -ForegroundColor Green
}

Write-Host ''
Write-Host '============================================' -ForegroundColor DarkCyan
Write-Host '  完成' -ForegroundColor Green
Write-Host '============================================' -ForegroundColor DarkCyan
Write-Host ''
Write-Host "运行： $exe"
Write-Host "自检： $exe --selftest --dump"
Write-Host ''
