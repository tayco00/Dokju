param(
    [switch]$Package,
    [string]$Repository,
    [string]$Commit,
    [long]$Sequence,
    [string]$NodePath,
    [string]$NodeLicense
)

$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 (64-Bit) wird zum Build benötigt.' }

$output = Join-Path $project 'bin'
[void](New-Item -ItemType Directory -Path $output -Force)
$exe = Join-Path $output 'Dokju.exe'
$starter = Join-Path $output 'Dokju.Start.exe'
$references = @(
    'System.Security.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.Net.Http.dll',
    'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$shared = Join-Path $project 'shared\Installation.cs'
$sources = Get-ChildItem -LiteralPath (Join-Path $project 'desktop') -Filter '*.cs' -File |
    ForEach-Object FullName
$layout = Join-Path $project 'desktop\Main.xaml'
$icon = Join-Path $project 'desktop\Dokju.ico'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$icon" "/resource:$layout,Dokju.Main.xaml" @references $shared @sources
if ($LASTEXITCODE -ne 0) { throw 'Kompilierung fehlgeschlagen.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$starter" "/win32icon:$icon" @references $shared (Join-Path $project 'launcher\Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Kompilierung des Starters fehlgeschlagen.' }
Copy-Item -LiteralPath $starter -Destination (Join-Path $project 'Dokju.exe') -Force

$identity = Join-Path $output 'release.json'
if ($Repository -or $Commit -or $Sequence) {
    if ($Repository -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$' -or $Repository -match '/\.{1,2}$' -or
        $Commit -cnotmatch '^[0-9a-f]{40}$' -or $Sequence -le 0 -or $Sequence -ge 1000000000000000000) {
        throw 'Für ein Release Repository, vollständigen Commit und Buildnummer angeben.'
    }
    $metadata = @{ format = 1; repository = $Repository; commit = $Commit; sequence = $Sequence; tag = "dokju-$Sequence-$Commit" }
    [IO.File]::WriteAllText($identity, ($metadata | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
} elseif (Test-Path -LiteralPath $identity) {
    Remove-Item -LiteralPath $identity
}

$runtime = Join-Path $output 'node.exe'
$runtimeLicense = Join-Path $output 'NODE-LICENSE.txt'
if ($NodePath -or $NodeLicense) {
    if (!$NodePath -or !$NodeLicense) { throw 'Node.js und die dazugehörige Lizenzdatei zusammen angeben.' }
    Copy-Item -LiteralPath $NodePath -Destination $runtime -Force
    Copy-Item -LiteralPath $NodeLicense -Destination $runtimeLicense -Force
} else {
    foreach ($file in @($runtime, $runtimeLicense)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}

$backend = Join-Path $output 'backend'
[void](New-Item -ItemType Directory -Path $backend -Force)
$backendSources = Get-ChildItem -LiteralPath (Join-Path $project 'backend') -Filter '*.mjs' -File
$backendSources | Copy-Item -Destination $backend -Force
Write-Output $exe

if ($Package) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $project 'Dokju.zip'
    $temporary = Join-Path $project ('Dokju-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $files = @('README.md', 'Dokju.exe', 'Start-Dokju.cmd', 'bin\Dokju.exe', 'bin\Dokju.Start.exe')
    if ($Repository) { $files += 'bin\release.json' }
    if ($NodePath) { $files += @('bin\node.exe', 'bin\NODE-LICENSE.txt') }
    $files += $backendSources | ForEach-Object { Join-Path 'bin\backend' $_.Name }

    try {
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $files) {
                $entry = 'Dokju/' + $file.Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $project $file), $entry)
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $archivePath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
    Write-Output $archivePath
}
