#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not $Condition) {
        throw "Assertion failed: $Description"
    }
}

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
        [string]$Body,

        [AllowNull()]
        [byte[]]$Content,

        [hashtable]$Headers = @{}
    )

    return [pscustomobject]@{
        StatusCode = $StatusCode
        Body = $Body
        Content = $Content
        Headers = $Headers
    }
}

function ConvertTo-ZipBytes {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Entries
    )

    $memory = [System.IO.MemoryStream]::new()
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $memory,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($entryName in $Entries.Keys) {
                $entry = $archive.CreateEntry($entryName)
                $stream = $entry.Open()
                try {
                    $writer = [System.IO.StreamWriter]::new(
                        $stream,
                        [System.Text.UTF8Encoding]::new($false),
                        1024,
                        $true)
                    try {
                        $writer.Write([string]$Entries[$entryName])
                        $writer.Flush()
                    }
                    finally {
                        $writer.Dispose()
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function New-TestXml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,

        [Parameter(Mandatory = $true)]
        [string]$TestFullName,

        [switch]$Failed
    )

    $rootResult = if ($Failed) { "Failed" } else { "Passed" }
    $rootPassed = if ($Failed) { 1 } else { 2 }
    $rootFailed = if ($Failed) { 1 } else { 0 }
    $assemblyPassed = if ($Failed) { 0 } else { 1 }
    $assemblyFailed = if ($Failed) { 1 } else { 0 }

    return @"
<test-run result="$rootResult" total="2" passed="$rootPassed" failed="$rootFailed" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="$AssemblyName" result="$rootResult"
              total="1" passed="$assemblyPassed" failed="$assemblyFailed" skipped="0" inconclusive="0">
    <test-case fullname="$TestFullName" result="$rootResult" />
  </test-suite>
  <test-suite type="Assembly" name="Unrelated.Tests.dll" result="Passed"
              total="1" passed="1" failed="0" skipped="0" inconclusive="0">
    <test-case fullname="Unrelated.Test" result="Passed" />
  </test-suite>
</test-run>
"@
}

function New-ValidTarget {
    param(
        [string]$UnityVersion = "2022.3.40f1"
    )

    return [ordered]@{
        buildtargetid = "tests"
        enabled = $true
        platform = "standalonewindows64"
        settings = [ordered]@{
            autoBuild = $false
            autoDetectUnityVersion = $false
            unityVersion = $UnityVersion
            machineTypeLabel = "win_micro_v1"
            advanced = [ordered]@{
                unity = [ordered]@{
                    runUnitTests = $true
                    runEditModeTests = $true
                    runPlayModeTests = $true
                    failedUnitTestFailsBuild = $true
                    playerExporter = [ordered]@{
                        export = $false
                    }
                }
            }
        }
    }
}

function New-UbaScenario {
    param(
        [ValidateSet(
            "success",
            "failure",
            "canceled",
            "unknown",
            "timeout")]
        [string]$TerminalStatus = "success",

        [scriptblock]$MutateTarget,

        [ValidateSet(0, 401, 403)]
        [int]$TargetHttpFailure = 0,

        [ValidateRange(0, 3)]
        [int]$RateLimitCount = 0,

        [string]$UnityVersion = "2022.3.40f1",

        [switch]$OmitTriggerRequestedRevision,

        [switch]$WrongTriggerRequestedRevision,

        [ValidateRange(0, 2)]
        [int]$TriggerBuildCount = 1,

        [switch]$TriggerError,

        [switch]$MissingTriggerBuildNumber,

        [ValidateSet(
            "none",
            "empty_both",
            "partial_editmode",
            "missing_editmode",
            "missing_playmode",
            "passed_zero_editmode",
            "passed_zero_playmode",
            "failed_editmode",
            "failed_playmode")]
        [string]$TestSummaryMutation = "none",

        [switch]$WrongRevision,

        [switch]$MissingFinalRevision,

        [switch]$MissingPlayModeArtifact,

        [switch]$FailedEditModeArtifact,

        [switch]$DuplicateEditModeAssembly
    )

    $commitSha = "0123456789abcdef0123456789abcdef01234567"
    $target = New-ValidTarget -UnityVersion $UnityVersion
    if ($null -ne $MutateTarget) {
        & $MutateTarget $target
    }

    $editXml = New-TestXml `
        -AssemblyName "BuildSoft.MotionTakeStudio.Editor.Tests.dll" `
        -TestFullName "BuildSoft.MotionTakeStudio.Editor.Tests.Example.Passes" `
        -Failed:$FailedEditModeArtifact
    if ($DuplicateEditModeAssembly) {
        $editXml = $editXml.Replace(
            "</test-run>",
            @"
  <test-suite type="Assembly" name="BuildSoft.MotionTakeStudio.Editor.Tests.dll" result="Passed"
              total="1" passed="1" failed="0" skipped="0" inconclusive="0">
    <test-case fullname="BuildSoft.MotionTakeStudio.Editor.Tests.Duplicate.Passes" result="Passed" />
  </test-suite>
</test-run>
"@)
    }

    $playXml = New-TestXml `
        -AssemblyName "BuildSoft.MotionTakeStudio.PlayMode.Tests.dll" `
        -TestFullName "BuildSoft.MotionTakeStudio.PlayMode.Tests.Example.Passes"
    $playZip = ConvertTo-ZipBytes -Entries @{
        "nested/playmode-test-results.xml" = $playXml
        "nested/readme.txt" = "not a test result"
    }

    $state = [pscustomobject]@{
        Requests = [System.Collections.Generic.List[object]]::new()
        PollCount = 0
        DeleteCount = 0
        TargetCount = 0
        RateLimitRemaining = $RateLimitCount
        UnityVersion = $UnityVersion
        OmitTriggerRequestedRevision = [bool]$OmitTriggerRequestedRevision
        WrongTriggerRequestedRevision = [bool]$WrongTriggerRequestedRevision
        TriggerBuildCount = $TriggerBuildCount
        TriggerError = [bool]$TriggerError
        MissingTriggerBuildNumber = [bool]$MissingTriggerBuildNumber
        TestSummaryMutation = $TestSummaryMutation
        CommitSha = $commitSha
        Target = $target
        TargetHttpFailure = $TargetHttpFailure
        TerminalStatus = $TerminalStatus
        WrongRevision = [bool]$WrongRevision
        MissingFinalRevision = [bool]$MissingFinalRevision
        MissingPlayModeArtifact = [bool]$MissingPlayModeArtifact
        EditXmlBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($editXml)
        PlayZipBytes = $playZip
        EditSignedUri = "https://signed.invalid/editmode-test-results.xml?X-Goog-Signature=DO-NOT-LEAK"
        PlaySignedUri = "https://signed.invalid/playmode-test-results.zip?sig=DO-NOT-LEAK"
        KeyId = "key-id-DO-NOT-LEAK"
        SecretKey = "secret-key-DO-NOT-LEAK"
    }

    $responseFactory = ${function:New-MockResponse}
    $handler = {
        param($Request)

        $state.Requests.Add($Request)
        $uri = [uri]$Request.Uri
        $path = $uri.AbsolutePath
        $targetPath = "/v2/orgs/org-1/projects/project-1/buildtargets/tests"
        $buildsPath = "$targetPath/builds"

        if ($Request.Method -eq "GET" -and $path -eq $targetPath) {
            $state.TargetCount++
            if ($state.TargetHttpFailure -ne 0) {
                return & $responseFactory `
                    -StatusCode $state.TargetHttpFailure `
                    -Body ("credential=" + $state.SecretKey +
                        " https://signed.invalid/leak?sig=DO-NOT-LEAK")
            }

            if ($state.RateLimitRemaining -gt 0) {
                $state.RateLimitRemaining--
                return & $responseFactory `
                    -StatusCode 429 `
                    -Body '{"detail":"rate limited"}' `
                    -Headers @{ "Retry-After" = "0" }
            }

            return & $responseFactory `
                -StatusCode 200 `
                -Body ($state.Target | ConvertTo-Json -Depth 20 -Compress)
        }

        if ($Request.Method -eq "POST" -and $path -eq $buildsPath) {
            $body = $Request.Body | ConvertFrom-Json
            if ($body.commit -ne $state.CommitSha -or $body.branch -ne "main") {
                return & $responseFactory -StatusCode 400 -Body '{"detail":"wrong revision"}'
            }

            $responses = @()
            for ($index = 0; $index -lt $state.TriggerBuildCount; $index++) {
                $trigger = [ordered]@{
                    buildtargetid = "tests"
                    buildStatus = "queued"
                    platform = "standalonewindows64"
                }
                if (-not $state.MissingTriggerBuildNumber) {
                    $trigger.build = 17 + $index
                }
                if (-not $state.OmitTriggerRequestedRevision) {
                    $trigger.requestedRevision = if ($state.WrongTriggerRequestedRevision) {
                        "ffffffffffffffffffffffffffffffffffffffff"
                    }
                    else {
                        $state.CommitSha
                    }
                }
                if ($state.TriggerError) {
                    $trigger.error = "fixture rejected build"
                }
                $responses += [pscustomobject]$trigger
            }
            $responseBody = if ($responses.Count -eq 0) {
                "[]"
            }
            else {
                $responses | ConvertTo-Json -Depth 10 -Compress -AsArray
            }
            return & $responseFactory -StatusCode 202 -Body $responseBody
        }

        if ($path -eq "$buildsPath/17" -and $Request.Method -eq "DELETE") {
            $state.DeleteCount++
            return & $responseFactory -StatusCode 204 -Body ""
        }

        if ($path -eq "$buildsPath/17" -and $Request.Method -eq "GET") {
            $state.PollCount++
            $status = if ($state.TerminalStatus -eq "timeout") {
                "queued"
            }
            elseif ($state.PollCount -eq 1) {
                "assignedToBuilder"
            }
            else {
                $state.TerminalStatus
            }

            $revision = if ($state.MissingFinalRevision) {
                $null
            }
            elseif ($state.WrongRevision) {
                "ffffffffffffffffffffffffffffffffffffffff"
            }
            else {
                $state.CommitSha
            }
            $testResults = [ordered]@{
                unit_test_editmode = [ordered]@{ passed = 95; failed = 0; duration = 1.0 }
                unit_test_playmode = [ordered]@{ passed = 2; failed = 0; duration = 1.0 }
            }
            switch ($state.TestSummaryMutation) {
                "empty_both" {
                    $testResults.unit_test_editmode = [ordered]@{}
                    $testResults.unit_test_playmode = [ordered]@{}
                }
                "partial_editmode" {
                    $testResults.unit_test_editmode = [ordered]@{ passed = 95 }
                }
                "missing_editmode" { [void]$testResults.Remove("unit_test_editmode") }
                "missing_playmode" { [void]$testResults.Remove("unit_test_playmode") }
                "passed_zero_editmode" { $testResults.unit_test_editmode.passed = 0 }
                "passed_zero_playmode" { $testResults.unit_test_playmode.passed = 0 }
                "failed_editmode" { $testResults.unit_test_editmode.failed = 1 }
                "failed_playmode" { $testResults.unit_test_playmode.failed = 1 }
            }
            return & $responseFactory -StatusCode 200 -Body ([ordered]@{
                build = 17
                buildtargetid = "tests"
                buildStatus = $status
                platform = "standalonewindows64"
                requestedRevision = $state.CommitSha
                lastBuiltRevision = $revision
                scmBranch = "main"
                unityVersion = $state.UnityVersion
                testResults = $testResults
            } | ConvertTo-Json -Depth 20 -Compress)
        }

        if ($path -eq "$buildsPath/17/failures" -and $Request.Method -eq "GET") {
            return & $responseFactory -StatusCode 200 -Body ([ordered]@{
                failures = @([ordered]@{
                    displayName = "Build failed"
                    publicMessage = "redacted diagnostic"
                    logline = "credential=" + $state.SecretKey + " " + $state.EditSignedUri
                })
            } | ConvertTo-Json -Depth 10 -Compress)
        }

        if ($path -eq "$buildsPath/17/log" -and $Request.Method -eq "GET") {
            return & $responseFactory -StatusCode 200 -Body (
                "log contained " + $state.KeyId + " " + $state.SecretKey +
                " Authorization: Basic Zm9vOmJhcg== " + $state.EditSignedUri)
        }

        if ($path -eq "$buildsPath/17/artifacts" -and $Request.Method -eq "GET") {
            $files = [System.Collections.Generic.List[object]]::new()
            $files.Add([ordered]@{
                filename = "editmode-test-results.xml"
                href = $state.EditSignedUri
                redirect = $false
            })
            if (-not $state.MissingPlayModeArtifact) {
                $files.Add([ordered]@{
                    filename = "playmode-test-results.zip"
                    href = "/v2/downloads/playmode-test-results.zip"
                    redirect = $true
                })
            }

            return & $responseFactory -StatusCode 200 -Body (@(
                [ordered]@{
                    key = "unit-tests"
                    name = "Unit test results"
                    primary = $false
                    show_download = $true
                    files = @($files)
                }
            ) | ConvertTo-Json -Depth 20 -Compress -AsArray)
        }

        if ($path -eq "/v2/downloads/playmode-test-results.zip" -and
            $Request.Method -eq "GET") {
            return & $responseFactory `
                -StatusCode 303 `
                -Body (@{ url = $state.PlaySignedUri } | ConvertTo-Json -Compress)
        }

        if ($Request.Uri -eq $state.EditSignedUri -and $Request.Method -eq "GET") {
            return & $responseFactory -StatusCode 200 -Body $null -Content $state.EditXmlBytes
        }

        if ($Request.Uri -eq $state.PlaySignedUri -and $Request.Method -eq "GET") {
            return & $responseFactory -StatusCode 200 -Body $null -Content $state.PlayZipBytes
        }

        return & $responseFactory -StatusCode 404 -Body '{"detail":"fixture route missing"}'
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
$clientPath = Join-Path $repositoryRoot ".github/scripts/Invoke-UnityBuildAutomation.ps1"
if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "UBA v2 client is missing: $clientPath"
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "MotionTakeStudio-UbaClientTests-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

$keyId = "key-id-DO-NOT-LEAK"
$secretKey = "secret-key-DO-NOT-LEAK"
$commitSha = "0123456789abcdef0123456789abcdef01234567"

function Invoke-TestClient {
    param(
        [Parameter(Mandatory = $true)]
        $Scenario,

        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory,

        [System.Collections.Generic.List[int]]$Sleeps,

        [int]$TimeoutSeconds = 4
    )

    if ($null -eq $Sleeps) {
        $Sleeps = [System.Collections.Generic.List[int]]::new()
    }
    $sleepAction = {
        param([int]$Seconds)
        $Sleeps.Add($Seconds)
    }.GetNewClosure()

    $previousKeyId = $env:UNITY_UBA_KEY_ID
    $previousSecretKey = $env:UNITY_UBA_SECRET_KEY
    try {
        $env:UNITY_UBA_KEY_ID = $keyId
        $env:UNITY_UBA_SECRET_KEY = $secretKey
        return & $clientPath `
            -OrganizationId "org-1" `
            -ProjectId "project-1" `
            -BuildTargetId "tests" `
            -CommitSha $commitSha `
            -Branch "main" `
            -ExpectedUnityVersion "2022.3.40f1" `
            -OutputDirectory $OutputDirectory `
            -PollIntervalSeconds 1 `
            -TimeoutSeconds $TimeoutSeconds `
            -RequestInvoker $Scenario.Handler `
            -SleepAction $sleepAction
    }
    finally {
        $env:UNITY_UBA_KEY_ID = $previousKeyId
        $env:UNITY_UBA_SECRET_KEY = $previousSecretKey
    }
}

try {
    $successDirectory = Join-Path $fixtureRoot "success"
    $success = New-UbaScenario
    $mockResponseFunction = ${function:New-MockResponse}
    Remove-Item -LiteralPath Function:\New-MockResponse
    try {
        $result = Invoke-TestClient -Scenario $success -OutputDirectory $successDirectory
    }
    finally {
        Set-Item -LiteralPath Function:\New-MockResponse -Value $mockResponseFunction
    }
    Assert-Equal $result.BuildStatus "success" "successful UBA status"
    Assert-Equal $result.LastBuiltRevision $commitSha "exact built revision"
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $successDirectory "editmode-results.xml")) `
        "canonical EditMode NUnit XML exists"
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $successDirectory "playmode-results.xml")) `
        "canonical PlayMode NUnit XML exists"

    foreach ($mode in @(
        @{ File = "editmode-results.xml"; Assembly = "BuildSoft.MotionTakeStudio.Editor.Tests.dll" },
        @{ File = "playmode-results.xml"; Assembly = "BuildSoft.MotionTakeStudio.PlayMode.Tests.dll" }
    )) {
        [xml]$xml = Get-Content -LiteralPath (Join-Path $successDirectory $mode.File) -Raw
        Assert-Equal $xml.DocumentElement.Name "test-run" "$($mode.File) root"
        $assemblies = @($xml.SelectNodes("//test-suite[@type='Assembly']"))
        Assert-Equal $assemblies.Count 1 "$($mode.File) contains one Assembly suite"
        Assert-Equal $assemblies[0].GetAttribute("name") $mode.Assembly `
            "$($mode.File) contains the expected assembly"
        Assert-Equal $xml.DocumentElement.GetAttribute("total") "1" `
            "$($mode.File) root counters come from its assembly"
    }

    $emptySummaryDirectory = Join-Path $fixtureRoot "empty-summary-artifact-fallback"
    $emptySummary = New-UbaScenario -TestSummaryMutation empty_both
    $emptySummaryResult = Invoke-TestClient `
        -Scenario $emptySummary `
        -OutputDirectory $emptySummaryDirectory
    Assert-Equal $emptySummaryResult.BuildStatus "success" `
        "empty UBA summaries fall back to authoritative NUnit XML"
    foreach ($filename in @("editmode-results.xml", "playmode-results.xml")) {
        Assert-True `
            (Test-Path -LiteralPath (Join-Path $emptySummaryDirectory $filename)) `
            "empty UBA summaries still produce $filename"
    }

    $failedArtifact = New-UbaScenario `
        -TestSummaryMutation empty_both `
        -FailedEditModeArtifact
    $null = Assert-Throws `
        -Description "failed EditMode artifact behind an empty UBA summary" `
        -ExpectedMessagePattern "EditMode.*artifact.*failed" `
        -Action {
            Invoke-TestClient `
                -Scenario $failedArtifact `
                -OutputDirectory (Join-Path $fixtureRoot "empty-summary-failed-artifact")
        }

    $postRequest = $success.State.Requests |
        Where-Object { $_.Method -eq "POST" -and $_.Uri -match "/builds$" } |
        Select-Object -First 1
    $posted = $postRequest.Body | ConvertFrom-Json
    Assert-Equal $posted.commit $commitSha "trigger request pins the exact SHA"
    Assert-Equal $posted.branch "main" "trigger request pins the protected branch"
    Assert-Equal $posted.clean $false "trigger request preserves cache by default"
    Assert-Equal $posted.delay 0 "trigger request has no delay"

    $missingRequestedRevision = New-UbaScenario -OmitTriggerRequestedRevision
    $missingRequestedResult = Invoke-TestClient `
        -Scenario $missingRequestedRevision `
        -OutputDirectory (Join-Path $fixtureRoot "missing-trigger-requested-revision")
    Assert-Equal $missingRequestedResult.LastBuiltRevision $commitSha `
        "optional trigger requestedRevision is not needed when the final SHA matches"

    $underscoreVersion = New-UbaScenario -UnityVersion "2022_3_40f1"
    $underscoreResult = Invoke-TestClient `
        -Scenario $underscoreVersion `
        -OutputDirectory (Join-Path $fixtureRoot "underscore-unity-version")
    Assert-Equal $underscoreResult.BuildStatus "success" `
        "UBA underscore and Unity dotted version forms are equivalent"

    $expectedAuth = "Basic " + [Convert]::ToBase64String(
        [System.Text.Encoding]::UTF8.GetBytes("${keyId}:${secretKey}"))
    $authenticatedRequests = @($success.State.Requests |
        Where-Object { ([uri]$_.Uri).Host -eq "build-automation.services.api.unity.com" })
    Assert-True ($authenticatedRequests.Count -gt 0) "UBA API requests were made"
    foreach ($request in $authenticatedRequests) {
        Assert-Equal $request.Headers.Authorization $expectedAuth `
            "UBA API uses service-account HTTP Basic authentication"
    }
    $signedRequests = @($success.State.Requests |
        Where-Object { ([uri]$_.Uri).Host -eq "signed.invalid" })
    Assert-Equal $signedRequests.Count 2 "both signed test artifacts were downloaded"
    foreach ($request in $signedRequests) {
        Assert-True (-not $request.Headers.ContainsKey("Authorization")) `
            "signed storage requests never receive Unity credentials"
    }

    $allPersistedText = Get-ChildItem -LiteralPath $successDirectory -File -Recurse |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    $persisted = $allPersistedText -join "`n"
    foreach ($forbidden in @($keyId, $secretKey, "DO-NOT-LEAK", "signed.invalid")) {
        Assert-True (-not $persisted.Contains($forbidden, [StringComparison]::Ordinal)) `
            "artifacts do not persist '$forbidden'"
    }

    $preflightCases = @(
        @{ Name = "disabled target"; Pattern = "enabled"; Mutation = { param($t) $t.enabled = $false } },
        @{ Name = "auto-build target"; Pattern = "autoBuild"; Mutation = { param($t) $t.settings.autoBuild = $true } },
        @{ Name = "Unity auto-detect target"; Pattern = "autoDetectUnityVersion"; Mutation = { param($t) $t.settings.autoDetectUnityVersion = $true } },
        @{ Name = "wrong platform target"; Pattern = "platform"; Mutation = { param($t) $t.platform = "standalonelinux64" } },
        @{ Name = "wrong machine target"; Pattern = "machineTypeLabel"; Mutation = { param($t) $t.settings.machineTypeLabel = "win_standard_v1" } },
        @{ Name = "wrong Unity"; Pattern = "Unity version"; Mutation = { param($t) $t.settings.unityVersion = "2022.3.39f1" } },
        @{ Name = "unit tests off"; Pattern = "runUnitTests"; Mutation = { param($t) $t.settings.advanced.unity.runUnitTests = $false } },
        @{ Name = "EditMode off"; Pattern = "runEditModeTests"; Mutation = { param($t) $t.settings.advanced.unity.runEditModeTests = $false } },
        @{ Name = "PlayMode off"; Pattern = "runPlayModeTests"; Mutation = { param($t) $t.settings.advanced.unity.runPlayModeTests = $false } },
        @{ Name = "test failure ignored"; Pattern = "failedUnitTestFailsBuild"; Mutation = { param($t) $t.settings.advanced.unity.failedUnitTestFailsBuild = $false } },
        @{ Name = "player export enabled"; Pattern = "playerExporter.export"; Mutation = { param($t) $t.settings.advanced.unity.playerExporter.export = $true } }
    )
    foreach ($case in $preflightCases) {
        $scenario = New-UbaScenario -MutateTarget $case.Mutation
        $caseDirectory = Join-Path $fixtureRoot ("preflight-" +
            ($case.Name -replace "[^a-zA-Z0-9]", "-"))
        $null = Assert-Throws `
            -Description $case.Name `
            -ExpectedMessagePattern $case.Pattern `
            -Action { Invoke-TestClient -Scenario $scenario -OutputDirectory $caseDirectory }
        Assert-Equal `
            (@($scenario.State.Requests | Where-Object { $_.Method -eq "POST" }).Count) `
            0 `
            "$($case.Name) fails before triggering a build"
    }

    foreach ($triggerCase in @(
        @{ Name = "empty trigger response"; Pattern = "exactly one"; Parameters = @{ TriggerBuildCount = 0 } },
        @{ Name = "multiple trigger response"; Pattern = "exactly one"; Parameters = @{ TriggerBuildCount = 2 } },
        @{ Name = "trigger error"; Pattern = "rejected"; Parameters = @{ TriggerError = $true } },
        @{ Name = "missing trigger build"; Pattern = "build number"; Parameters = @{ MissingTriggerBuildNumber = $true } },
        @{ Name = "mismatched trigger revision"; Pattern = "requested revision"; Parameters = @{ WrongTriggerRequestedRevision = $true } }
    )) {
        $triggerParameters = $triggerCase.Parameters
        $scenario = New-UbaScenario @triggerParameters
        $null = Assert-Throws `
            -Description $triggerCase.Name `
            -ExpectedMessagePattern $triggerCase.Pattern `
            -Action {
                Invoke-TestClient `
                    -Scenario $scenario `
                    -OutputDirectory (Join-Path $fixtureRoot ("trigger-" +
                        ($triggerCase.Name -replace "[^a-zA-Z0-9]", "-")))
            }
        Assert-Equal $scenario.State.PollCount 0 "$($triggerCase.Name) fails before polling"
    }

    foreach ($summaryCase in @(
        @{ Mutation = "partial_editmode"; Pattern = "unit_test_editmode" },
        @{ Mutation = "missing_editmode"; Pattern = "unit_test_editmode" },
        @{ Mutation = "missing_playmode"; Pattern = "unit_test_playmode" },
        @{ Mutation = "passed_zero_editmode"; Pattern = "unit_test_editmode" },
        @{ Mutation = "passed_zero_playmode"; Pattern = "unit_test_playmode" },
        @{ Mutation = "failed_editmode"; Pattern = "unit_test_editmode" },
        @{ Mutation = "failed_playmode"; Pattern = "unit_test_playmode" }
    )) {
        $scenario = New-UbaScenario -TestSummaryMutation $summaryCase.Mutation
        $null = Assert-Throws `
            -Description "invalid $($summaryCase.Mutation) test summary" `
            -ExpectedMessagePattern $summaryCase.Pattern `
            -Action {
                Invoke-TestClient `
                    -Scenario $scenario `
                    -OutputDirectory (Join-Path $fixtureRoot ("summary-" + $summaryCase.Mutation))
            }
    }

    foreach ($terminalState in @("failure", "canceled", "unknown")) {
        $scenario = New-UbaScenario -TerminalStatus $terminalState
        $caseDirectory = Join-Path $fixtureRoot "terminal-$terminalState"
        $message = Assert-Throws `
            -Description "$terminalState terminal state" `
            -ExpectedMessagePattern $terminalState `
            -Action { Invoke-TestClient -Scenario $scenario -OutputDirectory $caseDirectory }
        Assert-True (-not $message.Contains($secretKey, [StringComparison]::Ordinal)) `
            "$terminalState error does not leak credentials"
        $terminalFiles = @(Get-ChildItem -LiteralPath $caseDirectory -File -Recurse -ErrorAction SilentlyContinue)
        $terminalText = ($terminalFiles | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw
        }) -join "`n"
        foreach ($forbidden in @($keyId, $secretKey, "DO-NOT-LEAK", "signed.invalid")) {
            Assert-True (-not $terminalText.Contains($forbidden, [StringComparison]::Ordinal)) `
                "$terminalState diagnostic artifacts do not leak '$forbidden'"
        }
    }

    $timeout = New-UbaScenario -TerminalStatus timeout
    $null = Assert-Throws `
        -Description "poll timeout" `
        -ExpectedMessagePattern "timed out" `
        -Action {
            Invoke-TestClient `
                -Scenario $timeout `
                -OutputDirectory (Join-Path $fixtureRoot "timeout") `
                -TimeoutSeconds 2
        }
    Assert-Equal $timeout.State.DeleteCount 1 "timeout cancels the exact UBA build"

    foreach ($statusCode in @(401, 403)) {
        $authFailure = New-UbaScenario -TargetHttpFailure $statusCode
        $authMessage = Assert-Throws `
            -Description "HTTP $statusCode" `
            -ExpectedMessagePattern "$statusCode" `
            -Action {
                Invoke-TestClient `
                    -Scenario $authFailure `
                    -OutputDirectory (Join-Path $fixtureRoot "http-$statusCode")
            }
        Assert-Equal $authFailure.State.TargetCount 1 "HTTP $statusCode is not retried"
        foreach ($forbidden in @($keyId, $secretKey, "signed.invalid", "DO-NOT-LEAK")) {
            Assert-True (-not $authMessage.Contains($forbidden, [StringComparison]::Ordinal)) `
                "HTTP $statusCode error does not leak '$forbidden'"
        }
    }

    $retrySleeps = [System.Collections.Generic.List[int]]::new()
    $rateLimited = New-UbaScenario -RateLimitCount 2
    $null = Invoke-TestClient `
        -Scenario $rateLimited `
        -OutputDirectory (Join-Path $fixtureRoot "rate-limit") `
        -Sleeps $retrySleeps
    Assert-Equal $rateLimited.State.TargetCount 3 "HTTP 429 is retried with a bound"
    Assert-True ($retrySleeps.Count -ge 2) "HTTP 429 observes retry delays"

    $wrongRevision = New-UbaScenario -WrongRevision
    $null = Assert-Throws `
        -Description "wrong built revision" `
        -ExpectedMessagePattern "different commit" `
        -Action {
            Invoke-TestClient `
                -Scenario $wrongRevision `
                -OutputDirectory (Join-Path $fixtureRoot "wrong-revision")
        }

    $missingFinalRevision = New-UbaScenario -MissingFinalRevision
    $null = Assert-Throws `
        -Description "missing final built revision" `
        -ExpectedMessagePattern "different commit" `
        -Action {
            Invoke-TestClient `
                -Scenario $missingFinalRevision `
                -OutputDirectory (Join-Path $fixtureRoot "missing-final-revision")
        }

    $missingArtifact = New-UbaScenario -MissingPlayModeArtifact
    $null = Assert-Throws `
        -Description "missing PlayMode artifact" `
        -ExpectedMessagePattern "PlayMode.*missing" `
        -Action {
            Invoke-TestClient `
                -Scenario $missingArtifact `
                -OutputDirectory (Join-Path $fixtureRoot "missing-artifact")
        }

    $duplicateAssembly = New-UbaScenario -DuplicateEditModeAssembly
    $null = Assert-Throws `
        -Description "duplicate EditMode assembly" `
        -ExpectedMessagePattern "duplicate.*EditMode" `
        -Action {
            Invoke-TestClient `
                -Scenario $duplicateAssembly `
                -OutputDirectory (Join-Path $fixtureRoot "duplicate-assembly")
        }

    Write-Host "Unity Build Automation v2 client tests passed."
}
finally {
    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedFixtureRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedFixtureRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
