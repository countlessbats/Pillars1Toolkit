# How Pillars1Toolkit works

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
- Extra-close camera zoom lowers `GameState.Option.MinZoom` at runtime. The vanilla camera wheel still
  calls `SyncCameraOrthoSettings.SetZoomLevelDelta()`; it simply clamps against the toolkit's lower floor
  instead of the stock `0.75`. Disabling the option restores the remembered vanilla floor.
- Character creation settings mirror the Nice Stats chargen approach: while
  `UICharacterCreationManager` is creating a new player or new companion, the toolkit writes
  `TotalPointBuy` and `StatHardMaximum`; when the manager changes or exits that mode, it restores the
  original values it observed.
- The skill bonus is a reversible flat adjustment via `CharacterStats.AdjustSkillBonus()` for every
  primary party member and every skill. Each character records the amount applied so changing the setting
  live only applies the delta, and leaving/removing a party member removes the old delta.
- `Grant level` targets selected party members, falling back to the primary party. It adds enough XP for
  one additional level-up opportunity, capped at the game's current `CharacterStats.PlayerLevelCap`, and
  leaves the actual level-up choices to the vanilla UI.
- Space runs a small priority model each frame, evaluated **before** the game's own input readers.
  The mod's MonoBehaviour carries `[DefaultExecutionOrder(-30000)]`, so its `Update()` runs first and,
  when it decides to act, it *consumes* the physical Space key with the game's own per-frame handled-flag
  (`GameInput.GetKeyDown(KeyCode.Space, setHandled: true)`, which auto-resets in `GameInput.LateUpdate`).
  That prevents any other Space-bound control (PAUSE, the dead PASS_TURN binding, …) from also firing.
  Priority, highest first:
  1. **A conversation is open** → hands off. Space/Enter advancing a "Continue" is the game's own
     `MappedControl.CONV_CONTINUE`; the mod only *adds* number keys, and does so in `LateUpdate` (after
     the game has read the frame's input) so the advance can't leak the keypress onto the next node. The
     advance itself is the game's own `UIConversationManager.OnButton(null)`, which self-guards to a no-op
     on real choice nodes.
  2. **Player-paused** (`TimeController.Paused && !UiPaused`, i.e. the RTwP pause, not a UI/menu freeze)
     → consume Space and `SafePaused = false` (which only ever unpauses). Nothing else.
  3. **Turn-based combat on a controllable party member's turn**
     (`TacticalModeManager.IsInTacticalCombat()` + `WhoseTurn.IsControllablePartyMember()`) → consume
     Space and `FinishTurn(WhoseTurn, PassTurnStyle.UI)`, mirroring the End-Turn button's `CanEndTurn`
     interruptibility check (queued until the unit stops moving). Enemy/environment turns are skipped.
  4. **Otherwise** → don't consume; the game's normal Space binding (default: pause) runs untouched.
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
