# Pillars1Speedhack

A configurable **time-acceleration ("fast-forward") mod** for **Pillars of Eternity 1**.

Speeds up the whole game — exploration, dialogue-free travel, and **combat** (which the vanilla
"Fast" speed refuses to do, and caps at 1.8×). Bind a hold key and/or a toggle key, pick a
multiplier up to 10×, and blast through the slow parts. Pause and inventory still freeze time
normally.

An in-game overlay (default **`F10`**) lets you set the multiplier and rebind keys — no external
tool, no CheatEngine, no separate launcher.

---

## Features

- Accelerate everything by a configurable **1.25×–10×** multiplier.
- Works **in combat** (unlike the built-in Fast speed).
- Two independent keys: **hold-to-accelerate** and **toggle-acceleration** (+ a Clear button).
- In-game overlay to tailor the speed and keys; settings persist across sessions.
- Respects pause and inventory freezes.
- **Space always unpauses** a paused game, even if Space is not bound to Pause. It does not pause
  the game unless your normal keybinds do that.
- Optional **skip intro movies** toggle, on by default.
- Tiny footprint: one sidecar DLL called once per frame from `GameState.Update()`.

---

## Installation

Requires Pillars of Eternity 1 (Windows).

### Option A — Quick install (no compiling) — recommended

1. Download **`Pillars1Speedhack-v1.1.0.zip`** from the
   [Releases](https://github.com/countlessbats/Pillars1Speedhack/releases) page and extract it.
2. **Close the game.**
3. **Double-click `install.bat`** and approve the administrator prompt.

That's it — no compiler, no .NET SDK, no runtime install. `install.bat` runs the bundled
`install.ps1`, which copies the prebuilt sidecar, backs up `Assembly-CSharp.dll` once, and injects
the hook using the bundled (MIT-licensed) `Mono.Cecil.dll`. It auto-detects a Steam install (and
prompts for the path if it can't find one), and is safe to re-run.

```bat
install.bat -GameDir "D:\Games\Pillars of Eternity"
```

(Prefer PowerShell directly? `powershell -ExecutionPolicy Bypass -File .\install.ps1 -GameDir "<path>"`.)

4. Launch the game and press **`F10`** to open the Time Accelerator menu.

### Option B — Build from source (developers)

Needs the Roslyn C# compiler (`csc.exe`) and, for the hook, either `install.ps1` or the Mono.Cecil
patcher.

```powershell
./build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Pillars of Eternity"
# then run install.ps1 once to inject the GameState.Update hook
```

---

## Using it

- Press your **Toggle** key (default `\`) to switch acceleration on/off, or hold your **Hold** key
  (default unbound) for momentary fast-forward. A `>> Time xN` badge shows when it's active.
- Press **`F10`** to open the menu: drag the slider or use the `2×`/`3×`/`5×` presets, and click a
  keybind row to rebind it (`Esc` cancels a rebind). **Clear both accelerate keys** unbinds them.
- Press **Space** while paused to unpause, regardless of whether Space is bound to Pause.
- Settings save to `…\AppData\LocalLow\Obsidian Entertainment\Pillars of Eternity\LoomTimeAccelerator.cfg`.

---

## Backups & uninstalling

`install.ps1` saves your original assembly to `Assembly-CSharp.dll.speedhack-backup` (once).
To uninstall:

1. Restore `Assembly-CSharp.dll` from `Assembly-CSharp.dll.speedhack-backup`.
2. Delete `PillarsOfEternity_Data/Managed/LoomTimeAccelerator.dll`.

Steam → Verify integrity of game files also restores the original assembly.

---

## Notes & caveats

- Because it drives Unity's `Time.timeScale`, sped-up audio is pitched up — same as any
  fast-forward. Pause/inventory (timescale 0) are untouched.
- Very high multipliers can make fast-paced combat hard to control; 2×–4× is a comfortable range.
- Coexists with other `GameState.Update` sidecar mods (e.g. Reaper Stance): each injects its own
  call, and this installer only adds its own hook once.
- A future game patch that changes `GameState.Update` could require a reinstall.
- **Internal name:** the sidecar/DLL/hook use the identifier `LoomTimeAccelerator` on purpose (the
  injected call must match the DLL). The mod itself is "Pillars1Speedhack".

---

## License

[MIT](LICENSE). This repository contains only original mod code — no Obsidian Entertainment game
code or assets. You must own Pillars of Eternity to use it.
