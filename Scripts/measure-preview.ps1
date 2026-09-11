# 测量预览延迟：冷启动应用后连续预览若干文件，输出每次「请求 -> 内容就绪」的耗时。
#
# 用法：pwsh -NoProfile -File .\Scripts\measure-preview.ps1
#       pwsh -NoProfile -File .\Scripts\measure-preview.ps1 -Files test.png,test.md
#
# 说明：请求与 shell 的打开方式一致（再启动一个 QuickLook-Next.exe 把路径转发给
# 常驻实例），所以数值里包含进程启动与消息转发的开销。

param(
    [string[]]$Files = @('test.png', 'test.md', 'test.pptx', 'test.xlsx'),
    [int]$StartupWaitMs = 1500,
    [int]$TimeoutMs = 20000
)

$ErrorActionPreference = 'Stop'
# -File 模式下 "a,b" 会作为单个字符串传进来，这里拆开。
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$timing = Join-Path $smoke 'timing.txt'

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item -LiteralPath $timing -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing', '/test-no-focusmonitor'
Start-Sleep -Milliseconds $StartupWaitMs

Write-Host ("{0,-18} {1}" -f '文件', '请求->内容就绪')
foreach ($file in $Files) {
    $path = Join-Path $smoke $file
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ("{0,-18} 跳过（文件不存在）" -f $file)
        continue
    }

    $before = if (Test-Path -LiteralPath $timing) { @(Get-Content -LiteralPath $timing).Count } else { 0 }
    $t0 = [DateTime]::UtcNow
    Start-Process -FilePath $exe -ArgumentList $path | Out-Null

    $stamp = $null
    $budget = [Diagnostics.Stopwatch]::StartNew()
    while ($budget.ElapsedMilliseconds -lt $TimeoutMs) {
        Start-Sleep -Milliseconds 20
        if (-not (Test-Path -LiteralPath $timing)) { continue }

        $lines = @(Get-Content -LiteralPath $timing)
        if ($lines.Count -gt $before) {
            $stamp = [DateTime]::Parse($lines[-1].Split('|')[0], $null,
                [Globalization.DateTimeStyles]::RoundtripKind)
            break
        }
    }

    if ($stamp) {
        $ms = [math]::Round(($stamp - $t0).TotalMilliseconds)
        Write-Host ("{0,-18} {1,5} ms" -f $file, $ms)
    }
    else {
        Write-Host ("{0,-18} 超时" -f $file)
    }

    Start-Sleep -Milliseconds 400
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
