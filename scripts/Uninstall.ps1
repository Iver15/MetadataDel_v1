$ErrorActionPreference = "Stop"
param(
	[string]$ExePath = "$PSScriptRoot\..\publish\MetadataDel.exe"
)

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

Write-Host "Контекстное меню удалено (резервная очистка реестра)."
