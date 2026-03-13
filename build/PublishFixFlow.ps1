param(
    [string]$PublishDir,
    [string]$FixFlowSharedDir
)

$ErrorActionPreference = 'Stop'

function Normalize-PathParam([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $clean = $value.Trim()
    $clean = $clean.Trim('"')
    if ($clean.Contains(';')) {
        $clean = $clean.Split(';')[0]
    }
    $clean = $clean.TrimEnd(';')
    $clean = $clean -replace "[\x00-\x1F]", ""

    $invalid = [System.IO.Path]::GetInvalidPathChars()
    foreach ($ch in $invalid) {
        $clean = $clean.Replace([string]$ch, [string]::Empty)
    }

    return $clean
}

$PublishDir = Normalize-PathParam $PublishDir
$FixFlowSharedDir = Normalize-PathParam $FixFlowSharedDir

Write-Host "FixFlow publish script: raw PublishDir='$PublishDir'"
Write-Host "FixFlow publish script: raw FixFlowSharedDir='$FixFlowSharedDir'"

function Extract-RootedPath([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $driveMatch = [regex]::Match($value, '([A-Za-z]:\\[^"'';]+)')
    if ($driveMatch.Success) {
        return $driveMatch.Groups[1].Value
    }

    $uncMatch = [regex]::Match($value, '(\\\\[^"'';]+)')
    if ($uncMatch.Success) {
        return $uncMatch.Groups[1].Value
    }

    return $value
}

$PublishDir = Extract-RootedPath $PublishDir
$FixFlowSharedDir = Extract-RootedPath $FixFlowSharedDir

Write-Host "FixFlow publish script: extracted PublishDir='$PublishDir'"
Write-Host "FixFlow publish script: extracted FixFlowSharedDir='$FixFlowSharedDir'"

function Try-GetFullPath([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $value
    }

    try {
        return [System.IO.Path]::GetFullPath($value)
    } catch {
        return $value
    }
}

function Is-RootedPath([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    try {
        return [System.IO.Path]::IsPathRooted($value)
    } catch {
        return $false
    }
}

function Safe-Combine([string]$basePath, [string]$child) {
    if ([string]::IsNullOrWhiteSpace($basePath)) {
        return $null
    }

    try {
        return [System.IO.Path]::Combine($basePath, $child)
    } catch {
        return $null
    }
}

function Safe-GetDirectoryName([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        return $null
    }

    try {
        return [System.IO.Path]::GetDirectoryName($path)
    } catch {
        return $null
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($FixFlowSharedDir)) {
        if (-not [string]::IsNullOrWhiteSpace($PublishDir)) {
            $FixFlowSharedDir = Try-GetFullPath (Join-Path $PublishDir "..\\Shared")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($FixFlowSharedDir) -and (Is-RootedPath $FixFlowSharedDir)) {
        $sharedAppSettings = Safe-Combine $FixFlowSharedDir "appsettings.json"
        if ([string]::IsNullOrWhiteSpace($sharedAppSettings)) {
            throw "Unable to build shared appsettings path."
        }

        $sharedDir = Safe-GetDirectoryName $sharedAppSettings
        if (-not [string]::IsNullOrWhiteSpace($sharedDir)) {
            New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null
        }

        $changed = $false
        if (Test-Path $sharedAppSettings) {
            $raw = Get-Content -Raw -Path $sharedAppSettings
            if ([string]::IsNullOrWhiteSpace($raw)) {
                $json = [ordered]@{}
                $changed = $true
            } else {
                $json = $raw | ConvertFrom-Json
            }
        } else {
            $json = [ordered]@{}
            $changed = $true
        }

        if (-not ($json.PSObject.Properties.Name -contains 'FixSessionQualifiers')) {
            $json | Add-Member -NotePropertyName FixSessionQualifiers -NotePropertyValue ([ordered]@{})
            $changed = $true
        }

        $qual = $json.FixSessionQualifiers
        if ($null -eq $qual -or ($qual -isnot [System.Collections.IDictionary] -and $qual -isnot [pscustomobject])) {
            $qual = [ordered]@{}
            $json.FixSessionQualifiers = $qual
            $changed = $true
        }

        function Ensure-Qualifier([string]$Key, [string]$Value) {
            if (-not ($qual.PSObject.Properties.Name -contains $Key) -or [string]::IsNullOrWhiteSpace($qual.$Key)) {
                $qual | Add-Member -NotePropertyName $Key -NotePropertyValue $Value -Force
                $script:changed = $true
            }
        }

        Ensure-Qualifier "Web" "FixFlowWeb"
        Ensure-Qualifier "Client" "FixFlowClient"
        Ensure-Qualifier "Service" "FixFlowService"

        if ($changed) {
            $json | ConvertTo-Json -Depth 32 | Set-Content -Path $sharedAppSettings
        }
    }
} catch {
    Write-Host "FixFlow shared appsettings update failed: $($_.Exception.Message)"
}

try {
    if (-not [string]::IsNullOrWhiteSpace($PublishDir) -and (Is-RootedPath $PublishDir)) {
        $webConfig = Safe-Combine $PublishDir "web.config"
        if ([string]::IsNullOrWhiteSpace($webConfig)) {
            throw "Unable to build web.config path."
        }
        if (Test-Path $webConfig) {
            [xml]$doc = Get-Content -Path $webConfig
            $nodes = $doc.SelectNodes("//aspNetCore/environmentVariables/environmentVariable")
            if ($nodes -and $nodes.Count -gt 0) {
                $seen = @{}
                $changedWeb = $false
                for ($i = $nodes.Count - 1; $i -ge 0; $i--) {
                    $name = $nodes[$i].GetAttribute("name")
                    if ([string]::IsNullOrWhiteSpace($name)) {
                        continue
                    }
                    if ($seen.ContainsKey($name)) {
                        $null = $nodes[$i].ParentNode.RemoveChild($nodes[$i])
                        $changedWeb = $true
                    } else {
                        $seen[$name] = $true
                    }
                }

                if ($changedWeb) {
                    $doc.Save($webConfig)
                }
            }
        }
    }
} catch {
    Write-Host "FixFlow web.config cleanup failed: $($_.Exception.Message)"
}
