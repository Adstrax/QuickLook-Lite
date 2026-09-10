# capture-screenshots.ps1 - launches the app, previews the smoke-test files and
# captures each window to docs\screenshots for the README.
#
# 用法: powershell -File Scripts\capture-screenshots.ps1
#       powershell -File Scripts\capture-screenshots.ps1 -OnlyPdf

param([switch]$OnlyPdf)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$outDir = Join-Path $root 'docs\screenshots'
$env:QL_SMOKE_DIR = $smoke
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not (Test-Path $exe)) { throw "未找到 $exe，请先构建 Release" }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class ShotHelper {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder t, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    public static string Find(uint pid, string match) {
        string result = "";
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
                if (sb.Length > 0 && sb.ToString().Contains(match)) {
                    RECT r; GetWindowRect(h, out r);
                    result = h.ToInt64() + "|" + r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top);
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Capture-Rect($left, $top, $w, $h, $outPath) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Capture-Preview($p, $file, $title, $outName) {
    & $exe (Join-Path $smoke $file) | Out-Null
    Start-Sleep -Seconds 7
    $info = [ShotHelper]::Find($p.Id, $title)
    if (-not $info) { Write-Host "SKIP: $outName (window not found)"; return }
    $parts = $info.Split('|')
    $w = [int]$parts[3]; $h = [int]$parts[4]
    if ($w -lt 40 -or $h -lt 40) { Write-Host "SKIP: $outName (too small $w x $h)"; return }
    $out = Join-Path $outDir "$outName.png"
    Capture-Rect ([int]$parts[1]) ([int]$parts[2]) $w $h $out
    Write-Host "saved $out"
}

# 1. 常驻实例
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process -FilePath $exe -ArgumentList '/autorun' -PassThru
Start-Sleep -Seconds 8

# 2. 各格式预览窗口
if (-not $OnlyPdf) {
    Capture-Preview $p 'test.png' 'test.png' 'preview-image'
    Capture-Preview $p 'test.md' 'test.md' 'preview-markdown'
    Capture-Preview $p 'test.xlsx' 'test.xlsx' 'preview-excel'
    Capture-Preview $p 'test.docx' 'test.docx' 'preview-word'
    Capture-Preview $p 'test.pptx' 'test.pptx' 'preview-powerpoint'
}
Capture-Preview $p 'test.pdf' 'test.pdf' 'preview-pdf'

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (-not $OnlyPdf) {
    # 3. 托盘菜单（全屏抓取，菜单出现在光标处）
    $p2 = Start-Process -FilePath $exe -ArgumentList '/autorun /test-tray-menu' -PassThru
    Start-Sleep -Seconds 4
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Capture-Rect $screen.Left $screen.Top $screen.Width $screen.Height (Join-Path $outDir 'tray-menu.png')
    Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    # 4. 插件管理面板
    $p3 = Start-Process -FilePath $exe -ArgumentList '/autorun /test-plugin-manager' -PassThru
    Start-Sleep -Seconds 6
    $info = [ShotHelper]::Find($p3.Id, 'Manage Plugins')
    if ($info) {
        $parts = $info.Split('|')
        Capture-Rect ([int]$parts[1]) ([int]$parts[2]) ([int]$parts[3]) ([int]$parts[4]) (Join-Path $outDir 'plugin-manager.png')
        Write-Host 'saved plugin-manager.png'
    }
    Stop-Process -Id $p3.Id -Force -ErrorAction SilentlyContinue
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'done'
