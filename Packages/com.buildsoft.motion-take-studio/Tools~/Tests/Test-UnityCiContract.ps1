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
$safeGameCiRunnerPath = Join-Path $repositoryRoot ".github/scripts/run-gameci-package-tests.sh"
$safeGameCiRunnerTestPath = Join-Path $repositoryRoot ".github/scripts/test-run-gameci-package-tests.sh"
$onlineActivationPath = Join-Path $repositoryRoot ".github/scripts/gameci-activate-online.sh"
$returnLicensePath = Join-Path $repositoryRoot ".github/scripts/gameci-return-license.sh"
$secureRunStepsPath = Join-Path $repositoryRoot ".github/scripts/gameci-secure-run-steps.sh"

foreach ($requiredPath in @(
    $runnerPath,
    $workflowPath,
    $releasePath,
    $safeGameCiRunnerPath,
    $safeGameCiRunnerTestPath,
    $onlineActivationPath,
    $returnLicensePath,
    $secureRunStepsPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "CI contract file is missing: $requiredPath"
    }
}

$runnerText = Get-Content -LiteralPath $runnerPath -Raw
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$releaseText = Get-Content -LiteralPath $releasePath -Raw
$safeGameCiRunnerText = Get-Content -LiteralPath $safeGameCiRunnerPath -Raw
$onlineActivationText = Get-Content -LiteralPath $onlineActivationPath -Raw
$returnLicenseText = Get-Content -LiteralPath $returnLicensePath -Raw
$secureRunStepsText = Get-Content -LiteralPath $secureRunStepsPath -Raw
$safeGameCiRunnerTestText = Get-Content -LiteralPath $safeGameCiRunnerTestPath -Raw

Assert-ContainsLiteral $runnerText "ValidateResultsOnly" "the GameCI XML validation entry point"
Assert-ContainsLiteral $runnerText "MinimumTestCount" "the minimum discovered-test guard"
Assert-ContainsLiteral $runnerText `
    "ArmedOptionalProcessor_WaitsForCompletionBeforeReady" `
    "the optional-processor Play Mode integration test"

Assert-ContainsLiteral $workflowText "pull_request:" "the pull-request trigger"
Assert-ContainsLiteral $workflowText "workflow_call:" "the reusable release trigger"
Assert-ContainsLiteral $workflowText "repository: game-ci/unity-test-runner" `
    "the GameCI source checkout"
Assert-ContainsLiteral $workflowText "ref: 0ff419b913a3630032cbe0de48a0099b5a9f0ed9" `
    "the SHA-pinned GameCI source"
Assert-ContainsLiteral $workflowText "./.github/scripts/run-gameci-package-tests.sh" `
    "the credential-safe GameCI wrapper"
Assert-ContainsLiteral $workflowText "./.github/scripts/test-run-gameci-package-tests.sh" `
    "the credential-safe Docker argument regression test"
Assert-ContainsLiteral $workflowText "-ValidateResultsOnly" "post-run NUnit XML validation"
foreach ($credentialName in @("UNITY_LICENSE", "UNITY_EMAIL", "UNITY_PASSWORD")) {
    Assert-ContainsLiteral $workflowText `
        ('{0}: ${{{{ secrets.{0} }}}}' -f $credentialName) `
        "the protected $credentialName environment secret"
}
Assert-ContainsLiteral $workflowText "Unity CI Gate" "the stable required-check name"
Assert-ContainsLiteral $workflowText 'PACKAGE_MODE: "true"' "the package-only dependency boundary"
Assert-ContainsLiteral $workflowText `
    "unityci/editor@sha256:1c7b9cf8a65a304bb99f222d91c3452f99148ad647d1416ed658a3908a9f8dea" `
    "the immutable Unity container image"

if ($workflowText.Contains("uses: game-ci/unity-test-runner@", [System.StringComparison]::Ordinal)) {
    throw "Unity CI must not use the upstream action path that expands credentials into a Docker command string."
}

if ($workflowText.Contains("customParameters:", [System.StringComparison]::Ordinal)) {
    throw "Unity CI must not pass space-delimited customParameters through Docker environment construction."
}

if ([regex]::IsMatch($workflowText, 'UNITY_SERIAL:\s*\$\{\{\s*secrets\.')) {
    throw "UNITY_SERIAL must be derived inside the trusted wrapper, not stored as a GitHub secret."
}

$persistCredentialsCount = [regex]::Matches(
    $workflowText,
    '(?m)^\s+persist-credentials:\s+false\s*$'
).Count
if ($persistCredentialsCount -lt 3) {
    throw "Every checkout must disable persisted GitHub credentials."
}

foreach ($credentialName in @(
    "UNITY_EMAIL",
    "UNITY_PASSWORD",
    "UNITY_SERIAL",
    "GIT_CONFIG_EXTENSIONS",
    "CUSTOM_PARAMETERS"
)) {
    Assert-MatchesPattern $safeGameCiRunnerText `
        ("(?m)^  {0}\r?$" -f [regex]::Escape($credentialName)) `
        "the Docker environment-name allowlist entry for $credentialName"
}
if ([regex]::IsMatch($safeGameCiRunnerText, '(?m)^  UNITY_LICENSE$')) {
    throw "The raw ULF must not be forwarded into the Unity test container."
}
Assert-ContainsLiteral $safeGameCiRunnerText 'dockerArguments+=("--env" "$environmentName")' `
    "name-only Docker environment forwarding"
Assert-ContainsLiteral $safeGameCiRunnerText 'docker "${dockerArguments[@]}"' `
    "array-based Docker invocation without shell re-parsing"
Assert-ContainsLiteral $safeGameCiRunnerText 'docker stop --timeout "$timeoutSeconds"' `
    "a bounded graceful Docker stop before forced cleanup"
Assert-ContainsLiteral $safeGameCiRunnerText 'stopContainerGracefully 20' `
    "the normal-exit Unity license-return window"
Assert-ContainsLiteral $safeGameCiRunnerText 'trap handleHostSignal INT TERM' `
    "immediate host-side Docker shutdown on workflow cancellation"
Assert-ContainsLiteral $safeGameCiRunnerText 'stopContainerGracefully 6' `
    "a cancellation stop window that fits GitHub's signal grace period"
Assert-ContainsLiteral $safeGameCiRunnerText '::add-mask::%s' `
    "GitHub masking of the ULF-derived Unity serial"
Assert-ContainsLiteral $safeGameCiRunnerText 'UNITY_SERIAL_MASKED' `
    "GitHub masking of Unity's serial form with the final four characters replaced by XXXX"
Assert-ContainsLiteral $safeGameCiRunnerText 'unset UNITY_LICENSE' `
    "removal of the raw ULF before the Docker client starts"
Assert-ContainsLiteral $safeGameCiRunnerText "BuildSoft.MotionTakeStudio.Editor.Tests" `
    "the Editor assembly filter inside the safe container boundary"
Assert-ContainsLiteral $safeGameCiRunnerText "BuildSoft.MotionTakeStudio.PlayMode.Tests" `
    "the Play Mode assembly filter inside the safe container boundary"
if ([regex]::IsMatch(
    $safeGameCiRunnerText,
    '--env\s+(UNITY_LICENSE|UNITY_EMAIL|UNITY_PASSWORD|UNITY_SERIAL)='
)) {
    throw "The safe GameCI wrapper must forward credential names without Docker argument values."
}

Assert-ContainsLiteral $onlineActivationText 'Serial number assigned to:' `
    "a positive online-activation success marker"
Assert-ContainsLiteral $onlineActivationText 'License is not active' `
    "an explicit online-activation failure marker"
Assert-ContainsLiteral $onlineActivationText '>/dev/null 2>&1' `
    "suppressed Unity activation launcher output"
Assert-ContainsLiteral $returnLicenseText '-returnlicense' `
    "online license return"
Assert-ContainsLiteral $returnLicenseText 'returnSuccessMarker' `
    "positive Unity license-return evidence"
Assert-ContainsLiteral $returnLicenseText 'licenseArtifactRemoved' `
    "local license-artifact removal evidence"
Assert-ContainsLiteral $returnLicenseText '>/dev/null 2>&1' `
    "suppressed Unity license-return launcher output"
Assert-ContainsLiteral $secureRunStepsText `
    'unset UNITY_LICENSE UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL' `
    "credential removal before Unity tests run"
foreach ($credentialName in @("UNITY_LICENSE", "UNITY_EMAIL", "UNITY_PASSWORD", "UNITY_SERIAL")) {
    Assert-ContainsLiteral $secureRunStepsText ("-u {0}" -f $credentialName) `
        "the explicit $credentialName removal from the Unity test subprocess"
}
Assert-ContainsLiteral $secureRunStepsText 'trap handleSignal INT TERM' `
    "best-effort license return during cancellation"
Assert-ContainsLiteral $secureRunStepsText 'stopActiveChildProcess' `
    "termination of the active Unity child before cancellation return"
Assert-ContainsLiteral $secureRunStepsText 'testCommand=(setsid' `
    "a dedicated test process group for bounded cancellation"
Assert-ContainsLiteral $secureRunStepsText 'finishInFlightReturnOnSignal' `
    "a bounded completion window for an in-flight license return"
if ($secureRunStepsText.Contains("MTS_UNITY_CREDENTIAL_DIRECTORY", [System.StringComparison]::Ordinal)) {
    throw "The secure GameCI flow must not persist account credentials for license return."
}
foreach ($scriptContract in @(
    @{ Text = $onlineActivationText; LogName = 'activationLog'; Description = 'activation' },
    @{ Text = $returnLicenseText; LogName = 'returnLog'; Description = 'license-return' }
)) {
    foreach ($unsafeLogCommand in @('cat', 'less', 'more', 'tail', 'head')) {
        $unsafePattern = ('(?m)^\s*{0}\s+.*\${1}' -f `
            [regex]::Escape($unsafeLogCommand),
            [regex]::Escape($scriptContract.LogName))
        if ([regex]::IsMatch($scriptContract.Text, $unsafePattern)) {
            throw "The $($scriptContract.Description) script must not print raw Unity logs."
        }
    }
}

foreach ($regressionLiteral in @(
    "no-marker",
    "false-success",
    "credential_present=false",
    "no-marker",
    "generic-error",
    "TEST_BLOCK_UNTIL_SIGNAL",
    "return-timeout",
    "manual recovery",
    "Outer TERM handling",
    "expected 130 within 7s",
    "-x"
)) {
    Assert-ContainsLiteral $safeGameCiRunnerTestText $regressionLiteral `
        "the $regressionLiteral credential-safety regression"
}

Assert-ContainsLiteral $workflowText "cancel-in-progress: false" `
    "completion of license return before a newer main run starts"
Assert-ContainsLiteral $workflowText "group: motion-take-studio-unity-license" `
    "serialization of main and release use of the dedicated Unity CI account"

Assert-MatchesPattern $workflowText `
    '(?ms)^  unity:\r?\n.*?^    if: github\.event_name != ''pull_request'' && github\.ref == ''refs/heads/main''\r?$.*?^    environment: unity-ci\r?$' `
    "the Unity job's trusted-main environment boundary"
Assert-MatchesPattern $workflowText `
    '(?ms)^  gate:\r?\n.*?^    name: Unity CI Gate\r?\n    if: always\(\)\r?$.*?^    needs:\r?\n      - contract\r?\n      - unity\r?$' `
    "the stable gate's dependency on both CI jobs"
Assert-ContainsLiteral $workflowText 'EVENT_NAME: ${{ github.event_name }}' `
    "the stable gate's pull-request result policy"
Assert-ContainsLiteral $workflowText '"$UNITY_RESULT" != "skipped"' `
    "the pull-request gate's requirement that the secret-bearing Unity job stays skipped"
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
Assert-ContainsLiteral $releaseText "persist-credentials: false" `
    "release checkout credential cleanup"
if ($releaseText.Contains("secrets: inherit", [System.StringComparison]::Ordinal)) {
    throw "The release workflow must rely only on the reusable workflow's unity-ci Environment secrets."
}
Assert-MatchesPattern $releaseText `
    '(?ms)^  build:\r?\n.*?^    needs:\r?\n      - config\r?\n      - unity-tests\r?$' `
    "the release build's dependency on Unity tests"

Write-Host "Unity CI workflow contract passed."
