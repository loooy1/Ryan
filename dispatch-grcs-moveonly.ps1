param(
    [Parameter(Mandatory = $true)]
    [string] $VehicleInfoUrl,

    [Parameter(Mandatory = $true)]
    [string] $MoveTaskUrl,

    [Parameter(Mandatory = $true)]
    [string] $TargetStation,

    [Parameter(Mandatory = $true)]
    [string] $SceneName,

    # Vehicle response field mappings. Use dot paths, e.g. data.vehicles.
    [string] $VehicleListPath = "",
    [string] $VehicleNamePath = "vehicleName",
    [string] $VehicleModePath = "runMode",
    [string] $BatteryPath = "battery",
    [string] $AutoModeValue = "AUTO",
    [double] $MinimumBattery = 80,

    # By default, dispatch only one vehicle: the eligible vehicle with the highest battery.
    [switch] $AllQualifiedVehicles,

    # Without -Execute, the script only prints the candidate and request body.
    [switch] $Execute,

    # Optional bearer token for both APIs.
    [string] $BearerToken = ""
)

$ErrorActionPreference = "Stop"

function Get-JsonPathValue {
    param(
        [Parameter(Mandatory = $true)] $InputObject,
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ,$InputObject
    }

    $current = $InputObject
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$part]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    return ,$current
}

$headers = @{ Accept = "application/json" }
if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
    $headers.Authorization = "Bearer $BearerToken"
}

Write-Host "读取车辆信息: $VehicleInfoUrl"
$vehicleResponse = Invoke-RestMethod -Method Get -Uri $VehicleInfoUrl -Headers $headers -TimeoutSec 30
$vehicleList = Get-JsonPathValue -InputObject $vehicleResponse -Path $VehicleListPath
if ($null -eq $vehicleList) {
    throw "车辆列表为空或 VehicleListPath '$VehicleListPath' 不正确。请根据接口响应设置 VehicleListPath。"
}

# A single vehicle object is also accepted; normally the value is an array.
if ($vehicleList -isnot [System.Collections.IEnumerable] -or $vehicleList -is [string] -or $vehicleList -is [System.Collections.IDictionary]) {
    $vehicleList = @($vehicleList)
}

$candidates = @()
foreach ($vehicle in $vehicleList) {
    $name = Get-JsonPathValue -InputObject $vehicle -Path $VehicleNamePath
    $mode = Get-JsonPathValue -InputObject $vehicle -Path $VehicleModePath
    $batteryValue = Get-JsonPathValue -InputObject $vehicle -Path $BatteryPath

    if ([string]::IsNullOrWhiteSpace([string]$name) -or $null -eq $batteryValue) { continue }
    if ([string]$mode -ine $AutoModeValue) { continue }

    $battery = 0.0
    if (-not [double]::TryParse([string]$batteryValue, [ref]$battery)) { continue }
    # Some APIs report battery as a fraction (0..1), others as percent (0..100).
    if ($battery -ge 0 -and $battery -le 1) { $battery *= 100 }
    if ($battery -gt $MinimumBattery) {
        $candidates += [pscustomobject]@{
            VehicleName = [string]$name
            Mode        = [string]$mode
            Battery     = [math]::Round($battery, 1)
        }
    }
}

if ($candidates.Count -eq 0) {
    Write-Host "没有找到自动模式且电量高于 $MinimumBattery% 的车辆。"
    exit 0
}

if (-not $AllQualifiedVehicles) {
    $candidates = @($candidates | Sort-Object Battery -Descending | Select-Object -First 1)
}

Write-Host "符合条件的车辆："
$candidates | Format-Table VehicleName, Mode, Battery -AutoSize

foreach ($vehicle in $candidates) {
    $now = Get-Date
    $orderId = "MV_{0}_{1}" -f $now.ToString("yyyyMMddHHmmss"), ([guid]::NewGuid().ToString("N").Substring(0, 8))
    $payload = [ordered]@{
        orderId      = $orderId
        sceneName    = $SceneName
        orderType    = "MOVE_ONLY"
        orderName    = "纯移动任务"
        vehicleName  = $vehicle.VehicleName
        priority     = 50
        stationCodes = @($TargetStation)
        createTime   = $now.ToString("yyyy-MM-dd HH:mm:ss")
        errorCode    = ""
    }
    $json = $payload | ConvertTo-Json -Depth 10

    Write-Host "`n车辆 $($vehicle.VehicleName) ($($vehicle.Battery)%) 的 MOVE_ONLY 请求体："
    Write-Host $json

    if (-not $Execute) {
        Write-Host "预览模式：未发送。确认接口地址、字段映射和站点后，添加 -Execute 才会下发。"
        continue
    }

    Write-Host "正在下发到 $MoveTaskUrl ..."
    $response = Invoke-RestMethod -Method Post -Uri $MoveTaskUrl -Headers $headers `
        -ContentType "application/json; charset=utf-8" -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 30
    Write-Host "RCS 响应："
    $response | ConvertTo-Json -Depth 20 | Write-Host
}
