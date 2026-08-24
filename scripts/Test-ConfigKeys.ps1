<#
.SYNOPSIS
    Validates every Config.Bind section and key name in the source.

.DESCRIPTION
    BepInEx rejects = \n \t \ " ' [ ] in section and key names, and it does so
    by throwing from ConfigDefinition's constructor. That happens inside Awake,
    so a single apostrophe does not degrade the plugin - it stops it from
    loading at all, with a stack trace and nothing else. The compiler cannot
    catch it, and it only shows up on a real game start.

    So the check runs BepInEx's OWN validator against the strings found in the
    source: ConfigDefinition is plain .NET and constructs fine outside Unity.
    Nothing here duplicates the rule, which means it cannot drift from it.
#>
param()

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$bepInExPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "..\..\BepInEx\core\BepInEx.dll"))
if (-not (Test-Path -LiteralPath $bepInExPath)) {
    throw "BepInEx.dll was not found at $bepInExPath."
}
Add-Type -Path $bepInExPath

# Config.Bind("section", "key", ... - with or without an explicit type argument.
$pattern = 'Config\.Bind(?:<[^>]+>)?\(\s*"([^"]*)"\s*,\s*"([^"]*)"'
$problems = New-Object System.Collections.Generic.List[string]
$checked = 0

foreach ($file in Get-ChildItem -LiteralPath $repositoryRoot -Filter "*.cs" -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, $pattern)) {
        $section = $match.Groups[1].Value
        $key = $match.Groups[2].Value
        $checked++
        try {
            $definition = New-Object BepInEx.Configuration.ConfigDefinition($section, $key)
            Write-Host "  ok  [$($definition.Section)] $($definition.Key)" -ForegroundColor Green
        }
        catch {
            $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
            $problems.Add("$($file.Name): [$section] $key -> $reason")
        }
    }
}

if ($checked -eq 0) {
    throw "No Config.Bind calls were found - the pattern in this script no longer matches the source."
}

if ($problems.Count -gt 0) {
    Write-Host "Invalid config definitions ($($problems.Count)):" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "BepInEx would throw out of Awake and the plugin would not load."
}

Write-Host "All $checked config definitions are valid." -ForegroundColor Green
