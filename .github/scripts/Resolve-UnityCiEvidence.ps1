#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha,

    [Parameter()]
    [scriptblock]$RequestInvoker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = "ChanyaVRC/MotionTakeStudio"
$workflowFile = "unity-tests.yml"
$workflowPath = ".github/workflows/unity-tests.yml"
$workflowId = 333412576L
$requiredJobs = @("CI Contract", "Unity EditMode + PlayMode", "Unity CI Gate")
$artifactName = "unity-tests-$CommitSha"
$apiRoot = "https://api.github.com/repos/$repository"

$token = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "GITHUB_TOKEN is required to verify Unity CI evidence."
}

if ($null -eq $RequestInvoker) {
    $RequestInvoker = {
        param($Request)

        try {
            $response = Invoke-WebRequest `
                -Method $Request.Method `
                -Uri $Request.Uri `
                -Headers $Request.Headers `
                -SkipHttpErrorCheck
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Body = [string]$response.Content
                Headers = $response.Headers
            }
        }
        catch {
            throw "GitHub Actions evidence request transport failed."
        }
    }
}

$headers = @{
    Accept = "application/vnd.github+json"
    Authorization = "Bearer $token"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent" = "MotionTakeStudio-Release-Evidence"
}

function Invoke-GitHubJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri
    )

    try {
        $response = & $RequestInvoker ([pscustomobject]@{
            Method = "GET"
            Uri = $Uri
            Headers = $headers.Clone()
        })
    }
    catch {
        throw "GitHub Actions evidence request transport failed."
    }

    if ($null -eq $response -or $null -eq $response.PSObject.Properties["StatusCode"]) {
        throw "GitHub Actions evidence response is invalid."
    }

    $statusCode = [int]$response.StatusCode
    if ($statusCode -ne 200) {
        throw "GitHub Actions evidence request failed with HTTP $statusCode."
    }

    try {
        return [string]$response.Body | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "GitHub Actions evidence response contains invalid JSON."
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($null -eq $Object) {
        throw "$Context is invalid."
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Context is missing '$Name'."
    }

    return $property.Value
}

function Assert-ExactText {
    param(
        [AllowNull()]
        $Actual,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ([string]$Actual -cne $Expected) {
        throw "$Description must be '$Expected'."
    }
}

$encodedSha = [uri]::EscapeDataString($CommitSha)
$runsUri = "$apiRoot/actions/workflows/$workflowFile/runs" +
    "?branch=main&event=push&head_sha=$encodedSha&per_page=100"
$runsResponse = Invoke-GitHubJson -Uri $runsUri
$runs = @(Get-PropertyValue -Object $runsResponse -Name "workflow_runs" -Context "Workflow runs response")
$reportedRunCount = [int64](Get-PropertyValue -Object $runsResponse -Name "total_count" -Context "Workflow runs response")
if ($reportedRunCount -ne $runs.Count) {
    throw "Workflow runs response is incomplete; pagination is not accepted."
}
if ($runs.Count -ne 1) {
    throw "Release requires exactly one Unity CI workflow run for the requested commit."
}

$run = $runs[0]
Assert-ExactText `
    -Actual (Get-PropertyValue $run "event" "Unity CI workflow run") `
    -Expected "push" `
    -Description "Unity CI workflow run event"
Assert-ExactText `
    -Actual (Get-PropertyValue $run "head_branch" "Unity CI workflow run") `
    -Expected "main" `
    -Description "Unity CI workflow run branch"
if (-not [string]::Equals(
    [string](Get-PropertyValue $run "head_sha" "Unity CI workflow run"),
    $CommitSha,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unity CI workflow run commit does not match the release commit."
}
Assert-ExactText `
    -Actual (Get-PropertyValue $run "status" "Unity CI workflow run") `
    -Expected "completed" `
    -Description "Unity CI workflow run status"
Assert-ExactText `
    -Actual (Get-PropertyValue $run "conclusion" "Unity CI workflow run") `
    -Expected "success" `
    -Description "Unity CI workflow run conclusion"
Assert-ExactText `
    -Actual (Get-PropertyValue $run "path" "Unity CI workflow run") `
    -Expected $workflowPath `
    -Description "Unity CI workflow path"
if ([int64](Get-PropertyValue $run "workflow_id" "Unity CI workflow run") -ne $workflowId) {
    throw "Unity CI workflow identity is not the trusted workflow."
}

$runRepository = Get-PropertyValue $run "repository" "Unity CI workflow run"
$headRepository = Get-PropertyValue $run "head_repository" "Unity CI workflow run"
Assert-ExactText `
    -Actual (Get-PropertyValue $runRepository "full_name" "Unity CI workflow repository") `
    -Expected $repository `
    -Description "Unity CI workflow repository"
Assert-ExactText `
    -Actual (Get-PropertyValue $headRepository "full_name" "Unity CI workflow head repository") `
    -Expected $repository `
    -Description "Unity CI workflow head repository"

$runId = [int64](Get-PropertyValue $run "id" "Unity CI workflow run")
$runAttempt = [int](Get-PropertyValue $run "run_attempt" "Unity CI workflow run")
if ($runId -le 0 -or $runAttempt -le 0) {
    throw "Unity CI workflow run identity is invalid."
}

$jobsResponse = Invoke-GitHubJson -Uri (
    "$apiRoot/actions/runs/$runId/attempts/$runAttempt/jobs?per_page=100")
$jobs = @(Get-PropertyValue $jobsResponse "jobs" "Workflow jobs response")
$reportedJobCount = [int64](Get-PropertyValue $jobsResponse "total_count" "Workflow jobs response")
if ($reportedJobCount -ne $jobs.Count) {
    throw "Workflow jobs response is incomplete; pagination is not accepted."
}

foreach ($requiredJob in $requiredJobs) {
    $matches = @($jobs | Where-Object { [string]$_.name -ceq $requiredJob })
    if ($matches.Count -ne 1) {
        throw "Required job '$requiredJob' must appear exactly once in the verified run attempt."
    }

    $job = $matches[0]
    if ([int64](Get-PropertyValue $job "run_id" "Required job '$requiredJob'") -ne $runId -or
        [int](Get-PropertyValue $job "run_attempt" "Required job '$requiredJob'") -ne $runAttempt -or
        -not [string]::Equals(
            [string](Get-PropertyValue $job "head_sha" "Required job '$requiredJob'"),
            $CommitSha,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string](Get-PropertyValue $job "status" "Required job '$requiredJob'") -cne "completed" -or
        [string](Get-PropertyValue $job "conclusion" "Required job '$requiredJob'") -cne "success") {
        throw "Required job '$requiredJob' is not a successful result for the verified commit and run attempt."
    }
}

$artifactsResponse = Invoke-GitHubJson -Uri (
    "$apiRoot/actions/runs/$runId/artifacts?per_page=100")
$artifacts = @(Get-PropertyValue $artifactsResponse "artifacts" "Workflow artifacts response")
$reportedArtifactCount = [int64](Get-PropertyValue $artifactsResponse "total_count" "Workflow artifacts response")
if ($reportedArtifactCount -ne $artifacts.Count) {
    throw "Workflow artifacts response is incomplete; pagination is not accepted."
}
$artifactMatches = @($artifacts | Where-Object { [string]$_.name -ceq $artifactName })
if ($artifactMatches.Count -ne 1) {
    throw "Unity CI artifact '$artifactName' must appear exactly once in the verified run."
}

$artifact = $artifactMatches[0]
if ([bool](Get-PropertyValue $artifact "expired" "Unity CI artifact")) {
    throw "Unity CI artifact '$artifactName' has expired."
}
if ([int64](Get-PropertyValue $artifact "size_in_bytes" "Unity CI artifact") -le 0) {
    throw "Unity CI artifact '$artifactName' is empty."
}
$artifactRun = Get-PropertyValue $artifact "workflow_run" "Unity CI artifact"
if ([int64](Get-PropertyValue $artifactRun "id" "Unity CI artifact workflow run") -ne $runId -or
    -not [string]::Equals(
        [string](Get-PropertyValue $artifactRun "head_sha" "Unity CI artifact workflow run"),
        $CommitSha,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unity CI artifact does not belong to the verified workflow run and commit."
}

return [pscustomobject]@{
    RunId = $runId
    RunAttempt = $runAttempt
    ArtifactId = [int64](Get-PropertyValue $artifact "id" "Unity CI artifact")
    ArtifactName = $artifactName
}
