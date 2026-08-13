#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OrganizationId,
    [Parameter(Mandatory = $true)][string]$ProjectId,
    [Parameter(Mandatory = $true)][string]$BuildTargetId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$Branch,
    [Parameter()][string]$ExpectedUnityVersion = $env:UNITY_VERSION,
    [Parameter()][string]$OutputDirectory = "artifacts",
    [Parameter()][uri]$BaseUri = "https://build-automation.services.api.unity.com/v2",
    [Parameter()][ValidateRange(1, 300)][int]$PollIntervalSeconds = 15,
    [Parameter()][ValidateRange(1, 21600)][int]$TimeoutSeconds = 5400,
    [Parameter()][scriptblock]$RequestInvoker,
    [Parameter()][scriptblock]$SleepAction = { param([int]$Seconds) Start-Sleep -Seconds $Seconds }
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ObjectValue {
    param([AllowNull()]$InputObject, [Parameter(Mandatory = $true)][string[]]$Path)
    $value = $InputObject
    foreach ($segment in $Path) {
        if ($null -eq $value) { return $null }
        $property = $value.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $value = $property.Value
    }
    return $value
}

function ConvertFrom-JsonBody {
    param($Response, [string]$Operation)
    if ([string]::IsNullOrWhiteSpace([string]$Response.Body)) {
        throw "UBA $Operation returned an empty response."
    }
    try { return $Response.Body | ConvertFrom-Json -Depth 100 }
    catch { throw "UBA $Operation returned invalid JSON." }
}

function ConvertTo-NormalizedUnityVersion {
    param([AllowNull()][string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) { return "" }
    return $Version.Trim().Replace('_', '.')
}

function New-DefaultRequestInvoker {
    return {
        param($Request)
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $client = [System.Net.Http.HttpClient]::new($handler)
        try {
            $message = [System.Net.Http.HttpRequestMessage]::new(
                [System.Net.Http.HttpMethod]::new($Request.Method), [uri]$Request.Uri)
            try {
                foreach ($name in $Request.Headers.Keys) {
                    [void]$message.Headers.TryAddWithoutValidation($name, [string]$Request.Headers[$name])
                }
                if ($null -ne $Request.Body) {
                    $message.Content = [System.Net.Http.StringContent]::new(
                        [string]$Request.Body, [System.Text.Encoding]::UTF8, "application/json")
                }
                $response = $client.Send($message)
                try {
                    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                    $headers = @{}
                    foreach ($header in $response.Headers) { $headers[$header.Key] = ($header.Value -join ",") }
                    foreach ($header in $response.Content.Headers) { $headers[$header.Key] = ($header.Value -join ",") }
                    return [pscustomobject]@{
                        StatusCode = [int]$response.StatusCode
                        Body = [System.Text.Encoding]::UTF8.GetString($bytes)
                        Content = $bytes
                        Headers = $headers
                    }
                }
                finally { $response.Dispose() }
            }
            finally { $message.Dispose() }
        }
        finally { $client.Dispose(); $handler.Dispose() }
    }
}

$keyId = $env:UNITY_UBA_KEY_ID
$secretKey = $env:UNITY_UBA_SECRET_KEY
if ([string]::IsNullOrWhiteSpace($keyId) -or [string]::IsNullOrWhiteSpace($secretKey)) {
    throw "UNITY_UBA_KEY_ID and UNITY_UBA_SECRET_KEY must be provided through the environment."
}
if ([string]::IsNullOrWhiteSpace($ExpectedUnityVersion)) {
    throw "Expected Unity version is required."
}
if ($null -eq $RequestInvoker) { $RequestInvoker = New-DefaultRequestInvoker }

$base = $BaseUri.AbsoluteUri.TrimEnd('/')
$apiHost = $BaseUri.Host
$basic = "Basic " + [Convert]::ToBase64String(
    [System.Text.Encoding]::UTF8.GetBytes("${keyId}:${secretKey}"))

function Invoke-Http {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [AllowNull()][string]$Body,
        [switch]$AllowRedirectResponse,
        [switch]$Binary
    )
    for ($attempt = 0; $attempt -le 3; $attempt++) {
        $headers = @{}
        if (([uri]$Uri).Host -eq $apiHost) { $headers.Authorization = $basic }
        $request = [pscustomobject]@{ Method = $Method; Uri = $Uri; Headers = $headers; Body = $Body }
        try { $response = & $RequestInvoker $request }
        catch { throw "UBA request transport failed." }
        if ($null -eq $response) { throw "UBA request returned no response." }
        $status = [int]$response.StatusCode
        if ($status -eq 429 -and $attempt -lt 3) {
            $retryAfter = 0
            if ($null -ne $response.Headers -and $response.Headers.ContainsKey("Retry-After")) {
                [void][int]::TryParse([string]$response.Headers["Retry-After"], [ref]$retryAfter)
            }
            if ($retryAfter -le 0) { $retryAfter = [Math]::Min(8, [Math]::Pow(2, $attempt)) }
            & $SleepAction ([int]$retryAfter)
            continue
        }
        if ($status -eq 401 -or $status -eq 403) { throw "UBA request was rejected with HTTP $status." }
        if ($AllowRedirectResponse -and $status -eq 303) { return $response }
        if ($status -lt 200 -or $status -ge 300) { throw "UBA request failed with HTTP $status." }
        return $response
    }
    throw "UBA request exceeded the retry limit."
}

function Write-SafeJson {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)][string]$Path)
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Save-CanonicalResult {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlElement]$SourceRoot,
        [Parameter(Mandatory = $true)][System.Xml.XmlElement]$Assembly,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $document = [System.Xml.XmlDocument]::new()
    $root = $document.CreateElement("test-run")
    [void]$document.AppendChild($root)
    foreach ($attribute in $SourceRoot.Attributes) { $root.SetAttribute($attribute.Name, $attribute.Value) }
    foreach ($name in @("result", "total", "passed", "failed", "skipped", "inconclusive", "duration")) {
        if ($Assembly.HasAttribute($name)) { $root.SetAttribute($name, $Assembly.GetAttribute($name)) }
    }
    [void]$root.AppendChild($document.ImportNode($Assembly, $true))
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
}

function Assert-PassingTestAssembly {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlElement]$Assembly,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    if ($Assembly.GetAttribute("result") -ne "Passed") {
        throw "UBA $Mode test artifact failed."
    }

    $counts = @{}
    foreach ($name in @("total", "passed", "failed", "skipped", "inconclusive")) {
        [int64]$value = 0
        $text = $Assembly.GetAttribute($name)
        if ([string]::IsNullOrWhiteSpace($text) -or
            -not [int64]::TryParse($text, [ref]$value) -or
            $value -lt 0) {
            throw "UBA $Mode test artifact has an invalid $name count."
        }
        $counts[$name] = $value
    }

    if ($counts.total -le 0 -or
        $counts.passed -le 0 -or
        $counts.failed -ne 0 -or
        $counts.skipped -ne 0 -or
        $counts.inconclusive -ne 0 -or
        $counts.total -ne $counts.passed) {
        throw "UBA $Mode test artifact failed."
    }
}

function Get-DownloadBytes {
    param([Parameter(Mandatory = $true)]$File)
    $href = [string](Get-ObjectValue $File @("href"))
    if ([string]::IsNullOrWhiteSpace($href)) { throw "UBA test artifact has no download href." }
    $uri = if ([uri]::IsWellFormedUriString($href, [UriKind]::Absolute)) {
        $href
    } else {
        ([uri]::new($BaseUri, $href)).AbsoluteUri
    }
    $response = Invoke-Http -Method GET -Uri $uri -AllowRedirectResponse -Binary
    if ([int]$response.StatusCode -eq 303) {
        $redirect = ConvertFrom-JsonBody $response "artifact redirect"
        $signedUri = if ($redirect -is [string]) { $redirect } else { [string](Get-ObjectValue $redirect @("url")) }
        if ([string]::IsNullOrWhiteSpace($signedUri) -or
            -not [uri]::IsWellFormedUriString($signedUri, [UriKind]::Absolute)) {
            throw "UBA artifact redirect did not contain a valid URL."
        }
        $response = Invoke-Http -Method GET -Uri $signedUri -Binary
    }
    if ($null -ne $response.Content -and $response.Content.Count -gt 0) { return [byte[]]$response.Content }
    return [System.Text.Encoding]::UTF8.GetBytes([string]$response.Body)
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$targetPath = "$base/orgs/$([uri]::EscapeDataString($OrganizationId))/projects/$([uri]::EscapeDataString($ProjectId))/buildtargets/$([uri]::EscapeDataString($BuildTargetId))"

$target = ConvertFrom-JsonBody (Invoke-Http GET $targetPath) "target preflight"
$unity = Get-ObjectValue $target @("settings", "advanced", "unity")
$targetUnityVersion = ConvertTo-NormalizedUnityVersion `
    ([string](Get-ObjectValue $target @("settings", "unityVersion")))
$normalizedExpectedUnityVersion = ConvertTo-NormalizedUnityVersion $ExpectedUnityVersion
$checks = [ordered]@{
    "enabled" = ((Get-ObjectValue $target @("enabled")) -eq $true)
    "autoBuild" = ((Get-ObjectValue $target @("settings", "autoBuild")) -eq $false)
    "autoDetectUnityVersion" = ((Get-ObjectValue $target @("settings", "autoDetectUnityVersion")) -eq $false)
    "platform" = ([string](Get-ObjectValue $target @("platform")) -eq "standalonewindows64")
    "machineTypeLabel" = ([string](Get-ObjectValue $target @("settings", "machineTypeLabel")) -eq "win_micro_v1")
    "Unity version" = ($targetUnityVersion -eq $normalizedExpectedUnityVersion)
    "runUnitTests" = ((Get-ObjectValue $unity @("runUnitTests")) -eq $true)
    "runEditModeTests" = ((Get-ObjectValue $unity @("runEditModeTests")) -eq $true)
    "runPlayModeTests" = ((Get-ObjectValue $unity @("runPlayModeTests")) -eq $true)
    "failedUnitTestFailsBuild" = ((Get-ObjectValue $unity @("failedUnitTestFailsBuild")) -eq $true)
    "playerExporter.export" = ((Get-ObjectValue $unity @("playerExporter", "export")) -eq $false)
}
foreach ($check in $checks.GetEnumerator()) {
    if (-not $check.Value) { throw "UBA target preflight failed: $($check.Key)." }
}

$triggerBody = [ordered]@{ clean = $false; delay = 0; commit = $CommitSha.ToLowerInvariant(); branch = $Branch } |
    ConvertTo-Json -Compress
$start = ConvertFrom-JsonBody (Invoke-Http POST "$targetPath/builds" $triggerBody) "build trigger"
$started = @($start)
if ($started.Count -ne 1) { throw "UBA build trigger must return exactly one build." }
$build = $started[0]
if (-not [string]::IsNullOrWhiteSpace([string](Get-ObjectValue $build @("error")))) {
    throw "UBA build trigger rejected the request."
}
$number = Get-ObjectValue $build @("build")
if ($null -eq $number) { throw "UBA build trigger did not return a build number." }
$triggerRequestedRevision = [string](Get-ObjectValue $build @("requestedRevision"))
if (-not [string]::IsNullOrWhiteSpace($triggerRequestedRevision) -and
    $triggerRequestedRevision -ine $CommitSha) {
    throw "UBA build trigger did not accept the requested revision."
}
$buildPath = "$targetPath/builds/$number"
$maxPolls = [Math]::Max(1, [Math]::Ceiling($TimeoutSeconds / $PollIntervalSeconds))
$final = $null
for ($poll = 0; $poll -lt $maxPolls; $poll++) {
    $pollUri = "$buildPath`?include=testResults%2Clinks.artifacts"
    $current = ConvertFrom-JsonBody (Invoke-Http GET $pollUri) "build status"
    $status = [string](Get-ObjectValue $current @("buildStatus"))
    switch ($status) {
        "success" { $final = $current; break }
        "failure" { $final = $current; break }
        "canceled" { $final = $current; break }
        "unknown" { $final = $current; break }
        { $_ -in @("created", "queued", "assignedToBuilder", "sentToBuilder", "started", "restarted", "processing") } {
            if ($poll + 1 -lt $maxPolls) { & $SleepAction $PollIntervalSeconds }
        }
        default { throw "UBA returned an unsupported build status." }
    }
    if ($null -ne $final) { break }
}
if ($null -eq $final) {
    try { $null = Invoke-Http DELETE $buildPath } catch { }
    throw "UBA build timed out."
}

$status = [string](Get-ObjectValue $final @("buildStatus"))
$safeResult = [ordered]@{
    build = $number
    buildTargetId = $BuildTargetId
    buildStatus = $status
    requestedRevision = [string](Get-ObjectValue $final @("requestedRevision"))
    lastBuiltRevision = [string](Get-ObjectValue $final @("lastBuiltRevision"))
    scmBranch = [string](Get-ObjectValue $final @("scmBranch"))
    unityVersion = [string](Get-ObjectValue $final @("unityVersion"))
    testResults = Get-ObjectValue $final @("testResults")
}
Write-SafeJson $safeResult (Join-Path $output "uba-result.json")
if ($status -ne "success") {
    try {
        $failureData = ConvertFrom-JsonBody (Invoke-Http GET "$buildPath/failures") "failure details"
        $safeFailures = @((Get-ObjectValue $failureData @("failures"))) | ForEach-Object {
            [ordered]@{
                displayName = [string](Get-ObjectValue $_ @("displayName"))
                stage = [string](Get-ObjectValue $_ @("stage"))
                step = [string](Get-ObjectValue $_ @("step"))
                failureIndicatorLabel = [string](Get-ObjectValue $_ @("failureIndicatorLabel"))
            }
        }
        Write-SafeJson @($safeFailures) (Join-Path $output "uba-failures.json")
    } catch { }
    throw "UBA build ended with status '$status'."
}

if ([string](Get-ObjectValue $final @("lastBuiltRevision")) -ine $CommitSha) {
    throw "UBA built a different commit than requested."
}
if ([string](Get-ObjectValue $final @("scmBranch")) -ne $Branch) { throw "UBA built a different branch." }
$actualUnityVersion = [string](Get-ObjectValue $final @("unityVersion"))
if ([string]::IsNullOrWhiteSpace($actualUnityVersion) -or
    (ConvertTo-NormalizedUnityVersion $actualUnityVersion) -ne $normalizedExpectedUnityVersion) {
    throw "UBA built with a different Unity version."
}
foreach ($testMode in @("unit_test_editmode", "unit_test_playmode")) {
    $test = Get-ObjectValue $final @("testResults", $testMode)
    if ($null -eq $test) {
        throw "UBA $testMode result is missing or failed."
    }

    $passedProperty = $test.PSObject.Properties["passed"]
    $failedProperty = $test.PSObject.Properties["failed"]
    if ($null -eq $passedProperty -and
        $null -eq $failedProperty -and
        @($test.PSObject.Properties).Count -eq 0) {
        continue
    }

    [int64]$passed = 0
    [int64]$failed = 0
    if ($null -eq $passedProperty -or
        $null -eq $failedProperty -or
        -not [int64]::TryParse([string]$passedProperty.Value, [ref]$passed) -or
        -not [int64]::TryParse([string]$failedProperty.Value, [ref]$failed) -or
        $passed -le 0 -or
        $failed -ne 0) {
        throw "UBA $testMode result is missing or failed."
    }
}

$artifacts = ConvertFrom-JsonBody (Invoke-Http GET "$buildPath/artifacts") "test artifacts"
$xmlDocuments = [System.Collections.Generic.List[System.Xml.XmlDocument]]::new()
foreach ($artifact in @($artifacts)) {
    foreach ($file in @((Get-ObjectValue $artifact @("files")))) {
        $filename = [string](Get-ObjectValue $file @("filename"))
        if ($filename -notmatch '(?i)(test|result).*\.(xml|zip)$') { continue }
        $bytes = Get-DownloadBytes $file
        if ($filename -match '(?i)\.zip$') {
            $memory = [System.IO.MemoryStream]::new($bytes, $false)
            $archive = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                foreach ($entry in $archive.Entries) {
                    if ($entry.FullName -notmatch '(?i)(test|result).*\.xml$') { continue }
                    $stream = $entry.Open()
                    $reader = [System.IO.StreamReader]::new($stream)
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
                    try { $doc = [System.Xml.XmlDocument]::new(); $doc.LoadXml($text) } catch { continue }
                    if ($doc.DocumentElement.Name -eq "test-run") { $xmlDocuments.Add($doc) }
                }
            } finally { $archive.Dispose(); $memory.Dispose() }
        } else {
            try { $doc = [System.Xml.XmlDocument]::new(); $doc.LoadXml([System.Text.Encoding]::UTF8.GetString($bytes)) }
            catch { continue }
            if ($doc.DocumentElement.Name -eq "test-run") { $xmlDocuments.Add($doc) }
        }
    }
}

foreach ($contract in @(
    @{ Mode = "EditMode"; Assembly = "BuildSoft.MotionTakeStudio.Editor.Tests.dll"; File = "editmode-results.xml" },
    @{ Mode = "PlayMode"; Assembly = "BuildSoft.MotionTakeStudio.PlayMode.Tests.dll"; File = "playmode-results.xml" }
)) {
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($doc in $xmlDocuments) {
        foreach ($node in @($doc.SelectNodes("//test-suite[@type='Assembly' and @name='$($contract.Assembly)']"))) {
            $matches.Add([pscustomobject]@{ Root = $doc.DocumentElement; Assembly = $node })
        }
    }
    if ($matches.Count -eq 0) { throw "UBA $($contract.Mode) test artifact is missing." }
    if ($matches.Count -ne 1) { throw "UBA test artifacts contain a duplicate $($contract.Mode) assembly." }
    Assert-PassingTestAssembly $matches[0].Assembly $contract.Mode
    Save-CanonicalResult $matches[0].Root $matches[0].Assembly (Join-Path $output $contract.File)
}

return [pscustomobject]@{
    Build = $number
    BuildStatus = $status
    LastBuiltRevision = [string](Get-ObjectValue $final @("lastBuiltRevision"))
    OutputDirectory = $output
}
