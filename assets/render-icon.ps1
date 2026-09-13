$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$dir  = "E:\dshwork\winquad\assets"
$out  = "$dir\png"
$prof = "$dir\_edgeprofile"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null
Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue

function Render-Master([int]$px) {
    $page = "$dir\_master$px.html"
    $html = @"
<!doctype html><html><head><meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}
img{display:block;width:${px}px;height:${px}px}</style></head>
<body><img src="icon.svg"></body></html>
"@
    [System.IO.File]::WriteAllText($page, $html, (New-Object System.Text.UTF8Encoding($false)))
    $png = "$out\_master$px.png"
    Remove-Item $png -Force -ErrorAction SilentlyContinue
    $url = "file:///" + ($page -replace '\\','/')
    & $edge --headless=new --disable-gpu --no-sandbox --hide-scrollbars `
            --user-data-dir="$prof" --force-device-scale-factor=1 `
            --default-background-color=00000000 `
            --window-size="$px,$px" --screenshot="$png" $url 2>&1 | Out-Null
    for ($i = 0; $i -lt 60 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 150 }
    Start-Sleep -Milliseconds 200
    Remove-Item $page -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $png)) { throw "渲染 $px 失败" }
    $img = [System.Drawing.Image]::FromFile($png)
    $ok = ($img.Width -eq $px -and $img.Height -eq $px)
    $img.Dispose()
    if (-not $ok) { throw "渲染 $px 尺寸不对" }
    return $png
}

function Resize-Png($srcPath, [int]$to, $dstPath) {
    $src = [System.Drawing.Image]::FromFile($srcPath)
    $bmp = New-Object System.Drawing.Bitmap($to, $to, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $to, $to)))
    $g.Dispose()
    $bmp.Save($dstPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $src.Dispose()
}

Write-Host "渲染母版…"
$m256 = Render-Master 256
$m512 = Render-Master 512
Write-Host "  256 母版: $((Get-Item $m256).Length) 字节"
Write-Host "  512 母版: $((Get-Item $m512).Length) 字节"

# 小尺寸从 256 母版降，大尺寸从 512 母版降，降幅不超过 4 倍，避免糊掉
$plan = @(
    @(16,  $m256), @(20, $m256), @(24, $m256), @(32, $m256), @(40, $m256), @(48, $m256), @(64, $m256),
    @(128, $m512), @(256, $m512)
)
foreach ($p in $plan) {
    Resize-Png $p[1] $p[0] "$out\icon-$($p[0]).png"
}
Remove-Item "$out\_master*.png" -Force
Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n=== 成品 ==="
Get-ChildItem $out -Filter *.png | Sort-Object { [int]($_.BaseName -replace 'icon-','') } | ForEach-Object {
    $img = [System.Drawing.Image]::FromFile($_.FullName)
    "  {0,-14} {1,4}x{2,-4} {3,8:N0} 字节" -f $_.Name, $img.Width, $img.Height, $_.Length
    $img.Dispose()
}
