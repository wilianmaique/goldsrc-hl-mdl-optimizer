param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Instale o .NET Framework 4.8 de 64 bits para compilar.' }
$src = Join-Path $PSScriptRoot 'src'
$assemblyInfo = Join-Path $src 'AssemblyInfo.cs'
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
$core = Join-Path $out 'MdlOptimizer.Core.dll'
$common = @('/nologo','/optimize+','/checked-','/platform:x64','/langversion:5','/codepage:65001','/warn:4','/r:System.Core.dll','/r:System.Drawing.dll')
$gui = @(Get-ChildItem -LiteralPath $src -Filter 'Gui*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$files = @(Get-ChildItem -LiteralPath $src -Filter '*.cs' -File | Where-Object { $_.Name -ne 'Cli.cs' -and $_.FullName -notin $gui } | Sort-Object Name | ForEach-Object FullName)
$catalogs = @(Get-ChildItem -LiteralPath $src -Filter 'Strings*.txt' -File | ForEach-Object { "/resource:$($_.FullName),MdlOptimizer.$($_.BaseName)" })
& $compiler @common '/target:library' '/r:System.Web.Extensions.dll' "/out:$core" @catalogs @files
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o núcleo.' }
& $compiler @common '/target:exe' "/win32manifest:$(Join-Path $src 'app.manifest')" "/r:$core" "/out:$(Join-Path $out 'MdlOptimizer.Cli.exe')" $assemblyInfo (Join-Path $src 'Cli.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar a interface de linha de comando.' }
$icon = Join-Path $src 'App.ico'
& $compiler @common '/target:winexe' '/r:System.Windows.Forms.dll' "/win32icon:$icon" "/resource:$icon,MdlOptimizer.App.ico" "/win32manifest:$(Join-Path $src 'app.manifest')" "/r:$core" "/out:$(Join-Path $out 'MdlOptimizer.exe')" $assemblyInfo @gui
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o aplicativo.' }
foreach ($exe in @('MdlOptimizer.exe','MdlOptimizer.Cli.exe')) { Copy-Item -LiteralPath (Join-Path $src 'app.config') -Destination (Join-Path $out ($exe+'.config')) -Force }
$bundle = @('MdlOptimizer.exe','MdlOptimizer.exe.config','MdlOptimizer.Cli.exe','MdlOptimizer.Cli.exe.config','MdlOptimizer.Core.dll') | ForEach-Object { Join-Path $out $_ }
$bundle += Join-Path $PSScriptRoot 'docs/README.md'
Compress-Archive -LiteralPath $bundle -DestinationPath (Join-Path $out 'MDL-Optimizer-Windows-x64.zip') -Force
Write-Output "Aplicativo compilado em $out"
