param(
    [Parameter(Mandatory=$true)][string]$Original,
    [Parameter(Mandatory=$true)][string[]]$Candidates,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$src = Join-Path (Split-Path $PSScriptRoot) 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('mdl-holdout-'+[Guid]::NewGuid().ToString('N').Substring(0,12))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    $runner = Join-Path $scratch 'HoldoutTests.exe'
    $files = @('Studio.cs','DiskPath.cs','Visual.cs','VisualRaster.cs','Localization.cs','LocalizedText.cs') | ForEach-Object {Join-Path $src $_}
    $catalogs = @(Get-ChildItem -LiteralPath $src -Filter 'Strings*.txt' -File | ForEach-Object { "/resource:$($_.FullName),MdlOptimizer.$($_.BaseName)" })
    $files += Join-Path $PSScriptRoot 'HoldoutTests.cs'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /langversion:5 /codepage:65001 /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$runner" @catalogs @files
    if ($LASTEXITCODE -ne 0) {throw 'Falha ao compilar auditoria independente.'}
    Copy-Item -LiteralPath (Join-Path $src 'app.config') -Destination ($runner+'.config')
    & $runner $Original $OutputDirectory @Candidates
    $status = $LASTEXITCODE
}
finally {
    $item = Get-Item -LiteralPath $scratch
    if ($item.Parent.FullName.TrimEnd('\') -eq ([IO.Path]::GetTempPath()).TrimEnd('\') -and $item.Name -match '^mdl-holdout-[0-9a-f]{12}$' -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}
exit $status
