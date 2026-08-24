<#
.SYNOPSIS
    Verifies that the compiled plugin references Anvil-WebOverlay only from
    method bodies inside the gate class.

.DESCRIPTION
    Rule 5 of the library's docs/SOFT-DEPENDENCY.md: no field, base type,
    interface, generic argument or method signature anywhere in the assembly
    may name a type from Anvil-WebOverlay. Only method bodies may, and only
    inside UI\WebOverlayGate.

    The reason is Mono's loading order. A method body is resolved the first
    time that method runs, so a body full of library types is harmless on a
    machine without the library - as long as it never runs, which the gate's
    IsUsable check guarantees. Fields, base types and signatures are resolved
    when the TYPE loads, and one of those makes Assembly.GetTypes() over this
    plugin throw for everyone else: other mods scan all loaded assemblies that
    way, and the damage lands on them, not here.

    Compiler-generated closure classes are the trap this catches in practice -
    a lambda capturing an IWebOverlay local turns that local into a field.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$assemblyPath = Join-Path $repositoryRoot "bin\$Configuration\maschine-RaidReviewOverlay.dll"
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Build the $Configuration configuration first - $assemblyPath does not exist."
}

# Mono.Cecil ships with BepInEx; the game install is two levels up from here.
$cecilPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "..\..\BepInEx\core\Mono.Cecil.dll"))
if (-not (Test-Path -LiteralPath $cecilPath)) {
    throw "Mono.Cecil was not found at $cecilPath."
}
Add-Type -Path $cecilPath

$library = "Anvil-WebOverlay"
$gateType = "RaidReviewOverlay.UI.WebOverlayGate"

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
$violations = New-Object System.Collections.Generic.List[string]

function Test-Reference {
    param($TypeReference)
    if ($null -eq $TypeReference) { return $false }
    if ($TypeReference.Scope -and $TypeReference.Scope.Name -like "$library*") { return $true }
    # Generic arguments hide references inside an otherwise innocent type.
    if ($TypeReference -is [Mono.Cecil.GenericInstanceType]) {
        foreach ($argument in $TypeReference.GenericArguments) {
            if (Test-Reference $argument) { return $true }
        }
    }
    if ($TypeReference.IsArray -or $TypeReference.IsByReference -or $TypeReference.IsPointer) {
        return (Test-Reference $TypeReference.ElementType)
    }
    return $false
}

foreach ($type in $assembly.MainModule.GetTypes()) {
    if (Test-Reference $type.BaseType) {
        $violations.Add("$($type.FullName): base type $($type.BaseType.FullName)")
    }
    foreach ($interface in $type.Interfaces) {
        if (Test-Reference $interface.InterfaceType) {
            $violations.Add("$($type.FullName): interface $($interface.InterfaceType.FullName)")
        }
    }
    foreach ($field in $type.Fields) {
        if (Test-Reference $field.FieldType) {
            $violations.Add("$($type.FullName).$($field.Name): field of type $($field.FieldType.FullName)")
        }
    }
    foreach ($property in $type.Properties) {
        if (Test-Reference $property.PropertyType) {
            $violations.Add("$($type.FullName).$($property.Name): property of type $($property.PropertyType.FullName)")
        }
    }
    foreach ($method in $type.Methods) {
        if (Test-Reference $method.ReturnType) {
            $violations.Add("$($type.FullName).$($method.Name): returns $($method.ReturnType.FullName)")
        }
        foreach ($parameter in $method.Parameters) {
            if (Test-Reference $parameter.ParameterType) {
                $violations.Add("$($type.FullName).$($method.Name): parameter '$($parameter.Name)' of type $($parameter.ParameterType.FullName)")
            }
        }

        # Bodies are allowed - but only in the gate itself.
        if ($type.FullName -eq $gateType -or $type.FullName -like "$gateType/*") { continue }
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            $operand = $instruction.Operand
            if ($null -eq $operand) { continue }
            $declaring = $null
            if ($operand -is [Mono.Cecil.TypeReference]) { $declaring = $operand }
            elseif ($operand -is [Mono.Cecil.MemberReference]) { $declaring = $operand.DeclaringType }
            if (Test-Reference $declaring) {
                $violations.Add("$($type.FullName).$($method.Name): body uses $($declaring.FullName) outside the gate")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Soft-dependency violations ($($violations.Count)):" -ForegroundColor Red
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "The plugin would break Assembly.GetTypes() for other mods when $library is missing."
}

Write-Host "Soft dependency clean: $library appears only in $gateType method bodies." -ForegroundColor Green
