#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$UnityPath,

    [Parameter()]
    [string]$ProjectPath,

    [Parameter()]
    [string]$ResultsDirectory,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TestTimeoutMinutes = 15,

    [Parameter()]
    [switch]$SelfTest,

    [Parameter()]
    [switch]$ValidateResultsOnly,

    [Parameter()]
    [ValidateSet("EditMode", "PlayMode")]
    [string]$ValidationMode,

    [Parameter()]
    [string]$ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Find-UnityProjectPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StartPath
    )

    $directory = Get-Item -LiteralPath $StartPath
    while ($null -ne $directory) {
        $versionPath = Join-Path $directory.FullName "ProjectSettings/ProjectVersion.txt"
        $manifestPath = Join-Path $directory.FullName "Packages/manifest.json"
        if ((Test-Path -LiteralPath $versionPath -PathType Leaf) -and
            (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            return $directory.FullName
        }

        $directory = $directory.Parent
    }

    throw "Package path から Unity project root を特定できません: $StartPath"
}

function Resolve-UnityPath {
    param(
        [string]$RequestedPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedProjectPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolvedRequestedPath = Resolve-FullPath -Path $RequestedPath -BasePath (Get-Location).Path
        if (-not (Test-Path -LiteralPath $resolvedRequestedPath -PathType Leaf)) {
            throw "指定された Unity Editor が見つかりません: $resolvedRequestedPath"
        }

        return (Resolve-Path -LiteralPath $resolvedRequestedPath).Path
    }

    $candidatePaths = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:UNITY_PATH)) {
        $candidatePaths.Add((Resolve-FullPath -Path $env:UNITY_PATH -BasePath (Get-Location).Path))
    }

    $projectVersionPath = Join-Path $ResolvedProjectPath "ProjectSettings/ProjectVersion.txt"
    if (Test-Path -LiteralPath $projectVersionPath -PathType Leaf) {
        $versionLine = Select-String -LiteralPath $projectVersionPath -Pattern '^m_EditorVersion:\s*(.+)$' |
            Select-Object -First 1
        if ($null -ne $versionLine) {
            $editorVersion = $versionLine.Matches[0].Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles})) {
                $candidatePaths.Add((Join-Path ${env:ProgramFiles} "Unity/Hub/Editor/$editorVersion/Editor/Unity.exe"))
            }

            if (-not [string]::IsNullOrWhiteSpace($env:HOME)) {
                $candidatePaths.Add((Join-Path $env:HOME "Unity/Hub/Editor/$editorVersion/Editor/Unity"))
            }

            $candidatePaths.Add("/Applications/Unity/Hub/Editor/$editorVersion/Unity.app/Contents/MacOS/Unity")
        }
    }

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidatePath).Path
        }
    }

    foreach ($commandName in @("Unity.exe", "Unity")) {
        $command = Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "Unity Editor が見つかりません。-UnityPath を指定するか UNITY_PATH 環境変数を設定してください。"
}

function ConvertTo-CommandLineArgument {
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\', $backslashes * 2 + 1)
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\', $backslashes)
            $backslashes = 0
        }

        [void]$builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append('\', $backslashes * 2)
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-RequiredIntegerAttribute {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$ResultPath
    )

    $rawValue = $Element.GetAttribute($Name)
    $parsedValue = 0
    if ([string]::IsNullOrWhiteSpace($rawValue) -or
        -not [int]::TryParse($rawValue, [ref]$parsedValue)) {
        throw "テスト結果 '$ResultPath' の test-run に整数属性 '$Name' がありません。"
    }

    return $parsedValue
}

function Get-TestModeContract {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("EditMode", "PlayMode")]
        [string]$Mode
    )

    switch ($Mode) {
        "EditMode" {
            return [pscustomobject]@{
                Mode = "EditMode"
                AssemblyName = "BuildSoft.MotionTakeStudio.Editor.Tests"
                MinimumTestCount = 95
                RequiredTestNames = @(
                    "BuildSoft.MotionTakeStudio.Editor.Tests.MotionCapturePlayModeIntegrationTests.ArmedOptionalProcessor_WaitsForCompletionBeforeReady",
                    "BuildSoft.MotionTakeStudio.Editor.Tests.MotionCapturePlayModeIntegrationTests.CaptureReviewElbowCorrectionValidationAndBake_RoundTripsAcrossPlayerFrames"
                )
            }
        }
        "PlayMode" {
            return [pscustomobject]@{
                Mode = "PlayMode"
                AssemblyName = "BuildSoft.MotionTakeStudio.PlayMode.Tests"
                MinimumTestCount = 2
                RequiredTestNames = @(
                    "BuildSoft.MotionTakeStudio.PlayMode.Tests.MotionTakeRuntimePlayModeTests.MotionCaptureAvatarMarker_ConfigurePersistsAcrossTheNextPlayerFrame",
                    "BuildSoft.MotionTakeStudio.PlayMode.Tests.MotionTakeRuntimePlayModeTests.TwoBoneIkSolver_ReachesMovingTargetAcrossRealPlayerFramesWithoutFlipping"
                )
            }
        }
    }
}

function Assert-TestResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string]$ResultPath,

        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MinimumTestCount = 1,

        [string[]]$RequiredTestNames = @()
    )

    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "$Mode の XML 結果が生成されませんでした: $ResultPath"
    }

    try {
        [xml]$document = Get-Content -LiteralPath $ResultPath -Raw
    }
    catch {
        throw "$Mode の XML 結果を読み取れません: $ResultPath`n$($_.Exception.Message)"
    }

    $testRun = $document.SelectSingleNode("/test-run")
    if ($null -eq $testRun -or -not ($testRun -is [System.Xml.XmlElement])) {
        throw "$Mode の XML 結果に test-run ルートがありません: $ResultPath"
    }

    $total = Get-RequiredIntegerAttribute -Element $testRun -Name "total" -ResultPath $ResultPath
    $passed = Get-RequiredIntegerAttribute -Element $testRun -Name "passed" -ResultPath $ResultPath
    $failed = Get-RequiredIntegerAttribute -Element $testRun -Name "failed" -ResultPath $ResultPath
    $skipped = Get-RequiredIntegerAttribute -Element $testRun -Name "skipped" -ResultPath $ResultPath
    $inconclusive = Get-RequiredIntegerAttribute -Element $testRun -Name "inconclusive" -ResultPath $ResultPath
    $result = $testRun.GetAttribute("result")

    if ($total -lt $MinimumTestCount) {
        throw "$Mode の実行件数が基準未満です " +
            "(total=$total, minimum=$MinimumTestCount)。assembly discovery と package testables を確認してください。"
    }

    if ($passed -le 0) {
        throw "$Mode には成功したテストがありません (total=$total, passed=$passed, failed=$failed)。"
    }

    if ($failed -ne 0) {
        throw "$Mode で $failed 件のテストが失敗しました: $ResultPath"
    }

    if ($skipped -ne 0 -or $inconclusive -ne 0 -or $passed -ne $total) {
        throw "$Mode に未完走テストがあります (total=$total, passed=$passed, skipped=$skipped, inconclusive=$inconclusive)。"
    }

    $expectedAssemblyFile = "$AssemblyName.dll"
    $matchingAssembly = $document.SelectNodes("//test-suite[@type='Assembly']") |
        Where-Object {
            [string]::Equals(
                $_.GetAttribute("name"),
                $expectedAssemblyFile,
                [System.StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
    if ($null -eq $matchingAssembly) {
        throw "$Mode の XML に対象 assembly '$expectedAssemblyFile' がありません: $ResultPath"
    }

    $assemblyTotal = Get-RequiredIntegerAttribute `
        -Element $matchingAssembly -Name "total" -ResultPath $ResultPath
    $assemblyPassed = Get-RequiredIntegerAttribute `
        -Element $matchingAssembly -Name "passed" -ResultPath $ResultPath
    $assemblyFailed = Get-RequiredIntegerAttribute `
        -Element $matchingAssembly -Name "failed" -ResultPath $ResultPath
    $assemblySkipped = Get-RequiredIntegerAttribute `
        -Element $matchingAssembly -Name "skipped" -ResultPath $ResultPath
    $assemblyInconclusive = Get-RequiredIntegerAttribute `
        -Element $matchingAssembly -Name "inconclusive" -ResultPath $ResultPath
    $assemblyResult = $matchingAssembly.GetAttribute("result")
    if ($total -ne $assemblyTotal) {
        throw "$Mode の結果に対象外 assembly が混在しています " +
            "(run total=$total, target assembly total=$assemblyTotal)。"
    }

    if ($assemblyTotal -le 0 -or $assemblyPassed -ne $assemblyTotal -or
        $assemblyFailed -ne 0 -or $assemblySkipped -ne 0 -or $assemblyInconclusive -ne 0 -or
        -not [string]::Equals(
            $assemblyResult,
            "Passed",
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Mode の対象 assembly が完走していません " +
            "(total=$assemblyTotal, passed=$assemblyPassed, failed=$assemblyFailed, " +
            "skipped=$assemblySkipped, inconclusive=$assemblyInconclusive, result=$assemblyResult)。"
    }

    if (-not [string]::Equals($result, "Passed", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Mode の test-run result は '$result' です。期待値は 'Passed' です: $ResultPath"
    }

    $testCases = @($matchingAssembly.SelectNodes(".//test-case"))
    if ($testCases.Count -ne $assemblyTotal) {
        throw "$Mode の対象 assembly 集計と test-case 数が一致しません " +
            "(assembly total=$assemblyTotal, test cases=$($testCases.Count))。"
    }

    $executedTests = New-Object `
        'System.Collections.Generic.Dictionary[string,string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($testCase in $testCases) {
        $testName = $testCase.GetAttribute("fullname")
        $testResult = $testCase.GetAttribute("result")
        if ([string]::IsNullOrWhiteSpace($testName) -or
            -not [string]::Equals(
                $testResult,
                "Passed",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Mode の対象 assembly に未完走 test-case があります: '$testName' ($testResult)"
        }

        if (-not $executedTests.TryAdd($testName, $testResult)) {
            throw "$Mode の対象 assembly に重複 fullname があります: $testName"
        }
    }

    foreach ($requiredTestName in $RequiredTestNames) {
        if (-not $executedTests.ContainsKey($requiredTestName) -or
            -not [string]::Equals(
                $executedTests[$requiredTestName],
                "Passed",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Mode の必須統合テストが成功していません: $requiredTestName"
        }
    }

    Write-Host "$Mode passed: assembly=$expectedAssemblyFile, total=$total, passed=$passed, failed=$failed"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$CaseName,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessagePattern) {
            throw "Runner contract self-test '$CaseName' rejected input for the wrong reason: $($_.Exception.Message)"
        }

        return
    }

    throw "Runner contract self-test '$CaseName' did not reject invalid input."
}

function Invoke-RunnerContractTests {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        "MotionTakeStudioRunnerTests-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    try {
        $validPath = Join-Path $fixtureRoot "valid.xml"
        Set-Content -LiteralPath $validPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" result="Passed"
              total="1" passed="1" failed="0" skipped="0" inconclusive="0">
    <test-case fullname="Runner.RequiredTest" result="Passed" />
  </test-suite>
</test-run>
'@
        Assert-TestResult `
            -Mode "RunnerSelfTest" `
            -ResultPath $validPath `
            -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests" `
            -RequiredTestNames @("Runner.RequiredTest")

        Assert-Throws -CaseName "missing required test" `
            -ExpectedMessagePattern "必須統合テスト" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $validPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests" `
                -RequiredTestNames @("Runner.MissingTest")
        }

        $zeroPath = Join-Path $fixtureRoot "zero.xml"
        Set-Content -LiteralPath $zeroPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="0" passed="0" failed="0" skipped="0" inconclusive="0" />
'@
        Assert-Throws -CaseName "zero tests" -ExpectedMessagePattern "基準未満" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $zeroPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        Assert-Throws -CaseName "missing XML" -ExpectedMessagePattern "生成されませんでした" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" `
                -ResultPath (Join-Path $fixtureRoot "missing.xml") `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $failedPath = Join-Path $fixtureRoot "failed.xml"
        Set-Content -LiteralPath $failedPath -Encoding UTF8 -Value @'
<test-run result="Failed" total="1" passed="0" failed="1" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" />
</test-run>
'@
        Assert-Throws -CaseName "failed tests" -ExpectedMessagePattern "成功したテストがありません" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $failedPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $skippedPath = Join-Path $fixtureRoot "skipped.xml"
        Set-Content -LiteralPath $skippedPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="2" passed="1" failed="0" skipped="1" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" />
</test-run>
'@
        Assert-Throws -CaseName "skipped tests" -ExpectedMessagePattern "未完走テスト" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $skippedPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $inconclusivePath = Join-Path $fixtureRoot "inconclusive.xml"
        Set-Content -LiteralPath $inconclusivePath -Encoding UTF8 -Value @'
<test-run result="Passed" total="2" passed="1" failed="0" skipped="0" inconclusive="1">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" />
</test-run>
'@
        Assert-Throws -CaseName "inconclusive tests" -ExpectedMessagePattern "未完走テスト" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $inconclusivePath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $malformedPath = Join-Path $fixtureRoot "malformed.xml"
        Set-Content -LiteralPath $malformedPath -Encoding UTF8 -Value '<test-run'
        Assert-Throws -CaseName "malformed XML" -ExpectedMessagePattern "読み取れません" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $malformedPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $wrongAssemblyPath = Join-Path $fixtureRoot "wrong-assembly.xml"
        Set-Content -LiteralPath $wrongAssemblyPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="Some.Other.Tests.dll" />
</test-run>
'@
        Assert-Throws -CaseName "wrong assembly" -ExpectedMessagePattern "対象 assembly" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $wrongAssemblyPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }


        $assemblyFailedPath = Join-Path $fixtureRoot "assembly-failed.xml"
        Set-Content -LiteralPath $assemblyFailedPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" result="Failed"
              total="1" passed="0" failed="1" skipped="0" inconclusive="0" />
</test-run>
'@
        Assert-Throws -CaseName "failed target assembly" -ExpectedMessagePattern "対象 assembly が完走" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $assemblyFailedPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        Assert-Throws -CaseName "case-sensitive required fullname" `
            -ExpectedMessagePattern "必須統合テスト" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $validPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests" `
                -RequiredTestNames @("runner.requiredtest")
        }

        Assert-Throws -CaseName "minimum test count" -ExpectedMessagePattern "基準未満" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $validPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests" `
                -MinimumTestCount 2
        }

        $countMismatchPath = Join-Path $fixtureRoot "count-mismatch.xml"
        Set-Content -LiteralPath $countMismatchPath -Encoding UTF8 -Value @'
<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" result="Passed"
              total="2" passed="2" failed="0" skipped="0" inconclusive="0">
    <test-case fullname="Runner.OnlyVisibleTest" result="Passed" />
  </test-suite>
</test-run>
'@
        Assert-Throws -CaseName "forged test count" `
            -ExpectedMessagePattern "test-case 数が一致" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $countMismatchPath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $duplicatePath = Join-Path $fixtureRoot "duplicate-fullname.xml"
        Set-Content -LiteralPath $duplicatePath -Encoding UTF8 -Value @'
<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" result="Passed"
              total="2" passed="2" failed="0" skipped="0" inconclusive="0">
    <test-case fullname="Runner.Duplicate" result="Passed" />
    <test-case fullname="Runner.Duplicate" result="Passed" />
  </test-suite>
</test-run>
'@
        Assert-Throws -CaseName "duplicate fullname" `
            -ExpectedMessagePattern "重複 fullname" -Action {
            Assert-TestResult -Mode "RunnerSelfTest" -ResultPath $duplicatePath `
                -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests"
        }

        $escapedTrailingSlash = ConvertTo-CommandLineArgument 'C:\My Project\'
        if ($escapedTrailingSlash -ne '"C:\My Project\\"') {
            throw "Runner argument escaping did not preserve a trailing backslash: $escapedTrailingSlash"
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
    }

    Write-Host "Runner contract self-tests passed."
}

function Invoke-TestMode {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UnityExecutable,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedResultsDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutMinutes,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MinimumTestCount = 1,

        [string[]]$RequiredTestNames = @()
    )

    $resultPath = Join-Path $ResolvedResultsDirectory "$Mode-results.xml"
    $logPath = Join-Path $ResolvedResultsDirectory "$Mode.log"
    foreach ($stalePath in @($resultPath, $logPath)) {
        if (Test-Path -LiteralPath $stalePath) {
            Remove-Item -LiteralPath $stalePath -Force
        }
    }

    $arguments = @(
        "-batchmode",
        "-nographics",
        "-runTests",
        "-projectPath", $ResolvedProjectPath,
        "-testPlatform", $Mode,
        "-assemblyNames", $AssemblyName,
        "-testResults", $resultPath,
        "-logFile", $logPath
    )

    $quotedArguments = $arguments | ForEach-Object {
        ConvertTo-CommandLineArgument ([string]$_)
    }

    Write-Host "Running $Mode tests from $AssemblyName ..."
    $unityProcess = Start-Process `
        -FilePath $UnityExecutable `
        -ArgumentList $quotedArguments `
        -PassThru
    $timeoutMilliseconds = [Math]::Min(
        [int]::MaxValue,
        [long]$TimeoutMinutes * 60L * 1000L)
    if (-not $unityProcess.WaitForExit([int]$timeoutMilliseconds)) {
        Stop-Process -Id $unityProcess.Id -Force -ErrorAction SilentlyContinue
        throw "Unity の $Mode テストが $TimeoutMinutes 分以内に終了しませんでした。ログ: $logPath"
    }

    $unityExitCode = $unityProcess.ExitCode
    $loadFailure = $null
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        $loadFailure = Select-String -LiteralPath $logPath -Pattern @(
            "will not be loaded due to errors",
            "Unable to resolve reference",
            "error CS[0-9]{4}",
            "Script compilation failed"
        ) | Select-Object -First 1
    }

    if ($unityExitCode -ne 0) {
        $diagnostic = if ($null -ne $loadFailure) {
            " assembly load/compile error: $($loadFailure.Line.Trim())"
        }
        else {
            ""
        }
        throw "Unity の $Mode テストが exit code $unityExitCode で終了しました。$diagnostic`nLog: $logPath"
    }

    if ($null -ne $loadFailure) {
        throw "Unity の $Mode assembly load/compile に失敗しました: $($loadFailure.Line.Trim())`nLog: $logPath"
    }

    Assert-TestResult `
        -Mode $Mode `
        -ResultPath $resultPath `
        -AssemblyName $AssemblyName `
        -MinimumTestCount $MinimumTestCount `
        -RequiredTestNames $RequiredTestNames
}

$scriptPath = $MyInvocation.MyCommand.Path
$packageRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)

Invoke-RunnerContractTests
if ($SelfTest) {
    return
}

if ($ValidateResultsOnly) {
    if ([string]::IsNullOrWhiteSpace($ValidationMode)) {
        throw "-ValidateResultsOnly には -ValidationMode EditMode|PlayMode が必要です。"
    }

    if ([string]::IsNullOrWhiteSpace($ResultPath)) {
        throw "-ValidateResultsOnly には -ResultPath が必要です。"
    }

    $validationContract = Get-TestModeContract -Mode $ValidationMode
    $resolvedResultPath = Resolve-FullPath -Path $ResultPath -BasePath (Get-Location).Path
    Assert-TestResult `
        -Mode $validationContract.Mode `
        -ResultPath $resolvedResultPath `
        -AssemblyName $validationContract.AssemblyName `
        -MinimumTestCount $validationContract.MinimumTestCount `
        -RequiredTestNames $validationContract.RequiredTestNames
    return
}

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Find-UnityProjectPath -StartPath $packageRoot
}

$resolvedProjectPath = Resolve-FullPath -Path $ProjectPath -BasePath (Get-Location).Path
if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $resolvedProjectPath "ProjectSettings/ProjectVersion.txt") -PathType Leaf)) {
    throw "Unity project path が不正です: $resolvedProjectPath"
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $resolvedProjectPath "Library/MotionTakeStudio/TestResults"
}

$resolvedResultsDirectory = Resolve-FullPath -Path $ResultsDirectory -BasePath $resolvedProjectPath
New-Item -ItemType Directory -Path $resolvedResultsDirectory -Force | Out-Null
$resolvedUnityPath = Resolve-UnityPath -RequestedPath $UnityPath -ResolvedProjectPath $resolvedProjectPath

foreach ($testMode in @("EditMode", "PlayMode")) {
    $contract = Get-TestModeContract -Mode $testMode
    Invoke-TestMode `
        -UnityExecutable $resolvedUnityPath `
        -ResolvedProjectPath $resolvedProjectPath `
        -ResolvedResultsDirectory $resolvedResultsDirectory `
        -Mode $contract.Mode `
        -AssemblyName $contract.AssemblyName `
        -TimeoutMinutes $TestTimeoutMinutes `
        -MinimumTestCount $contract.MinimumTestCount `
        -RequiredTestNames $contract.RequiredTestNames
}

Write-Host "Motion Take Studio の EditMode / PlayMode テストが完了しました。"
Write-Host "Results: $resolvedResultsDirectory"
