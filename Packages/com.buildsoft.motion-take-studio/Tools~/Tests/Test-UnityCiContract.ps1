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
$ubaClientPath = Join-Path $repositoryRoot ".github/scripts/Invoke-UnityBuildAutomation.ps1"
$ubaClientTestPath = Join-Path $packageRoot "Tools~/Tests/Test-UnityBuildAutomationClient.ps1"
$evidenceClientPath = Join-Path $repositoryRoot ".github/scripts/Resolve-UnityCiEvidence.ps1"
$evidenceClientTestPath = Join-Path $packageRoot "Tools~/Tests/Test-UnityCiEvidence.ps1"
$manifestPath = Join-Path $repositoryRoot "Packages/manifest.json"
$projectVersionPath = Join-Path $repositoryRoot "ProjectSettings/ProjectVersion.txt"
$packageManifestPath = Join-Path $packageRoot "package.json"
$packagesIgnorePath = Join-Path $repositoryRoot "Packages/.gitignore"

foreach ($requiredPath in @(
    $runnerPath,
    $workflowPath,
    $releasePath,
    $evidenceClientPath,
    $evidenceClientTestPath,
    $packageManifestPath,
    $packagesIgnorePath,
    $manifestPath,
    $projectVersionPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "CI contract file is missing: $requiredPath"
    }
}

$runnerText = Get-Content -LiteralPath $runnerPath -Raw
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$releaseText = Get-Content -LiteralPath $releasePath -Raw
$projectVersionText = Get-Content -LiteralPath $projectVersionPath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
$packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json -Depth 100
$packagesIgnoreText = Get-Content -LiteralPath $packagesIgnorePath -Raw

# The standalone project must be reproducible from Git alone. VCC bootstrap and
# resolver packages can download/import code during Editor startup, so they are
# forbidden from the tracked CI project even when a developer has local VCC files.
$trackedVrchatPackages = @(@(& git -C $repositoryRoot ls-files -- `
    "Packages/com.vrchat.core.bootstrap/**" `
    "Packages/com.vrchat.core.vpm-resolver/**") | Where-Object {
        Test-Path -LiteralPath (Join-Path $repositoryRoot $_) -PathType Leaf
    })
if ($LASTEXITCODE -ne 0) {
    throw "CI contract could not inspect tracked package files."
}
if ($trackedVrchatPackages.Count -ne 0) {
    throw "Standalone CI must not track VRChat bootstrap/resolver files: $($trackedVrchatPackages -join ', ')"
}
if ($packagesIgnoreText.Contains("!com.vrchat.core", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Packages/.gitignore must not unignore local VRChat bootstrap/resolver packages."
}
foreach ($localVrchatPackage in @(
    "Packages/com.vrchat.core.bootstrap/package.json",
    "Packages/com.vrchat.core.vpm-resolver/package.json"
)) {
    $null = & git -C $repositoryRoot check-ignore --no-index -- $localVrchatPackage
    if ($LASTEXITCODE -ne 0) {
        throw "Packages/.gitignore must ignore local VCC package: $localVrchatPackage"
    }
}

$declaredPackageDependencies = @($packageManifest.dependencies.PSObject.Properties)
if ($declaredPackageDependencies.Count -ne 1 -or
    $declaredPackageDependencies[0].Name -cne "com.unity.animation.rigging" -or
    [string]$declaredPackageDependencies[0].Value -cne "1.2.1") {
    throw "The package must depend only on com.unity.animation.rigging 1.2.1."
}
foreach ($dependency in $declaredPackageDependencies) {
    if ($dependency.Name -match '(?i)(ndmf|nadena|vrchat|vrc)') {
        throw "NDMF and VRChat packages must remain optional: $($dependency.Name)"
    }
}

Assert-ContainsLiteral $runnerText "ValidateResultsOnly" `
    "the Unity Build Automation XML validation entry point"
Assert-ContainsLiteral $runnerText "MinimumTestCount" "the minimum discovered-test guard"
Assert-ContainsLiteral $runnerText `
    "ArmedOptionalProcessor_WaitsForCompletionBeforeReady" `
    "the optional-processor Play Mode integration test"

# RED guard for the GameCI-to-UBA migration. Remove both the workflow references and
# the trusted-host license scripts before the new hosted path can be considered safe.
foreach ($legacyToken in @(
    "game-ci",
    "GameCI",
    "unityci/editor",
    "UNITY_LICENSE",
    "UNITY_EMAIL",
    "UNITY_PASSWORD",
    "UNITY_SERIAL"
)) {
    if ($workflowText.Contains($legacyToken, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unity CI still contains legacy GameCI credential or runner token: $legacyToken"
    }
}

foreach ($legacyScriptName in @(
    "gameci-activate-online.sh",
    "gameci-return-license.sh",
    "gameci-secure-run-steps.sh",
    "run-gameci-package-tests.sh",
    "test-run-gameci-package-tests.sh"
)) {
    $legacyScriptPath = Join-Path $repositoryRoot ".github/scripts/$legacyScriptName"
    if (Test-Path -LiteralPath $legacyScriptPath) {
        throw "Legacy GameCI script must be removed: $legacyScriptPath"
    }
}

foreach ($ubaPath in @($ubaClientPath, $ubaClientTestPath)) {
    if (-not (Test-Path -LiteralPath $ubaPath -PathType Leaf)) {
        throw "CI contract file is missing: $ubaPath"
    }
}

$ubaClientText = Get-Content -LiteralPath $ubaClientPath -Raw
$evidenceClientText = Get-Content -LiteralPath $evidenceClientPath -Raw

Assert-ContainsLiteral $workflowText "pull_request:" "the pull-request trigger"
if ($workflowText.Contains("workflow_call:", [System.StringComparison]::Ordinal)) {
    throw "Unity CI must not expose a reusable release trigger after exact-SHA evidence reuse is enabled."
}
Assert-ContainsLiteral $workflowText "./.github/scripts/Invoke-UnityBuildAutomation.ps1" `
    "the repository-owned Unity Build Automation client"
Assert-ContainsLiteral $workflowText `
    "./Packages/com.buildsoft.motion-take-studio/Tools~/Tests/Test-UnityBuildAutomationClient.ps1" `
    "the secret-free UBA client fixture suite"
Assert-ContainsLiteral $workflowText `
    "./Packages/com.buildsoft.motion-take-studio/Tools~/Tests/Test-UnityCiEvidence.ps1" `
    "the secret-free exact-SHA evidence fixture suite"
Assert-ContainsLiteral $workflowText "-ValidateResultsOnly" `
    "post-download NUnit XML validation"
Assert-ContainsLiteral $workflowText "-ResultPath artifacts/editmode-results.xml" `
    "strict EditMode result validation"
Assert-ContainsLiteral $workflowText "-ResultPath artifacts/playmode-results.xml" `
    "strict PlayMode result validation"
Assert-ContainsLiteral $workflowText "Unity CI Gate" "the stable required-check name"
Assert-ContainsLiteral $workflowText "UNITY_VERSION: 2022.3.40f1" `
    "the supported Unity 2022 LTS patch"
Assert-ContainsLiteral $workflowText "group: motion-take-studio-unity-build-automation" `
    "serialization of protected-main and release UBA builds"
Assert-ContainsLiteral $workflowText "cancel-in-progress: false" `
    "completion of the exact UBA build before a newer run starts"

$contractJobMatch = [regex]::Match(
    $workflowText,
    '(?ms)^  contract:\r?\n(?<body>.*?)^  unity:\r?\n'
)
if (-not $contractJobMatch.Success) {
    throw "CI contract cannot isolate the secret-free contract job."
}
if ([regex]::IsMatch($contractJobMatch.Groups['body'].Value, 'secrets\.|UNITY_UBA_(KEY_ID|SECRET_KEY)')) {
    throw "The pull-request contract job must not reference UBA credentials."
}

$ubaStepMatch = [regex]::Match(
    $workflowText,
    '(?ms)^      - name: Run Unity Build Automation tests without VRChat SDK or NDMF\r?\n(?<body>.*?)^      - name: Validate EditMode NUnit XML\r?\n'
)
if (-not $ubaStepMatch.Success) {
    throw "CI contract cannot isolate the credential-scoped UBA invocation step."
}
$secretReferences = [regex]::Matches($workflowText, 'secrets\.UNITY_UBA_(KEY_ID|SECRET_KEY)')
if ($secretReferences.Count -ne 2) {
    throw "UBA credentials must each appear exactly once in the workflow."
}
if ([regex]::Matches(
    $ubaStepMatch.Groups['body'].Value,
    'secrets\.UNITY_UBA_(KEY_ID|SECRET_KEY)'
).Count -ne 2) {
    throw "UBA credentials must be scoped only to the UBA invocation step."
}

foreach ($mapping in @(
    @{ Name = "UNITY_UBA_KEY_ID"; Source = 'secrets.UNITY_UBA_KEY_ID' },
    @{ Name = "UNITY_UBA_SECRET_KEY"; Source = 'secrets.UNITY_UBA_SECRET_KEY' },
    @{ Name = "UNITY_UBA_ORG_ID"; Source = 'vars.UNITY_UBA_ORG_ID' },
    @{ Name = "UNITY_UBA_PROJECT_ID"; Source = 'vars.UNITY_UBA_PROJECT_ID' },
    @{ Name = "UNITY_UBA_BUILD_TARGET_ID"; Source = 'vars.UNITY_UBA_BUILD_TARGET_ID' }
)) {
    Assert-ContainsLiteral $workflowText `
        (('{0}: ${{{{ {1} }}}}' -f $mapping.Name, $mapping.Source)) `
        "the protected $($mapping.Name) environment mapping"
}

foreach ($identifierArgument in @(
    '-OrganizationId $env:UNITY_UBA_ORG_ID',
    '-ProjectId $env:UNITY_UBA_PROJECT_ID',
    '-BuildTargetId $env:UNITY_UBA_BUILD_TARGET_ID',
    '-CommitSha $env:GITHUB_SHA',
    '-ExpectedUnityVersion $env:UNITY_VERSION',
    '-OutputDirectory artifacts'
)) {
    Assert-ContainsLiteral $workflowText $identifierArgument `
        "the UBA invocation argument $identifierArgument"
}

if ([regex]::IsMatch($workflowText, '(?m)^\s+-(KeyId|SecretKey)\s+')) {
    throw "UBA credentials must be read from the process environment, not command arguments."
}

if ($workflowText.Contains("pull_request_target:", [System.StringComparison]::Ordinal)) {
    throw "Unity CI must not execute pull-request code with pull_request_target privileges."
}

Assert-MatchesPattern $workflowText `
    '(?ms)^  unity:\r?\n.*?^    if: github\.event_name != ''pull_request'' && github\.ref == ''refs/heads/main''\r?$.*?^    environment: unity-ci\r?$' `
    "the Unity job's trusted-main Environment boundary"
Assert-MatchesPattern $workflowText `
    '(?ms)^  gate:\r?\n.*?^    name: Unity CI Gate\r?\n    if: always\(\)\r?$.*?^    needs:\r?\n      - contract\r?\n      - unity\r?$' `
    "the stable gate's dependency on both CI jobs"
Assert-ContainsLiteral $workflowText 'EVENT_NAME: ${{ github.event_name }}' `
    "the stable gate's pull-request result policy"
Assert-ContainsLiteral $workflowText '"$UNITY_RESULT" != "skipped"' `
    "the pull-request requirement that the credential-bearing UBA job remains skipped"

$persistCredentialsCount = [regex]::Matches(
    $workflowText,
    '(?m)^\s+persist-credentials:\s+false\s*$'
).Count
if ($persistCredentialsCount -lt 2) {
    throw "Every workflow checkout must disable persisted GitHub credentials."
}

Assert-ContainsLiteral $workflowText "if: always()" `
    "UBA diagnostic artifact upload after failure"
Assert-ContainsLiteral $workflowText "actions/upload-artifact@" `
    "Unity Build Automation artifact upload"
Assert-ContainsLiteral $workflowText "path: artifacts" `
    "the canonical Unity result artifact directory"

foreach ($credentialName in @("UNITY_UBA_KEY_ID", "UNITY_UBA_SECRET_KEY")) {
    Assert-ContainsLiteral $ubaClientText ('$env:{0}' -f $credentialName) `
        "the environment-only $credentialName lookup"
}
Assert-ContainsLiteral $ubaClientText "playerExporter" `
    "the unit-test-only target preflight"
Assert-ContainsLiteral $ubaClientText "export" `
    "the disabled Player export assertion"
foreach ($unsafeCredentialParameter in @("-KeyId", "-SecretKey")) {
    if ($workflowText.Contains($unsafeCredentialParameter, [System.StringComparison]::Ordinal)) {
        throw "The workflow must not expand UBA credentials into $unsafeCredentialParameter."
    }
}

Assert-ContainsLiteral $projectVersionText "m_EditorVersion: 2022.3.22f1" `
    "the Unity 2022.3.22f1 repository project baseline"
Assert-ContainsLiteral $projectVersionText "m_EditorVersionWithRevision: 2022.3.22f1 (887be4894c44)" `
    "the immutable Unity 2022.3.22f1 editor revision"

$testables = @($manifest.testables)
if ($testables -notcontains "com.buildsoft.motion-take-studio") {
    throw "Packages/manifest.json must expose com.buildsoft.motion-take-studio through testables."
}

if ($releaseText.Contains("uses: ./.github/workflows/unity-tests.yml", [System.StringComparison]::Ordinal)) {
    throw "Release must reuse exact-SHA Unity CI evidence instead of launching another UBA build."
}
Assert-ContainsLiteral $releaseText "verify-unity-ci:" `
    "the release exact-SHA Unity CI evidence job"
Assert-ContainsLiteral $releaseText "actions: read" `
    "read-only access to workflow runs and artifacts"
Assert-ContainsLiteral $releaseText "./.github/scripts/Resolve-UnityCiEvidence.ps1" `
    "the release exact-SHA evidence verifier"
Assert-ContainsLiteral $releaseText 'GITHUB_TOKEN: ${{ github.token }}' `
    "the verifier's repository-scoped token"
Assert-ContainsLiteral $releaseText '-CommitSha $env:GITHUB_SHA' `
    "the immutable release commit passed to the verifier"
Assert-ContainsLiteral $releaseText `
    "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c" `
    "the full-SHA-pinned exact-run artifact downloader"
Assert-ContainsLiteral $releaseText 'run-id: ${{ steps.evidence.outputs.run_id }}' `
    "the verified workflow run id"
Assert-ContainsLiteral $releaseText 'name: unity-tests-${{ github.sha }}' `
    "the exact-SHA Unity result artifact"
Assert-ContainsLiteral $releaseText "-ValidationMode EditMode" `
    "strict release EditMode XML validation"
Assert-ContainsLiteral $releaseText "-ValidationMode PlayMode" `
    "strict release PlayMode XML validation"
Assert-ContainsLiteral $releaseText "-ResultPath artifacts/editmode-results.xml" `
    "the release EditMode evidence path"
Assert-ContainsLiteral $releaseText "-ResultPath artifacts/playmode-results.xml" `
    "the release PlayMode evidence path"
Assert-MatchesPattern $releaseText `
    '(?ms)^  verify-unity-ci:\r?\n    needs: config\r?\n    if: needs\.config\.outputs\.config_package == ''true'' && github\.ref == ''refs/heads/main''\r?$' `
    "the release evidence job's valid-package and main-branch gate"

$releaseEvidenceJobMatch = [regex]::Match(
    $releaseText,
    '(?ms)^  verify-unity-ci:\r?\n(?<body>.*?)^  # Build and release the Package\r?$'
)
if (-not $releaseEvidenceJobMatch.Success) {
    throw "CI contract cannot isolate the release evidence job."
}
$releaseEvidenceJob = $releaseEvidenceJobMatch.Groups['body'].Value
Assert-MatchesPattern $releaseEvidenceJob `
    '(?ms)^    permissions:\r?\n      actions: read\r?\n      contents: read\r?$' `
    "the release evidence job's read-only permissions"
foreach ($forbiddenEvidenceToken in @(
    "write",
    "secrets.",
    "environment: unity-ci",
    "Invoke-UnityBuildAutomation.ps1"
)) {
    if ($releaseEvidenceJob.Contains($forbiddenEvidenceToken, [System.StringComparison]::Ordinal)) {
        throw "Release evidence job crossed its read-only trust boundary: $forbiddenEvidenceToken"
    }
}
foreach ($requiredEvidenceToken in @(
    "./.github/scripts/Resolve-UnityCiEvidence.ps1",
    "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c",
    'run-id: ${{ steps.evidence.outputs.run_id }}',
    'name: unity-tests-${{ github.sha }}',
    "-ValidationMode EditMode",
    "-ResultPath artifacts/editmode-results.xml",
    "-ValidationMode PlayMode",
    "-ResultPath artifacts/playmode-results.xml"
)) {
    Assert-ContainsLiteral $releaseEvidenceJob $requiredEvidenceToken `
        "the release evidence job token $requiredEvidenceToken"
}
$releaseEvidenceOrder = @(
    $releaseEvidenceJob.IndexOf("Resolve-UnityCiEvidence.ps1", [StringComparison]::Ordinal),
    $releaseEvidenceJob.IndexOf("actions/download-artifact@", [StringComparison]::Ordinal),
    $releaseEvidenceJob.IndexOf("-ValidationMode EditMode", [StringComparison]::Ordinal),
    $releaseEvidenceJob.IndexOf("-ValidationMode PlayMode", [StringComparison]::Ordinal)
)
for ($index = 0; $index -lt $releaseEvidenceOrder.Count; $index++) {
    if ($releaseEvidenceOrder[$index] -lt 0 -or
        ($index -gt 0 -and $releaseEvidenceOrder[$index] -le $releaseEvidenceOrder[$index - 1])) {
        throw "Release must verify the run, download the artifact, then validate EditMode and PlayMode in order."
    }
}
Assert-ContainsLiteral $releaseText "refs/heads/main" "the main-only release guard"
Assert-ContainsLiteral $releaseText "persist-credentials: false" `
    "release checkout credential cleanup"
foreach ($forbiddenReleaseToken in @(
    "secrets: inherit",
    "UNITY_UBA_KEY_ID",
    "UNITY_UBA_SECRET_KEY",
    "environment: unity-ci"
)) {
    if ($releaseText.Contains($forbiddenReleaseToken, [System.StringComparison]::Ordinal)) {
        throw "Release evidence reuse must not receive UBA credentials: $forbiddenReleaseToken"
    }
}
Assert-MatchesPattern $releaseText `
    '(?ms)^  build:\r?\n.*?^    needs:\r?\n      - config\r?\n      - verify-unity-ci\r?$' `
    "the release build's dependency on verified exact-SHA Unity evidence"

foreach ($evidenceLiteral in @(
    '$env:GITHUB_TOKEN',
    'ChanyaVRC/MotionTakeStudio',
    '.github/workflows/unity-tests.yml',
    '333412576',
    'CI Contract',
    'Unity EditMode + PlayMode',
    'Unity CI Gate',
    'event=push',
    'branch=main',
    'attempts/$runAttempt/jobs',
    'unity-tests-$CommitSha'
)) {
    Assert-ContainsLiteral $evidenceClientText $evidenceLiteral `
        "the exact-SHA evidence invariant $evidenceLiteral"
}
if ([regex]::IsMatch($evidenceClientText, '(?m)^\s*Method\s*=\s*"(POST|PUT|PATCH|DELETE)"')) {
    throw "The release evidence verifier must only issue read-only GitHub API requests."
}

Write-Host "Unity Build Automation workflow contract passed."
