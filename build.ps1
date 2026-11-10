# Сборка HaPlayer в один exe для Windows x64.
# .NET на компьютере пользователя не нужен: среда выполнения упакована внутрь.
#
#   build.cmd                 двойной щелчок: собрать
#   build.cmd -Run            собрать и запустить
#
# Номер версии берётся из имени папки: «HaPlayer - 1.1» → 1.1. Новая версия = новая папка,
# её создаёт new-version.cmd в корне проекта.
#
# Результат:
#   publish\HaPlayer\HaPlayer.exe                     готовая программа, данные в %APPDATA%\HaPlayer
#   publish\HaPlayer-<версия>-win-x64.zip             портативный архив (данные рядом с программой)
#   ..\releases\HaPlayer - <версия>\                  копия exe и архива для раздачи

param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\HaPlayer\HaPlayer.csproj'
$out = Join-Path $root 'publish\HaPlayer'

$releaseName = Split-Path -Leaf $root
if ($releaseName -notmatch '^HaPlayer - (\d+(\.\d+){1,3})$') {
    throw "Папка версии должна называться «HaPlayer - 1.0», а называется «$releaseName»"
}
$version = $Matches[1]
$asmVersion = (($version.Split('.') + @('0', '0'))[0..2]) -join '.'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'Не найден .NET 9 SDK. Установите: winget install Microsoft.DotNet.SDK.9' }

$releases = Join-Path (Split-Path -Parent $root) 'releases'
$releaseDir = Join-Path $releases $releaseName

# Запущенный HaPlayer держит exe, и сборка не сможет его перезаписать.
# Сами программу не закрываем: в ней может играть музыка. Просим закрыть.
$busy = Get-Process HaPlayer -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and ($_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) -or
                  $_.Path.StartsWith($releaseDir, [StringComparison]::OrdinalIgnoreCase))
}
if ($busy) { throw "Закройте HaPlayer версии $version и запустите сборку ещё раз." }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Собираю HaPlayer $version…"
& $dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$asmVersion `
    -p:InformationalVersion=$version `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

$zip = Join-Path $root "publish\HaPlayer-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip

# В архив кладём метку портативного режима: распакованный HaPlayer хранит данные рядом с собой.
# В publish\HaPlayer метки нет, поэтому собранный exe работает с данными из %APPDATA%\HaPlayer.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Update')
$entry = $archive.CreateEntry('HaPlayer.portable')
$writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding $true))
$writer.Write("Этот файл включает портативный режим HaPlayer: данные хранятся в папке «HaPlayer Data» рядом с программой.`r`n")
$writer.Dispose()
$archive.Dispose()

$exe = Join-Path $out 'HaPlayer.exe'

# Копия в releases\HaPlayer - <версия>\. У каждой версии своя папка, другие версии не трогаются.
New-Item -ItemType Directory -Force $releaseDir | Out-Null
Copy-Item $exe (Join-Path $releaseDir 'HaPlayer.exe') -Force
Copy-Item $zip (Join-Path $releaseDir "$releaseName portable win-x64.zip") -Force

# Чистый код этой версии для выкладки: ..\source code\HaPlayer - <версия>\ и zip рядом.
$sourceScript = Join-Path (Split-Path -Parent $root) 'source-code.ps1'
if (Test-Path $sourceScript) { & $sourceScript -Version $version | Out-Null }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Готово: HaPlayer $version ($size МБ)"
Write-Host "Релиз:  $releaseDir"
Write-Host "Код:    $(Join-Path (Split-Path -Parent $root) "source code\$releaseName")"

if ($Run) { Start-Process (Join-Path $releaseDir 'HaPlayer.exe') }
