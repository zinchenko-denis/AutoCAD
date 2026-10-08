# Requires Windows PowerShell 5.1+ and a licensed full AutoCAD 2024 desktop.
# This is a native MakeDynRef adapter probe, NOT full product acceptance.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AcadExe,
    [Parameter(Mandatory = $true)][string]$InputDwg,
    [Parameter(Mandatory = $true)][string]$ACladDll,
    [Parameter(Mandatory = $true)][string]$HarnessDll,
    [Parameter(Mandatory = $true)][string]$CasesConfig,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ProfileName = 'AutoCAD-QA-2024',
    [ValidateSet('ru-RU', 'en-US')][string]$Language = 'ru-RU',
    [ValidateRange(30, 3600)][int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ownedProcess = $null
$ownedStartedUtc = $null
$outputPath = $null
$runId = [Guid]::NewGuid().ToString('D')
$phase = 'preflight'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$summary = [ordered]@{
    schema_version = 1
    run_id = $runId
    test_kind = 'autocad-2024-production-makedynref-runner'
    status = 'BLOCKED'
    exit_code = 2
    full_native_gate = 'NOT_RUN'
    started_utc = [DateTime]::UtcNow.ToString('o')
}

function Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function ExistingFile([string]$Path, [string]$Label) {
    if (-not [IO.Path]::IsPathRooted($Path)) { throw "$Label must be an absolute path." }
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer) { throw "$Label must be a file." }
    return $item.FullName
}

function WriteJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), $utf8)
}

function RequireEqual($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) { throw "Native report mismatch: $Label." }
}

function QuoteArgument([string]$Value) {
    # Windows filenames cannot contain quotes; reject a quote/newline in a profile too.
    if ($Value -match '["\r\n]' -or $Value.EndsWith('\')) {
        throw 'Unsupported quote/newline/trailing separator in process argument.'
    }
    return '"' + $Value + '"'
}

function StopOwnedProcess {
    if ($null -eq $ownedProcess -or $ownedProcess.HasExited) { return }
    # The handle is retained from Process.Start. Never enumerate/kill acad by name.
    if ($null -ne $ownedStartedUtc -and
        $ownedProcess.StartTime.ToUniversalTime() -ne $ownedStartedUtc) {
        throw 'Owned process identity changed; refusing to terminate it.'
    }
    $ownedProcess.Kill()
    if (-not $ownedProcess.WaitForExit(10000)) {
        throw 'Owned AutoCAD process did not stop after timeout.'
    }
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'Full AutoCAD 2024 requires the configured Windows desktop host.' }
    if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        throw 'An interactive Windows test session is required; a service/session 0 is not a GUI host.'
    }
    if (-not [IO.Path]::IsPathRooted($OutputDirectory) -or $OutputDirectory -match '[^\x20-\x7E]') {
        throw 'OutputDirectory must be an absolute ASCII path for the AutoCAD SCR bootstrap.'
    }
    if (Test-Path -LiteralPath $OutputDirectory) {
        throw 'OutputDirectory must be new. Existing reports or drawings are never overwritten.'
    }
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    $acadPath = ExistingFile $AcadExe 'AcadExe'
    $sourcePath = ExistingFile $InputDwg 'InputDwg'
    $productionPath = ExistingFile $ACladDll 'ACladDll'
    $harnessPath = ExistingFile $HarnessDll 'HarnessDll'
    $casePath = ExistingFile $CasesConfig 'CasesConfig'
    if ([IO.Path]::GetFileName($acadPath) -ine 'acad.exe') { throw 'Full acad.exe is required; Core Console is a different host.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($acadPath)
    if ($version.FileMajorPart -ne 24 -or $version.FileMinorPart -ne 3 -or $version.ProductName -notmatch 'AutoCAD') {
        throw 'The selected executable is not AutoCAD 2024 / file version 24.3.'
    }
    if ($ProfileName -notmatch '^AutoCAD-QA-[A-Za-z0-9_-]+$') {
        throw 'Use a dedicated existing test profile named AutoCAD-QA-..., never a production profile.'
    }
    $profiles = @(Get-ChildItem -LiteralPath 'HKCU:\Software\Autodesk\AutoCAD\R24.3' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.PSPath ('Profiles\' + $ProfileName)) })
    if ($profiles.Count -eq 0) {
        throw 'Create the dedicated AutoCAD 2024 test profile first. The runner does not create or modify profiles.'
    }
    $caseInput = Get-Content -LiteralPath $casePath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($key in @('fixture_id', 'input_sha256', 'source_handle', 'width_property', 'height_property', 'outline_layer', 'tolerance', 'repeats', 'cases')) {
        if ($null -eq $caseInput.PSObject.Properties[$key]) { throw "CasesConfig lacks $key." }
    }
    if ([string]$caseInput.input_sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'CasesConfig must pin input_sha256 of its exact private fixture DWG.'
    }
    if (@($caseInput.cases).Count -eq 0) { throw 'CasesConfig must include at least one explicit native test case.' }
    if ($caseInput.repeats -lt 2) { throw 'At least two repetitions are required.' }
    foreach ($case in $caseInput.cases) {
        if ($null -eq $case.PSObject.Properties['mode'] -or $case.mode -cnotin @('sample', 'definition')) {
            throw 'Each case must explicitly choose mode sample or definition; implicit sample coverage is insufficient.'
        }
    }

    $payloadPath = Join-Path $outputPath 'payload'
    [IO.Directory]::CreateDirectory($payloadPath) | Out-Null
    $workingPath = Join-Path $outputPath 'input-copy.dwg'
    $productionCopy = Join-Path $payloadPath 'ACladPlugin.dll'
    $harnessCopy = Join-Path $payloadPath 'AutoCAD.NativeAdapterCheck.dll'
    $configPath = Join-Path $outputPath 'native-config.json'
    $resultPath = Join-Path $outputPath 'native-result.json'
    $scriptPath = Join-Path $outputPath 'bootstrap.scr'
    $sourceHash = Sha256 $sourcePath
    RequireEqual $sourceHash (([string]$caseInput.input_sha256).ToLowerInvariant()) 'fixture DWG pinned by CasesConfig'
    $productionHash = Sha256 $productionPath
    $harnessHash = Sha256 $harnessPath
    Copy-Item -LiteralPath $sourcePath -Destination $workingPath
    Copy-Item -LiteralPath $productionPath -Destination $productionCopy
    Copy-Item -LiteralPath $harnessPath -Destination $harnessCopy
    RequireEqual (Sha256 $workingPath) $sourceHash 'working copy before launch'
    RequireEqual (Sha256 $productionCopy) $productionHash 'production DLL copy'
    RequireEqual (Sha256 $harnessCopy) $harnessHash 'harness DLL copy'
    $config = [ordered]@{
        schema_version = 1; run_id = $runId; fixture_id = $caseInput.fixture_id
        source_dwg = $sourcePath; source_sha256 = $sourceHash
        working_dwg = $workingPath; working_sha256 = $sourceHash
        production_dll = $productionCopy; production_sha256 = $productionHash
        harness_sha256 = $harnessHash
        source_handle = $caseInput.source_handle
        width_property = $caseInput.width_property; height_property = $caseInput.height_property
        outline_layer = $caseInput.outline_layer
        tolerance = $caseInput.tolerance; repeats = $caseInput.repeats; cases = @($caseInput.cases)
    }
    WriteJson $configPath $config
    # Copies have ASCII paths; bootstrap encoding cannot corrupt localized source paths.
    $script = @('_.NETLOAD', (QuoteArgument $productionCopy), '_.NETLOAD', (QuoteArgument $harnessCopy),
        'ATNATIVECLADTEST', '_.QUIT', '_No', '') -join "`r`n"
    [IO.File]::WriteAllText($scriptPath, $script, [Text.Encoding]::ASCII)
    $summary['source_sha256'] = $sourceHash
    $summary['production_dll_sha256'] = $productionHash
    $summary['harness_sha256'] = $harnessHash
    $summary['config_sha256'] = Sha256 $configPath
    $summary['acad_file_version'] = $version.FileVersion
    $summary['profile'] = $ProfileName

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $acadPath
    $start.WorkingDirectory = $outputPath
    $start.UseShellExecute = $false
    $start.Arguments = ((@($workingPath, '/product', 'ACAD', '/language', $Language, '/p', $ProfileName, '/nologo', '/b', $scriptPath) |
        ForEach-Object { QuoteArgument $_ }) -join ' ')
    # Set variables only for this new process, never in the user's environment/registry.
    $start.EnvironmentVariables['AT_NATIVE_CONFIG'] = $configPath
    $start.EnvironmentVariables['AT_NATIVE_RESULT'] = $resultPath
    $phase = 'native execution'
    $launchedUtc = [DateTime]::UtcNow
    $ownedProcess = [Diagnostics.Process]::Start($start)
    $ownedStartedUtc = $ownedProcess.StartTime.ToUniversalTime()
    $summary['process_id'] = $ownedProcess.Id
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $ownedProcess.WaitForExit(500)) {
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            StopOwnedProcess
            throw "AutoCAD timed out after $TimeoutSeconds seconds; no native PASS is accepted."
        }
    }
    $summary['process_exit_code'] = $ownedProcess.ExitCode
    if ($ownedProcess.ExitCode -ne 0) { throw 'Owned AutoCAD process exited abnormally.' }
    $phase = 'report validation'
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'AutoCAD produced no native report. Check loading/trust/licensing; no PASS is accepted.' }
    $reportFile = Get-Item -LiteralPath $resultPath
    if ($reportFile.LastWriteTimeUtc -lt $launchedUtc.AddSeconds(-2)) { throw 'Native report is stale.' }
    $report = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.status -eq 'BLOCKED') {
        $phase = 'native prerequisites'
        throw 'Native adapter reports BLOCKED. See native-result.json for its original diagnosis.'
    }
    RequireEqual $report.schema_version 1 'schema_version'
    RequireEqual $report.run_id $runId 'run_id'
    RequireEqual $report.fixture_id $config.fixture_id 'fixture_id'
    RequireEqual $report.configuration_sha256 $summary.config_sha256 'configuration SHA256'
    RequireEqual $report.test_kind 'autocad-2024-production-makedynref' 'test_kind'
    RequireEqual $report.full_native_gate 'NOT_RUN' 'scope'
    RequireEqual $report.host.process_id $ownedProcess.Id 'owned AutoCAD PID'
    RequireEqual $report.input_sha256 $sourceHash 'input SHA256'
    RequireEqual $report.source_sha256 $sourceHash 'source SHA256'
    RequireEqual $report.production_dll_sha256 $productionHash 'production DLL SHA256'
    RequireEqual $report.harness_sha256 $harnessHash 'harness DLL SHA256'
    RequireEqual (Sha256 $sourcePath) $sourceHash 'original DWG after execution'
    RequireEqual (Sha256 $workingPath) $sourceHash 'working DWG after aborted transactions'
    RequireEqual $report.status 'PASS' 'adapter status'
    RequireEqual $report.exit_code 0 'adapter exit_code'
    RequireEqual $report.native_execution $true 'native_execution'
    RequireEqual $report.input_files_unchanged $true 'input_files_unchanged'
    $actualCases = @($report.cases)
    RequireEqual $actualCases.Count (@($config.cases).Count * [int]$config.repeats) 'case/repetition count'
    $caseIndex = 0
    foreach ($expectedCase in $config.cases) {
        for ($repeat = 1; $repeat -le [int]$config.repeats; $repeat++) {
            $actualCase = $actualCases[$caseIndex]
            RequireEqual $actualCase.id $expectedCase.id 'case ID/order'
            RequireEqual $actualCase.repeat $repeat 'case repetition/order'
            RequireEqual $actualCase.requested.id $expectedCase.id 'requested case ID'
            RequireEqual $actualCase.requested.mode $expectedCase.mode 'requested production branch'
            foreach ($dimension in @('width', 'height', 'x', 'y')) {
                RequireEqual ([double]$actualCase.requested.$dimension) ([double]$expectedCase.$dimension) ('requested ' + $dimension)
            }
            RequireEqual $actualCase.status 'PASS' 'positive case status'
            $expectedAppearance = if ($expectedCase.mode -ceq 'sample') { 'PASS' } else { 'NOT_APPLICABLE_DEFINITION_MODE' }
            RequireEqual $actualCase.appearance $expectedAppearance 'branch-specific appearance scope'
            RequireEqual $actualCase.rollback 'PASS' 'case rollback'
            RequireEqual $actualCase.source_fingerprint_unchanged_during $true 'source during case'
            RequireEqual $actualCase.source_fingerprint_unchanged_after_abort $true 'source after Abort'
            RequireEqual (@($actualCase.native_outline_vertices).Count) 4 'independent native outline vertices'
            $caseIndex++
        }
    }
    $summary['status'] = 'PASS'
    $summary['exit_code'] = 0
    $summary['native_report_sha256'] = Sha256 $resultPath
    Write-Host 'ADAPTER PASS: actual MakeDynRef in AutoCAD 2024. Full UI/native acceptance remains NOT_RUN.'
}
catch {
    $blocked = $phase -in @('preflight', 'native prerequisites')
    $summary['status'] = if ($blocked) { 'BLOCKED' } else { 'FAIL' }
    $summary['exit_code'] = if ($blocked) { 2 } else { 1 }
    $summary['phase'] = $phase
    $summary['error'] = $_.Exception.Message
    try { StopOwnedProcess } catch { $summary['cleanup_error'] = $_.Exception.Message }
    Write-Host ($summary.status + ': ' + $summary.error)
}
finally {
    $summary['finished_utc'] = [DateTime]::UtcNow.ToString('o')
    if ($null -ne $outputPath) {
        try { WriteJson (Join-Path $outputPath 'runner-result.json') $summary }
        catch {
            $summary['status'] = 'FAIL'
            $summary['exit_code'] = 1
            Write-Host ('FAIL: Cannot preserve runner report: ' + $_.Exception.Message)
        }
    }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
}
exit ([int]$summary.exit_code)
