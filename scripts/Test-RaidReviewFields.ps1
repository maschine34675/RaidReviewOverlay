<#
.SYNOPSIS
    Checks a RAID_REVIEW.dll for the static members this addon reflects on.

.DESCRIPTION
    The addon reads three public static members from Raid Review's plugin
    class and works around each of them being absent - but "works around"
    means the browser tab comes back, quietly, for anyone who does not read
    the log. Run this against a new Raid Review release to find out before the
    players do.

    Verified against Raid Review 1.5.0 (Chazut/SPT-RaidReview, branch 4.1.X).
#>
param(
    [string]$Path
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "..\.."))

if (-not $Path) {
    $candidates = @(
        (Join-Path $gameRoot "BepInEx\plugins\RAID_REVIEW.dll"),
        (Join-Path $gameRoot "BepInEx\plugins\RaidReview\RAID_REVIEW.dll"),
        (Join-Path $gameRoot "BepInEx\plugins\Raid Review\RAID_REVIEW.dll")
    )
    $Path = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Path) {
        $found = Get-ChildItem -LiteralPath (Join-Path $gameRoot "BepInEx\plugins") -Recurse -Filter "RAID_REVIEW.dll" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $Path = $found.FullName }
    }
}

if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
    throw "RAID_REVIEW.dll was not found. Install Raid Review, or pass -Path <file>."
}

$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecilPath)) {
    throw "Mono.Cecil was not found at $cecilPath."
}
Add-Type -Path $cecilPath

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path)

# The plugin class is whichever type carries [BepInPlugin("ekky.raidreview", ...)];
# finding it by attribute rather than by name survives a rename.
$pluginType = $null
foreach ($type in $assembly.MainModule.GetTypes()) {
    foreach ($attribute in $type.CustomAttributes) {
        if ($attribute.AttributeType.Name -ne "BepInPlugin") { continue }
        $guid = $attribute.ConstructorArguments[0].Value
        if ($guid -eq "ekky.raidreview") { $pluginType = $type }
    }
}

if (-not $pluginType) {
    throw "No type in $Path carries [BepInPlugin(`"ekky.raidreview`")]."
}

Write-Host "Plugin class: $($pluginType.FullName)  ($Path)"

# Single-quoted on purpose: the backtick in a generic type name (ConfigEntry`1)
# is PowerShell's escape character inside double quotes and would silently
# vanish, turning every comparison below into a false alarm.
$expected = @(
    @{ Name = "RAID_REVIEW_HTTP_Server"; Type = 'System.String';                                                               Used = "the web interface address" },
    @{ Name = "LaunchWebpageKey";        Type = 'BepInEx.Configuration.ConfigEntry`1<BepInEx.Configuration.KeyboardShortcut>'; Used = "the hotkey takeover" },
    @{ Name = "InsertMenuItem";          Type = 'BepInEx.Configuration.ConfigEntry`1<System.Boolean>';                         Used = "suppressing Raid Review's own menu button" }
)

$problems = New-Object System.Collections.Generic.List[string]

foreach ($member in $expected) {
    $field = $pluginType.Fields | Where-Object { $_.Name -eq $member.Name } | Select-Object -First 1
    if (-not $field) {
        $problems.Add("missing: $($member.Name) - $($member.Used) falls back")
        continue
    }
    if (-not $field.IsStatic -or -not $field.IsPublic) {
        $problems.Add("not public static: $($member.Name)")
        continue
    }
    if ($field.FieldType.FullName -ne $member.Type) {
        $problems.Add("type changed: $($member.Name) is $($field.FieldType.FullName), expected $($member.Type)")
        continue
    }
    Write-Host "  ok  $($member.Name) : $($field.FieldType.FullName)" -ForegroundColor Green
}

if ($problems.Count -gt 0) {
    Write-Host "Problems:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "This Raid Review build does not match what the addon reflects on."
}

Write-Host "All three members are as expected." -ForegroundColor Green
