$errDir = "testdata/err"; $outDir = "testdata/err/_seiton_actual"
$yamlFiles = Get-ChildItem -Path $errDir -Filter "*.yaml" | Sort-Object Name
$missingDetections = @()
foreach ($yaml in $yamlFiles) {
    $name = $yaml.BaseName
    $outFile = Join-Path $errDir "$name.out"
    $actualFile = Join-Path $outDir "$name.actual"
    if (-not (Test-Path $outFile)) { continue }
    if (-not (Test-Path $actualFile)) { continue }
    $expectedLines = @(Get-Content $outFile | Where-Object { $_.Trim() -ne "" })
    $actualLines = @(Get-Content $actualFile | Where-Object { $_.Trim() -ne "" })
    foreach ($exp in $expectedLines) {
        if ($exp -match '^[^:]+:(\d+):(\d+):(.+)$') {
            $eLine = $matches[1]; $eCol = $matches[2]; $eRest = $matches[3].Trim()
            $eRuleId = ""; if ($eRest -match '\[([^\]]+)\]\s*$') { $eRuleId = $matches[1] }
            $found = $false
            foreach ($act in $actualLines) {
                if ($act -match '^[^:]+:(\d+):(\d+):(.+)$') {
                    if ($matches[1] -eq $eLine -and $matches[2] -eq $eCol) { $found = $true; break }
                }
            }
            if (-not $found) {
                $sameLineDiffCol = $false
                foreach ($act in $actualLines) {
                    if ($act -match '^[^:]+:(\d+):(\d+):(.+)$') {
                        if ($matches[1] -eq $eLine) {
                            $sameLineDiffCol = $true
                            $missingDetections += "$name | WRONG_COL | expected=$eLine`:$eCol | actual=$($matches[1])`:$($matches[2]) | [$eRuleId]"
                            break
                        }
                    }
                }
                if (-not $sameLineDiffCol) {
                    $missingDetections += "$name | MISSING | line:col=$eLine`:$eCol | [$eRuleId] | $eRest"
                }
            }
        }
    }
}
$missingDetections | ForEach-Object { $_ } | Out-File -FilePath "testdata/err/_comparison_result.txt" -Encoding UTF8
"Total: $($missingDetections.Count) issues" | Out-File -FilePath "testdata/err/_comparison_result.txt" -Append -Encoding UTF8
