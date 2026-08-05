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
- Set the game's native **Fast mode** anywhere from its vanilla **1.8×** to **4×**.
- Optional **Fast Scouting** keeps full running speed while scouting.
- Works **in combat** (unlike the built-in Fast speed).
- Two independent keys: **hold-to-accelerate** and **toggle-acceleration** (+ a Clear button).
- In-game overlay to tailor the speed and keys; settings persist across sessions.
- Respects pause and inventory freezes.
- Optional **extra-close camera zoom** with a closest-zoom slider and quick presets.
- Optional **mouse untrap** so the cursor can leave the game window in windowed mode.
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
- Standard BepInEx plugin; no game assembly or data-file edits.

---

## Installation

Requires Pillars of Eternity 1 (Windows).

Install BepInEx 5, then place `LoomTimeAccelerator.dll` under
`BepInEx/plugins/Pillars1Toolkit`. Launch the game and press **F10**.

### Option B — Build from source (developers)

Needs the Roslyn C# compiler (`csc.exe`) and an installed BepInEx 5 development target.

```powershell
./build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Pillars of Eternity"
```

---

## Using it

- Press your **Toggle** key (default `\`) to switch acceleration on/off, or hold your **Hold** key
  (default unbound) for momentary fast-forward. A `>> Time xN` badge shows when it's active.
- Press **`F10`** to open the menu: adjust the built-in Fast mode speed, and click a
  keybind row to rebind it (`Esc` cancels a rebind). **Clear both accelerate keys** unbinds them.
- Enable **extra-close camera zoom** and set the closest zoom value. Lower values zoom closer; `Close`
  defaults to `0.20`, and `Extreme` goes to `0.10`.
- Leave **Let mouse leave the game window** enabled if you want to move to another monitor/window
  without Alt-Tab. Disable it to restore the game's normal cursor clipping behavior.
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

## Uninstalling

Close the game and delete `BepInEx/plugins/Pillars1Toolkit`.

---

## Notes & caveats

- Because it drives Unity's `Time.timeScale`, sped-up audio is pitched up — same as any
  fast-forward. Pause/inventory (timescale 0) are untouched.
- Very high multipliers can make fast-paced combat hard to control; 2×–4× is a comfortable range.
- Uses BepInEx's shared runtime loader and Harmony copy.
- **Internal name:** the plugin assembly remains `LoomTimeAccelerator.dll` for save/config continuity.

---

## License

[MIT](LICENSE). This repository contains only original mod code — no Obsidian Entertainment game
code or assets. You must own Pillars of Eternity to use it.
