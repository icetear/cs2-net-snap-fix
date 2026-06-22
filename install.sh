#!/bin/zsh
# Installs the fix: keeps Game.dll.orig in sync with the current original, copies in the patched dll,
# and ensures Burst is enabled.  Override the game path if needed:  CS2_DATA="/path/.../Cities2_Data" ./install.sh
#
# Backup hardening: Game.dll.orig ALWAYS tracks the currently installed original.
#   - If the installed Game.dll is already our patch -> keep the existing backup (never back up a patch).
#   - Otherwise the installed Game.dll is the current original -> (re)create the backup from it
#     (this also captures a fresh original right after a game update).
# It also refuses to install a Game.dll.patched that was built from a DIFFERENT game version.
set -e
HERE="${0:A:h}"
CS2_DATA="${CS2_DATA:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data}"
MAN="$CS2_DATA/Managed"
PLG="$CS2_DATA/Plugins/x86_64"

if pgrep -fil "Cities2|Cities Skylines" 2>/dev/null | grep -qiv "pgrep"; then
  echo "ERROR: CS2 is running. Close it first."; exit 1
fi
[ -d "$MAN" ] || { echo "ERROR: Managed dir not found: $MAN (set CS2_DATA)"; exit 1; }

LIVE="$MAN/Game.dll"
BACKUP="$MAN/Game.dll.orig"
PATCHED="$HERE/Game.dll.patched"
[ -f "$PATCHED" ] || { echo "ERROR: Game.dll.patched missing. Run ./patch.sh first."; exit 1; }

# Keep the backup pointing at the current original.
if cmp -s "$LIVE" "$PATCHED"; then
  # installed Game.dll is already our patch -> the original lives only in the backup
  [ -f "$BACKUP" ] || { echo "ERROR: Game.dll is already patched but no Game.dll.orig backup exists."; \
                        echo "Restore the original via Steam 'Verify integrity of game files', then re-run."; exit 1; }
  ORIG="$BACKUP"
  echo "Game.dll is already patched; keeping existing backup."
else
  # installed Game.dll is the current original -> refresh the backup from it
  cp "$LIVE" "$BACKUP"
  ORIG="$BACKUP"
  echo "Backup (re)created from current original: Game.dll.orig"
fi

# Refuse to install a patched build made from a different game version.
if [ -f "$PATCHED.srchash" ]; then
  want=$(cat "$PATCHED.srchash")
  have=$(shasum -a 256 "$ORIG" | cut -d' ' -f1)
  if [ "$want" != "$have" ]; then
    echo "ERROR: Game.dll.patched was built from a different Game.dll (game updated?)."
    echo "  patched built from: $want"
    echo "  current original:   $have"
    echo "Run ./patch.sh first to rebuild against the current version."; exit 1
  fi
fi

cp "$PATCHED" "$LIVE"
echo "Patched Game.dll installed."

# The fix needs Burst ENABLED. Undo the rename-workaround if present.
if [ -f "$PLG/lib_burst_generated.dll.off" ] && [ ! -f "$PLG/lib_burst_generated.dll" ]; then
  mv "$PLG/lib_burst_generated.dll.off" "$PLG/lib_burst_generated.dll"
  echo "Re-enabled Burst (lib_burst_generated.dll)."
fi

echo "DONE. Launch the game and drag an elevated network over a ground structure to verify."
echo "Revert anytime: ./uninstall.sh"
