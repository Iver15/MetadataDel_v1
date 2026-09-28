param(
	[string]$ExePath = "$PSScriptRoot\..\publish\MetadataDel.exe"
)
$ErrorActionPreference = "Stop"

if (Test-Path $ExePath) {
	$resolvedExe = (Resolve-Path $ExePath).Path
	& $resolvedExe --uninstall-shell
	exit $LASTEXITCODE
}

$exts = ".pdf", ".docx", ".doc", ".xlsx", ".xls"
foreach ($ext in $exts) {
	$base = "HKCU:Software\Classes\SystemFileAssociations\$ext\shell\Удалить метаданные"
	if (Test-Path $base) {
		Remove-Item -Recurse -Force $base
	}
}

$directoryKey = 'HKCU:Software\Classes\Directory\shell\Удалить метаданные'
if (Test-Path $directoryKey) { Remove-Item -Recurse -Force $directoryKey }
$sendTo = [Environment]::GetFolderPath('SendTo')
Remove-Item (Join-Path $sendTo 'Удалить метаданные.cmd') -ErrorAction SilentlyContinue
Write-Host "Контекстное меню удалено (резервная очистка реестра)."
