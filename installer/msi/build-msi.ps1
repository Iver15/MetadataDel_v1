param(
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\publish'),
    [string]$Version = '1.0.0'
)

if (-not (Test-Path $PublishDir)) {
    Write-Error "Не найден каталог публикации '$PublishDir'. Сначала выполните 'dotnet publish'."
    exit 1
}

$resolvedPublishDir = (Resolve-Path $PublishDir).Path
$wixCli = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCli) {
    Write-Error "Команда 'wix' не найдена. Установите WiX Toolset 4 CLI: 'dotnet tool install --global wix'."
    exit 1
}

$harvestFile = Join-Path $PSScriptRoot 'PublishFiles.wxs'

wix harvest dir $resolvedPublishDir `
    -var PublishDir `
    -cg PublishFiles `
    -dr INSTALLFOLDER `
    -o $harvestFile `
    --bindpath $resolvedPublishDir

$msiName = "MetadataDel-$Version.msi"
$msiPath = Join-Path $PSScriptRoot $msiName

wix build `
    (Join-Path $PSScriptRoot 'MetadataDel.wxs') `
    $harvestFile `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -dProductVersion=$Version `
    -dPublishDir=$resolvedPublishDir `
    -o $msiPath

Write-Host "MSI создан:" $msiPath
