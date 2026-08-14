#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot,

    [Parameter()]
    [string]$WorkflowPath,

    [Parameter()]
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "Pages workflow security contract failed: $Message"
    }
}

function Get-JobBlock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkflowText,

        [Parameter(Mandatory = $true)]
        [string]$JobName
    )

    $escapedJobName = [regex]::Escape($JobName)
    $match = [regex]::Match(
        $WorkflowText,
        "(?ms)^  ${escapedJobName}:\s*\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*(?:#.*)?$|\z)"
    )

    Assert-Contract $match.Success "job '$JobName' is required."
    return $match.Groups["body"].Value
}

function Get-PermissionEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [int]$KeyIndent
    )

    $keyPadding = " " * $KeyIndent
    $entryPadding = " " * ($KeyIndent + 2)
    $pattern = "(?m)^" + [regex]::Escape($keyPadding) +
        "permissions:\s*\r?\n(?<body>(?:^" + [regex]::Escape($entryPadding) +
        "[^\r\n]+\r?\n?)+)"
    $match = [regex]::Match($Text, $pattern)
    Assert-Contract $match.Success "a permissions mapping at indent $KeyIndent is required."
    return @($match.Groups["body"].Value -split "\r?\n" | ForEach-Object {
        $_.Trim()
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$scriptPath = $MyInvocation.MyCommand.Path
$testsRoot = Split-Path -Parent $scriptPath
$toolsRoot = Split-Path -Parent $testsRoot
$packageRoot = Split-Path -Parent $toolsRoot
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $packageRoot)
}

$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($WorkflowPath)) {
    $WorkflowPath = Join-Path $repositoryRoot ".github/workflows/build-listing.yml"
}

$workflowPath = [System.IO.Path]::GetFullPath($WorkflowPath)
Assert-Contract (Test-Path -LiteralPath $workflowPath -PathType Leaf) `
    "workflow file is missing: $workflowPath"

$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$jobsIndex = $workflowText.IndexOf("jobs:", [System.StringComparison]::Ordinal)
Assert-Contract ($jobsIndex -ge 0) "top-level jobs mapping is required."
$workflowHeader = $workflowText.Substring(0, $jobsIndex)

Assert-Contract (
    [regex]::IsMatch(
        $workflowText,
        "(?m)^\s{4}if:\s*.*github\.event_name\s*!=\s*'workflow_run'.*github\.event\.workflow_run\.conclusion\s*==\s*'success'\s*$"
    )
) "workflow_run executions must be gated on a successful upstream conclusion."

$usesMatches = [regex]::Matches($workflowText, "(?m)^\s*uses:\s*(?<value>[^\s#]+)")
Assert-Contract ($usesMatches.Count -gt 0) "at least one action reference is required."
foreach ($usesMatch in $usesMatches) {
    $usesValue = $usesMatch.Groups["value"].Value
    Assert-Contract ($usesValue -match "^[^@]+@[0-9a-f]{40}$") `
        "action '$usesValue' must be pinned to a full lowercase commit SHA."
}

$refMatches = [regex]::Matches($workflowText, "(?m)^\s*ref:\s*(?<value>[^\s#]+)")
Assert-Contract ($refMatches.Count -gt 0) "the secondary repository must have a pinned ref."
foreach ($refMatch in $refMatches) {
    $refValue = $refMatch.Groups["value"].Value
    Assert-Contract ($refValue -match "^[0-9a-f]{40}$") `
        "repository ref '$refValue' must be a full lowercase commit SHA."
}

$checkoutSteps = @([regex]::Matches(
    $workflowText,
    "(?ms)^\s{6}- (?:name|uses):.*?(?=^\s{6}- (?:name|uses):|\z)"
) | Where-Object { $_.Value -match "uses:\s*actions/checkout@" })
Assert-Contract ($checkoutSteps.Count -gt 0) "at least one checkout step is required."
foreach ($checkoutStep in $checkoutSteps) {
    Assert-Contract (
        [regex]::IsMatch($checkoutStep.Value, "(?m)^\s+persist-credentials:\s*false\s*$")
    ) "every checkout step must set persist-credentials: false."
}

$secondaryCheckout = @($checkoutSteps | Where-Object {
    $_.Value -match "(?m)^\s+repository:\s*vrchat-community/package-list-action\s*$"
})
Assert-Contract ($secondaryCheckout.Count -eq 1) `
    "the package-list-action checkout must appear exactly once."
Assert-Contract (
    [regex]::IsMatch($secondaryCheckout[0].Value, "(?m)^\s+ref:\s*[0-9a-f]{40}\s*(?:#.*)?$")
) "the secondary repository checkout ref must be a full commit SHA."

$workflowPermissions = @(Get-PermissionEntries -Text $workflowHeader -KeyIndent 0)
Assert-Contract (($workflowPermissions -join "|") -eq "contents: read") `
    "workflow-level permissions must contain only contents: read."

$buildJob = Get-JobBlock -WorkflowText $workflowText -JobName "build-listing"
$buildPermissions = @(Get-PermissionEntries -Text $buildJob -KeyIndent 4)
Assert-Contract (($buildPermissions -join "|") -eq "contents: read") `
    "the build-listing job permissions must contain only contents: read."
Assert-Contract (-not [regex]::IsMatch($buildJob, "(?m)^    environment:")) `
    "the build-listing job must not receive the privileged Pages environment."
Assert-Contract ($buildJob -match "uses:\s*actions/upload-pages-artifact@") `
    "the read-only build-listing job must upload the Pages artifact."
Assert-Contract (-not ($buildJob -match "uses:\s*actions/configure-pages@")) `
    "Pages configuration must remain in the isolated deploy-pages job."

$deployJob = Get-JobBlock -WorkflowText $workflowText -JobName "deploy-pages"
Assert-Contract ($deployJob -match "(?m)^    needs:\s*build-listing\s*$") `
    "the deploy-pages job must consume the completed build-listing job."
$deployPermissions = @(Get-PermissionEntries -Text $deployJob -KeyIndent 4)
Assert-Contract (($deployPermissions -join "|") -eq "pages: write|id-token: write") `
    "the deploy-pages job permissions must contain only Pages and OIDC write access."
Assert-Contract ($deployJob -match "(?ms)^    environment:\s*\r?\n\s{6}name:\s*github-pages\s*$") `
    "the deploy-pages job must use the github-pages environment."
Assert-Contract (-not [regex]::IsMatch($deployJob, "(?m)^\s+run:")) `
    "the privileged deploy-pages job must not execute shell commands."
Assert-Contract (-not ($deployJob -match "actions/checkout@|repository:|package-list-action")) `
    "the privileged deploy-pages job must not checkout or execute repository code."

$deployUses = [regex]::Matches($deployJob, "(?m)^\s*uses:\s*(?<value>[^\s#]+)")
Assert-Contract ($deployUses.Count -eq 2) `
    "the privileged deploy-pages job must contain only configure-pages and deploy-pages actions."
$deployActionNames = @($deployUses | ForEach-Object {
    $_.Groups["value"].Value -replace "@[0-9a-f]{40}$", ""
})
Assert-Contract (
    $deployActionNames.Count -eq 2 -and
    $deployActionNames[0] -eq "actions/configure-pages" -and
    $deployActionNames[1] -eq "actions/deploy-pages"
) "the privileged job must only configure and deploy Pages, in that order."

if ($SelfTest) {
    $mutations = [ordered]@{
        "workflow_run failure guard" = {
            param([string]$Text)
            return [regex]::Replace(
                $Text,
                "(?m)^    if:.*github\.event\.workflow_run\.conclusion.*$",
                "    if: always()",
                1
            )
        }
        "full-SHA action pin" = {
            param([string]$Text)
            return [regex]::Replace(
                $Text,
                "actions/cache@[0-9a-f]{40}",
                "actions/cache@v6",
                1
            )
        }
        "full-SHA secondary repository ref" = {
            param([string]$Text)
            return [regex]::Replace(
                $Text,
                "(?m)^(\s*ref:)\s*[0-9a-f]{40}\s*$",
                '`${1} main',
                1
            )
        }
        "checkout credential persistence" = {
            param([string]$Text)
            return [regex]::Replace(
                $Text,
                "persist-credentials: false",
                "persist-credentials: true",
                1
            )
        }
        "untrusted code in privileged deploy job" = {
            param([string]$Text)
            return [regex]::Replace(
                $Text,
                "(?m)^(    steps:\r?\n)(      - name: Setup Pages)\r?$",
                "`${1}      - name: Execute checked-out builder code`n        run: ./ci/build.cmd`n`n`${2}",
                1
            )
        }
        "Pages setup in read-only build job" = {
            param([string]$Text)
            $configureStep = @"
      - name: Setup Pages
        uses: actions/configure-pages@45bfe0192ca1faeb007ade9deae92b16b8254a0d # fixture

"@
            $withoutDeploySetup = [regex]::Replace(
                $Text,
                "(?m)^      - name: Setup Pages\r?\n        uses: actions/configure-pages@[0-9a-f]{40}[^\r\n]*\r?\n\r?\n",
                "",
                1
            )
            return [regex]::Replace(
                $withoutDeploySetup,
                "(?m)^      - name: Upload Pages Artifact$",
                $configureStep + "      - name: Upload Pages Artifact",
                1
            )
        }
    }

    $fixtureRoot = Join-Path (
        [System.IO.Path]::GetTempPath()
    ) ("motion-take-studio-pages-contract-{0}" -f [guid]::NewGuid().ToString("N"))
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    try {
        $fixtureIndex = 0
        foreach ($fixtureName in $mutations.Keys) {
            $fixtureIndex++
            $fixtureText = & $mutations[$fixtureName] $workflowText
            Assert-Contract ($fixtureText -ne $workflowText) `
                "self-test fixture '$fixtureName' did not mutate the workflow."

            $fixturePath = Join-Path $fixtureRoot ("fixture-{0:D2}.yml" -f $fixtureIndex)
            [System.IO.File]::WriteAllText($fixturePath, $fixtureText)
            $fixtureWasRejected = $false
            try {
                & $scriptPath `
                    -RepositoryRoot $repositoryRoot `
                    -WorkflowPath $fixturePath | Out-Null
            }
            catch {
                $fixtureWasRejected = $true
            }

            Assert-Contract $fixtureWasRejected `
                "self-test fixture '$fixtureName' was incorrectly accepted."
        }
    }
    finally {
        if ([System.IO.Directory]::Exists($fixtureRoot)) {
            [System.IO.Directory]::Delete($fixtureRoot, $true)
        }
    }

    Write-Output "Pages workflow security mutation fixtures passed: $($mutations.Count)."
}

Write-Output "Pages workflow security contract passed."
