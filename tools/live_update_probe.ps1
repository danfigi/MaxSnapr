# Checks the real update path against the public downloads repo: latest-release lookup, trusted asset URLs,
# checksum file format and an end-to-end SHA-256 match. Read-only; downloads to a temp folder that is removed.
param([string]$Repo = 'danfigi/MaxSnapr-descargas')
$ErrorActionPreference = 'Stop'
$h = @{ 'User-Agent' = 'MaxSnapr-probe'; 'Accept' = 'application/vnd.github+json' }
$r = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $h
"última versión: $($r.tag_name)  (preliminar: $($r.prerelease))"
$setup = $r.assets | Where-Object { $_.name -like 'MaxSnapr-Setup-*.exe' } | Select-Object -First 1
$sums = $r.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
"instalador: $($setup.browser_download_url)"
"sumas:      $($sums.browser_download_url)"
$trusted = $setup.browser_download_url -like "https://github.com/$Repo/releases/download/*"
"origen de confianza: $trusted"
$tmp = Join-Path $env:TEMP ("mx-probe-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
  $s = (Invoke-WebRequest $sums.browser_download_url -UseBasicParsing).Content
  if ($s -is [byte[]]) { $s = [Text.Encoding]::ASCII.GetString($s) }
  $line = ($s -split "`n" | Where-Object { $_ -match [regex]::Escape($setup.name) }).Trim()
  $expected = ($line -split '\s+')[0]
  $file = Join-Path $tmp $setup.name
  Invoke-WebRequest $setup.browser_download_url -OutFile $file -UseBasicParsing
  $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
  "huella publicada: $expected"
  "huella descargada: $actual"
  "COINCIDE: $($expected -eq $actual)"
} finally { Remove-Item $tmp -Recurse -Force }
