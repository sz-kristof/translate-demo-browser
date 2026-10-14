# Downloads Firefox Translations models from Mozilla's public model registry
# (the same files Firefox uses) and verifies each file's SHA-256.
#
#   ./fetch-models.ps1                      # en->hu and hu->en into ./models
#   ./fetch-models.ps1 -Pairs en-de,de-en -Dest C:\models

param(
    [string[]]$Pairs = @("en-hu", "hu-en"),
    [string]$Dest = (Join-Path $PSScriptRoot "models")
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$records = (Invoke-RestMethod "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models/records").data
$cdn = "https://firefox-settings-attachments.cdn.mozilla.net/"

foreach ($pair in $Pairs) {
    $src, $tgt = $pair.Split("-")
    $files = $records | Where-Object { $_.fromLang -eq $src -and $_.toLang -eq $tgt -and -not $_.filter_expression }
    if (-not $files) { Write-Warning "no model for $pair"; continue }
    # newest version that has all three files
    $version = ($files | Group-Object version | Where-Object Count -ge 3 |
        Sort-Object { [version]($_.Name -replace '[^0-9.]', '') } -Descending | Select-Object -First 1).Name
    $dir = Join-Path $Dest "$src$tgt"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($f in $files | Where-Object version -eq $version) {
        $out = Join-Path $dir $f.name
        Invoke-WebRequest ($cdn + $f.attachment.location) -OutFile $out -UseBasicParsing
        $hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $f.attachment.hash) { Remove-Item $out; throw "checksum mismatch: $($f.name)" }
        Write-Host ("{0,-40} {1,6:N1} MB  ok" -f "$src$tgt/$($f.name)", ($f.attachment.size / 1MB))
    }
}
