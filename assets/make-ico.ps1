$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$dir = "E:\dshwork\winquad\assets"
$pngDir = "$dir\png"
$ico = "$dir\icon.ico"

# ICO 容器：Windows Vista 以后允许条目直接塞 PNG，不用转成 BMP。
# 这样多尺寸图标可以保持带 Alpha 的 PNG，体积也小。
$sizes = 16,20,24,32,40,48,64,128,256
$images = @()
foreach ($s in $sizes) {
    $p = "$pngDir\icon-$s.png"
    if (-not (Test-Path $p)) { throw "缺 $p" }
    $images += ,@{ Size = $s; Bytes = [System.IO.File]::ReadAllBytes($p) }
}

$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$images.Count)  # count

# ICONDIRENTRY × N
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
    $s = $im.Size
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # 宽，0 表示 256
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # 高
    $bw.Write([Byte]0)            # 调色板数
    $bw.Write([Byte]0)            # reserved
    $bw.Write([UInt16]1)          # planes
    $bw.Write([UInt16]32)         # 位深
    $bw.Write([UInt32]$im.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $im.Bytes.Length
}

# 数据区
foreach ($im in $images) { $bw.Write($im.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host ("生成 {0}  ({1:N0} 字节)" -f $ico, (Get-Item $ico).Length)

# ── 校验：把 ico 读回来，确认每个尺寸都在 ──
$ico2 = New-Object System.Drawing.Icon($ico)
Write-Host ("`n系统读回：默认尺寸 {0}x{1}" -f $ico2.Width, $ico2.Height)
$ico2.Dispose()

$raw = [System.IO.File]::ReadAllBytes($ico)
$n = [BitConverter]::ToUInt16($raw, 4)
Write-Host "容器里共 $n 个尺寸："
for ($i = 0; $i -lt $n; $i++) {
    $b = 6 + 16 * $i
    $w = $raw[$b]; if ($w -eq 0) { $w = 256 }
    $len = [BitConverter]::ToUInt32($raw, $b + 8)
    $off = [BitConverter]::ToUInt32($raw, $b + 12)
    $isPng = ($raw[$off] -eq 0x89 -and $raw[$off+1] -eq 0x50)
    "  {0,3}x{0,-3} {1,8:N0} 字节  {2}" -f $w, $len, $(if ($isPng) { 'PNG' } else { 'BMP' })
}
