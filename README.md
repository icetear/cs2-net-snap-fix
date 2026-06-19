# CS2 Net-Snap Fix (Apple Silicon / Rosetta / CrossOver)

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
Instead of disabling Burst permanently, it patches the managed assembly `Game.dll` so Burst is turned
off **only while the Net tool is active**, and even then only for the network systems — everything else
(traffic, pathfinding, citizens, rendering, …) keeps running on Burst.

Technically:
- `NetToolSystem.OnStartRunning` sets `BurstCompiler.Options.EnableBurstCompilation = false`
  (and a newly added `OnStopRunning` sets it back to `true`). The setter flips
  `JobsUtility.JobCompilerEnabled`, so newly scheduled jobs run managed.
- Every job-scheduling system **except** `Game.Net` / `Game.Tools` / `Game.Objects` gets its `OnUpdate`
  wrapped in `try/finally` that **re-enables** Burst for that system's update and restores the previous
  state afterwards (save/restore).

So while the Net tool is open, only the network-build systems run managed (that's where the faulty
height logic lives → bug fixed), while the heavy systems stay on Burst → FPS stays good. When the tool
is not active, Burst is fully on → no performance impact. `lib_burst_generated.dll` stays **enabled**.

The actual mistranslated SSE instruction was not isolated (not needed for the fix — the managed path is
provably correct). The likely culprit is a `math.all(bool3)` reduction over a `float3` in
`MathUtils.Intersect(Bounds3, Bounds3)` used during network node merging.

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
Steam overwrites `Game.dll` on updates and on "Verify integrity of game files". Re-apply:
```sh
./uninstall.sh    # optional, if an old backup is in the way
./patch.sh        # re-patch the fresh original
./install.sh
```
`patch.sh` re-derives everything from your current `Game.dll`, so it adapts to new game versions
(as long as the relevant types/methods still exist).

## How it's built (for the curious)
`patcher/` is a small [Mono.Cecil](https://github.com/jbevain/cecil) tool (.NET 9). `patch.sh` enumerates
all ECS systems with `monodis`, excludes `Game.Net`/`Game.Tools`/`Game.Objects`, and asks the patcher's
`smart` mode to: add the global Net-tool toggle and wrap the remaining job-scheduling systems'
`OnUpdate` to re-enable Burst. Systems whose `OnUpdate` has its own try/catch, or that schedule no jobs,
are skipped (safety + no benefit).

## Caveats
- Re-apply after every game update / Steam file verification.
- Mod compatibility: only a runtime flag is toggled around system updates (no game logic changed), so
  risk is low; untested with mods that touch the same systems or `BurstCompiler.Options`.
- Long-term stability testing is still ongoing.
- Use at your own risk. This modifies a game file; always keep the `Game.dll.orig` backup.

## License
MIT (see `LICENSE`) — applies to the tooling in this repo only, not to any Colossal Order game files.
