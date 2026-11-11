#!/bin/sh
# Every installer check, with no release scan skipped. Run from the product folder. Stops at the first failure.
# DOTNET overrides the SDK. Output stays in installer/out, which is ignored.
set -e
# An explicit DOTNET wins; then the maintainers' tool folder when it exists; then the `dotnet` on the PATH.
if [ -z "${DOTNET:-}" ]; then
  if [ -x /tmp/hs-prototype-tools/dotnet/dotnet ]; then DOTNET=/tmp/hs-prototype-tools/dotnet/dotnet; else DOTNET="$(command -v dotnet)"; fi
fi
"$DOTNET" build installer/SoloStone.Launcher/SoloStone.Launcher.csproj -c Release -p:NuGetAudit=false --output installer/out/app -nologo -v q
"$DOTNET" build installer/SoloStone.Linux/SoloStone.Linux.csproj -c Release -p:NuGetAudit=false --artifacts-path installer/out/linux -nologo -v q
"$DOTNET" build plugin/OfflineProbe.csproj -c Debug -p:EngineSource=true -p:Installer=true -p:NuGetAudit=false --artifacts-path installer/out/plugin-release -nologo -v q
"$DOTNET" build installer/SoloStone.Launcher.Tests/SoloStone.Launcher.Tests.csproj -c Release --artifacts-path installer/out/gate -p:NuGetAudit=false -nologo -v q
SOLOSTONE_REQUIRE_ALL=1 "$DOTNET" installer/out/gate/bin/SoloStone.Launcher.Tests/release/SoloStone.Launcher.Tests.dll
/usr/bin/python3 -I installer/test_loader_inputs.py
/usr/bin/python3 -I installer/test_stage.py
/usr/bin/python3 -I installer/test_package_linux.py
