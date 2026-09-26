param(
    [string]$Version = "1.1.1.0",
    [string]$PublisherCN = "CN=FAFCE538-611A-4FA6-9046-8E37F31B8034",
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\bin\publish\win-x64"),
    [string]$OutDir = (Join-Path $PSScriptRoot "Output")
)

# ShoreHue MSIX 打包脚本：
#   1. 暂存 AppxManifest + ShoreHue.exe + 磁贴素材
#   2. MakeAppx 打包为 .msix
#   3. 自签名证书（CN 与 Publisher 一致）签名；商店审核时会由微软重新签名
#
# 前置：先执行 dotnet publish -c Release -p:PublishProfile=win-x64

$ErrorActionPreference = "Stop"

# ---------- 1) 定位 Windows SDK 工具 ----------
$kitsRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeAppx = $null
$signtool = $null
if (Test-Path $kitsRoot) {
    foreach ($v in (Get-ChildItem $kitsRoot -Directory | Sort-Object Name -Descending)) {
        if (-not $makeAppx) {
            $ma = Join-Path $v.FullName "x64\makeappx.exe"
            if (Test-Path $ma) { $makeAppx = $ma }
        }
        if (-not $signtool) {
            $st = Join-Path $v.FullName "x64\signtool.exe"
            if (Test-Path $st) { $signtool = $st }
        }
    }
}
if (-not $makeAppx) { throw "未找到 makeappx.exe（需要 Windows SDK）" }
if (-not $signtool) { throw "未找到 signtool.exe（需要 Windows SDK）" }
Write-Output "MakeAppx: $makeAppx"
Write-Output "SignTool: $signtool"

# ---------- 2) 暂存目录 ----------
$staging = Join-Path $env:TEMP "ShoreHue-msix-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging "Assets") | Out-Null

Copy-Item (Join-Path $PSScriptRoot "AppxManifest.xml") $staging -Force
$exe = Join-Path $PublishDir "ShoreHue.exe"
if (-not (Test-Path $exe)) { throw "未找到发布产物: $exe" }
Copy-Item $exe $staging -Force
Get-ChildItem (Join-Path $PSScriptRoot "Assets") -Filter *.png | Copy-Item -Destination (Join-Path $staging "Assets") -Force

# ---------- 3) 写入版本号 ----------
$manifest = Join-Path $staging "AppxManifest.xml"
$xmlText = [System.IO.File]::ReadAllText($manifest)
# 只替换 <Identity> 元素里的 Version，避免误伤 TargetDeviceFamily 的 MinVersion
$xmlText = [System.Text.RegularExpressions.Regex]::Replace($xmlText, '(?<=<Identity[^>]*Version=")[0-9.]+(?=")', $Version)
[System.IO.File]::WriteAllText($manifest, $xmlText, (New-Object System.Text.UTF8Encoding($false)))

# ---------- 4) MakeAppx 打包 ----------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outMsix = Join-Path $OutDir "ShoreHue-$Version-x64.msix"
Remove-Item $outMsix -ErrorAction SilentlyContinue
& $makeAppx pack /d $staging /p $outMsix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败 (exit=$LASTEXITCODE)" }

# ---------- 5) 签名 ----------
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $PublisherCN -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $cert) {
    Write-Output "创建自签名代码签名证书: $PublisherCN"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject $PublisherCN `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(3)
}
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $outMsix
if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败 (exit=$LASTEXITCODE)" }

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "MSIX 已生成: $outMsix"
Write-Output "SHA256: $((Get-FileHash $outMsix -Algorithm SHA256).Hash.ToLowerInvariant())"
