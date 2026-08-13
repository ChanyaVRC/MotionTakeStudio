#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-ContainsLiteral {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not $Text.Contains($Expected, [System.StringComparison]::Ordinal)) {
        throw "CI contract is missing $Description ('$Expected')."
    }
}

function Assert-MatchesPattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not [regex]::IsMatch($Text, $Pattern)) {
        throw "CI contract does not enforce $Description."
    }
}

$scriptPath = $MyInvocation.MyCommand.Path
$toolsRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
$packageRoot = Split-Path -Parent $toolsRoot
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $packageRoot)
}

$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$runnerPath = Join-Path $toolsRoot "Run-MotionTakeStudioTests.ps1"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/unity-tests.yml"
$releasePath = Join-Path $repositoryRoot ".github/workflows/release.yml"

foreach ($requiredPath in @($runnerPath, $workflowPath, $releasePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "CI contract file is missing: $requiredPath"
    }
}

$runnerText = Get-Content -LiteralPath $runnerPath -Raw
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$releaseText = Get-Content -LiteralPath $releasePath -Raw

Assert-ContainsLiteral $runnerText "ValidateResultsOnly" "the GameCI XML validation entry point"
Assert-ContainsLiteral $runnerText "MinimumTestCount" "the minimum discovered-test guard"
Assert-ContainsLiteral $runnerText `
    "ArmedOptionalProcessor_WaitsForCompletionBeforeReady" `
    "the optional-processor Play Mode integration test"

Assert-ContainsLiteral $workflowText "pull_request:" "the pull-request trigger"
Assert-ContainsLiteral $workflowText "workflow_call:" "the reusable release trigger"
Assert-ContainsLiteral $workflowText "game-ci/unity-test-runner@0ff419b913a3630032cbe0de48a0099b5a9f0ed9" `
    "the SHA-pinned GameCI runner"
Assert-ContainsLiteral $workflowText "BuildSoft.MotionTakeStudio.Editor.Tests" "the Editor assembly filter"
Assert-ContainsLiteral $workflowText "BuildSoft.MotionTakeStudio.PlayMode.Tests" "the Play Mode assembly filter"
Assert-ContainsLiteral $workflowText "-ValidateResultsOnly" "post-run NUnit XML validation"
Assert-ContainsLiteral $workflowText "UNITY_LICENSE" "the Personal-license secret"
Assert-ContainsLiteral $workflowText "UNITY_SERIAL" "the Pro-license secret"
Assert-ContainsLiteral $workflowText "Unity CI Gate" "the stable required-check name"
Assert-ContainsLiteral $workflowText "packageMode: true" "the package-only dependency boundary"
Assert-ContainsLiteral $workflowText `
    "unityci/editor@sha256:1c7b9cf8a65a304bb99f222d91c3452f99148ad647d1416ed658a3908a9f8dea" `
    "the immutable Unity container image"

Assert-MatchesPattern $workflowText `
    '(?ms)^  unity:\r?\n.*?^    if: github\.event_name != ''pull_request'' && github\.ref == ''refs/heads/main''\r?$.*?^    environment: unity-ci\r?$' `
    "the Unity job's trusted-main environment boundary"
Assert-MatchesPattern $workflowText `
    '(?ms)^  gate:\r?\n.*?^    name: Unity CI Gate\r?$.*?^    needs:\r?\n      - contract\r?\n      - unity\r?$' `
    "the stable gate's dependency on both CI jobs"
Assert-MatchesPattern $workflowText `
    '(?ms)^      - name: Validate EditMode NUnit XML\r?\n.*?-ResultPath artifacts/editmode-results\.xml\r?$' `
    "strict EditMode result validation"
Assert-MatchesPattern $workflowText `
    '(?ms)^      - name: Validate PlayMode NUnit XML\r?\n.*?-ResultPath artifacts/playmode-results\.xml\r?$' `
    "strict PlayMode result validation"

if ($workflowText.Contains("pull_request_target:", [System.StringComparison]::Ordinal)) {
    throw "Unity CI must not execute pull-request code with pull_request_target privileges."
}

Assert-ContainsLiteral $releaseText "uses: ./.github/workflows/unity-tests.yml" `
    "the release-to-Unity-CI dependency"
Assert-ContainsLiteral $releaseText "refs/heads/main" "the main-only release guard"
Assert-MatchesPattern $releaseText `
    '(?ms)^  build:\r?\n.*?^    needs:\r?\n      - config\r?\n      - unity-tests\r?$' `
    "the release build's dependency on Unity tests"

Write-Host "Unity CI workflow contract passed."
