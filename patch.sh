#!/bin/zsh
# Builds Game.dll.patched from your ORIGINAL (unpatched) Game.dll (reproducible, version-independent).
# Requirements: .NET 9 SDK (`dotnet`) and Mono (`monodis`).  (Install: `brew install --cask dotnet-sdk` + `brew install mono`)
#
# Modes:
#   ./patch.sh          performant fix (default): Burst stays ON globally. Only the three network systems
#                       that compute the snap/height math (NetToolSystem, CourseSplitSystem, ValidationSystem)
#                       run managed while the Net tool is active, and their jobs are forced to complete WHILE
#                       Burst is still off, so they execute parallel-managed (correct) -> snap bug fixed, and
#                       simulation/rendering keep full FPS.
#                       Escalation without editing this script:
#                         CS2_NETSNAP_EXTRA="Game.Tools.FooSystem,..." ./patch.sh   # add more systems
#                         CS2_NETSNAP_FULL=1 ./patch.sh                             # wrap ALL Game.Net/Tools/Objects
#   ./patch.sh smart    fallback: a global Burst toggle while the tool is active. Simpler, but it makes the
#                       whole game run managed while the Net tool is selected (~50% FPS). Use only if the
#                       default misbehaves on your setup.
#
# Override the game path if your CrossOver bottle/location differs:
#   CS2_DATA="/path/to/Cities Skylines II/Cities2_Data" ./patch.sh
set -e
MODE="${1:-fast}"   # default = performant (gated) fix; 'smart' = global-toggle fallback
case "$MODE" in fast|gated|smart) ;; *) echo "Unknown mode '$MODE' (use: <none> for the performant fix, or 'smart')"; exit 1;; esac
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

# Build the patcher first (it is also used for robust patch detection below).
PATCHER="$HERE/patcher/bin/Release/net9.0/patcher.dll"
[ -f "$PATCHER" ] || ( cd "$HERE/patcher" && dotnet build -c Release -v q )

# Pick the unpatched ORIGINAL to build from. Detection uses an IL marker (`ispatched`), NOT a byte
# compare against Game.dll.patched: a byte compare misfires when you switch modes (smart <-> default),
# and could then back up a patched dll over the real original.
if [ "$(dotnet "$PATCHER" ispatched "$LIVE" 2>/dev/null | tail -1)" = "patched" ]; then
  # installed Game.dll is already our patch -> the original is in the backup
  ORIG="$BACKUP"
  [ -f "$ORIG" ] || { echo "ERROR: installed Game.dll is already patched but no Game.dll.orig backup exists."; \
                      echo "Restore the original via Steam 'Verify integrity of game files', then re-run ./patch.sh."; exit 1; }
  # The backup MUST be a real original (not itself a patch), otherwise we'd patch a patch.
  if [ "$(dotnet "$PATCHER" ispatched "$ORIG" 2>/dev/null | tail -1)" = "patched" ]; then
    echo "ERROR: backup Game.dll.orig is itself patched (not an original)."; \
    echo "Restore the original via Steam 'Verify integrity of game files', then re-run ./patch.sh."; exit 1
  fi
else
  # installed Game.dll is the current original (fresh install / right after a game update)
  ORIG="$LIVE"
fi
echo "Source (original): $ORIG"

if [ "$MODE" = "smart" ]; then
  # All top-level ECS *System types EXCEPT Game.Net / Game.Tools / Game.Objects get Burst re-enabled.
  LIST=$(monodis --typedef "$ORIG" 2>/dev/null \
    | grep -oE "Game\.[A-Za-z]+(\.[A-Za-z]+)*\.[A-Za-z]+System \(" \
    | sed 's/ (//' | grep -vE "/" \
    | grep -vE "^Game\.(Net|Tools|Objects)\." | sort -u | paste -sd, -)
  echo "Mode: smart (fallback) | systems re-enabled to Burst: $(echo "$LIST" | tr ',' '\n' | grep -c .)"
  dotnet "$PATCHER" smart "$ORIG" "$PATCHED" "$LIST" | tail -2
else
  # Performant (gated) fix. Only the network systems that actually compute the snap/height math run
  # managed, and only while the Net tool is active; their jobs are completed inside the finally WHILE
  # Burst is still off, so they execute parallel-managed (correct). Everything else stays on Burst.
  #
  # System list = the confirmed minimal set: NetToolSystem + CourseSplitSystem (the CourseHeight* jobs) +
  # ValidationSystem. NetToolSystem alone is NOT enough in-game. Wrapping just these three (instead of all
  # ~108 Game.Net/Tools/Objects *System types) avoids dozens of forced main-thread .Complete()/frame while
  # building -> noticeably smoother, same fix result. (Matches alien-agent/cs2-macos-patcher's findings.)
  if [ "${CS2_NETSNAP_FULL:-0}" = "1" ]; then
    # Escalation: if a future game version moves the snap math into a different network system, the full
    # filter covers every Game.Net/Tools/Objects *System (more sync while building, but broader coverage).
    LIST=$(monodis --typedef "$ORIG" 2>/dev/null \
      | grep -oE "Game\.(Net|Tools|Objects)\.[A-Za-z]+System \(" \
      | sed 's/ (//' | grep -vE "/" | sort -u | paste -sd, -)
  else
    LIST="Game.Tools.NetToolSystem,Game.Tools.CourseSplitSystem,Game.Tools.ValidationSystem"
    # Add extra systems without editing this script: CS2_NETSNAP_EXTRA="Full.Type.Name,..."
    [ -n "${CS2_NETSNAP_EXTRA:-}" ] && LIST="$LIST,$CS2_NETSNAP_EXTRA"
  fi
  echo "Mode: performant (default) | network systems: $(echo "$LIST" | tr ',' '\n' | grep -c .)"
  dotnet "$PATCHER" gated "$ORIG" "$PATCHED" "$LIST" | tail -2
fi

# Record the source original's hash so install.sh can detect a stale patch (built for another version).
shasum -a 256 "$ORIG" | cut -d' ' -f1 > "$PATCHED.srchash"
echo "DONE: built $PATCHED  ->  now run ./install.sh"
