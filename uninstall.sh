#!/bin/zsh
# Restores the original Game.dll (removes the fix).
# Override the game path if needed:  CS2_DATA="/path/to/.../Cities2_Data" ./uninstall.sh
set -e
CS2_DATA="${CS2_DATA:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data}"
MAN="$CS2_DATA/Managed"

if pgrep -fil "Cities2|Cities Skylines" 2>/dev/null | grep -qiv "pgrep"; then
  echo "ERROR: CS2 is running. Close it first."; exit 1
fi
if [ -f "$MAN/Game.dll.orig" ]; then
  cp "$MAN/Game.dll.orig" "$MAN/Game.dll"
  echo "Original Game.dll restored (fix removed). Backup kept."
else
  echo "No Game.dll.orig backup found. Use Steam 'Verify integrity of game files' to restore the original."
fi
