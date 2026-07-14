# CS2 Net-Snap Fix (Apple Silicon / Rosetta / CrossOver)

> ## 👉 Recommended: use [alien-agent/cs2-macos-patcher](https://github.com/alien-agent/cs2-macos-patcher) instead
>
> That project fixes **all** the known macOS/CrossOver problems in Cities: Skylines II — not just this one.
> Alongside the elevated-network snap bug (which it fixes with the **same approach** as this repo, and
> credits this repo for finding the root cause), it also fixes Paradox Mods downloads, mod
> loading/installation, the pause-menu crash and spurious error dialogs. **If you just want a fully working
> game on Apple Silicon, use theirs** — it's a superset of this fix.
>
> This repository stays up as the **focused, standalone fix for the snap bug only**, and as the place where
> the root cause was originally tracked down.

Fixes the bug where **elevated networks (bridges, power lines, water pipes) snap down onto the
structure below them** in Cities: Skylines II — because the vertical (Y / height) check is ignored
(it behaves purely 2D). Affects CS2 running on Apple Silicon Macs via CrossOver/Rosetta 2.

This patch keeps the game's performance: it only disables Burst for the small set of network systems
while the **Net tool is active**, so the heavy simulation keeps running on Burst.

## The bug
CS2 ships an x86-64 **Unity Burst** library (`lib_burst_generated.dll`) with SIMD (SSE) code for its
ECS jobs. On Apple Silicon the game runs through Rosetta 2, which **mistranslates one SIMD routine** in
the network height/geometry check and drops the Y lane. Result: an elevated network is treated as being
at the same height as whatever is below it, so it connects/snaps down to the ground structure regardless
of the height difference (e.g. a bridge 50 m up still snaps to a road below).

- The exact same binary is correct on native Windows.
- The game's **managed C# path** (Mono) computes it correctly.

A known sledgehammer workaround is to rename `lib_burst_generated.dll` so the whole game falls back to
the managed path — this fixes the bug but costs **~50% FPS permanently** (the entire simulation then
runs without Burst).

## What this fix does
Instead of disabling Burst permanently, it patches the managed assembly `Game.dll` so that — **only
while the Net tool is active** — just the network systems run managed, while everything else (traffic,
pathfinding, citizens, rendering, the Unity engine systems) keeps running on Burst.

Technically:
- A static flag `NetToolSystem.s_ForceManaged` is set to `true` in `OnStartRunning` and back to `false`
  in a newly added `OnStopRunning` — it simply marks "the Net tool is active".
- Each targeted network system gets its `OnUpdate` wrapped so that, while the flag is set, it turns
  `BurstCompiler.Options.EnableBurstCompilation` off for the duration of that update, schedules its jobs as
  usual (in parallel), then **forces them to run via `JobHandle.Complete()` while Burst is still off**, and
  finally restores the previous flag state. By default only the three systems that actually compute the
  snap/height math are wrapped — `NetToolSystem`, `CourseSplitSystem` and `ValidationSystem` (the confirmed
  minimal set; `NetToolSystem` alone is not enough in-game). Wrapping just these three, rather than every
  `Game.Net`/`Game.Tools`/`Game.Objects` system, avoids dozens of forced main-thread completes per frame
  while building. Set `CS2_NETSNAP_EXTRA="Type,..."` to add systems, or `CS2_NETSNAP_FULL=1` to wrap them all.

That last step is the key. `EnableBurstCompilation` only affects a job when it actually *executes*, and
network jobs execute asynchronously — *after* the update returns. Just toggling the flag around the
update (an earlier attempt) didn't work: the jobs ran later, back on Burst, and the bug returned. And
forcing each job to run synchronously/single-threaded crashes parallel jobs. Completing them inside the
`finally` runs them **right then, parallel-managed** — correct, and crash-free — before Burst is switched
back on. Tool systems are completed via their returned `JobHandle`; other systems via `this.Dependency`.

So while the Net tool is open, only the network jobs run managed (that's where the faulty height logic
lives → bug fixed); simulation, rendering and the Unity engine systems stay on Burst → FPS stays good.
When the tool is not active it's a no-op → zero performance impact. `lib_burst_generated.dll` stays
**enabled**.

A simpler **`smart` fallback** (`./patch.sh smart`) is also available: it just flips the *global* Burst
flag while the tool is selected. It fixes the bug too, but makes the whole game run managed while the
tool is open (~50% FPS), so use it only if the default misbehaves on your setup.

The actual mistranslated SSE instruction was not isolated (not needed for the fix — the managed path is
provably correct). The culprit is a SIMD routine in the network height/geometry check that drops the Y
lane, reached from one of the parallel network-build jobs.

## Requirements
- macOS on Apple Silicon, CS2 via CrossOver (Mono scripting backend — the default for CS2).
- **.NET 9 SDK** (`dotnet`) and **Mono** (`monodis`) to build/run the patcher:
  - `brew install --cask dotnet-sdk`
  - `brew install mono`

> No copyrighted game files are distributed in this repo. You build the patch from **your own**
> `Game.dll`. The patcher only rewrites a few method bodies; it does not redistribute Colossal Order code.

## Install
```sh
git clone https://github.com/icetear/cs2-net-snap-fix.git
cd cs2-net-snap-fix
./patch.sh        # builds Game.dll.patched from your original Game.dll
./install.sh      # close the game first; backs up Game.dll -> Game.dll.orig, installs the patch
```
Launch CS2, pick a network, raise its elevation, and drag the end over an existing ground road — it
should no longer snap down.

If your game is in a non-standard location, set `CS2_DATA`:
```sh
CS2_DATA="/path/to/Cities Skylines II/Cities2_Data" ./patch.sh
CS2_DATA="/path/to/Cities Skylines II/Cities2_Data" ./install.sh
```

## Uninstall
```sh
./uninstall.sh    # restores the original Game.dll
```

## After a game update
Steam overwrites `Game.dll` on updates and on "Verify integrity of game files". Just re-apply:
```sh
./patch.sh        # re-patch the fresh original
./install.sh      # refreshes the Game.dll.orig backup to the new original
```
`patch.sh`/`install.sh` auto-detect — via an **IL marker** in the assembly, not a byte compare — whether
the installed `Game.dll` is a fresh original or already our patch, and pull the source/backup accordingly.
This is robust even when you switch between the default and `smart` modes, and a stale or corrupt
`Game.dll.orig` can no longer poison the build (a backup that is itself a patch is refused). `install.sh`
also refuses to install a patch built from a different game version (it records the source hash in
`Game.dll.patched.srchash`). By default `patch.sh` targets the fixed minimal system list, which stays
valid across versions as long as those three types still exist; if a future version moves the snap math
elsewhere, use `CS2_NETSNAP_EXTRA=...` or `CS2_NETSNAP_FULL=1` (which re-derives the full list from your
current `Game.dll`).

## How it's built (for the curious)
`patcher/` is a small [Mono.Cecil](https://github.com/jbevain/cecil) tool (.NET 9). By default `patch.sh`
passes the three snap systems (`NetToolSystem`, `CourseSplitSystem`, `ValidationSystem`) — extendable via
`CS2_NETSNAP_EXTRA`, or `CS2_NETSNAP_FULL=1` to enumerate every `Game.Net`/`Game.Tools`/`Game.Objects`
system with `monodis` as before — and asks the patcher's `gated` mode to:
add the `s_ForceManaged` flag plus the `OnStartRunning`/`OnStopRunning` toggle on `NetToolSystem`, and
wrap each network system's job-scheduling `OnUpdate` with the flag-gated "disable Burst → schedule →
complete-while-off → restore" logic described above (tool systems via their returned `JobHandle`, others
via `this.Dependency`). Systems whose `OnUpdate` has its own try/catch are skipped (a nested try/finally
would corrupt the IL). The `smart` fallback instead re-enables Burst on every *non*-network system.

## Caveats
- Re-apply after every game update / Steam file verification.
- Mod compatibility: only a runtime flag is toggled around system updates (no game logic changed), so
  risk is low; untested with mods that touch the same systems or `BurstCompiler.Options`.
- Long-term stability testing is still ongoing.
- Use at your own risk. This modifies a game file; always keep the `Game.dll.orig` backup.

## License
MIT (see `LICENSE`) — applies to the tooling in this repo only, not to any Colossal Order game files.
