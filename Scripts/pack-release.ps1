# 生成用户友好的发布包：Build\Release -> Build\Package -> Build\QuickLook-Next-<version>.zip
#
# 目录结构（v3.2.0 起）：
#   根目录：QuickLook-Next.exe（用户双击它）、QuickLook-Next.dll、
#           QuickLook-Next.deps.json、QuickLook-Next.runtimeconfig.json、
#           Translations.config、QLPlugin.ico、portable.lock
#   lib\：  其余所有运行库 DLL（第三方依赖 + QuickLook.Common）
#   runtimes\：原生运行库
#   QuickLook.Plugin\：内置插件
# 不再把十几个 dll / config 文件与 exe 混在根目录。
#
# 用法：
#   .\Scripts\pack-release.ps1             # 只整理到 Build\Package
#   .\Scripts\pack-release.ps1 -MakeZip    # 整理并生成 zip

param(
    [switch]$MakeZip,
    # v3.32.0: 一个发布包只带一种架构的原生运行库（默认 x64）。
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'Build\Release'
$package = Join-Path $root 'Build\Package'

if (-not (Test-Path $release)) {
    throw "未找到构建产物：$release（请先执行 .\build.ps1）"
}

$version = & git -C $root describe --always --tags --exclude latest 2>$null
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = '0.0.0'
}

# 重建 Package 目录
if (Test-Path $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
New-Item -ItemType Directory -Path $package | Out-Null

# 根目录只放程序入口和它必需的清单/配置
foreach ($name in @('QuickLook-Next.exe', 'QuickLook-Next.dll',
        'QuickLook-Next.deps.json', 'QuickLook-Next.runtimeconfig.json',
        'Translations.config', 'QLPlugin.ico')) {
    $src = Join-Path $release $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $package -Force
    }
}

# 其余所有托管 DLL 收进 lib\ 子目录（程序启动时的 AssemblyResolve 兜底会
# 递归搜索整个程序目录，lib 里的程序集可以正常加载）。主程序 QuickLook-Next.dll
# 必须留在根目录（apphost 靠它启动）。
$lib = Join-Path $package 'lib'
New-Item -ItemType Directory -Path $lib | Out-Null
Get-ChildItem -LiteralPath $release -Filter *.dll -File |
    Where-Object { $_.Name -ne 'QuickLook-Next.dll' } |
    Copy-Item -Destination $lib -Force

# 原生运行库与内置插件保持子目录
if (Test-Path -LiteralPath (Join-Path $release 'runtimes')) {
    Copy-Item -LiteralPath (Join-Path $release 'runtimes') `
        -Destination $package -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $release 'QuickLook.Plugin') `
    -Destination (Join-Path $package 'QuickLook.Plugin') -Recurse -Force

# v3.3.0: 把多个插件各自携带的共享依赖去重到 lib\ 一份（程序启动时的
# AssemblyResolve 兜底会从 lib\ 加载托管程序集；WebView2Loader.dll 是原生
# 加载器，一并收进 lib\ 后由 lib 里的 WebView2 托管程序集解析）。只处理
# 纯托管或与托管程序集成对的原生加载器；带独立原生库的（MediaInfo、
# SQLitePCLRaw、freetype 等）保持原位。只有字节完全一致的副本才会被移除。
$dedupeLibNames = @(
    'UtfUnknown.dll',
    'PureSharpCompress.dll',
    'ICSharpCode.SharpZipLib.dll',
    'System.ComponentModel.Composition.dll',
    'Microsoft.Bcl.HashCode.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll',
    # v3.32.0: 共享 WebView2 宿主库（Html/Markdown/Office/CHM/Mail/Font/SVG 共用）
    'QuickLook.Shared.dll',
    # v3.32.0: 实测字节完全一致的其它重复项（MediaInfoViewer/VideoViewer 的
    # 托管 MediaInfo 程序集、DbViewer/OfficeViewer 的 MiniExcel 等）。
    # 只有哈希一致的副本才会被移除，因此列表里多写几个名字是安全的。
    'QuickLook.MediaInfo.dll',
    'MiniExcel.dll',
    'CommunityToolkit.HighPerformance.dll',
    'Google.Protobuf.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.core.dll',
    'LiteDB.dll',
    'ELFSharp.dll',
    'CsvHelper.dll',
    'MsgReader.dll'
)

# 确保去重清单里的每个文件在 lib\ 有基准副本（没有就从插件目录取一份）
foreach ($name in $dedupeLibNames) {
    $libCopy = Join-Path $lib $name
    if (Test-Path -LiteralPath $libCopy) {
        continue
    }
    $first = Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
        -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $first) {
        Copy-Item -LiteralPath $first.FullName -Destination $libCopy -Force
    }
}

$removedDedup = 0
foreach ($name in $dedupeLibNames) {
    $libCopy = Join-Path $lib $name
    if (-not (Test-Path -LiteralPath $libCopy)) {
        continue
    }
    $libHash = (Get-FileHash -LiteralPath $libCopy -Algorithm SHA256).Hash
    Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
        -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $libHash } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            $removedDedup++
        }
}
Write-Host "已去重共享依赖：移除 $removedDedup 个重复文件"

# v3.32.0: 同一个插件程序集只应存在一份。增量构建不会清理旧产物，历史上
# PDFViewer 目录里就残留过一份 QuickLook.Plugin.HtmlViewer.dll，运行时会触发
# “Assembly with same name is already loaded”。打包时把重复副本清掉，只保留
# 与程序集同名的那个插件目录里的副本（找不到就保留最新的那份）。
$pluginDllGroups = Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
    -Recurse -File -Filter 'QuickLook.Plugin.*.dll' -ErrorAction SilentlyContinue |
    Group-Object Name
$removedDuplicates = 0
foreach ($group in $pluginDllGroups) {
    if ($group.Count -le 1) {
        continue
    }

    $expectedFolder = [System.IO.Path]::GetFileNameWithoutExtension($group.Name)
    $keeper = $group.Group |
        Where-Object { (Split-Path (Split-Path $_.FullName -Parent) -Leaf) -eq $expectedFolder } |
        Select-Object -First 1
    if ($null -eq $keeper) {
        $keeper = $group.Group | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    }

    $group.Group | Where-Object { $_.FullName -ne $keeper.FullName } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removedDuplicates++
    }
}
if ($removedDuplicates -gt 0) {
    Write-Host "已移除重复的插件程序集副本：$removedDuplicates 个"
}

# 发布包不需要调试符号，也不需要 .NET Framework 时代的 App.config
Get-ChildItem -LiteralPath $package -Recurse -Filter *.pdb |
    Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $package 'QuickLook-Next.dll.config') `
    -ErrorAction SilentlyContinue

# v3.4.0: 运行时用不到的文件不进发布包：
# - *.xml 是 IntelliSense 文档（约 4MB）
# - 插件目录下的 *.deps.json 只对 dotnet 工具链有意义（插件走
#   Assembly.LoadFrom）；根目录的 QuickLook-Next.deps.json 是 apphost 必需的，
#   必须保留
# - *.dylib 是 macOS 原生库（Windows 包不需要）
Get-ChildItem -LiteralPath $package -Recurse -File |
    Where-Object {
        $_.Extension -in '.xml', '.dylib' -or
        ($_.Name -like '*.deps.json' -and $_.FullName -like '*\QuickLook.Plugin\*')
    } |
    Remove-Item -Force

# v3.4.0: VideoViewer 根目录偶尔残留的 MediaInfo.dll 冗余副本（插件实际从
# runtimes\win-x64\native\ 加载），只保留 runtimes 那份。
$videoRootMediaInfo = Join-Path $package 'QuickLook.Plugin\QuickLook.Plugin.VideoViewer\MediaInfo.dll'
$videoRuntimeMediaInfo = Join-Path $package `
    'QuickLook.Plugin\QuickLook.Plugin.VideoViewer\runtimes\win-x64\native\MediaInfo.dll'
if ((Test-Path -LiteralPath $videoRootMediaInfo) -and
    (Test-Path -LiteralPath $videoRuntimeMediaInfo)) {
    Remove-Item -LiteralPath $videoRootMediaInfo -Force
    Write-Host '已移除 VideoViewer 根目录冗余的 MediaInfo.dll'
}

# v3.10.0/v3.32.0: 发布包只保留目标架构的原生运行库。win-x86 永远不会被用到
# （本项目不产出 x86 版本），非目标架构的目录（ChmViewer / OfficeViewer 里的
# WebView2Loader 等）也一并移除，避免同一个包同时带上多份加载器。
$keepArch = if ($Architecture -eq 'arm64') { 'win-arm64' } else { 'win-x64' }
$dropArchDirs = @('win-x86', 'win-arm64', 'win-x64') | Where-Object { $_ -ne $keepArch }
$pluginRoot = Join-Path $package 'QuickLook.Plugin'
foreach ($archDir in $dropArchDirs) {
    Get-ChildItem -LiteralPath $pluginRoot -Recurse -Directory -Filter $archDir -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            Write-Host "已移除 $($_.FullName.Substring($package.Length + 1))"
        }
}

# 便携标记：设置数据目录跟随程序目录
Set-Content -LiteralPath (Join-Path $package 'portable.lock') `
    -Value 'This file makes QuickLook-Next portable.' -Encoding ASCII

# v3.20.0: 首次使用说明（尤其是 .NET 运行时依赖），随包一起分发
$firstRunNote = @'
QuickLook-Next 使用说明

1. 双击根目录的 QuickLook-Next.exe 即可使用。
2. 选中文件后按空格预览，Esc 关闭。
3. 需要 .NET 10 Desktop Runtime（Windows 10 / 11）。
   如果启动时提示缺少运行时，点击提示窗口中的下载按钮安装，然后重新打开。
   下载地址：https://dotnet.microsoft.com/download/dotnet/10.0
4. 便携模式：数据目录跟随本文件夹（UserData），可整体移动。
'@
Set-Content -LiteralPath (Join-Path $package '使用说明.txt') `
    -Value $firstRunNote -Encoding UTF8

# v3.32.0: 打包自检 + 体积报告。发布前先确认包里确实有启动必需的文件，
# 并让「这个包有多大、大头是什么」一眼可见（历史上出现过手工打包漏掉
# lib 目录、或把 pdb 打进去的情况）。
foreach ($required in @('QuickLook-Next.exe', 'QuickLook-Next.dll',
        'QuickLook-Next.deps.json', 'QuickLook-Next.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required))) {
        throw "发布包缺少必需文件：$required"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'lib\QuickLook.Common.dll'))) {
    throw '发布包缺少 lib\QuickLook.Common.dll'
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'QuickLook.Plugin'))) {
    throw '发布包缺少 QuickLook.Plugin'
}

$leftover = @(Get-ChildItem -LiteralPath $package -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' })
if ($leftover.Count -gt 0) {
    $leftover | Remove-Item -Force
    Write-Host "已清理打包后残留的调试/文档文件：$($leftover.Count) 个"
}

$packageFiles = Get-ChildItem -LiteralPath $package -Recurse -File
Write-Host ("发布包体积：{0} MB（{1} 个文件）" -f `
        [math]::Round((($packageFiles | Measure-Object Length -Sum).Sum / 1MB), 1), $packageFiles.Count)
Write-Host '体积前十：'
$packageFiles | Sort-Object Length -Descending | Select-Object -First 10 | ForEach-Object {
    Write-Host ("  {0,7:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName.Substring($package.Length + 1))
}

if (-not $MakeZip) {
    Write-Host "已整理到：$package"
    Write-Host "（加 -Zip 参数可生成压缩包）"
    exit 0
}

$zip = Join-Path $root "Build\QuickLook-Next-$version.zip"
Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

# 手动写 zip 并统一使用正斜杠分隔符，避免 Windows 下反斜杠路径导致
# 部分解压工具（macOS / Linux 等）把条目当成单文件名
$base = $package.TrimEnd('\')
$fileStream = [System.IO.File]::Open($zip, 'Create')
$archive = New-Object System.IO.Compression.ZipArchive($fileStream, 'Create')
try {
    Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, 'Optimal')
        $inputStream = [System.IO.File]::OpenRead($_.FullName)
        try {
            $entryStream = $entry.Open()
            try {
                $inputStream.CopyTo($entryStream)
            } finally {
                $entryStream.Dispose()
            }
        } finally {
            $inputStream.Dispose()
        }
    }
} finally {
    $archive.Dispose()
    $fileStream.Dispose()
}
Remove-Item -LiteralPath (Join-Path $package 'portable.lock')

Write-Host "已生成发布包：$zip"
Write-Host ("压缩包体积：{0} MB" -f [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1))
