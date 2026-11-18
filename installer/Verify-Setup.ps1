$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $env:RUNNER_TEMP ('Dokju-Installation-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $env:LOCALAPPDATA 'Dokju'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{027FBFD8-323E-4C51-AC23-64E1EF9D4389}_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Dokju.lnk'
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $data)) {
    throw 'Für die Installationsprüfung wird ein frisches Windows-Profil ohne Dokju-Daten benötigt.'
}
[void](New-Item -ItemType Directory -Path $folder)
$install = Join-Path $folder 'App'
$probe = Join-Path $folder 'Vorgänge'
$log = Join-Path $folder 'setup.log'
$marker = Join-Path $data ('installation-check-' + [Guid]::NewGuid().ToString('N'))
$updateCache = Join-Path $data 'App'

function Install-Dokju {
    $setup = Start-Process -FilePath (Join-Path $project 'Dokju-Setup.exe') -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="' + $install + '" /LOG="' + $log + '"') -WindowStyle Hidden -PassThru
    if (!$setup.WaitForExit(90000)) { Stop-Process -Id $setup.Id; throw 'Die Installation hat zu lange gedauert.' }
    if ($setup.ExitCode -ne 0) { if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 35 }; throw "Setup meldet Fehler $($setup.ExitCode)." }
}

function Uninstall-Dokju([string]$options = '') {
    $remove = Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART ' + $options) -WindowStyle Hidden -PassThru
    if (!$remove.WaitForExit(45000)) { Stop-Process -Id $remove.Id; throw 'Die Deinstallation hat zu lange gedauert.' }
    if ($remove.ExitCode -ne 0 -or (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $shortcut) -or
        (Test-Path -LiteralPath (Join-Path $install 'Dokju.exe')) -or (Test-Path -LiteralPath $updateCache)) {
        throw 'Die Deinstallation wurde nicht sauber abgeschlossen.'
    }
}

try {
    [void](New-Item -ItemType Directory -Path $data)
    Set-Content -LiteralPath $marker -Value 'preserve'
    Install-Dokju
    if (!(Test-Path -LiteralPath $registration) -or !(Test-Path -LiteralPath $shortcut)) { throw 'Windows-App-Eintrag oder Startmenü-Verknüpfung fehlt.' }
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
    if ($target -ne (Join-Path $install 'Dokju.exe')) { throw 'Startmenü-Verknüpfung zeigt auf die falsche Anwendung.' }
    [void](New-Item -ItemType Directory -Path $probe)
    $app = Start-Process -FilePath $target -ArgumentList ('--smoke "' + $probe + '"') -WindowStyle Hidden -PassThru
    if (!$app.WaitForExit(40000)) { Stop-Process -Id $app.Id; throw 'Die installierte App konnte nicht rechtzeitig geprüft werden.' }
    if (Test-Path -LiteralPath (Join-Path $probe 'smoke-error.txt')) { throw (Get-Content -LiteralPath (Join-Path $probe 'smoke-error.txt') -Raw) }
    if ($app.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $probe 'smoke-ok.txt'))) { throw 'Die installierte App konnte nicht gestartet werden.' }

    [void](New-Item -ItemType Directory -Path $updateCache -Force)
    Set-Content -LiteralPath (Join-Path $updateCache 'update-check') -Value 'remove'
    Uninstall-Dokju
    if ((Get-Content -LiteralPath $marker) -ne 'preserve') { throw 'Die Standard-Deinstallation muss lokale Daten behalten.' }

    Install-Dokju
    $export = Join-Path $folder 'Nachweis.pdf'
    Set-Content -LiteralPath $export -Value 'external'
    [void](New-Item -ItemType Directory -Path (Join-Path $data 'Unterordner'))
    Set-Content -LiteralPath (Join-Path $data 'Unterordner\nachweis.enc') -Value 'remove'
    Set-Content -LiteralPath (Join-Path $data 'key.dpapi') -Value 'remove'
    Uninstall-Dokju '/PURGELOCALDATA=1'
    if (Test-Path -LiteralPath $data) { throw 'Die gewählte Datenlöschung muss den gesamten lokalen Datenordner entfernen.' }
    if ((Get-Content -LiteralPath $export) -ne 'external') { throw 'Ein separat gespeicherter Export muss erhalten bleiben.' }
    Write-Output 'Installation, Programmstart, Deinstallation mit Datenerhalt und mit vollständiger lokaler Löschung geprüft.'
} finally {
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
}
