[CmdletBinding(DefaultParameterSetName = 'Local')]
param(
    [Parameter(ParameterSetName = 'Local')][switch]$Package,
    [Parameter(ParameterSetName = 'Live', Mandatory)][string]$LiveRepository,
    [Parameter(ParameterSetName = 'Live', Mandatory)][long]$LiveSequence,
    [switch]$KeepSmoke
)

$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$bin = (New-Item -ItemType Directory -Path (Join-Path $project 'bin') -Force).FullName
$name = 'test-' + [Guid]::NewGuid().ToString('N')
$scratch = Join-Path $bin $name
[void](New-Item -ItemType Directory -Path $scratch)
try {
    if (!$LiveRepository) {
        $node = Join-Path $bin 'node.exe'
        if (!(Test-Path -LiteralPath $node)) { $node = (Get-Command node.exe).Source }
        & $node --test (Join-Path $project 'tests\dokju.test.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Die Tests des Einwilligungsdienstes sind fehlgeschlagen.' }
    }

    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = @('System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.Net.Http.dll',
        'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll') |
        ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    $suite = Join-Path $scratch 'UpdateTests.exe'
    $sources = @('shared\Installation.cs', 'launcher\Program.cs', 'desktop\GitHubUpdates.cs', 'tests\UpdateTests.cs') |
        ForEach-Object { Join-Path $project $_ }
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /main:Dokju.UpdateTests "/out:$suite" @references @sources
    if ($LASTEXITCODE -ne 0) { throw 'Die Updatetests konnten nicht kompiliert werden.' }
    $desktop = Join-Path $bin 'Dokju.exe'
    if ($LiveRepository) {
        $desktop = & $suite --live $scratch $LiveRepository $LiveSequence
        if ($LASTEXITCODE -ne 0 -or !$desktop) { throw 'Der Live-Download aus GitHub ist fehlgeschlagen.' }
        Write-Output 'GitHub: Update gefunden, heruntergeladen und auf Integrität geprüft.'
    } else {
        if ($Package) { & $suite $scratch (Join-Path $project 'Dokju.zip') $bin }
        else { & $suite $scratch }
        if ($LASTEXITCODE -ne 0) { throw 'Die Updatetests sind fehlgeschlagen.' }
    }

    $desktopData = Join-Path $scratch 'desktop'
    [void](New-Item -ItemType Directory -Path $desktopData)
    $application = Start-Process -FilePath $desktop -ArgumentList ('--smoke "' + $desktopData + '"') -WindowStyle Hidden -PassThru
    if (!$application.WaitForExit(25000)) {
        Stop-Process -Id $application.Id
        throw 'Die Desktopoberfläche wurde nicht rechtzeitig geprüft.'
    }
    $failure = Join-Path $desktopData 'smoke-error.txt'
    if (Test-Path -LiteralPath $failure) { throw (Get-Content -LiteralPath $failure -Raw) }
    if ($application.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $desktopData 'smoke-ok.txt'))) {
        throw 'Die Desktop-Abnahme ist fehlgeschlagen.'
    }
    Write-Output 'Desktop: QR-Code, Softwareupdates-Dialog und Nachweisexport bestanden.'
} finally {
    if ($KeepSmoke) { Write-Output $scratch }
    else {
        $item = Get-Item -LiteralPath $scratch
        if ($item.FullName -ne (Join-Path $bin $name) -or $item.Parent.FullName -ne $bin -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unerwartetes Testverzeichnis.'
        }
        Remove-Item -LiteralPath $scratch -Recurse
    }
}
