param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [switch]$AllowInstallInCurrentProfile
)
$ErrorActionPreference = 'Stop'
if (-not $AllowInstallInCurrentProfile) { throw 'Use only in a disposable Windows profile. Pass -AllowInstallInCurrentProfile explicitly.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('mdel-install-' + [guid]::NewGuid())
$app = Join-Path $work 'app'
New-Item -ItemType Directory -Path $work | Out-Null
function Run-Checked([string]$File, [string[]]$Arguments, [int]$Expected = 0) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Timed out: $File (possible unexpected GUI)" }
    if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $File" }
}
try {
    Run-Checked (Resolve-Path $Installer).Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$app`"", '/TASKS=ctxmenu,sendto')
    $exe = Join-Path $app 'MetadataDel.exe'
    if (-not (Test-Path $exe)) { throw 'Installed EXE missing' }
    foreach ($extension in @('.pdf','.docx','.xlsx','.doc','.xls')) {
        $key = "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\Удалить метаданные\command"
        $command = (Get-Item $key).GetValue('')
        if ($command -ne "`"$exe`" --log `"%1`"") { throw "Shell command changed: $extension" }
    }
    Run-Checked $exe @('--help')
    $file = Join-Path $work 'Повреждённый документ.docx'
    [IO.File]::WriteAllText($file, 'invalid QA document')
    $before = (Get-FileHash $file).Hash
    Run-Checked $exe @('--log', "`"$file`"") 2
    if ((Get-FileHash $file).Hash -ne $before) { throw 'Original changed after failure' }
    if (Test-Path "$file.bak") { throw 'Shell backup behavior changed' }
    # Same installer twice must keep the selected shell entry functional.
    Run-Checked (Resolve-Path $Installer).Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$app`"")
    Run-Checked $exe @('--help')
    Write-Output 'Windows: install/reinstall/shell routing/original preservation PASS'
} finally {
    $uninstaller = Join-Path $app 'unins000.exe'
    if (Test-Path $uninstaller) { Run-Checked $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
