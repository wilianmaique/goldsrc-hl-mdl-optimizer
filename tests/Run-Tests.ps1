param(
    [string]$ModelsDirectory = (Join-Path $PSScriptRoot 'fixtures'),
    [string]$ResultsDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$ModelsDirectory = (Resolve-Path -LiteralPath $ModelsDirectory).Path
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $project ('artifacts/testes-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$ResultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
if ((Test-Path -LiteralPath $ResultsDirectory) -and (Get-ChildItem -Force -LiteralPath $ResultsDirectory | Select-Object -First 1)) { throw 'Escolha uma pasta vazia para registrar esta execução de testes.' }
[IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
if (-not $SkipBuild) { & (Join-Path $project 'build.ps1') }
$dist = Join-Path $project 'dist'
$cli = Join-Path $dist 'MdlOptimizer.Cli.exe'
$models = @(Get-ChildItem -File -LiteralPath $ModelsDirectory -Filter '*.mdl' | Sort-Object Name)
if ($models.Count -eq 0) { throw 'Nenhum modelo de teste encontrado.' }
$hashes = @{}
foreach ($f in $models) { $hashes[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
$scratch = Join-Path $ResultsDirectory ('.test-'+[guid]::NewGuid().ToString('N').Substring(0,12))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$consoleEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    Copy-Item -LiteralPath (Join-Path $dist 'MdlOptimizer.Core.dll'),(Join-Path $dist 'MdlOptimizer.exe') -Destination $scratch
    $common = @('/nologo','/optimize+','/langversion:5','/codepage:65001','/platform:x64','/r:System.Core.dll',"/win32manifest:$(Join-Path $project 'src/app.manifest')","/r:$(Join-Path $scratch 'MdlOptimizer.Core.dll')")
    $componentTests=@('FamilyTests','VisualRasterTests','VisibilityTests','RegionalTests','PatchAtlasTests')
    foreach ($exe in @('WorkflowTests','GeometryTests')+$componentTests) { Copy-Item -LiteralPath (Join-Path $project 'src/app.config') -Destination (Join-Path $scratch ($exe+'.exe.config')) }
    $geo = Join-Path $scratch 'GeometryTests.exe'
    & $csc @common "/out:$geo" (Join-Path $PSScriptRoot 'GeometryTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar testes geométricos.' }
    & $geo $ModelsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Falha nos testes geométricos.' }
    foreach ($test in $componentTests) {
        $runner = Join-Path $scratch ($test+'.exe')
        & $csc @common '/r:System.Drawing.dll' "/out:$runner" (Join-Path $PSScriptRoot ($test+'.cs'))
        if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar $test." }
        $testArgs=@($ModelsDirectory)
        # This test splits the known, contiguous texture section of the bundled model.
        if ($test -eq 'FamilyTests') { $testArgs=@((Join-Path $PSScriptRoot 'fixtures/piramide.mdl'), $scratch) }
        & $runner @testArgs
        if ($LASTEXITCODE -ne 0) { throw "Falha em $test." }
    }
    $wf = Join-Path $scratch 'WorkflowTests.exe'
    & $csc @common '/r:System.Drawing.dll' '/r:System.Windows.Forms.dll' "/r:$(Join-Path $scratch 'MdlOptimizer.exe')" "/out:$wf" (Join-Path $PSScriptRoot 'WorkflowTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar testes da interface.' }
    & $wf $models[0].FullName $scratch $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Falha no fluxo completo da interface.' }
    $bad = Join-Path $scratch 'formato-invalido.mdl'
    [IO.File]::WriteAllBytes($bad,[byte[]](1,2,3))
    $mixedOutput = & $cli optimize $bad $models[0].FullName --out (Join-Path $scratch 'lote-misto') --fast --exact-only
    if ($LASTEXITCODE -ne 1) { throw 'O lote misto deveria informar a falha de uma entrada.' }
    $mixed = $mixedOutput | ConvertFrom-Json
    if ($mixed.Count -ne 2 -or -not $mixed[0].error -or -not $mixed[1].output) { throw 'O lote misto deixou de processar o modelo válido.' }
    $paths = @($models | ForEach-Object FullName)
    $batch = & $cli optimize @paths --out $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Falha na otimização em lote.' }
    $repeat = & $cli optimize @paths --out (Join-Path $scratch 'repeticao')
    if ($LASTEXITCODE -ne 0) { throw 'Falha na repetição do lote.' }
    $byInput=@{}
    foreach ($row in ($repeat | ConvertFrom-Json)) { $byInput[$row.input]=$row.output }
    $summary = @()
    foreach ($item in ($batch | ConvertFrom-Json)) {
        if ((Get-FileHash -LiteralPath $item.input -Algorithm SHA256).Hash -ne $hashes[$item.input]) { throw 'Um arquivo de entrada foi alterado.' }
        $report = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $item.output 'relatorio.json') | ConvertFrom-Json
        foreach ($entry in @($report.exact,$report.game)) {
            $exported = Join-Path $item.output $entry.file
            if ((Get-FileHash -LiteralPath $exported -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Hash divergente: $exported" }
            $repeated=Join-Path $byInput[$item.input] $entry.file
            if ((Get-FileHash -LiteralPath $repeated -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Resultado não determinístico: $exported" }
            $validation = & $cli verify $item.input $exported
            if ($LASTEXITCODE -ne 0) { throw "Verificação falhou: $exported" }
            $v = $validation | ConvertFrom-Json
            if (-not $v.passed -or -not $v.metadata_verified -or $v.triangles -ne $entry.triangles) { throw 'Validação inconsistente.' }
            if ($entry.file -eq $report.exact.file -and -not $v.exact_pixels) { throw 'Pixels diferentes na reabertura da cópia exata.' }
        }
        if (-not $report.exact.visual.exact_pixels -or $report.game.triangles -gt $report.exact.triangles) { throw 'As condições de fidelidade não foram atendidas.' }
        if (-not $report.exact.detail.exact_pixels -or -not $report.game.detail.passed) { throw 'Falha no percurso complementar de câmeras.' }
        if (-not $report.exact.reserved.exact_pixels -or $null -eq $report.game.reserved -or -not (Test-Path -LiteralPath (Join-Path $item.output 'auditoria-reservada.png'))) { throw 'Auditoria reservada ausente ou controle exato divergente.' }
        if ($v.reserved.passed -ne $report.game.reserved.passed -or $v.reserved.failed_views -ne $report.game.reserved.failed_views) { throw 'O diagnóstico da saída divergiu ao reabrir o arquivo.' }
        $strict = & $cli verify $item.input (Join-Path $item.output $report.game.file) --strict
        $expected=if ($report.game.reserved.passed) {0} else {3}
        if ($LASTEXITCODE -ne $expected -or ($strict | ConvertFrom-Json).passed -ne $report.game.reserved.passed) { throw 'Verificação estrita inconsistente.' }
        $budget=$report.game.refinement_budget
        if ($report.game.visual.silhouette_iou -lt $budget.min_silhouette_iou -or $report.game.visual.mean_rgb_error_255 -gt $budget.max_mean_rgb_error_255) { throw 'O refinamento excedeu o orçamento de fidelidade.' }
        $summary += [pscustomobject]@{modelo=$report.input;original=$report.triangles_original;exato=$report.exact.triangles;jogo=$report.game.triangles;vistas=$report.game.visual.views;vistas_complementares=$report.game.detail.views;vistas_reservadas=$report.game.reserved.views;reservadas_reprovadas=$report.game.reserved.failed_views;silhueta=$report.game.visual.silhouette_iou;erro_rgb=$report.game.visual.mean_rgb_error_255;original_sha256=$report.input_sha256}
    }
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'resumo.json'),(ConvertTo-Json -InputObject $summary -Depth 6),[Text.UTF8Encoding]::new($false))
    $summary | Format-Table modelo,original,exato,jogo,vistas
}
finally {
    [Console]::OutputEncoding = $consoleEncoding
    $actual = Get-Item -LiteralPath $scratch
    if ($actual.Parent.FullName.TrimEnd('\') -eq $ResultsDirectory.TrimEnd('\') -and $actual.Name -match '^\.test-[0-9a-f]{12}$' -and -not ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        # Windows PowerShell 5.1 cannot recursively remove the long-path test using Remove-Item.
        $tree = if ($actual.FullName.StartsWith('\\')) { '\\?\UNC\' + $actual.FullName.Substring(2) } else { '\\?\' + $actual.FullName }
        [IO.Directory]::Delete($tree, $true)
    }
}
