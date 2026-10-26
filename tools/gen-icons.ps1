#!/usr/bin/env pwsh
# Regenerates every raster icon asset from the source-of-truth SVG (postern.svg).
#
#   src/Postern.App/Assets/postern.ico            multi-res ICO (.exe ApplicationIcon, vpk --icon, window + tray)
#   src/Postern.App/Assets/postern-dev.ico        the same in Postern (Dev)'s pink
#   src/Postern.App/Assets/postern-attention.ico  the same in amber, while an approval waits
#   src/Postern.App/Assets/icon.png, icon-dev.png 256x256 PNG (in-app logo)
#   landing-icon.png                              512x512 PNG (README header)
#
# Run this after editing postern.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'Postern.IconGen'
dotnet run --project $proj -c Release
