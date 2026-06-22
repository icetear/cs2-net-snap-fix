#!/bin/zsh
# Builds Game.dll.patched from your ORIGINAL (unpatched) Game.dll (reproducible, version-independent).
# Requirements: .NET 9 SDK (`dotnet`) and Mono (`monodis`).  (Install: `brew install --cask dotnet-sdk` + `brew install mono`)
#
# Override the game path if your CrossOver bottle/location differs:
#   CS2_DATA="/path/to/Cities Skylines II/Cities2_Data" ./patch.sh
#
# Source selection (hardened against a stale backup after a game update):
#   - If the installed Game.dll is already OUR patched build, the original lives only in the backup
#     Game.dll.orig  ->  build from that.
#   - Otherwise the installed Game.dll IS the current original (fresh install or right after a game
#     update)  ->  build from it and ignore any (possibly stale) Game.dll.orig.
#   This guarantees we never patch a leftover backup from an older game version.
set -e
HERE="${0:A:h}"
CS2_DATA="${CS2_DATA:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data}"
MAN="$CS2_DATA/Managed"
export PATH="$PATH:/opt/homebrew/bin:$HOME/.dotnet/tools"
export CS2_MANAGED="$MAN"

[ -d "$MAN" ] || { echo "ERROR: Managed dir not found:"; echo "  $MAN"; echo "Set CS2_DATA to your Cities2_Data path."; exit 1; }
command -v dotnet  >/dev/null || { echo "ERROR: 'dotnet' (.NET 9 SDK) not found."; exit 1; }
command -v monodis >/dev/null || { echo "ERROR: 'monodis' (Mono) not found. Install Mono."; exit 1; }

LIVE="$MAN/Game.dll"          # the currently installed Game.dll
BACKUP="$MAN/Game.dll.orig"   # our backup of the original
PATCHED="$HERE/Game.dll.patched"

# Pick the unpatched ORIGINAL to build from.
if [ -f "$PATCHED" ] && cmp -s "$LIVE" "$PATCHED"; then
  # installed Game.dll is already our patch -> the original is in the backup
  ORIG="$BACKUP"
  [ -f "$ORIG" ] || { echo "ERROR: installed Game.dll is already patched but no Game.dll.orig backup exists."; \
                      echo "Restore the original via Steam 'Verify integrity of game files', then re-run ./patch.sh."; exit 1; }
else
  # installed Game.dll is the current original (fresh install / right after a game update)
  ORIG="$LIVE"
fi
echo "Source (original): $ORIG"

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

dotnet "$PATCHER" smart "$ORIG" "$PATCHED" "$LIST" | tail -2

# Record the source original's hash so install.sh can detect a stale patch (built for another version).
shasum -a 256 "$ORIG" | cut -d' ' -f1 > "$PATCHED.srchash"
echo "DONE: built $PATCHED  ->  now run ./install.sh"
