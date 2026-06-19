#!/bin/zsh
# Installs the fix: backs up your Game.dll once, copies in the patched one, ensures Burst is enabled.
# Override the game path if needed:  CS2_DATA="/path/to/.../Cities2_Data" ./install.sh
set -e
HERE="${0:A:h}"
CS2_DATA="${CS2_DATA:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data}"
MAN="$CS2_DATA/Managed"
PLG="$CS2_DATA/Plugins/x86_64"

if pgrep -fil "Cities2|Cities Skylines" 2>/dev/null | grep -qiv "pgrep"; then
  echo "ERROR: CS2 is running. Close it first."; exit 1
fi
[ -d "$MAN" ] || { echo "ERROR: Managed dir not found: $MAN (set CS2_DATA)"; exit 1; }
[ -f "$HERE/Game.dll.patched" ] || { echo "ERROR: Game.dll.patched missing. Run ./patch.sh first."; exit 1; }

# Back up the original ONLY once (so we never overwrite the backup with an already-patched dll)
if [ ! -f "$MAN/Game.dll.orig" ]; then
  cp "$MAN/Game.dll" "$MAN/Game.dll.orig"
  echo "Backup created: Game.dll.orig"
fi

cp "$HERE/Game.dll.patched" "$MAN/Game.dll"
echo "Patched Game.dll installed."

# The fix needs Burst ENABLED. Undo the rename-workaround if present.
if [ -f "$PLG/lib_burst_generated.dll.off" ] && [ ! -f "$PLG/lib_burst_generated.dll" ]; then
  mv "$PLG/lib_burst_generated.dll.off" "$PLG/lib_burst_generated.dll"
  echo "Re-enabled Burst (lib_burst_generated.dll)."
fi

echo "DONE. Launch the game and drag an elevated network over a ground structure to verify."
echo "Revert anytime: ./uninstall.sh"
