$ErrorActionPreference = "Stop"
param(
	[string]$ExePath = "$PSScriptRoot\..\publish\MetadataDel.exe"
)

if (-not (Test-Path $ExePath)) {
	throw "Не найден файл '$ExePath'."
}

$resolvedExe = (Resolve-Path $ExePath).Path
& $resolvedExe --install-shell
exit $LASTEXITCODE
