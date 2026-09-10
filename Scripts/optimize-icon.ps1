# optimize-icon.ps1 - 把 app.ico 的未压缩 BMP 帧无损重编码为 PNG 帧
# （Windows 10/11 支持 PNG 图标帧），体积可缩小数倍且像素完全一致。
#
# 用法: powershell -File Scripts\optimize-icon.ps1

param([string]$IcoPath = '')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($IcoPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $IcoPath = Join-Path $repoRoot 'QuickLookNext\Resources\app.ico'
}

$bytes = [System.IO.File]::ReadAllBytes($IcoPath)
$count = [BitConverter]::ToUInt16($bytes, 4)
$frames = @()

for ($n = 0; $n -lt $count; $n++) {
    $o = 6 + $n * 16
    $w = $bytes[$o]
    $h = $bytes[$o + 1]
    $size = [BitConverter]::ToInt32($bytes, $o + 8)
    $off = [BitConverter]::ToInt32($bytes, $o + 12)
    $data = $bytes[$off..($off + $size - 1)]

    # PNG frame - keep as-is
    if ($data[0] -eq 0x89 -and $data[1] -eq 0x50) {
        $frames += [pscustomobject]@{ W = $w; H = $h; Png = $data; Original = $data }
        continue
    }

    $biSize = [BitConverter]::ToInt32($data, 0)
    $width = [BitConverter]::ToInt32($data, 4)
    $heightField = [BitConverter]::ToInt32($data, 8)
    $bpp = [BitConverter]::ToUInt16($data, 14)
    $topDown = $heightField -lt 0
    $realHeight = [Math]::Abs($heightField)
    $includesMask = ($realHeight -gt 256 -and $w -eq 0) -or ($realHeight -eq $w * 2)
    if ($includesMask) { $realHeight = [int]($realHeight / 2) }

    if ($bpp -ne 32) {
        throw "Unsupported BPP $bpp in frame $n (only 32bpp BMP frames are supported)"
    }

    $stride = $width * 4
    $pixelStart = $biSize
    $bmp = New-Object System.Drawing.Bitmap($width, $realHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $realHeight)
    $bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        for ($y = 0; $y -lt $realHeight; $y++) {
            $srcY = if ($topDown) { $y } else { $realHeight - 1 - $y }
            $srcOff = $pixelStart + $srcY * $stride
            $dstOff = $bmpData.Scan0.ToInt64() + $y * $bmpData.Stride
            [System.Runtime.InteropServices.Marshal]::Copy($data, $srcOff,
                [IntPtr]$dstOff, $stride)
        }
    }
    finally {
        $bmp.UnlockBits($bmpData)
    }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()

    $frames += [pscustomobject]@{ W = $w; H = $h; Png = $png; Original = $data }
}

# Rebuild the ICO with PNG entries
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)       # reserved
$bw.Write([UInt16]1)       # type: icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $bw.Write([byte]$(if ($f.W -ge 256) { 0 } else { $f.W }))
    $bw.Write([byte]$(if ($f.H -ge 256) { 0 } else { $f.H }))
    $bw.Write([byte]0)     # colors
    $bw.Write([byte]0)     # reserved
    $bw.Write([UInt16]1)   # planes
    $bw.Write([UInt16]32)  # bpp
    $bw.Write([Int32]$f.Png.Length)
    $bw.Write([Int32]$offset)
    $offset += $f.Png.Length
}
foreach ($f in $frames) {
    $bw.Write($f.Png)
}
$bw.Flush()

$oldSize = $bytes.Length
$newBytes = $out.ToArray()
$out.Dispose()
[System.IO.File]::WriteAllBytes($IcoPath, $newBytes)

Write-Host ("app.ico: {0} KB -> {1} KB ({2} frames, PNG)" -f
    [math]::Round($oldSize / 1KB, 1),
    [math]::Round($newBytes.Length / 1KB, 1),
    $frames.Count)
