#!/bin/zsh
# Builds Game.dll.patched from your ORIGINAL Game.dll (reproducible, version-independent).
# Requirements: .NET 9 SDK (`dotnet`) and Mono (`monodis`).  (Install: `brew install --cask dotnet-sdk` + `brew install mono`)
#
# Override the game path if your CrossOver bottle/location differs:
#   CS2_DATA="/path/to/Cities Skylines II/Cities2_Data" ./patch.sh
set -e
HERE="${0:A:h}"
CS2_DATA="${CS2_DATA:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data}"
MAN="$CS2_DATA/Managed"
export PATH="$PATH:/opt/homebrew/bin:$HOME/.dotnet/tools"
export CS2_MANAGED="$MAN"

[ -d "$MAN" ] || { echo "ERROR: Managed dir not found:"; echo "  $MAN"; echo "Set CS2_DATA to your Cities2_Data path."; exit 1; }
command -v dotnet  >/dev/null || { echo "ERROR: 'dotnet' (.NET 9 SDK) not found."; exit 1; }
command -v monodis >/dev/null || { echo "ERROR: 'monodis' (Mono) not found. Install Mono."; exit 1; }

# Source = the unmodified original Game.dll (prefer our backup if present)
ORIG="$MAN/Game.dll.orig"; [ -f "$ORIG" ] || ORIG="$MAN/Game.dll"
echo "Source: $ORIG"

# Build the patcher if needed
PATCHER="$HERE/patcher/bin/Release/net9.0/patcher.dll"
[ -f "$PATCHER" ] || ( cd "$HERE/patcher" && dotnet build -c Release -v q )

# All top-level ECS *System types EXCEPT Game.Net / Game.Tools / Game.Objects.
# (the faulty height logic lives in those 3 namespaces -> they stay managed while the Net tool is active)
LIST=$(monodis --typedef "$ORIG" 2>/dev/null \
  | grep -oE "Game\.[A-Za-z]+(\.[A-Za-z]+)*\.[A-Za-z]+System \(" \
  | sed 's/ (//' | grep -vE "/" \
  | grep -vE "^Game\.(Net|Tools|Objects)\." | sort -u | paste -sd, -)
echo "Systems re-enabled to Burst: $(echo "$LIST" | tr ',' '\n' | grep -c .)"

dotnet "$PATCHER" smart "$ORIG" "$HERE/Game.dll.patched" "$LIST" | tail -2
echo "DONE: built $HERE/Game.dll.patched  ->  now run ./install.sh"
