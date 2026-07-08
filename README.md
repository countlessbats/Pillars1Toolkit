# Pillars1Toolkit

A compact **quality-of-life toolkit** for **Pillars of Eternity 1**.

Its original feature speeds up the whole game — exploration, dialogue-free travel, and **combat** (which the vanilla
"Fast" speed refuses to do, and caps at 1.8×). Bind a hold key and/or a toggle key, pick a
multiplier up to 10×, and blast through the slow parts. Pause and inventory still freeze time
normally. It also adds small camera and input conveniences.

An in-game overlay (default **`F10`**) lets you set the multiplier and rebind keys — no external
tool, no CheatEngine, no separate launcher.

---

## Features

- Accelerate everything by a configurable **1.25×–10×** multiplier.
- Works **in combat** (unlike the built-in Fast speed).
- Two independent keys: **hold-to-accelerate** and **toggle-acceleration** (+ a Clear button).
- In-game overlay to tailor the speed and keys; settings persist across sessions.
- Respects pause and inventory freezes.
- Optional **extra-close camera zoom** with a closest-zoom slider and quick presets.
- Configurable character-creation **attribute points** and **attribute cap**.
- Configurable **bonus to all skills** for the current primary party.
- **Grant level** button for selected party members, or the whole primary party if nobody is selected.
- **Smart Space key** — a priority model that makes Space do the most useful thing first:
  - **Unpause first, always.** If the game is paused (the real-time-with-pause combat pause), Space
    unpauses and does *nothing else* — regardless of what Space is otherwise bound to, and in any mode.
    Menu/inventory/dialogue freezes are left alone.
  - **End Turn in turn-based combat.** When it's one of *your* characters' turns (and the game isn't
    paused), Space ends that turn and only that turn — it can't also pause. On enemy/environment turns
    Space falls back to its normal behavior, so you can still pause.
  - **Otherwise**, Space keeps its normal binding (by default, pause).
- **Advance dialogue with Space, Enter, or any number key.** At a "Continue" prompt, Space and Enter
  advance it (vanilla) and so does any number key (0–9 or numpad) — no reaching for a specific key.
- Optional **skip intro movies** toggle, on by default.
- Tiny footprint: one sidecar DLL called once per frame from `GameState.Update()`.

---

## Installation

Requires Pillars of Eternity 1 (Windows).

### Option A — Quick install (no compiling) — recommended

1. Download **`Pillars1Toolkit-v1.2.0.zip`** from the
   [Releases](https://github.com/countlessbats/Pillars1Toolkit/releases) page and extract it.
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

4. Launch the game and press **`F10`** to open the Pillars1Toolkit menu.

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
- Press **`F10`** to open the menu: drag the speed slider or use the `2×`/`3×`/`5×` presets, and click a
  keybind row to rebind it (`Esc` cancels a rebind). **Clear both accelerate keys** unbinds them.
- Enable **extra-close camera zoom** and set the closest zoom value. Lower values zoom closer; `Close`
  defaults to `0.20`, and `Extreme` goes to `0.10`.
- Set character-creation attribute points and maximum attribute value. Defaults are vanilla-style
  `15` points and an `18` cap.
- Set **Bonus to all skills** to any integer value. `0` is vanilla; positive or negative values are
  applied live to Stealth, Athletics, Lore, Mechanics, Survival, and Crafting.
- Click **Grant level** to add enough XP for one more pending level-up on selected party members; if
  none are selected, it applies to the whole primary party.
- Press **Space** while paused to unpause (and nothing else), regardless of whether Space is bound to
  Pause. In turn-based combat, Space ends your character's turn when unpaused. At a dialogue "Continue",
  Space / Enter / any number key advances it.
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
  injected call must match the DLL). The mod itself is "Pillars1Toolkit".

---

## License

[MIT](LICENSE). This repository contains only original mod code — no Obsidian Entertainment game
code or assets. You must own Pillars of Eternity to use it.
