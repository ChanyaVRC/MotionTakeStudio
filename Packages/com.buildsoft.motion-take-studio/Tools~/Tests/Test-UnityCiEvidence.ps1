#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Equal {
    param(
        [AllowNull()]
        [Parameter(Mandatory = $true)]
        $Actual,

        [AllowNull()]
        [Parameter(Mandatory = $true)]
        $Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Actual -ne $Expected) {
        throw "Assertion failed: $Description. Expected '$Expected', got '$Actual'."
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessagePattern,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    try {
        $null = & $Action
    }
    catch {
        $message = $_.Exception.Message
        if ($message -notmatch $ExpectedMessagePattern) {
            throw "Assertion failed: $Description threw the wrong message: $message"
        }

        return $message
    }

    throw "Assertion failed: $Description did not throw."
}

function New-MockResponse {
    param(
        [Parameter(Mandatory = $true)]
        [int]$StatusCode,

        [AllowNull()]
        [string]$Body
    )

    return [pscustomobject]@{
        StatusCode = $StatusCode
        Body = $Body
        Headers = @{}
    }
}

function New-EvidenceScenario {
    param(
        [string]$RunEvent = "push",
        [string]$RunBranch = "main",
        [string]$RunSha = "0123456789abcdef0123456789abcdef01234567",
        [string]$RunStatus = "completed",
        [string]$RunConclusion = "success",
        [string]$RunPath = ".github/workflows/unity-tests.yml",
        [long]$WorkflowId = 333412576,
        [string]$RepositoryName = "ChanyaVRC/MotionTakeStudio",
        [string]$HeadRepositoryName = "ChanyaVRC/MotionTakeStudio",
        [ValidateRange(0, 2)]
        [int]$RunCount = 1,
        [string]$JobMutation = "none",
        [string]$ArtifactMutation = "none",
        [int]$HttpFailure = 0,
        [switch]$MalformedJson
    )

    $commitSha = "0123456789abcdef0123456789abcdef01234567"
    $runId = 123456789
    $attempt = 2
    $requiredNames = @("CI Contract", "Unity EditMode + PlayMode", "Unity CI Gate")

    $runs = @()
    for ($index = 0; $index -lt $RunCount; $index++) {
        $runs += [ordered]@{
            id = $runId + $index
            run_attempt = $attempt
            workflow_id = $WorkflowId
            path = $RunPath
            event = $RunEvent
            head_branch = $RunBranch
            head_sha = $RunSha
            status = $RunStatus
            conclusion = $RunConclusion
            repository = [ordered]@{ full_name = $RepositoryName }
            head_repository = [ordered]@{ full_name = $HeadRepositoryName }
        }
    }

    $jobs = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $requiredNames) {
        $jobs.Add([ordered]@{
            id = 100 + $jobs.Count
            name = $name
            run_id = $runId
            run_attempt = $attempt
            head_sha = $commitSha
            status = "completed"
            conclusion = "success"
        })
    }
    switch ($JobMutation) {
        "missing" { $jobs.RemoveAt(1) }
        "duplicate" {
            $jobs.Add([ordered]@{
                id = 999
                name = "Unity EditMode + PlayMode"
                run_id = $runId
                run_attempt = $attempt
                head_sha = $commitSha
                status = "completed"
                conclusion = "success"
            })
        }
        "skipped" { $jobs[1].conclusion = "skipped" }
        "failed" { $jobs[1].conclusion = "failure" }
        "wrong-sha" { $jobs[1].head_sha = "ffffffffffffffffffffffffffffffffffffffff" }
        "wrong-attempt" { $jobs[1].run_attempt = 1 }
    }

    $artifacts = [System.Collections.Generic.List[object]]::new()
    $artifacts.Add([ordered]@{
        id = 987654321
        name = "unity-tests-$commitSha"
        expired = $false
        size_in_bytes = 9047
        workflow_run = [ordered]@{
            id = $runId
            head_sha = $commitSha
        }
    })
    switch ($ArtifactMutation) {
        "missing" { $artifacts.Clear() }
        "duplicate" {
            $artifacts.Add([ordered]@{
                id = 987654322
                name = "unity-tests-$commitSha"
                expired = $false
                size_in_bytes = 9047
                workflow_run = [ordered]@{
                    id = $runId
                    head_sha = $commitSha
                }
            })
        }
        "expired" { $artifacts[0].expired = $true }
        "empty" { $artifacts[0].size_in_bytes = 0 }
        "wrong-sha" { $artifacts[0].workflow_run.head_sha = "ffffffffffffffffffffffffffffffffffffffff" }
        "wrong-run" { $artifacts[0].workflow_run.id = 42 }
    }

    $state = [pscustomobject]@{
        Requests = [System.Collections.Generic.List[object]]::new()
        CommitSha = $commitSha
        Runs = $runs
        Jobs = @($jobs)
        Artifacts = @($artifacts)
        HttpFailure = $HttpFailure
        MalformedJson = [bool]$MalformedJson
        Secret = "github-token-DO-NOT-LEAK"
    }
    $responseFactory = ${function:New-MockResponse}
    $handler = {
        param($Request)

        $state.Requests.Add($Request)
        if ($state.HttpFailure -ne 0) {
            return & $responseFactory `
                -StatusCode $state.HttpFailure `
                -Body ("credential=" + $state.Secret)
        }
        if ($state.MalformedJson) {
            return & $responseFactory -StatusCode 200 -Body "{not-json"
        }

        $uri = [uri]$Request.Uri
        if ($uri.AbsolutePath -eq "/repos/ChanyaVRC/MotionTakeStudio/actions/workflows/unity-tests.yml/runs") {
            $query = @{}
            foreach ($pair in $uri.Query.TrimStart('?').Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
                $parts = $pair.Split('=', 2)
                $query[[uri]::UnescapeDataString($parts[0])] = if ($parts.Count -eq 2) {
                    [uri]::UnescapeDataString($parts[1])
                }
                else {
                    ""
                }
            }
            if ($query["branch"] -cne "main" -or
                $query["event"] -cne "push" -or
                $query["head_sha"] -cne $state.CommitSha -or
                $query["per_page"] -cne "100") {
                return & $responseFactory -StatusCode 400 -Body '{"message":"wrong query"}'
            }
            return & $responseFactory -StatusCode 200 -Body ([ordered]@{
                total_count = $state.Runs.Count
                workflow_runs = $state.Runs
            } | ConvertTo-Json -Depth 20 -Compress)
        }
        if ($uri.AbsolutePath -eq "/repos/ChanyaVRC/MotionTakeStudio/actions/runs/123456789/attempts/2/jobs") {
            return & $responseFactory -StatusCode 200 -Body ([ordered]@{
                total_count = $state.Jobs.Count
                jobs = $state.Jobs
            } | ConvertTo-Json -Depth 20 -Compress)
        }
        if ($uri.AbsolutePath -eq "/repos/ChanyaVRC/MotionTakeStudio/actions/runs/123456789/artifacts") {
            return & $responseFactory -StatusCode 200 -Body ([ordered]@{
                total_count = $state.Artifacts.Count
                artifacts = $state.Artifacts
            } | ConvertTo-Json -Depth 20 -Compress)
        }

        return & $responseFactory -StatusCode 404 -Body '{"message":"fixture route missing"}'
    }.GetNewClosure()

    return [pscustomobject]@{
        State = $state
        Handler = $handler
    }
}

$scriptPath = $MyInvocation.MyCommand.Path
$toolsRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
$packageRoot = Split-Path -Parent $toolsRoot
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $packageRoot)
}

$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$clientPath = Join-Path $repositoryRoot ".github/scripts/Resolve-UnityCiEvidence.ps1"
if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "Unity CI evidence verifier is missing: $clientPath"
}

$commitSha = "0123456789abcdef0123456789abcdef01234567"
$token = "github-token-DO-NOT-LEAK"

function Invoke-TestVerifier {
    param(
        [Parameter(Mandatory = $true)]
        $Scenario
    )

    $previousToken = $env:GITHUB_TOKEN
    try {
        $env:GITHUB_TOKEN = $token
        return & $clientPath -CommitSha $commitSha -RequestInvoker $Scenario.Handler
    }
    finally {
        $env:GITHUB_TOKEN = $previousToken
    }
}

$success = New-EvidenceScenario
$result = Invoke-TestVerifier -Scenario $success
Assert-Equal $result.RunId 123456789 "verified workflow run id"
Assert-Equal $result.RunAttempt 2 "verified workflow run attempt"
Assert-Equal $result.ArtifactId 987654321 "verified artifact id"
Assert-Equal $result.ArtifactName "unity-tests-$commitSha" "verified artifact name"
Assert-Equal $success.State.Requests.Count 3 "verifier uses exactly three read-only API requests"
foreach ($request in $success.State.Requests) {
    Assert-Equal $request.Method "GET" "evidence API is read-only"
    Assert-Equal $request.Headers.Authorization "Bearer $token" "GitHub token is header-only"
    if ($request.Uri.Contains($token, [StringComparison]::Ordinal)) {
        throw "Assertion failed: GitHub token appeared in a request URI."
    }
}

$negativeCases = @(
    @{ Name = "PR run"; Pattern = "push"; Parameters = @{ RunEvent = "pull_request" } },
    @{ Name = "wrong branch"; Pattern = "main"; Parameters = @{ RunBranch = "feature" } },
    @{ Name = "wrong SHA"; Pattern = "commit"; Parameters = @{ RunSha = "ffffffffffffffffffffffffffffffffffffffff" } },
    @{ Name = "pending run"; Pattern = "completed"; Parameters = @{ RunStatus = "in_progress" } },
    @{ Name = "failed run"; Pattern = "success"; Parameters = @{ RunConclusion = "failure" } },
    @{ Name = "wrong workflow path"; Pattern = "workflow"; Parameters = @{ RunPath = ".github/workflows/other.yml" } },
    @{ Name = "wrong workflow id"; Pattern = "workflow"; Parameters = @{ WorkflowId = 99 } },
    @{ Name = "wrong repository"; Pattern = "repository"; Parameters = @{ RepositoryName = "attacker/fork" } },
    @{ Name = "wrong head repository"; Pattern = "repository"; Parameters = @{ HeadRepositoryName = "attacker/fork" } },
    @{ Name = "missing run"; Pattern = "exactly one"; Parameters = @{ RunCount = 0 } },
    @{ Name = "duplicate run"; Pattern = "exactly one"; Parameters = @{ RunCount = 2 } },
    @{ Name = "missing job"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "missing" } },
    @{ Name = "duplicate job"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "duplicate" } },
    @{ Name = "skipped Unity job"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "skipped" } },
    @{ Name = "failed Unity job"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "failed" } },
    @{ Name = "wrong job SHA"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "wrong-sha" } },
    @{ Name = "wrong job attempt"; Pattern = "Unity EditMode"; Parameters = @{ JobMutation = "wrong-attempt" } },
    @{ Name = "missing artifact"; Pattern = "artifact"; Parameters = @{ ArtifactMutation = "missing" } },
    @{ Name = "duplicate artifact"; Pattern = "artifact"; Parameters = @{ ArtifactMutation = "duplicate" } },
    @{ Name = "expired artifact"; Pattern = "expired"; Parameters = @{ ArtifactMutation = "expired" } },
    @{ Name = "empty artifact"; Pattern = "empty"; Parameters = @{ ArtifactMutation = "empty" } },
    @{ Name = "wrong artifact SHA"; Pattern = "artifact"; Parameters = @{ ArtifactMutation = "wrong-sha" } },
    @{ Name = "wrong artifact run"; Pattern = "artifact"; Parameters = @{ ArtifactMutation = "wrong-run" } },
    @{ Name = "HTTP failure"; Pattern = "403"; Parameters = @{ HttpFailure = 403 } },
    @{ Name = "malformed response"; Pattern = "invalid"; Parameters = @{ MalformedJson = $true } }
)

foreach ($case in $negativeCases) {
    $parameters = $case.Parameters
    $scenario = New-EvidenceScenario @parameters
    $message = Assert-Throws `
        -Description $case.Name `
        -ExpectedMessagePattern $case.Pattern `
        -Action { Invoke-TestVerifier -Scenario $scenario }
    if ($message.Contains($token, [StringComparison]::Ordinal)) {
        throw "Assertion failed: $($case.Name) leaked the GitHub token."
    }
}

$previousToken = $env:GITHUB_TOKEN
try {
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    $null = Assert-Throws `
        -Description "missing GitHub token" `
        -ExpectedMessagePattern "GITHUB_TOKEN" `
        -Action { & $clientPath -CommitSha $commitSha -RequestInvoker $success.Handler }
}
finally {
    $env:GITHUB_TOKEN = $previousToken
}

$transportScenario = [pscustomobject]@{
    Handler = { param($Request) throw "transport-DO-NOT-LEAK" }
}
$transportMessage = Assert-Throws `
    -Description "transport failure" `
    -ExpectedMessagePattern "transport failed" `
    -Action { Invoke-TestVerifier -Scenario $transportScenario }
if ($transportMessage.Contains("DO-NOT-LEAK", [StringComparison]::Ordinal)) {
    throw "Assertion failed: transport failure leaked its raw exception."
}

Write-Host "Unity CI exact-SHA evidence tests passed."
