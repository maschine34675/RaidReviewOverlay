<#
.SYNOPSIS
    Runs AddressRules.cs - the shipped file itself - on the game's own runtime
    against the addresses players type and the setups they run.

.DESCRIPTION
    Which address the overlay opens is the one part of this addon users have
    reported wrong: first 127.0.0.1 on a remote server, then a headless setup
    that needed an address entered by hand. It is pure string and URI handling,
    so it is tested without a game - but not on just any runtime. System.Uri
    differs between runtimes in exactly the places that matter here: .NET
    Framework unescapes %26 in ToString() and writes IPv6 hosts out in full,
    modern .NET does neither. The game runs Unity's Mono with its own
    System.dll, so that is what this test runs on:

    1. AddressRules.cs plus a small runner are compiled with the .NET SDK's
       Roslyn against the game's reference assemblies (EscapeFromTarkov_Data\
       Managed), as the plugin is;
    2. the runner executes under the Mono of the Unity editor version the game
       was built with, with MONO_PATH pointing at the game's Managed folder, so
       System.Uri comes from the game's own System.dll.

    The first output line names the runtime and System.dll that actually ran.
    Without that Mono the test fails rather than quietly passing on a runtime
    the game does not use; -MonoPath points it at one elsewhere.
#>
param(
    [string]$MonoPath
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "..\.."))
$managed = Join-Path $gameRoot "EscapeFromTarkov_Data\Managed"
foreach ($reference in @("mscorlib.dll", "System.dll", "System.Core.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $managed $reference))) {
        throw "The game's $reference was not found in $managed."
    }
}

# The Mono of the Unity version the game was built with.
if (-not $MonoPath) {
    $unity = (Get-Item -LiteralPath (Join-Path $gameRoot "UnityPlayer.dll")).VersionInfo.FileVersion
    $version = ($unity -split '\.')[0..2] -join '.'
    $MonoPath = Get-ChildItem -Path "$env:ProgramFiles\Unity\Hub\Editor" -Directory -Filter "$version*" -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "Editor\Data\MonoBleedingEdge\bin\mono.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $MonoPath) {
        throw "No Mono for Unity $version (the game's version) was found under $env:ProgramFiles\Unity\Hub\Editor. Install that editor version, or pass -MonoPath. Testing on another runtime would not show what the game does with an address."
    }
}

$csc = Get-ChildItem -Path "$env:ProgramFiles\dotnet\sdk\*\Roslyn\bincore\csc.dll" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw "No .NET SDK Roslyn compiler (csc.dll) was found." }

$runner = @'
using System;
using System.IO;
using System.Text;

namespace RaidReviewOverlay.Tests
{
    internal static class Runner
    {
        private static string Decode(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        private static string Encode(string value) { return value == null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }

        private static int Main(string[] args)
        {
            Type mono = Type.GetType("Mono.Runtime");
            string runtime = mono == null
                ? "CLR " + Environment.Version
                : "Mono " + mono.GetMethod("GetDisplayName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            Console.WriteLine("#\t" + Encode(runtime + " | System.dll: " + typeof(Uri).Assembly.Location));

            foreach (string line in File.ReadAllLines(args[0]))
            {
                string[] field = line.Split('\t');
                string result = null, extra = null;
                switch (field[0])
                {
                    case "N":
                        result = AddressRules.NormalizeManual(Decode(field[1]), Decode(field[2]), Decode(field[3]), out extra);
                        break;
                    case "L":
                        bool replaced;
                        result = AddressRules.CorrectLoopback(new Uri(Decode(field[1])), Decode(field[2]), out replaced);
                        extra = replaced ? "replaced" : "kept";
                        break;
                    case "F":
                        result = AddressRules.ForLog(Decode(field[1]));
                        break;
                }
                Console.WriteLine(field[0] + "\t" + Encode(result) + "\t" + Encode(extra));
            }
            return 0;
        }
    }
}
'@

# --- the cases -------------------------------------------------------------
# Normalize: what players type into 'Web Interface Address'. Scheme and port
# are the defaults Raid Review's own settings supply for a bare host.
$normalize = @(
    @{ In = "192.168.1.20";                   Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7829/" }
    @{ In = "192.168.1.20:7830";              Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7830/" }
    @{ In = "192.168.1.20";                   Scheme = "http";  Port = "8000"; Out = "http://192.168.1.20:8000/" }
    @{ In = "raid.example.com";               Scheme = "https"; Port = "";     Out = "https://raid.example.com/" }
    @{ In = "raid.example.com";               Scheme = "https"; Port = "7829"; Out = "https://raid.example.com:7829/" }
    @{ In = "http://192.168.1.20:7829";       Scheme = "https"; Port = "";     Out = "http://192.168.1.20:7829/" }
    @{ In = "  http://192.168.1.20:7829/  ";  Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7829/" }
    @{ In = "HTTP://Raid-Host:7829";          Scheme = "http";  Port = "7829"; Out = "http://raid-host:7829/" }
    @{ In = "https://raid.example.com";       Scheme = "http";  Port = "7829"; Out = "https://raid.example.com/" }
    @{ In = "https://raid.example.com/rr/?key=a%26b&x=%2F"; Scheme = "http"; Port = "7829"; Out = "https://raid.example.com/rr/?key=a%26b&x=%2F" }
    @{ In = "192.168.1.20:7829/some/path";    Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7829/some/path" }
    @{ In = "192.168.1.20#/replays";          Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7829/#/replays" }
    @{ In = "192.168.1.20?x=1";               Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7829/?x=1" }
    @{ In = "192.168.1.20:7830?x=1";          Scheme = "http";  Port = "7829"; Out = "http://192.168.1.20:7830/?x=1" }
    @{ In = "user:pw@192.168.1.20";           Scheme = "http";  Port = "7829"; Out = "http://user:pw@192.168.1.20:7829/" }
    @{ In = "localhost";                      Scheme = "http";  Port = "7829"; Out = "http://localhost:7829/" }
    @{ In = "[fd00::20]";                     Scheme = "http";  Port = "7829"; Out = "http://[fd00::20]:7829/" }
    @{ In = "[fd00::20]:7830";                Scheme = "http";  Port = "7829"; Out = "http://[fd00::20]:7830/" }
    @{ In = "fd00::20";                       Scheme = "http";  Port = "7829"; Error = "brackets" }
    @{ In = "[fd00::20]7829";                 Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "[fd00::20] :7829";               Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "[fd00::20";                      Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "http://[fd00::20]7829";          Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "http://[fd00::20] :7829";        Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "https://[fd00::20]abc";          Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "http://user:pw@[fd00::20]7829";  Scheme = "http";  Port = "7829"; Error = "closing ']'" }
    @{ In = "http://[fd00::20]:7830/x";       Scheme = "http";  Port = "7829"; Out = "http://[fd00::20]:7830/x" }
    @{ In = "raid-host:abc";                  Scheme = "http";  Port = "7829"; Error = "not a number" }
    @{ In = "raid-host:99999";                Scheme = "http";  Port = "7829"; Error = "not an address" }
    @{ In = ":7829";                          Scheme = "http";  Port = "7829"; Error = "not an address" }
    @{ In = "ws://192.168.1.20:7828";         Scheme = "http";  Port = "7829"; Error = "only http" }
    @{ In = "";                               Scheme = "http";  Port = "7829"; Error = "empty" }
    @{ In = "   ";                            Scheme = "http";  Port = "7829"; Error = "empty" }
)

# Loopback: Raid Review's own address on a remote server.
$loopback = @(
    @{ In = "http://127.0.0.1:7829";    Backend = "192.168.1.50";    Out = "http://192.168.1.50:7829/";    Replaced = $true }
    @{ In = "http://localhost:7829";    Backend = "192.168.1.50";    Out = "http://192.168.1.50:7829/";    Replaced = $true }
    @{ In = "http://127.0.0.5:7829";    Backend = "192.168.1.50";    Out = "http://192.168.1.50:7829/";    Replaced = $true }
    @{ In = "http://[::1]:7829";        Backend = "192.168.1.50";    Out = "http://192.168.1.50:7829/";    Replaced = $true }
    @{ In = "http://127.0.0.1";         Backend = "192.168.1.50";    Out = "http://192.168.1.50/";         Replaced = $true }
    @{ In = "https://127.0.0.1:7829";   Backend = "192.168.1.50";    Out = "https://192.168.1.50:7829/";   Replaced = $true }
    @{ In = "http://127.0.0.1:7829";    Backend = "spt.example.com"; Out = "http://spt.example.com:7829/"; Replaced = $true }
    @{ In = "http://127.0.0.1:7829/r?k=a%26b"; Backend = "192.168.1.50"; Out = "http://192.168.1.50:7829/r?k=a%26b"; Replaced = $true }
    @{ In = "http://192.168.1.99:7829"; Backend = "192.168.1.50";    Out = "http://192.168.1.99:7829/";    Replaced = $false }
    @{ In = "http://127.0.0.1:7829";    Backend = "127.0.0.1";       Out = "http://127.0.0.1:7829/";       Replaced = $false }
    @{ In = "http://127.0.0.1:7829";    Backend = "localhost";       Out = "http://127.0.0.1:7829/";       Replaced = $false }
    @{ In = "http://127.0.0.1:7829";    Backend = "";                Out = "http://127.0.0.1:7829/";       Replaced = $false }
)

# ForLog: what may appear in a log line players paste publicly. Passwords typed
# raw may contain '@', '#', '/', '?' or spaces - those are the addresses that
# fail to parse and get quoted in a warning, so none of it may survive. An '@'
# in a path hides the host too; that is the accepted price.
$forLog = @(
    @{ In = "http://user:pw@192.168.1.20:7829/";      Out = "http://***@192.168.1.20:7829/" }
    @{ In = "user:pw@192.168.1.20";                   Out = "***@192.168.1.20" }
    @{ In = "admin:Pa#ss@192.168.1.20:7829";          Out = "***@192.168.1.20:7829" }
    @{ In = "http://admin:p@ss@192.168.1.20:7829";    Out = "http://***@192.168.1.20:7829" }
    @{ In = "admin:p/s?s w@192.168.1.20";             Out = "***@192.168.1.20" }
    @{ In = "admin:pa://ss@192.168.1.20";             Out = "***@192.168.1.20" }
    @{ In = "http://192.168.1.20:7829/a@b";           Out = "http://***@b" }
    @{ In = "http://192.168.1.20:7829/";              Out = "http://192.168.1.20:7829/" }
)

# --- compile and run ---------------------------------------------------------
function Encode([string]$value) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value)) }
function Decode([string]$value) { if ($value -eq "-") { return $null }; [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value)) }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("rro-address-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    Set-Content -LiteralPath (Join-Path $work "Runner.cs") -Value $runner -Encoding UTF8
    $exe = Join-Path $work "AddressRulesRunner.exe"
    $compile = & dotnet $csc.FullName -nologo -nostdlib -noconfig -target:exe "-out:$exe" `
        "-r:$(Join-Path $managed 'mscorlib.dll')" "-r:$(Join-Path $managed 'System.dll')" "-r:$(Join-Path $managed 'System.Core.dll')" `
        (Join-Path $repositoryRoot "AddressRules.cs") (Join-Path $work "Runner.cs") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Compiling AddressRules.cs failed:`n$($compile -join "`n")" }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($c in $normalize) { $lines.Add("N`t$(Encode $c.In)`t$(Encode $c.Scheme)`t$(Encode $c.Port)") }
    foreach ($c in $loopback)  { $lines.Add("L`t$(Encode $c.In)`t$(Encode $c.Backend)") }
    foreach ($c in $forLog)    { $lines.Add("F`t$(Encode $c.In)") }
    $caseFile = Join-Path $work "cases.tsv"
    [System.IO.File]::WriteAllLines($caseFile, $lines)

    $previousMonoPath = $env:MONO_PATH
    $env:MONO_PATH = $managed
    try { $output = & $MonoPath $exe $caseFile 2>&1 } finally { $env:MONO_PATH = $previousMonoPath }
    if ($LASTEXITCODE -ne 0) { throw "The runner failed on Mono:`n$($output -join "`n")" }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# Mono writes a byte-order mark before its first line.
$output = @($output | ForEach-Object { ([string]$_).TrimStart([char]0xFEFF) } | Where-Object { $_ })
$runtimeLine = $output | Where-Object { $_.StartsWith("#`t") } | Select-Object -First 1
$results = @($output | Where-Object { -not $_.StartsWith("#`t") })
$runtime = Decode ($runtimeLine -split "`t")[1]
Write-Host "Runtime: $runtime"
if ($runtime -notlike "Mono*" -or $runtime -notlike "*$managed*") {
    throw "The runner did not run on Mono with the game's System.dll ($runtime)."
}
if ($results.Count -ne $lines.Count) {
    throw "The runner answered $($results.Count) of $($lines.Count) cases."
}

# --- compare -----------------------------------------------------------------
$failures = New-Object System.Collections.Generic.List[string]
$index = 0

foreach ($c in $normalize) {
    $field = $results[$index++] -split "`t"
    $result = Decode $field[1]; $reason = Decode $field[2]
    $label = "'$($c.In)' ($($c.Scheme), port '$($c.Port)')"
    if ($c.Error) {
        if ($null -ne $result -or -not ($reason -like "*$($c.Error)*")) { $failures.Add("$label -> expected an error containing '$($c.Error)', got '$result' / '$reason'") }
        else { Write-Host ("  ok  {0,-52} rejected: {1}" -f $label, $reason) -ForegroundColor Green }
    }
    elseif ($result -cne $c.Out) { $failures.Add("$label -> expected '$($c.Out)', got '$result' ($reason)") }
    else { Write-Host ("  ok  {0,-52} {1}" -f $label, $result) -ForegroundColor Green }
}

foreach ($c in $loopback) {
    $field = $results[$index++] -split "`t"
    $result = Decode $field[1]; $replaced = (Decode $field[2]) -eq "replaced"
    $label = "$($c.In) via '$($c.Backend)'"
    if ($result -cne $c.Out -or $replaced -ne $c.Replaced) { $failures.Add("$label -> expected '$($c.Out)' (replaced=$($c.Replaced)), got '$result' (replaced=$replaced)") }
    else { Write-Host ("  ok  {0,-52} {1}" -f $label, $result) -ForegroundColor Green }
}

foreach ($c in $forLog) {
    $field = $results[$index++] -split "`t"
    $result = Decode $field[1]
    if ($result -cne $c.Out) { $failures.Add("log '$($c.In)' -> expected '$($c.Out)', got '$result'") }
    else { Write-Host ("  ok  {0,-52} {1}" -f "log '$($c.In)'", $result) -ForegroundColor Green }
}

if ($failures.Count -gt 0) {
    Write-Host "Address rule failures ($($failures.Count)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "AddressRules.cs does not behave as the cases require on the game's runtime."
}

Write-Host "All $index address cases pass on the game's runtime." -ForegroundColor Green
