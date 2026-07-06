<#
.SYNOPSIS
    End-to-end test script for the Face Attendance API.

.DESCRIPTION
    Exercises /api/register, /api/checkin and /api/employees, covering both the
    happy paths and the validation/error responses documented in api.md.

    By default the script generates two synthetic JPEG test images (the API has
    no face detection, so any image flows through the full embed-and-match
    pipeline; checking in with the exact registration image should produce a
    similarity close to 1.0). For a realistic accuracy test, pass paths to real
    face photos of the same person.

.EXAMPLE
    .\test-api.ps1
    Runs the full suite against http://localhost:5002 with generated images.

.EXAMPLE
    .\test-api.ps1 -BaseUrl http://localhost:5002 -RegisterImages front.jpg,left.jpg -CheckInImage today.jpg
    Registers real photos and verifies a real check-in image.

.NOTES
    Requires the API to be running (dotnet run) and PostgreSQL to be up.
    Works on Windows PowerShell 5.1 and PowerShell 7+ (Windows).
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5002",
    [string]$EmployeeCode = ("TEST{0:yyMMddHHmmss}" -f (Get-Date)),
    [string[]]$RegisterImages = @(),
    [string]$CheckInImage = ""
)

$ErrorActionPreference = "Stop"
$script:Passed = 0
$script:Failed = 0

function Write-TestResult {
    param([string]$Name, [bool]$Condition, [string]$Detail = "")
    if ($Condition) {
        $script:Passed++
        Write-Host ("  [PASS] {0}" -f $Name) -ForegroundColor Green
    } else {
        $script:Failed++
        Write-Host ("  [FAIL] {0}" -f $Name) -ForegroundColor Red
    }
    if ($Detail) { Write-Host ("         {0}" -f $Detail) -ForegroundColor DarkGray }
}

# Calls the API and always returns @{ Status = <int>; Body = <parsed json or raw string> },
# including for 4xx/5xx responses.
function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null
    )
    $params = @{
        Method          = $Method
        Uri             = "$BaseUrl$Path"
        UseBasicParsing = $true
        TimeoutSec      = 300
    }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 5)
        $params.ContentType = "application/json"
    }
    try {
        $resp = Invoke-WebRequest @params
        $parsed = $null
        if ($resp.Content) { try { $parsed = $resp.Content | ConvertFrom-Json } catch { $parsed = $resp.Content } }
        return @{ Status = [int]$resp.StatusCode; Body = $parsed }
    } catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw }  # connection refused, DNS failure, timeout...
        $status = [int]$response.StatusCode
        $content = $null
        if ($response -is [System.Net.HttpWebResponse]) {
            # Windows PowerShell 5.1
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            $content = $reader.ReadToEnd()
            $reader.Dispose()
        } elseif ($_.ErrorDetails) {
            # PowerShell 7+
            $content = $_.ErrorDetails.Message
        }
        $parsed = $null
        if ($content) { try { $parsed = $content | ConvertFrom-Json } catch { $parsed = $content } }
        return @{ Status = $status; Body = $parsed }
    }
}

# Generates a deterministic synthetic JPEG (seeded random ellipses on a colored background).
function New-TestImage {
    param([string]$Path, [int]$Seed)
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap(224, 224)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $rand = New-Object System.Random($Seed)
    $gfx.Clear([System.Drawing.Color]::FromArgb($rand.Next(256), $rand.Next(256), $rand.Next(256)))
    for ($i = 0; $i -lt 15; $i++) {
        $color = [System.Drawing.Color]::FromArgb($rand.Next(256), $rand.Next(256), $rand.Next(256))
        $brush = New-Object System.Drawing.SolidBrush($color)
        $gfx.FillEllipse($brush, $rand.Next(180), $rand.Next(180), $rand.Next(20, 90), $rand.Next(20, 90))
        $brush.Dispose()
    }
    $gfx.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
}

function ConvertTo-Base64Image {
    param([string]$Path)
    return "data:image/jpeg;base64," + [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($Path))
}

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Face Attendance API test suite" -ForegroundColor Cyan
Write-Host ("  Base URL      : {0}" -f $BaseUrl)
Write-Host ("  Employee code : {0}" -f $EmployeeCode)
Write-Host ""

# Connectivity pre-check
try {
    $null = Invoke-Api -Method GET -Path "/api/employees"
} catch {
    Write-Host ("Cannot reach the API at {0}." -f $BaseUrl) -ForegroundColor Red
    Write-Host "Start it first with:  dotnet run   (and make sure PostgreSQL is running)" -ForegroundColor Yellow
    exit 1
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "face-api-tests"
if (-not (Test-Path $tempDir)) { New-Item -ItemType Directory -Path $tempDir | Out-Null }

if ($RegisterImages.Count -eq 0) {
    Write-Host "No -RegisterImages supplied; generating synthetic test images..." -ForegroundColor Yellow
    $img1 = Join-Path $tempDir "reg_angle_0.jpg"
    $img2 = Join-Path $tempDir "reg_angle_1.jpg"
    New-TestImage -Path $img1 -Seed 42
    New-TestImage -Path $img2 -Seed 43
    $RegisterImages = @($img1, $img2)
}
if (-not $CheckInImage) {
    # Same image as registration angle 0 => similarity should be ~1.0
    $CheckInImage = $RegisterImages[0]
}
$mismatchImage = Join-Path $tempDir "mismatch.jpg"
New-TestImage -Path $mismatchImage -Seed 999

$registerB64 = @($RegisterImages | ForEach-Object { ConvertTo-Base64Image $_ })
$checkInB64 = ConvertTo-Base64Image $CheckInImage
$mismatchB64 = ConvertTo-Base64Image $mismatchImage

# ---------------------------------------------------------------------------
# GET /api/employees
# ---------------------------------------------------------------------------
Write-Host "GET /api/employees" -ForegroundColor Cyan
$r = Invoke-Api -Method GET -Path "/api/employees"
Write-TestResult "returns 200" ($r.Status -eq 200) ("Employees currently registered: {0}" -f @($r.Body).Count)

# ---------------------------------------------------------------------------
# POST /api/register - validation errors
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "POST /api/register (validation)" -ForegroundColor Cyan

$r = Invoke-Api -Method POST -Path "/api/register" -Body @{ employeeCode = ""; firstName = "A"; lastName = "B"; faceImagesBase64 = @($registerB64[0]) }
Write-TestResult "missing employee code returns 400" ($r.Status -eq 400) $r.Body.message

$r = Invoke-Api -Method POST -Path "/api/register" -Body @{ employeeCode = $EmployeeCode; firstName = "A"; lastName = "B"; faceImagesBase64 = @() }
Write-TestResult "empty image list returns 400" ($r.Status -eq 400) $r.Body.message

$r = Invoke-Api -Method POST -Path "/api/register" -Body @{ employeeCode = $EmployeeCode; firstName = "A"; lastName = "B"; faceImagesBase64 = @("this-is-not-base64!!!") }
Write-TestResult "invalid base64 returns 400" ($r.Status -eq 400) $r.Body.message

# ---------------------------------------------------------------------------
# POST /api/register - happy path
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "POST /api/register (happy path)" -ForegroundColor Cyan

$r = Invoke-Api -Method POST -Path "/api/register" -Body @{
    employeeCode     = $EmployeeCode
    firstName        = "Test"
    lastName         = "User"
    faceImagesBase64 = $registerB64
}
Write-TestResult "returns 200" ($r.Status -eq 200) ("execution_time_ms: {0}" -f $r.Body.execution_time_ms)
Write-TestResult ("registers {0} angle(s)" -f $registerB64.Count) (@($r.Body.angles_registered).Count -eq $registerB64.Count)
Write-TestResult "embedding size is 512" (@($r.Body.angles_registered)[0].embedding_size -eq 512)
$employeeId = $r.Body.employee_id

$r = Invoke-Api -Method GET -Path "/api/employees"
$me = @($r.Body) | Where-Object { $_.employeeCode -eq $EmployeeCode }
Write-TestResult "new employee appears in /api/employees" ($null -ne $me)
Write-TestResult "faceAnglesCount matches" ($me.faceAnglesCount -eq $registerB64.Count) ("faceAnglesCount: {0}" -f $me.faceAnglesCount)

# ---------------------------------------------------------------------------
# POST /api/checkin - happy path
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "POST /api/checkin (happy path)" -ForegroundColor Cyan

$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = $EmployeeCode; faceImageBase64 = $checkInB64 }
Write-TestResult "returns 200" ($r.Status -eq 200)
Write-TestResult "match succeeds (similarity >= threshold)" ($r.Body.success -eq $true) `
    ("similarity: {0} | threshold: {1} | status: {2}" -f $r.Body.similarity, $r.Body.threshold, $r.Body.status)

# case-insensitive employee code lookup
$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = $EmployeeCode.ToLower(); faceImageBase64 = $checkInB64 }
Write-TestResult "employee code lookup is case-insensitive" ($r.Status -eq 200 -and $r.Body.success -eq $true)

# mismatching image - informational only (synthetic images are not faces, so the
# embedding distance is not guaranteed to fall below the threshold)
$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = $EmployeeCode; faceImageBase64 = $mismatchB64 }
Write-TestResult "different image returns 200" ($r.Status -eq 200) `
    ("INFO: mismatch similarity: {0} | success: {1} (informational)" -f $r.Body.similarity, $r.Body.success)

# ---------------------------------------------------------------------------
# POST /api/checkin - error cases
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "POST /api/checkin (errors)" -ForegroundColor Cyan

$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = "NO_SUCH_EMP_999"; faceImageBase64 = $checkInB64 }
Write-TestResult "unknown employee returns 404" ($r.Status -eq 404) $r.Body.message

$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = $EmployeeCode; faceImageBase64 = "" }
Write-TestResult "missing image returns 400" ($r.Status -eq 400) $r.Body.message

$r = Invoke-Api -Method POST -Path "/api/checkin" -Body @{ employeeCode = $EmployeeCode; faceImageBase64 = "not-valid-base64!!!" }
Write-TestResult "invalid base64 returns 400" ($r.Status -eq 400) $r.Body.message

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host ("Results: {0} passed, {1} failed" -f $script:Passed, $script:Failed) `
    -ForegroundColor $(if ($script:Failed -eq 0) { "Green" } else { "Red" })
Write-Host ("Note: test employee '{0}' (id {1}) was created in the database." -f $EmployeeCode, $employeeId) -ForegroundColor DarkGray
Write-Host ""

exit $(if ($script:Failed -eq 0) { 0 } else { 1 })
