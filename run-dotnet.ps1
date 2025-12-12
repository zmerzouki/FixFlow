# Pseudocode / plan (detailed):
# 1. Accept a project path and any additional dotnet arguments.
# 2. Defensive-clean the project argument in case the user accidentally pasted the PowerShell prompt
#    (e.g. "PS C:\My Apps\RJF.TradeAllocBridge> src/RJF.TradeAllocBridge.CLI").
# 3. Trim stray characters (leading "PS ...>" fragments and surrounding quotes).
# 4. Quote the project path if it contains spaces.
# 5. Invoke `dotnet run --project <project>` with remaining args forwarded.
# 6. Stream output to console and return dotnet's exit code.
#
# This prevents the error caused by pasting the shell prompt into the command and provides a safe wrapper.

param(
    [string] $Project = "src/RJF.TradeAllocBridge.CLI",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArgs
)

function Clean-ProjectArg {
    param([string] $input)
    if ([string]::IsNullOrWhiteSpace($input)) { return $input }

    # Remove common pasted prompt fragments like "PS C:\Path\To\Project>" or "C:\Path> "
    #  - Remove any leading "PS " up to the first ">" (greedy) if present.
    $clean = $input -replace '^\s*PS\s+[^>]*>\s*', ''

    # Also remove any trailing prompt-like '>' if left
    $clean = $clean.Trim()
    if ($clean.StartsWith('>')) { $clean = $clean.TrimStart('>',' ') }

    # Remove surrounding quotes if present
    if ($clean.StartsWith('"') -and $clean.EndsWith('"')) {
        $clean = $clean.Substring(1, $clean.Length - 2)
    } elseif ($clean.StartsWith("'") -and $clean.EndsWith("'")) {
        $clean = $clean.Substring(1, $clean.Length - 2)
    }

    return $clean
}

$projectClean = Clean-ProjectArg $Project

# If the user accidentally appended the command after the prompt fragment (e.g. "PS C:\...> dotnet ..."),
# allow the user to pass the whole pasted line by extracting the project token if it looks like a path.
if (-not (Test-Path $projectClean) -and $projectClean -match 'src[\\/].+?(\.csproj|[^\s]+)') {
    # try to extract a plausible src/... token
    $m = [regex]::Match($projectClean, '(src[\\/][^\s]+)')
    if ($m.Success) { $projectClean = $m.Value }
}

# Quote the project path if it contains spaces
$projectQuoted = if ($projectClean -match '\s') { '"' + $projectClean + '"' } else { $projectClean }

# Build arguments string safely (preserve spaces in each remaining arg)
$argsStr = ""
if ($RemainingArgs -and $RemainingArgs.Length -gt 0) {
    $escaped = $RemainingArgs | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }
    $argsStr = " " + ($escaped -join ' ')
}

Write-Host "Executing: dotnet run --project $projectQuoted$argsStr" -ForegroundColor Cyan

# Start process and wait for exit, returning same code
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --project $projectQuoted$argsStr"
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false
$psi.UseShellExecute = $true

try {
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    exit $proc.ExitCode
} catch {
    Write-Error "Failed to start dotnet: $_"
    exit 1
}