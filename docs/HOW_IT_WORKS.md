# How Pillars1Speedhack works

This is the design write-up. It contains **no game code** — only a description of the game's
observable behaviour and how the mod interoperates with it.

## The hook

Pillars of Eternity 1 ships its game logic in `Assembly-CSharp.dll` (Mono/Unity). The mod runs as
a **sidecar assembly** driven by a single injected call:

```
GameState.Update()  ->  LoomTimeAccelerator.Bootstrap.Tick()   // injected, first line
```

`install.ps1` injects it with Mono.Cecil. On its first call, `Tick()` spawns one persistent
`GameObject` carrying the `Accelerator` MonoBehaviour and then does nothing further — all the work
happens in that behaviour's Unity callbacks.

## The time model

The game controls simulation speed through a `TimeController` MonoBehaviour that writes
`UnityEngine.Time.timeScale` every frame in its own `Update()`. It computes the base scale from
scratch each frame:

- `0` when paused or when a UI window pauses the game,
- the combat-speed option while in tactical/real-time combat,
- otherwise the normal/slow/fast scalar.

The built-in "Fast" speed is only ~1.8× and is explicitly disabled in combat.

## The trick

`Accelerator` applies its multiplier in **`LateUpdate()`**, which Unity always runs *after* every
`Update()` — including `TimeController.Update()`. So each frame:

1. `TimeController.Update()` sets `Time.timeScale` to the correct base value.
2. `Accelerator.LateUpdate()` multiplies it: `Time.timeScale = base * multiplier`.

Because the base is recomputed from scratch every frame, there is **no compounding** — the mod
simply scales whatever the game intended, including combat. And because a paused game has a base of
`0`, `0 * multiplier` is still `0`, so pause and inventory keep working untouched. The mod only
multiplies when `Time.timeScale > 0` and the `TimeController` singleton exists (so it stays inert at
the main menu / during loads).

## Input & UI

- Two independent bindings — hold-to-accelerate and toggle-acceleration — are read with
  `UnityEngine.Input`, gated on the game's `UIWindowManager.KeyInputAvailable` so they don't fire
  while typing in a text field.
- Space has one special one-way behavior: when `TimeController.Instance.Paused` is already true,
  pressing Space sets `TimeController.Instance.SafePaused = false`. It never sets pause to true,
  so Space only pauses if the player's normal game keybinds already make it pause.
- The tailoring UI is a self-contained **IMGUI** (`OnGUI`) overlay, so it needs no surgery on the
  game's NGUI UI and no extra install. While the overlay is open the mod sets `GameInput.DisableInput`
  so clicks in the menu don't also move the party, and restores it on close.
- Key rebinding captures the next `KeyDown` via `Event.current` in `OnGUI` (`Esc` cancels).
- Settings persist to `LoomTimeAccelerator.cfg` under the game's `Application.persistentDataPath`.

## Why not add a real keybind-menu entry?

The vanilla keybinds menu is driven by the compiled `MappedControl` enum. Adding a brand-new named
binding would require a new enum value plus display-string and control-mapping data — not something
that can be done purely from a runtime sidecar. So the mod ships its own rebindable keys in its
overlay instead. (An alternative, if desired, is to read an existing bindable speed control such as
"Fast" / "Game Speed Cycle" so it can be rebound in the stock menu, at the cost of overriding that
key's built-in behaviour.)

## Coexistence with other mods

Multiple sidecar mods can hook `GameState.Update()` — the installer inserts its own `Tick()` call at
the top of the method and marks the assembly with its own reference name, so re-running is a no-op
and it doesn't disturb other mods' hooks (e.g. Reaper Stance).
