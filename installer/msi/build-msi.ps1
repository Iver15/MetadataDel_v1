param(
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\..\publish'),
    [string]$Version = '2.2.1'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $PublishDir 'MetadataDel.exe'))) {
    throw "Не найден MetadataDel.exe в '$PublishDir'. Сначала выполните dotnet publish."
}
$resolvedPublishDir = (Resolve-Path $PublishDir).Path
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw 'Нужен WiX Toolset 5 или новее: dotnet tool install --global wix --version 5.0.2'
}
wix extension add -g WixToolset.Util.wixext/5.0.2
if ($LASTEXITCODE -ne 0) { throw "Не удалось подключить WixToolset.Util.wixext (код $LASTEXITCODE)" }
$msiPath = Join-Path $PSScriptRoot "MetadataDel-$Version.msi"
wix build (Join-Path $PSScriptRoot 'MetadataDel.wxs') `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -d "ProductVersion=$Version" `
    -d "PublishDir=$resolvedPublishDir" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "WiX завершился с кодом $LASTEXITCODE" }
Write-Host 'MSI создан:' $msiPath
