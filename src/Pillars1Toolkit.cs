// Pillars1Toolkit — quality-of-life tools for
// Pillars of Eternity 1. See README.md and docs/HOW_IT_WORKS.md.
//
// The namespace / assembly is named "LoomTimeAccelerator" on purpose: it is the identifier
// the installer injects into GameState.Update(), so the sidecar DLL, the namespace, and the
// injected call must all agree. It is internal-only; the mod itself is "Pillars1Toolkit".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace LoomTimeAccelerator
{
    // Sidecar entry point. Assembly-CSharp is patched to call Bootstrap.Tick() at the top of
    // GameState.Update(); on first tick we spawn a persistent MonoBehaviour that does the work.
    public static class Bootstrap
    {
        private static bool s_spawned;

        public static void Tick()
        {
            if (s_spawned)
            {
                return;
            }

            try
            {
                GameObject go = new GameObject("LoomTimeAccelerator");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Accelerator>();
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] spawn failed: " + ex);
            }
            finally
            {
                s_spawned = true;
            }
        }
    }

    // Runs very early each frame (negative execution order) so its Space handling wins the input race:
    // it consumes the physical Space key via the game's own handled-flag before the game's own readers
    // (the PAUSE action-bar button, etc.) get to act on it. Unity honours DefaultExecutionOrder on a
    // runtime-added component. LateUpdate still runs after every Update (incl. TimeController's), so the
    // time-acceleration multiplier is unaffected.
    [DefaultExecutionOrder(-30000)]
    public partial class Accelerator : MonoBehaviour
    {
        private enum Capturing { None, Hold, Toggle, Menu }

        private const float MinMult = 1.25f;
        private const float MaxMult = 10f;
        private const float VanillaMinZoom = 0.75f;
        private const float ToolkitMinZoomFloor = 0.10f;
        private const int DefaultAttributePoints = 15;
        private const int DefaultStatMaximum = 18;
        private const float DefaultFastModeScale = 1.8f;

        private float m_multiplier = 3f;
        private float m_fastModeScale = DefaultFastModeScale;
        private bool m_fastScouting;
        private bool m_fastScoutingWasEnabled;
        private bool m_closeZoomEnabled = true;
        private float m_minZoom = 0.20f;
        private float m_seenVanillaMinZoom = VanillaMinZoom;
        private int m_chargenPoints = DefaultAttributePoints;
        private int m_chargenStatMaximum = DefaultStatMaximum;
        private int m_skillBonus;
        private string m_chargenPointsText = DefaultAttributePoints.ToString(CultureInfo.InvariantCulture);
        private string m_chargenStatMaximumText = DefaultStatMaximum.ToString(CultureInfo.InvariantCulture);
        private string m_skillBonusText = "0";
        private bool m_unclipCursor = true;
        private bool m_invulnerable;   // cheat: party never takes damage (topped up + revived each frame)
        private bool m_oneHitKills;    // cheat: any damaged hostile is finished via the player-damage path
        private bool m_maxFocusStart;  // cheat: ciphers open every fight at maximum focus
        private bool m_prevCombatForFocus; // combat-start edge for the focus fill
        private readonly List<Health> m_ohkScratch = new List<Health>(16);

        // Separate bindings: hold-to-accelerate and toggle-acceleration.
        private KeyCode m_holdKey = KeyCode.None;
        private KeyCode m_toggleKey = KeyCode.Backslash;
        private KeyCode m_menuKey = KeyCode.F10;

        private bool m_toggled;   // sticky on/off driven by the toggle key
        private bool m_enabled;   // effective this frame (toggle OR hold)
        private bool m_active;    // actually multiplying this frame

        // Coop bridge: on a Loom Coop CLIENT, local Time.timeScale is host-authoritative, so
        // multiplying it here only speeds LOCAL animation, not the real sim. Instead we forward
        // the requested game speed to the host (LoomCoop.CoopManager.ClientRequestTimeScale),
        // which accelerates the authoritative sim and echoes it back so our clock reconciles.
        private static bool s_coopProbed;
        private static float s_coopNextProbe;
        private static PropertyInfo s_coopIsClient;
        private static PropertyInfo s_coopActive;
        private static MethodInfo s_coopSetSpeed;
        private static PropertyInfo s_coopTbActive;   // host TB combat, streamed (client-side view)
        private static PropertyInfo s_coopMyTurn;     // a client-controlled unit is taking its turn
        private static MethodInfo s_coopEndTurn;      // request end-turn over the wire
        private float m_lastCoopWant = 1f;   // last shared game-speed we pushed (edge detection)

        private static bool CoopTbActive()
        {
            try
            {
                ResolveCoop();
                return s_coopTbActive != null && (bool)s_coopTbActive.GetValue(null, null);
            }
            catch { return false; }
        }

        private static bool CoopMyTurn()
        {
            try
            {
                ResolveCoop();
                return s_coopMyTurn != null && (bool)s_coopMyTurn.GetValue(null, null);
            }
            catch { return false; }
        }

        private static void CoopEndTurn()
        {
            try
            {
                ResolveCoop();
                if (s_coopEndTurn != null) { s_coopEndTurn.Invoke(null, null); }
            }
            catch { }
        }

        private bool m_skipIntros = true;   // auto-skip the startup logo movies
        private bool m_introHandled;        // stop looking once handled / past the intro window
        private bool m_disableTutorials;    // drive the game's SHOW_TUTORIALS option off
        private bool m_skipNewGameIntro = true; // skip the pan-up-adra + title cards on New Game
        private bool m_throttleFootsteps = true; // cap footstep sounds at 1.5x vanilla cadence while accelerated

        private bool m_menuOpen;
        private bool m_legacyMenu;
        private Capturing m_capturing = Capturing.None;
        private bool m_inputDisabledByUs;
        private Rect m_window = new Rect(60f, 60f, 340f, 0f);
        private string m_configPath;
        private UICharacterCreationManager m_seenCreationManager;
        private int m_originalPointBuy;
        private int m_originalStatHardMaximum;
        private bool m_hasOriginalCharacterCreationValues;
        private readonly Dictionary<CharacterStats, int[]> m_appliedSkillBonuses = new Dictionary<CharacterStats, int[]>();

        // --- Space-behavior expansion (unpause-first / end-turn / dialogue) ---
        private AIController m_pendingEndTurn;            // queued end-turn awaiting an interruptible moment
        private static MethodInfo s_convOnButton;         // cached UIConversationManager.OnButton(GameObject)
        private static bool s_convOnButtonResolved;

        private void Awake()
        {
            try
            {
                // Per-install config (v: host and client installs on one machine share
                // persistentDataPath, so a shared cfg lets either seat clobber the other's
                // binds/multiplier). dataPath is unique per install. One-time migration:
                // seed from the legacy shared cfg if the per-install one doesn't exist yet.
                m_configPath = Path.Combine(Application.dataPath, "LoomTimeAccelerator.cfg");
                if (!File.Exists(m_configPath))
                {
                    string legacy = Path.Combine(Application.persistentDataPath, "LoomTimeAccelerator.cfg");
                    if (File.Exists(legacy))
                    {
                        File.Copy(legacy, m_configPath);
                    }
                }
            }
            catch
            {
                m_configPath = "LoomTimeAccelerator.cfg";
            }
            LoadConfig();
            s_suppressTutorials = m_disableTutorials;
            s_skipNewGameIntro = m_skipNewGameIntro;
            s_throttleFootsteps = m_throttleFootsteps;
            TryPatchTutorials();
            TryPatchNewGameIntro();
            TryPatchFootsteps();
            TryPatchInvulnerability();
        }

        private static bool s_invulnerabilityPatched;

        private static void TryPatchInvulnerability()
        {
            if (s_invulnerabilityPatched) { return; }
            try
            {
                Harmony harmony = new Harmony("loom.toolkit.invulnerability");
                MethodInfo doDamage = typeof(Health).GetMethod("DoDamage",
                    new Type[] { typeof(DamageInfo), typeof(GameObject) });
                MethodInfo direct = typeof(Health).GetMethod("ApplyDamageDirectly",
                    new Type[] { typeof(float), typeof(DamagePacket.DamageType), typeof(GameObject),
                        typeof(StatusEffect), typeof(bool) });
                HarmonyMethod prefix = new HarmonyMethod(
                    typeof(Accelerator).GetMethod(nameof(InvulnerabilityDamagePrefix)));
                HarmonyMethod postfix = new HarmonyMethod(
                    typeof(Accelerator).GetMethod(nameof(InvulnerabilityDamagePostfix)));
                if (doDamage != null) { harmony.Patch(doDamage, prefix: prefix, postfix: postfix); }
                if (direct != null) { harmony.Patch(direct, prefix: prefix, postfix: postfix); }
                // CC immunity (v1.4): invulnerability also blocks HOSTILE status effects on
                // party members — knockdown, paralysis, charm, DoTs — which would otherwise
                // interrupt/wedge spellcasting even with damage nulled. Beneficial effects pass.
                // CharacterStats.CanApplyStatusEffect is the shared gate both Apply paths call.
                MethodInfo canApply = typeof(CharacterStats).GetMethod("CanApplyStatusEffect",
                    new Type[] { typeof(StatusEffect) });
                if (canApply != null)
                {
                    harmony.Patch(canApply, postfix: new HarmonyMethod(
                        typeof(Accelerator).GetMethod(nameof(InvulnerabilityStatusPostfix))));
                }
                s_invulnerabilityPatched = doDamage != null && direct != null;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] invulnerability Harmony patch failed: " + ex);
            }
        }

        public static void InvulnerabilityDamagePrefix(Health __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (!s_invulnerabilityActive || __instance == null) { return; }
                PartyMemberAI pm = __instance.GetComponent<PartyMemberAI>();
                if (pm == null || !PartyMemberAI.IsInPartyList(pm)) { return; }
                __state = __instance.TakesDamage;
                __instance.TakesDamage = false;
            }
            catch { __state = false; }
        }

        public static void InvulnerabilityDamagePostfix(Health __instance, bool __state)
        {
            if (__state && __instance != null) { __instance.TakesDamage = true; }
        }

        // While invulnerable: party members refuse HOSTILE status effects entirely (knockdown,
        // paralysis, stun, charm, DoTs). Beneficial effects (buffs, heals) apply normally.
        public static void InvulnerabilityStatusPostfix(CharacterStats __instance,
            StatusEffect effect, ref bool __result)
        {
            try
            {
                if (!s_invulnerabilityActive || !__result || __instance == null
                    || effect == null || effect.Params == null || !effect.Params.IsHostile)
                {
                    return;
                }
                PartyMemberAI pm = __instance.GetComponent<PartyMemberAI>();
                if (pm != null && PartyMemberAI.IsInPartyList(pm))
                {
                    __result = false;
                }
            }
            catch { }
        }

        // Hard stop for tutorials: a Harmony prefix on TutorialManager.TriggerTutorial (the single
        // chokepoint every tutorial routes through). When suppression is on it skips the original
        // entirely, so even the ShowEvenIfDisabled tutorials that survive the SHOW_TUTORIALS option
        // never appear. Isolated in try/catch so a missing/broken 0Harmony just degrades to the
        // option-only behavior instead of breaking the mod.
        private static bool s_suppressTutorials;
        private static bool s_tutorialsPatched;

        private static void TryPatchTutorials()
        {
            if (s_tutorialsPatched)
            {
                return;
            }
            try
            {
                MethodInfo target = typeof(TutorialManager).GetMethod("TriggerTutorial", new Type[] { typeof(int) });
                if (target == null)
                {
                    return;
                }
                Harmony harmony = new Harmony("loom.toolkit.tutorials");
                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(Accelerator).GetMethod(nameof(TriggerTutorialPrefix))));
                s_tutorialsPatched = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] tutorial Harmony patch failed (option-only fallback): " + ex);
            }
        }

        // Prefix on TutorialManager.TriggerTutorial(int): when suppressing, skip the original and
        // report "not shown".
        public static bool TriggerTutorialPrefix(ref bool __result)
        {
            if (s_suppressTutorials)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // Skip the New Game intro cutscene (pan up the adra cliff + "Obsidian Presents" + title cards).
        // FrontEndTitleIntroductionManager runs a state machine on New Game that ends by loading the
        // first area (which then opens character creation). The game already exposes SkipIntroToEnd()
        // (the Escape-key skip); we invoke it the instant the intro starts, so it works even when input
        // is gated during that phase. This just fast-forwards the game's own sanctioned skip path
        // (fade -> load) - no engine internals touched.
        private static bool s_skipNewGameIntro;
        private static bool s_introPatched;

        private static void TryPatchNewGameIntro()
        {
            if (s_introPatched)
            {
                return;
            }
            try
            {
                MethodInfo target = typeof(FrontEndTitleIntroductionManager).GetMethod(
                    "StartFrontEndIntroduction", new Type[0]);
                if (target == null)
                {
                    return;
                }
                Harmony harmony = new Harmony("loom.toolkit.newgameintro");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(Accelerator).GetMethod(nameof(StartFrontEndIntroPostfix))));
                s_introPatched = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] new-game intro Harmony patch failed: " + ex);
            }
        }

        // Postfix on FrontEndTitleIntroductionManager.StartFrontEndIntroduction(): when enabled, jump
        // straight to the intro's end so the pan/title never play.
        public static void StartFrontEndIntroPostfix(FrontEndTitleIntroductionManager __instance)
        {
            if (s_skipNewGameIntro && __instance != null)
            {
                __instance.SkipIntroToEnd();
            }
        }

        // FOOTSTEP THROTTLE: footsteps are animation events (AnimationController.OnEventFootstep ->
        // AudioFootsteps.anim_OnEventFootstep), so accelerated time fires them proportionally more
        // often per real second — x5 speed means five times the step-step-step. A Harmony prefix on
        // the event handler thins the events so at most MaxFootstepRate x the vanilla cadence is
        // actually played. The frame's true speed-up is Time.deltaTime / Time.unscaledDeltaTime
        // (deltaTime is what drove the animation, so this stays correct no matter who scaled time —
        // our multiplier, vanilla Fast mode, or both). A per-creature fractional accumulator picks
        // which events play, so the survivors stay evenly spaced instead of clustering. The armor
        // jostle event rides the same step cadence and is throttled identically (its own channel,
        // so a played step keeps its own rhythm). At speeds <= MaxFootstepRate every event passes.
        private const float MaxFootstepRate = 1.5f;
        private static bool s_throttleFootsteps = true;
        private static bool s_footstepsPatched;
        private static readonly Dictionary<int, float> s_stepAccumulators = new Dictionary<int, float>();

        private static void TryPatchFootsteps()
        {
            if (s_footstepsPatched)
            {
                return;
            }
            try
            {
                MethodInfo step = typeof(AudioFootsteps).GetMethod(
                    "anim_OnEventFootstep", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo jostle = typeof(AudioFootsteps).GetMethod(
                    "anim_OnEventJostle", BindingFlags.NonPublic | BindingFlags.Instance);
                if (step == null)
                {
                    Debug.LogError("[LoomTimeAccelerator] AudioFootsteps.anim_OnEventFootstep not found; footstep throttle disabled");
                    return;
                }
                Harmony harmony = new Harmony("loom.toolkit.footsteps");
                harmony.Patch(step, prefix: new HarmonyMethod(
                    typeof(Accelerator).GetMethod(nameof(FootstepEventPrefix))));
                if (jostle != null)
                {
                    harmony.Patch(jostle, prefix: new HarmonyMethod(
                        typeof(Accelerator).GetMethod(nameof(JostleEventPrefix))));
                }
                s_footstepsPatched = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] footstep Harmony patch failed: " + ex);
            }
        }

        public static bool FootstepEventPrefix(AudioFootsteps __instance)
        {
            return AllowStepSound(__instance, 0);
        }

        public static bool JostleEventPrefix(AudioFootsteps __instance)
        {
            return AllowStepSound(__instance, 1);
        }

        private static bool AllowStepSound(AudioFootsteps source, int channel)
        {
            if (!s_throttleFootsteps || source == null)
            {
                return true;
            }
            float unscaled = Time.unscaledDeltaTime;
            if (unscaled <= 0f)
            {
                return true;
            }
            float speed = Time.deltaTime / unscaled;
            if (speed <= MaxFootstepRate)
            {
                return true;
            }
            float share = MaxFootstepRate / speed; // fraction of events that equals 1.5x vanilla cadence
            int key = (source.GetInstanceID() << 1) | channel;
            float acc;
            s_stepAccumulators.TryGetValue(key, out acc);
            acc += share;
            if (acc >= 1f)
            {
                s_stepAccumulators[key] = Mathf.Min(acc - 1f, 1f);
                return true;
            }
            s_stepAccumulators[key] = acc;
            return false;
        }

        private void Update()
        {
            // Only the authoritative simulation may apply cheats. Healing a coop client's
            // mirror fought the next host snapshot forever, producing endless green numbers.
            s_invulnerabilityActive = m_invulnerable && !CoopClientActive();
            NativeToolkitMenu.Tick(this);

            if (!m_legacyMenu && NativeToolkitMenu.HandleCaptureInput(this))
            {
                return;
            }
            if (!m_legacyMenu && NativeToolkitMenu.HandleMenuHotkey(this))
            {
                return;
            }

            // Accumulator entries are keyed by AudioFootsteps instance IDs, which die with their
            // area — flush the map during loads so it can't grow across a long session.
            if (GameState.IsLoading && s_stepAccumulators.Count > 0)
            {
                s_stepAccumulators.Clear();
            }

            TrySkipIntro();
            ApplyZoomOverride();
            HandleCharacterCreation();
            ApplySkillBonusToParty();
            ApplyCursorUnclip();
            ApplyTutorialSetting();
            ApplyBuiltInFastModeScale();
            ApplyFastScouting();
            HandleSpacePriorities();
            PumpPendingEndTurn();

            bool keyInput = SafeKeyInputAvailable();

            // While rebinding, OnGUI captures the next key; suppress hotkeys meanwhile.
            if (m_capturing != Capturing.None)
            {
                return;
            }

            if (keyInput && m_menuKey != KeyCode.None && Input.GetKeyDown(m_menuKey))
            {
                SetMenuOpen(!m_menuOpen);
            }

            // F11: creature render-state dump to a PER-INSTALL file (host and client each
            // write their own CreatureDump.log — a shared Player.log gets clobbered when
            // two instances run). Hotkey rather than an F10 button: that menu is full.
            if (keyInput && Input.GetKeyDown(KeyCode.F11))
            {
                DumpCreatureStates();
            }

            bool hold = keyInput && m_holdKey != KeyCode.None && Input.GetKey(m_holdKey);
            if (keyInput && m_toggleKey != KeyCode.None && Input.GetKeyDown(m_toggleKey))
            {
                m_toggled = !m_toggled;
            }

            m_enabled = m_toggled || hold;
        }

        private void ApplyBuiltInFastModeScale()
        {
            TimeController controller = TimeController.Instance;
            if (controller == null || Mathf.Abs(controller.FastTime - m_fastModeScale) < 0.001f)
            {
                return;
            }
            bool wasFast = controller.Fast;
            controller.FastTime = m_fastModeScale;
            if (wasFast)
            {
                controller.Fast = true;
            }
        }

        private void ApplyFastScouting()
        {
            // The host/solo simulation owns movement. A coop client receives the resulting
            // positions from the host and must not invent a different local mover speed.
            bool apply = m_fastScouting && !CoopClientActive();
            PartyMemberAI[] party = PartyMemberAI.PartyMembers;
            for (int i = 0; party != null && i < party.Length; i++)
            {
                PartyMemberAI member = party[i];
                if (member == null || member.gameObject == null
                    || !Stealth.IsInStealthMode(member.gameObject)) { continue; }
                Mover mover = member.GetComponent<Mover>();
                if (mover == null) { continue; }
                if (apply)
                {
                    float run = mover.GetRunSpeed();
                    if (Mathf.Abs(mover.DesiredSpeed - run) > 0.001f) { mover.UseRunSpeed(); }
                }
                else if (m_fastScoutingWasEnabled)
                {
                    mover.UseWalkSpeed();
                }
            }
            m_fastScoutingWasEnabled = apply;
        }

        // Space priority model (highest first):
        //   0. A conversation window owns the keyboard. We never unpause or end a turn under it. (Number
        //      keys also advancing a "Continue" is handled in LateUpdate, after the game processes the
        //      frame's input, so we don't race it; Space/Enter advancing Continue is already vanilla.)
        //   1. Unpause-first: if the game is PLAYER-paused (RTwP pause, not a UI/menu/inventory freeze),
        //      Space ONLY unpauses and does nothing else. Works regardless of how Space is bound.
        //   2. Turn-based combat, on a controllable party member's turn: Space ends that turn and nothing
        //      else (queued until the unit is interruptible, exactly like the End Turn button). Enemy /
        //      environment turns fall through so Space can still pause there.
        //   3. Otherwise: leave Space to the game's own binding (default: pause).
        private void HandleSpacePriorities()
        {
            if (m_menuOpen || m_capturing != Capturing.None)
            {
                return;
            }
            if (GameState.IsLoading || TimeController.Instance == null)
            {
                return;
            }

            // Priority 0: don't touch Space while a conversation is up.
            if (ConversationActive())
            {
                return;
            }

            if (!Input.GetKeyDown(KeyCode.Space))
            {
                return;
            }

            // Priority 1: TURN-BASED FIRST. TB's command phase holds Time.timeScale at 0, and
            // TimeController.Paused is literally "timeScale == 0" — so the unpause-first branch
            // below read TB as player-paused, consumed Space, pointlessly unpaused (TB re-freezes
            // instantly), and end-turn never ran. In tactical combat Space means End Turn on our
            // controllable unit's turn and NOTHING else; on enemy/environment turns the press is
            // left un-consumed so the game's own binding still sees it. The unpause-first model
            // is RTwP-only — TB owns its own timescale.
            if (TacticalModeManager.IsInTacticalCombat())
            {
                TryEndTurnOnSpace();
                return;
            }

            // Coop CLIENT during host turn-based combat: the local TacticalModeManager never
            // runs, so the branch above can't fire. Space = End Turn for OUR active unit,
            // routed over the coop wire. CRITICAL: consume BEFORE the unpause-first branch —
            // the mirrored TB pause made that branch eat the press and pointlessly unpause,
            // which is why spacebar did nothing at all on the client.
            if (CoopClientActive() && CoopTbActive())
            {
                ConsumeSpace();
                if (CoopMyTurn())
                {
                    CoopEndTurn();
                }
                return; // not our turn: swallow (a unit takes no orders off-turn)
            }

            // Priority 2: unpause-first (RTwP). SafePaused=false always unpauses (and never
            // pauses). Gate on player pause only (UiPaused covers menus/inventory/dialogue,
            // which we leave alone).
            if (TimeController.Instance.Paused && !TimeController.Instance.UiPaused)
            {
                ConsumeSpace();
                TimeController.Instance.SafePaused = false;
                return;
            }

            // Priority 3: not consumed -> the game's normal Space binding runs (default: pause toggle).
        }

        private static bool ConversationActive()
        {
            try
            {
                UIConversationManager conv = UIConversationManager.Instance;
                return conv != null && conv.WindowActive();
            }
            catch
            {
                return false;
            }
        }

        // Mark the physical Space key handled for this frame via the game's own consume mechanism, so no
        // other Space-bound control (PAUSE, PASS_TURN, ...) can also act on this press. The flag auto-resets
        // in GameInput.LateUpdate, so there is nothing to clean up.
        private static void ConsumeSpace()
        {
            try
            {
                GameInput.GetKeyDown(KeyCode.Space, true);
            }
            catch
            {
            }
        }

        // True if Space was handled as an end-turn (ended now or queued). Only fires on a controllable
        // party member's turn; enemy / environment / no-active-turn cases return false and fall through.
        private bool TryEndTurnOnSpace()
        {
            try
            {
                if (!TacticalModeManager.IsInTacticalCombat())
                {
                    return false;
                }

                TacticalModeManager mgr = TacticalModeManager.Instance;
                if (mgr == null)
                {
                    return false;
                }

                AIController who = mgr.WhoseTurn;
                if (who == null || !who.IsControllablePartyMember())
                {
                    return false; // enemy / environment / nobody's active turn -> Space may still pause
                }

                // It's our unit's turn: Space means End Turn and ONLY End Turn — always
                // consumed. THE OLD FLAKINESS: TurnLocked early-returned WITHOUT consuming,
                // so a Space pressed during a brief locked window (animations, camera moves)
                // fell through to the vanilla binding and toggled PAUSE instead ("spacebar
                // doesn't reliably end the turn"). Locked/busy now QUEUES, like the button.
                ConsumeSpace();
                if (!mgr.TurnLocked && CanEndTurn(who))
                {
                    mgr.FinishTurn(who, PassTurnStyle.UI);
                    m_pendingEndTurn = null;
                }
                else
                {
                    m_pendingEndTurn = who; // locked / mid-action: queue and retry, like the button
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] end-turn failed: " + ex);
                return false;
            }
        }

        // Drives a queued end-turn until the unit becomes interruptible or the turn moves on.
        private void PumpPendingEndTurn()
        {
            if (m_pendingEndTurn == null)
            {
                return;
            }

            try
            {
                TacticalModeManager mgr = TacticalModeManager.Instance;
                if (mgr == null || !TacticalModeManager.IsInTacticalCombat()
                    || mgr.WhoseTurn != m_pendingEndTurn || !m_pendingEndTurn.IsControllablePartyMember())
                {
                    m_pendingEndTurn = null; // situation changed; abandon the queued end-turn
                    return;
                }

                if (!mgr.TurnLocked && CanEndTurn(m_pendingEndTurn))
                {
                    mgr.FinishTurn(m_pendingEndTurn, PassTurnStyle.UI);
                    m_pendingEndTurn = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] pending end-turn failed: " + ex);
                m_pendingEndTurn = null;
            }
        }

        // Mirrors UIPassTurnButton.CanEndTurn: a turn can't be ended while the unit is actively moving or
        // in a non-Wait action (unless it's a long cast). State types are matched by name so the sidecar
        // needs no reference to the game's AI.Player namespace.
        private static bool CanEndTurn(AIController actor)
        {
            if (actor == null)
            {
                return true;
            }
            if (!actor.IsControllablePartyMember())
            {
                return false;
            }

            TacticalModeManager mgr = TacticalModeManager.Instance;
            if (mgr != null && mgr.HasLongCastEvent(actor))
            {
                return true;
            }

            var state = actor.StateManager.CurrentState;
            if (state == null)
            {
                return true;
            }

            string name = state.GetType().Name;
            if (name == "Move" || name == "PathToPosition")
            {
                return !state.IsMoving();
            }
            if (name == "Wait")
            {
                return true;
            }
            return false;
        }

        // LateUpdate helper: while a conversation shows a "Continue", let any number key (0-9 or numpad)
        // advance it too, on top of the vanilla Space / Enter / Numpad-Enter. Runs in LateUpdate (after the
        // game has processed this frame's input) so advancing can't leak the keypress onto the next node.
        // OnButton self-guards (it only advances when there are no real player responses), so this is a
        // safe no-op on choice nodes.
        private void TryDialogueNumberAdvance()
        {
            if (m_menuOpen || m_capturing != Capturing.None)
            {
                return;
            }
            if (!ConversationActive() || !AnyNumberKeyDown())
            {
                return;
            }
            AdvanceConversation();
        }

        private static bool AnyNumberKeyDown()
        {
            for (KeyCode k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++)
            {
                if (Input.GetKeyDown(k)) { return true; }
            }
            for (KeyCode k = KeyCode.Keypad0; k <= KeyCode.Keypad9; k++)
            {
                if (Input.GetKeyDown(k)) { return true; }
            }
            return false;
        }

        private static void AdvanceConversation()
        {
            try
            {
                UIConversationManager conv = UIConversationManager.Instance;
                if (conv == null)
                {
                    return;
                }
                if (!s_convOnButtonResolved)
                {
                    s_convOnButton = typeof(UIConversationManager).GetMethod(
                        "OnButton", BindingFlags.NonPublic | BindingFlags.Instance);
                    s_convOnButtonResolved = true;
                }
                if (s_convOnButton != null)
                {
                    s_convOnButton.Invoke(conv, new object[] { null });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] dialogue advance failed: " + ex);
            }
        }

        // Auto-skip the startup logo movies by triggering the game's own skip on
        // CompanyIntroductionManager (the same thing pressing a key does). Our hook comes alive
        // during the intro because CompanyIntroductionManager.Start() creates the global prefab.
        private void TrySkipIntro()
        {
            if (m_introHandled)
            {
                return;
            }
            if (!m_skipIntros || Time.realtimeSinceStartup > 30f)
            {
                // Past the intro window (or disabled): stop scanning for it.
                if (Time.realtimeSinceStartup > 30f)
                {
                    m_introHandled = true;
                }
                return;
            }

            try
            {
                CompanyIntroductionManager intro = UnityEngine.Object.FindObjectOfType<CompanyIntroductionManager>();
                if (intro == null)
                {
                    return;
                }

                intro.StopAllCoroutines();
                System.Reflection.FieldInfo f = typeof(CompanyIntroductionManager).GetField(
                    "m_skipped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null)
                {
                    f.SetValue(intro, true);
                }
                m_introHandled = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] skip intro failed: " + ex);
                m_introHandled = true;
            }
        }

        private void LateUpdate()
        {
            // After the game has consumed this frame's input: number keys advance a dialogue "Continue".
            TryDialogueNumberAdvance();
            ApplyZoomOverride();

            // Cheats run in LateUpdate, after this frame's combat damage has been applied:
            // invuln restores/revives right after a hit lands; 1-hit-kill finishes any hostile
            // that took damage this frame.
            ApplyInvulnerability();
            ApplyOneHitKills();
            ApplyMaxFocusStart();

            // Apply after TimeController.Update() has set the frame's base timescale.
            m_active = false;

            // Coop (host OR client): game speed is ONE shared, host-authoritative value. Push it
            // EDGE-DRIVEN through CoopSetGameSpeed — NOT the local Time.timeScale multiply, and
            // NOT a per-frame re-assert. The old client path re-asserted its scale every 0.5s,
            // which dominated the shared clock so the host could never turn it off; and the host
            // path multiplied the local clock without touching the shared value, so the two
            // seats couldn't compose. Edge-driven means: on our Fast on/off (or a multiplier
            // change) we set the shared speed ONCE; either seat can then change it, and the
            // host's echo reconciles everyone. No re-assert => the other seat can always
            // override.
            if (CoopSessionActive())
            {
                float want = (m_enabled && !GameState.IsLoading)
                    ? Mathf.Clamp(m_multiplier, MinMult, MaxMult) : 1f;
                if (Mathf.Abs(want - m_lastCoopWant) > 0.01f)
                {
                    if (CoopSetGameSpeed(want)) { m_lastCoopWant = want; }
                }
                m_active = m_enabled; // HUD "Time xN" reflects intent
                return; // never touch Time.timeScale locally in coop
            }

            if (!m_enabled)
            {
                return;
            }
            if (TimeController.Instance == null || GameState.IsLoading)
            {
                return;
            }

            float ts = Time.timeScale;
            if (ts > 0.0001f) // don't disturb pause / UI-pause (timescale 0)
            {
                Time.timeScale = ts * m_multiplier;
                m_active = true;
            }
        }

        // ---- Coop bridge (reflection into LoomCoop; absent in solo play) ----
        // Resolve LoomCoop.CoopManager's client-check property + speed-request method. Sidecars
        // are Cecil/LoadFrom-injected, so Type.GetType by bare name fails — scan loaded
        // assemblies. Do NOT latch a miss permanently (LoomCoop may load after our first probe),
        // but throttle re-probes so solo play (no LoomCoop.dll) doesn't scan every frame.
        private static void ResolveCoop()
        {
            if (s_coopIsClient != null) { return; }
            if (s_coopProbed && Time.realtimeSinceStartup < s_coopNextProbe) { return; }
            s_coopProbed = true;
            s_coopNextProbe = Time.realtimeSinceStartup + 2f;
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = null;
                    try { t = asms[i].GetType("LoomCoop.CoopManager", false); }
                    catch { }
                    if (t != null)
                    {
                        s_coopIsClient = t.GetProperty("IsClientActive",
                            BindingFlags.Public | BindingFlags.Static);
                        s_coopActive = t.GetProperty("CoopActive",
                            BindingFlags.Public | BindingFlags.Static);
                        s_coopSetSpeed = t.GetMethod("CoopSetGameSpeed",
                            BindingFlags.Public | BindingFlags.Static, null,
                            new Type[] { typeof(float) }, null);
                        s_coopTbActive = t.GetProperty("CoopClientTbActive",
                            BindingFlags.Public | BindingFlags.Static);
                        s_coopMyTurn = t.GetProperty("CoopClientMyTurn",
                            BindingFlags.Public | BindingFlags.Static);
                        s_coopEndTurn = t.GetMethod("ClientRequestEndTurn",
                            BindingFlags.Public | BindingFlags.Static, null,
                            Type.EmptyTypes, null);
                        if (s_coopIsClient != null) { break; }
                    }
                }
            }
            catch { }
        }

        private static bool CoopClientActive()
        {
            try
            {
                ResolveCoop();
                return s_coopIsClient != null && (bool)s_coopIsClient.GetValue(null, null);
            }
            catch { return false; }
        }

        // True in ANY coop session (host OR client). Game speed is a shared, host-authoritative
        // value there, so both seats route Fast mode through CoopSetGameSpeed instead of the
        // local Time.timeScale multiply (which only accelerates local animation on a client and
        // fights the shared clock on a host).
        private static bool CoopSessionActive()
        {
            try
            {
                ResolveCoop();
                return s_coopActive != null && (bool)s_coopActive.GetValue(null, null);
            }
            catch { return false; }
        }

        // Set the shared coop game speed (host applies directly + echoes; client requests and
        // the host echo reconciles everyone). Edge-driven by the caller — never per-frame.
        private static bool CoopSetGameSpeed(float scale)
        {
            try
            {
                ResolveCoop();
                if (s_coopSetSpeed == null) { return false; }
                s_coopSetSpeed.Invoke(null, new object[] { scale });
                return true;
            }
            catch { return false; }
        }

        // INVULNERABILITY: keep every party member topped up and revive any that went down this
        // frame. Uses the game's own paths - Dead=false restores endurance, and
        // ApplyStaminaChangeDirectly(applyIfDead:true) heals stamina AND calls OnRevive() when the
        // target is unconscious - so state stays consistent (no husks, HUD/portraits correct). A
        // one-shot that KO's someone during Update is undone here in the same frame's LateUpdate.
        // MAX FOCUS AT COMBAT START: on the fight's opening edge, fill every party cipher's
        // focus pool. Runs on the HOST/solo only — in coop the sim (and the focus stream that
        // mirrors it to the client) is host-authoritative, and a client-side write would just
        // be overwritten by the next snapshot. The Focus setter routes through FocusTrait and
        // no-ops for classes without one, so no class check is needed.
        private void ApplyMaxFocusStart()
        {
            bool combat = false;
            try { combat = GameState.InCombat; } catch { }
            bool edge = combat && !m_prevCombatForFocus;
            m_prevCombatForFocus = combat;
            if (!edge || !m_maxFocusStart || GameState.IsLoading || CoopClientActive())
            {
                return;
            }
            try
            {
                PartyMemberAI[] members = PartyMemberAI.PartyMembers;
                for (int i = 0; members != null && i < members.Length; i++)
                {
                    PartyMemberAI pm = members[i];
                    if (pm == null) { continue; }
                    CharacterStats cs = pm.GetComponent<CharacterStats>();
                    if (cs == null) { continue; }
                    float max = cs.MaxFocus;
                    if (max > 0f && cs.Focus < max)
                    {
                        cs.Focus = max;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] max-focus-start failed: " + ex);
            }
        }

        private static bool s_invulnerabilityActive;

        private void ApplyInvulnerability()
        {
            if (!s_invulnerabilityActive || GameState.IsLoading)
            {
                return;
            }
            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            if (members == null)
            {
                return;
            }
            for (int i = 0; i < members.Length; i++)
            {
                PartyMemberAI member = members[i];
                if (member == null || member.Summoner != null)
                {
                    continue;
                }
                Health h = member.GetComponent<Health>();
                if (h == null)
                {
                    continue;
                }
                try
                {
                    if (h.Dead) { h.Dead = false; }                       // revive; restores endurance
                    if (h.CurrentHealth < h.MaxHealth) { h.CurrentHealth = h.MaxHealth; }
                    if (!h.Unconscious)
                    {
                        h.CurrentStamina = h.MaxStamina; // silent top-up; damage prefix prevents churn
                    }
                    else
                    {
                        float deficit = h.MaxStamina - h.CurrentStamina;
                        if (deficit > 0.01f)
                        {
                            h.ApplyStaminaChangeDirectly(deficit, null, true); // one-time revive
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[LoomTimeAccelerator] invuln failed: " + ex);
                }
            }
        }

        // 1-HIT KILLS: finish any HOSTILE that has actually taken damage (the hit still has to land,
        // so untouched enemies aren't instakilled on sight). ApplyDamageDirectlyAsPlayer routes
        // through the real death path - proper corpse, XP to the player, no targetable husk.
        // Kills are staged into a scratch list first: killing disables the creature's Faction, which
        // removes it from ActiveFactionComponents, so mutating that list mid-iteration is unsafe.
        private void ApplyOneHitKills()
        {
            if (!m_oneHitKills || GameState.IsLoading)
            {
                return;
            }
            List<Faction> factions = Faction.ActiveFactionComponents;
            if (factions == null)
            {
                return;
            }
            m_ohkScratch.Clear();
            for (int i = 0; i < factions.Count; i++)
            {
                Faction f = factions[i];
                if (f == null || f.gameObject == null || !f.gameObject.activeInHierarchy)
                {
                    continue;
                }
                try
                {
                    if (f.RelationshipToPlayer != Faction.Relationship.Hostile) { continue; } // enemies only
                    if (f.GetComponent<PartyMemberAI>() != null) { continue; }                 // never party
                    Health h = f.GetComponent<Health>();
                    if (h == null || h.Dead || h.Unconscious) { continue; }
                    if (h.CurrentStamina < h.MaxStamina - 0.01f) { m_ohkScratch.Add(h); }       // took damage
                }
                catch { }
            }
            for (int i = 0; i < m_ohkScratch.Count; i++)
            {
                try
                {
                    Health h = m_ohkScratch[i];
                    h.ApplyDamageDirectlyAsPlayer(h.MaxHealth + h.MaxStamina + 1000f);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[LoomTimeAccelerator] one-hit-kill failed: " + ex);
                }
            }
            m_ohkScratch.Clear();
        }

        private void OnGUI()
        {
            if (m_capturing != Capturing.None)
            {
                HandleCaptureEvent();
            }

            // Transient hotkey confirmation (F11 dump etc.) — top center, fades on its own.
            if (Time.realtimeSinceStartup < s_toastUntil && !string.IsNullOrEmpty(s_toast))
            {
                GUI.Label(new Rect(Screen.width * 0.5f - 250f, 8f, 500f, 24f),
                    "<b>" + s_toast + "</b>");
            }

            if (!m_menuOpen)
            {
                DrawCheatBadge();
            }

            if (m_active && !m_menuOpen)
            {
                DrawStatusBadge();
            }

            if (m_legacyMenu && m_menuOpen)
            {
                m_window = GUILayout.Window(0x54494D45, m_window, DrawWindow, "Pillars1Toolkit");
            }
        }

        private void DrawStatusBadge()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 16;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = Color.cyan;
            GUI.Label(new Rect(12f, 8f, 260f, 26f),
                ">> Time x" + m_multiplier.ToString("0.0#", CultureInfo.InvariantCulture), style);
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(4f);
            DrawKeyRow("Open this menu:", Capturing.Menu, m_menuKey);

            GUILayout.Space(10f);
            GUILayout.Label("Speed multiplier:  x" + m_multiplier.ToString("0.0#", CultureInfo.InvariantCulture));

            float slider = GUILayout.HorizontalSlider(m_multiplier, MinMult, MaxMult);
            if (Mathf.Abs(slider - m_multiplier) > 0.001f)
            {
                m_multiplier = RoundMult(slider);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("2x")) { m_multiplier = 2f; }
            if (GUILayout.Button("3x")) { m_multiplier = 3f; }
            if (GUILayout.Button("5x")) { m_multiplier = 5f; }
            GUILayout.EndHorizontal();

            GUILayout.Label("Built-in Fast mode:  x" + m_fastModeScale.ToString("0.0#", CultureInfo.InvariantCulture));
            float fastSlider = GUILayout.HorizontalSlider(m_fastModeScale, 1f, 10f);
            if (Mathf.Abs(fastSlider - m_fastModeScale) > 0.001f)
            {
                m_fastModeScale = Mathf.Round(fastSlider * 10f) / 10f;
                SaveConfig();
            }

            bool fastScouting = GUILayout.Toggle(m_fastScouting, " Fast Scouting");
            if (fastScouting != m_fastScouting)
            {
                m_fastScouting = fastScouting;
                SaveConfig();
            }

            bool throttleSteps = GUILayout.Toggle(m_throttleFootsteps, " Limit footstep sounds to 1.5x normal rate");
            if (throttleSteps != m_throttleFootsteps)
            {
                m_throttleFootsteps = throttleSteps;
                s_throttleFootsteps = throttleSteps;
                SaveConfig();
            }

            DrawKeyRow("Hold to accelerate:", Capturing.Hold, m_holdKey);
            DrawKeyRow("Toggle acceleration:", Capturing.Toggle, m_toggleKey);
            if (GUILayout.Button("Clear both accelerate keys"))
            {
                m_holdKey = KeyCode.None;
                m_toggleKey = KeyCode.None;
                m_toggled = false;
                SaveConfig();
            }

            GUILayout.Space(10f);
            DrawZoomControls();

            GUILayout.Space(10f);
            DrawStatsControls();

            GUILayout.Space(10f);
            DrawCheatControls();

            GUILayout.Space(10f);
            bool unclip = GUILayout.Toggle(m_unclipCursor, " Let mouse leave the game window");
            if (unclip != m_unclipCursor)
            {
                m_unclipCursor = unclip;
                ApplyCursorUnclip();
                SaveConfig();
            }

            GUILayout.Space(10f);
            m_skipIntros = GUILayout.Toggle(m_skipIntros, " Skip intro movies at game start");

            bool skipAdra = GUILayout.Toggle(m_skipNewGameIntro, " Skip New Game intro (adra pan + titles)");
            if (skipAdra != m_skipNewGameIntro)
            {
                m_skipNewGameIntro = skipAdra;
                s_skipNewGameIntro = skipAdra;
                SaveConfig();
            }

            bool noTutorials = GUILayout.Toggle(m_disableTutorials, " Disable tutorial pop-ups");
            if (noTutorials != m_disableTutorials)
            {
                m_disableTutorials = noTutorials;
                ApplyTutorialSetting(force: true);
                SaveConfig();
            }

            GUILayout.Space(10f);
            if (GUILayout.Button("Close"))
            {
                SetMenuOpen(false);
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawZoomControls()
        {
            // Checkbox only: it lowers the camera's minimum-zoom floor to m_minZoom (default 0.20);
            // the mouse wheel does the actual zooming within that range, so no slider is needed.
            bool closeZoom = GUILayout.Toggle(m_closeZoomEnabled, " Enable extra-close camera zoom");
            if (closeZoom != m_closeZoomEnabled)
            {
                m_closeZoomEnabled = closeZoom;
                ApplyZoomOverride(force: true);
                SaveConfig();
            }
        }

        private void ApplyZoomOverride(bool force = false)
        {
            try
            {
                if (GameState.Option == null)
                {
                    return;
                }

                float currentMin = GameState.Option.MinZoom;
                if (currentMin >= VanillaMinZoom - 0.001f)
                {
                    m_seenVanillaMinZoom = currentMin;
                }

                float target = m_closeZoomEnabled
                    ? Mathf.Clamp(m_minZoom, ToolkitMinZoomFloor, Mathf.Min(VanillaMinZoom, m_seenVanillaMinZoom))
                    : m_seenVanillaMinZoom;

                if (force || Mathf.Abs(currentMin - target) > 0.001f)
                {
                    GameState.Option.MinZoom = target;
                    SyncCameraOrthoSettings ortho = SyncCameraOrthoSettings.Instance;
                    if (ortho != null && ortho.GetZoomLevel() < target)
                    {
                        ortho.SetZoomLevel(target, force: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] zoom override failed: " + ex);
            }
        }

        private void DrawStatsControls()
        {
            GUILayout.Label("Character creation");
            DrawIntSetting("Attribute points:", ref m_chargenPoints, ref m_chargenPointsText, -9999, 9999);
            DrawIntSetting("Attribute maximum:", ref m_chargenStatMaximum, ref m_chargenStatMaximumText, 1, 9999);

            GUILayout.Space(6f);
            GUILayout.Label("Party");
            DrawIntSetting("Bonus to all skills:", ref m_skillBonus, ref m_skillBonusText, -9999, 9999);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Grant level"))
            {
                GrantLevel();
            }
            GUILayout.Label("selected, or party if none selected");
            GUILayout.EndHorizontal();
        }

        private void DrawCheatControls()
        {
            GUILayout.Label("Cheats");
            bool inv = GUILayout.Toggle(m_invulnerable, " Invulnerability (party never takes damage)");
            if (inv != m_invulnerable)
            {
                m_invulnerable = inv;
                SaveConfig();
            }
            bool ohk = GUILayout.Toggle(m_oneHitKills, " 1-Hit Kills (any damage kills enemies)");
            if (ohk != m_oneHitKills)
            {
                m_oneHitKills = ohk;
                SaveConfig();
            }
            bool mfs = GUILayout.Toggle(m_maxFocusStart, " Ciphers start fights at max focus");
            if (mfs != m_maxFocusStart)
            {
                m_maxFocusStart = mfs;
                SaveConfig();
            }
            if (GUILayout.Button("Remove Fog of War (this area)"))
            {
                RemoveFogOfWar();
            }
            GUILayout.Label("<i>F11: dump creature render states to CreatureDump.log</i>");
        }

        // Transient on-screen confirmation for hotkey actions (drawn in OnGUI).
        private static string s_toast = "";
        private static float s_toastUntil;

        private static void Toast(string msg)
        {
            s_toast = msg;
            s_toastUntil = Time.realtimeSinceStartup + 4f;
        }

        // The fader's cached fog flag lives in a protected field; read it for the dump.
        private static readonly System.Reflection.FieldInfo s_fogVisField =
            typeof(AIPackageController).GetField("m_isFogVisible",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // DIAGNOSTIC (F11): log the exact render/visibility state of every non-party creature
        // in the scene, so an "invisible" creature can be understood from ground truth instead
        // of guessed at. Fields answer the specific open questions: is the game's OWN fader
        // running (pkgOn) and what does its cached fog flag say (fogVis)? does the fog system
        // consider the spot visible (pv)? is anything actually rendering (rend)? how far is
        // the nearest party member (partyDist)? Written to a PER-INSTALL file —
        // <install>\PillarsOfEternity_Data\CreatureDump.log — because host and client share
        // one Player.log and clobber each other's dumps.
        private static void DumpCreatureStates()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("==== creature render dump " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " ====");
                // The unit under the mouse gets called out by name AND flagged on its line, so
                // a specific unit can be identified unambiguously ("hover it, press F11").
                GameObject hover = null;
                try
                {
                    hover = GameCursor.CharacterUnderCursor;
                    if (hover == null) { hover = GameCursor.GenericUnderCursor; }
                    if (hover == null) { hover = GameCursor.ObjectUnderCursor; }
                }
                catch { }
                sb.AppendLine("  moused-over: " + (hover != null ? hover.name : "(nothing)"));
                // Full detail on the hovered object UNCONDITIONALLY — party companions
                // (Heodan!) are excluded from the creature loop below, so this is the only
                // way to see their render state.
                if (hover != null)
                {
                    sb.AppendLine("  HOVER DETAIL: " + DescribeObject(hover));
                }
                System.Collections.Generic.List<Faction> facs = Faction.ActiveFactionComponents;
                PartyMemberAI[] party = PartyMemberAI.PartyMembers;
                int n = 0;
                for (int i = 0; facs != null && i < facs.Count; i++)
                {
                    Faction f = facs[i];
                    if (f == null || f.gameObject == null) { continue; }
                    GameObject go = f.gameObject;
                    if (go.GetComponent<PartyMemberAI>() != null) { continue; } // party excluded
                    Health h = go.GetComponent<Health>();
                    if (h == null) { continue; } // creatures only
                    int rendOn = 0, rendTot = 0;
                    try
                    {
                        Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
                        rendTot = rs.Length;
                        for (int r = 0; r < rs.Length; r++)
                        {
                            if (rs[r] != null && rs[r].enabled && rs[r].gameObject.activeInHierarchy) { rendOn++; }
                        }
                    }
                    catch { }
                    AlphaControl ac = go.GetComponent<AlphaControl>();
                    Vector3 p = go.transform.position;
                    // The game's own fader: enabled? and its cached verdict.
                    string pkgOn = "none", fogVis = "?";
                    try
                    {
                        AIPackageController pkg = go.GetComponent<AIPackageController>();
                        if (pkg != null)
                        {
                            pkgOn = pkg.enabled ? "on" : "OFF";
                            if (s_fogVisField != null) { fogVis = s_fogVisField.GetValue(pkg).ToString(); }
                        }
                    }
                    catch { }
                    bool pv = false;
                    try { pv = FogOfWar.Instance != null && FogOfWar.Instance.PointVisible(p); } catch { }
                    float partyDist = -1f;
                    try
                    {
                        for (int m = 0; party != null && m < party.Length; m++)
                        {
                            if (party[m] == null || party[m].gameObject == null) { continue; }
                            float d = Vector3.Distance(p, party[m].gameObject.transform.position);
                            if (partyDist < 0f || d < partyDist) { partyDist = d; }
                        }
                    }
                    catch { }
                    string rel = "?";
                    try { rel = f.RelationshipToPlayer.ToString(); } catch { }
                    // Display name too: quest NPCs (Heodan!) live on generic GameObjects
                    // (NPC_Caravanner_M_0X) — the go.name alone can't identify them.
                    string dispName = "";
                    try
                    {
                        CharacterStats cs = go.GetComponent<CharacterStats>();
                        if (cs != null)
                        {
                            string dn = cs.Name();
                            if (!string.IsNullOrEmpty(dn) && dn != "*NameError*") { dispName = " \"" + dn + "\""; }
                        }
                    }
                    catch { }
                    sb.AppendLine(string.Format(
                        "  {0}{1}{2} active={3} rend={4}/{5} alphaCtrl={6} pkg={7} fogVis={8} pv={9} isFow={10} partyDist={11:0.0} dead={12} hp={13:0} rel={14} pos=({15:0},{16:0},{17:0})",
                        (hover != null && go == hover ? ">>> MOUSED-OVER >>> " : ""),
                        go.name, dispName, go.activeInHierarchy, rendOn, rendTot,
                        (ac != null ? ac.Alpha.ToString("0.00") : "none"),
                        pkgOn, fogVis, pv, SafeIsFow(f), partyDist,
                        h.Dead, h.CurrentHealth, rel, p.x, p.y, p.z));
                    n++;
                }
                sb.AppendLine("==== end dump (" + n + " creatures) ====");
                string path = System.IO.Path.Combine(Application.dataPath, "CreatureDump.log");
                System.IO.File.AppendAllText(path, sb.ToString());
                Debug.Log("[LoomTimeAccelerator] creature dump: " + n + " creatures -> " + path);
                Toast("Dumped " + n + " creatures -> CreatureDump.log");
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] creature dump failed: " + ex);
                Toast("Creature dump FAILED (see Player.log)");
            }
        }

        private static string SafeIsFow(Faction f)
        {
            try { return f.isFowVisible.ToString(); } catch { return "?"; }
        }

        // Full render/visibility state of ANY GameObject — party members included, no filters.
        private static string DescribeObject(GameObject go)
        {
            try
            {
                int rendOn = 0, rendTot = 0;
                float matA = -1f;
                Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
                rendTot = rs.Length;
                for (int r = 0; r < rs.Length; r++)
                {
                    if (rs[r] == null) { continue; }
                    if (rs[r].enabled && rs[r].gameObject.activeInHierarchy) { rendOn++; }
                    if (matA < 0f && rs[r].sharedMaterial != null && rs[r].sharedMaterial.HasProperty("_Color"))
                    {
                        matA = rs[r].sharedMaterial.color.a;
                    }
                }
                AlphaControl ac = go.GetComponent<AlphaControl>();
                bool isParty = go.GetComponent<PartyMemberAI>() != null;
                string pkgOn = "none", fogVis = "?";
                AIPackageController pkg = go.GetComponent<AIPackageController>();
                if (pkg != null)
                {
                    pkgOn = pkg.enabled ? "on" : "OFF";
                    if (s_fogVisField != null) { try { fogVis = s_fogVisField.GetValue(pkg).ToString(); } catch { } }
                }
                Health h = go.GetComponent<Health>();
                Vector3 p = go.transform.position;
                return string.Format(
                    "party={0} active={1} activeInHier={2} rend={3}/{4} matAlpha={5:0.00} alphaCtrl={6} pkg={7} fogVis={8} scale={9:0.00} dead={10} pos=({11:0},{12:0},{13:0})",
                    isParty, go.activeSelf, go.activeInHierarchy, rendOn, rendTot, matA,
                    (ac != null ? ac.Alpha.ToString("0.00") : "none"), pkgOn, fogVis,
                    go.transform.lossyScale.x, (h != null ? h.Dead.ToString() : "?"), p.x, p.y, p.z);
            }
            catch (Exception ex)
            {
                return "(describe failed: " + ex.GetType().Name + ")";
            }
        }

        // One-shot: turn off this area's fog of war entirely (QueueDisable also drops the
        // line-of-sight hiding, so every creature on the map is visible regardless of where
        // your party is standing). Re-entering / reloading the area restores normal fog.
        private static void RemoveFogOfWar()
        {
            try
            {
                if (FogOfWar.Instance != null)
                {
                    FogOfWar.Instance.QueueDisable();
                    Debug.Log("[LoomTimeAccelerator] fog of war disabled for this area");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] remove fog failed: " + ex);
            }
        }

        private void DrawCheatBadge()
        {
            if (!m_invulnerable && !m_oneHitKills)
            {
                return;
            }
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 15;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.55f, 0.2f);
            string s = string.Empty;
            if (m_invulnerable) { s += "INVULN  "; }
            if (m_oneHitKills) { s += "1-HIT-KILL"; }
            GUI.Label(new Rect(12f, 34f, 340f, 24f), ">> " + s.Trim(), style);
        }

        // Every tutorial funnels through TutorialManager.TriggerTutorial, which checks the game's own
        // SHOW_TUTORIALS option, so "disable tutorials" just drives that option off - the native
        // mechanism, no patching. While enabled we re-assert it each frame (cheap; a load or the
        // options menu can flip it back); on un-check we restore it once. A couple of critical
        // tutorials are flagged ShowEvenIfDisabled and will still appear.
        private void ApplyTutorialSetting(bool force = false)
        {
            s_suppressTutorials = m_disableTutorials; // drives the Harmony prefix (nukes even ShowEvenIfDisabled)
            try
            {
                if (GameState.Option == null)
                {
                    return;
                }
                if (m_disableTutorials)
                {
                    if (GameState.Option.GetOption(GameOption.BoolOption.SHOW_TUTORIALS))
                    {
                        GameState.Option.SetOption(GameOption.BoolOption.SHOW_TUTORIALS, false);
                    }
                }
                else if (force)
                {
                    GameState.Option.SetOption(GameOption.BoolOption.SHOW_TUTORIALS, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] tutorial toggle failed: " + ex);
            }
        }

        private void ApplyCursorUnclip()
        {
            if (!m_unclipCursor)
            {
                return;
            }

            try
            {
                WinCursor.Clip(false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] cursor unclip failed: " + ex);
            }
        }

        private void DrawIntSetting(string label, ref int value, ref string text, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(160f));
            string next = GUILayout.TextField(text, GUILayout.Width(70f));
            if (next != text)
            {
                text = next;
                int parsed;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    value = Mathf.Clamp(parsed, min, max);
                    if (value.ToString(CultureInfo.InvariantCulture) != text)
                    {
                        text = value.ToString(CultureInfo.InvariantCulture);
                    }
                    SaveConfig();
                }
            }
            if (GUILayout.Button("-", GUILayout.Width(28f)))
            {
                value = Mathf.Clamp(value - 1, min, max);
                text = value.ToString(CultureInfo.InvariantCulture);
                SaveConfig();
            }
            if (GUILayout.Button("+", GUILayout.Width(28f)))
            {
                value = Mathf.Clamp(value + 1, min, max);
                text = value.ToString(CultureInfo.InvariantCulture);
                SaveConfig();
            }
            GUILayout.EndHorizontal();
        }

        private void HandleCharacterCreation()
        {
            try
            {
                UICharacterCreationManager manager = UICharacterCreationManager.Instance;
                if (manager == null)
                {
                    RestoreCharacterCreationValues();
                    return;
                }

                if (manager != m_seenCreationManager)
                {
                    RestoreCharacterCreationValues();
                    m_seenCreationManager = manager;
                    m_originalPointBuy = manager.TotalPointBuy;
                    m_originalStatHardMaximum = manager.StatHardMaximum;
                    m_hasOriginalCharacterCreationValues = true;
                }

                if (manager.CreationType == UICharacterCreationManager.CharacterCreationType.NewPlayer
                    || manager.CreationType == UICharacterCreationManager.CharacterCreationType.NewCompanion)
                {
                    manager.TotalPointBuy = m_chargenPoints;
                    manager.StatHardMaximum = m_chargenStatMaximum;
                }
                else
                {
                    RestoreCharacterCreationValues();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] chargen settings failed: " + ex);
            }
        }

        private void RestoreCharacterCreationValues()
        {
            if (m_seenCreationManager != null && m_hasOriginalCharacterCreationValues)
            {
                m_seenCreationManager.TotalPointBuy = m_originalPointBuy;
                m_seenCreationManager.StatHardMaximum = m_originalStatHardMaximum;
            }
            m_seenCreationManager = null;
            m_originalPointBuy = 0;
            m_originalStatHardMaximum = 0;
            m_hasOriginalCharacterCreationValues = false;
        }

        private void ApplySkillBonusToParty()
        {
            if (GameState.IsLoading || PartyMemberAI.PartyMembers == null)
            {
                return;
            }

            HashSet<CharacterStats> seen = new HashSet<CharacterStats>();
            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            for (int i = 0; i < members.Length; i++)
            {
                PartyMemberAI member = members[i];
                if (member == null || member.Secondary || member.Summoner != null)
                {
                    continue;
                }

                CharacterStats stats = member.GetComponent<CharacterStats>();
                if (stats == null)
                {
                    continue;
                }

                seen.Add(stats);
                ApplySkillBonus(stats);
            }

            CleanupMissingSkillBonuses(seen);
        }

        private void ApplySkillBonus(CharacterStats stats)
        {
            int[] applied = GetAppliedSkillArray(stats);
            CharacterStats.SkillType[] skills = Skills;
            for (int i = 0; i < skills.Length; i++)
            {
                int delta = m_skillBonus - applied[i];
                if (delta != 0)
                {
                    stats.AdjustSkillBonus(skills[i], delta);
                    applied[i] = m_skillBonus;
                }
            }
        }

        private void RemoveSkillBonus(CharacterStats stats)
        {
            int[] applied;
            if (!m_appliedSkillBonuses.TryGetValue(stats, out applied))
            {
                return;
            }

            CharacterStats.SkillType[] skills = Skills;
            for (int i = 0; i < skills.Length; i++)
            {
                if (applied[i] != 0)
                {
                    stats.AdjustSkillBonus(skills[i], -applied[i]);
                    applied[i] = 0;
                }
            }
        }

        private void CleanupMissingSkillBonuses(HashSet<CharacterStats> seen)
        {
            List<CharacterStats> remove = null;
            foreach (CharacterStats stats in m_appliedSkillBonuses.Keys)
            {
                if (stats == null || !seen.Contains(stats))
                {
                    if (stats != null)
                    {
                        RemoveSkillBonus(stats);
                    }
                    if (remove == null)
                    {
                        remove = new List<CharacterStats>();
                    }
                    remove.Add(stats);
                }
            }

            if (remove == null)
            {
                return;
            }
            for (int i = 0; i < remove.Count; i++)
            {
                m_appliedSkillBonuses.Remove(remove[i]);
            }
        }

        private int[] GetAppliedSkillArray(CharacterStats stats)
        {
            int[] applied;
            if (!m_appliedSkillBonuses.TryGetValue(stats, out applied))
            {
                applied = new int[Skills.Length];
                m_appliedSkillBonuses[stats] = applied;
            }
            return applied;
        }

        private void GrantLevel()
        {
            try
            {
                List<CharacterStats> targets = GetLevelGrantTargets();
                int granted = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    CharacterStats stats = targets[i];
                    if (stats == null || stats.Level >= CharacterStats.PlayerLevelCap)
                    {
                        continue;
                    }

                    int currentMax = stats.GetMaxLevelCanLevelUpTo();
                    int targetLevel = Mathf.Clamp(Mathf.Max(stats.Level, currentMax) + 1, 1, CharacterStats.PlayerLevelCap);
                    int needed = CharacterStats.ExperienceNeededForLevel(targetLevel) - stats.Experience;
                    if (needed > 0)
                    {
                        stats.AddExperience(needed);
                        granted++;
                    }
                }

                try
                {
                    Console.AddMessage("Pillars1Toolkit: granted a level to " + granted.ToString(CultureInfo.InvariantCulture)
                        + " character" + (granted == 1 ? "." : "s."), Color.cyan);
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] grant level failed: " + ex);
            }
        }

        private static List<CharacterStats> GetLevelGrantTargets()
        {
            List<CharacterStats> targets = new List<CharacterStats>();
            try
            {
                List<GameObject> selected = PartyMemberAI.GetSelectedPartyMembers();
                if (selected != null)
                {
                    for (int i = 0; i < selected.Count; i++)
                    {
                        AddStatsTarget(targets, selected[i]);
                    }
                }
            }
            catch
            {
            }

            if (targets.Count > 0)
            {
                return targets;
            }

            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            if (members != null)
            {
                for (int i = 0; i < members.Length; i++)
                {
                    PartyMemberAI member = members[i];
                    if (member == null || member.Secondary || member.Summoner != null)
                    {
                        continue;
                    }
                    AddStatsTarget(targets, member.gameObject);
                }
            }

            return targets;
        }

        private static void AddStatsTarget(List<CharacterStats> targets, GameObject obj)
        {
            if (obj == null)
            {
                return;
            }

            CharacterStats stats = obj.GetComponent<CharacterStats>();
            if (stats == null || targets.Contains(stats))
            {
                return;
            }
            targets.Add(stats);
        }

        private static CharacterStats.SkillType[] Skills
        {
            get
            {
                return new CharacterStats.SkillType[]
                {
                    CharacterStats.SkillType.Stealth,
                    CharacterStats.SkillType.Athletics,
                    CharacterStats.SkillType.Lore,
                    CharacterStats.SkillType.Mechanics,
                    CharacterStats.SkillType.Survival,
                    CharacterStats.SkillType.Crafting
                };
            }
        }

        private void DrawKeyRow(string label, Capturing which, KeyCode key)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(160f));
            string txt = (m_capturing == which) ? "press a key..." : key.ToString();
            if (GUILayout.Button(txt))
            {
                m_capturing = which;
            }
            GUILayout.EndHorizontal();
        }

        private void HandleCaptureEvent()
        {
            if (!m_legacyMenu)
            {
                GUI.SetNextControlName("Pillars1ToolkitKeyCapture");
                GUI.TextField(new Rect(-100f, -100f, 2f, 2f), string.Empty);
                GUI.FocusControl("Pillars1ToolkitKeyCapture");
            }

            Event e = Event.current;
            if (e == null || (e.type != EventType.KeyDown && e.type != EventType.KeyUp))
            {
                return;
            }

            KeyCode k = e.keyCode;
            if (k == KeyCode.None)
            {
                return;
            }

            // Escape cancels the rebind without changing anything.
            if (k != KeyCode.Escape)
            {
                switch (m_capturing)
                {
                    case Capturing.Hold: m_holdKey = k; break;
                    case Capturing.Toggle: m_toggleKey = k; break;
                    case Capturing.Menu: m_menuKey = k; break;
                }
            }

            m_capturing = Capturing.None;
            e.Use();
            SaveConfig();
            NativeToolkitMenu.FinishCapture(this);
        }

        private void SetMenuOpen(bool open)
        {
            if (!m_legacyMenu)
            {
                if (open)
                {
                    m_menuOpen = NativeToolkitMenu.Show(this);
                }
                else
                {
                    NativeToolkitMenu.Hide();
                    m_menuOpen = false;
                }
                return;
            }

            if (open == m_menuOpen)
            {
                return;
            }

            m_menuOpen = open;
            // Block game input (camera pan / unit orders) while the menu is up, then restore.
            if (open)
            {
                if (!GameInput.DisableInput)
                {
                    GameInput.DisableInput = true;
                    m_inputDisabledByUs = true;
                }
            }
            else
            {
                m_capturing = Capturing.None;
                if (m_inputDisabledByUs)
                {
                    GameInput.DisableInput = false;
                    m_inputDisabledByUs = false;
                }
                SaveConfig();
            }
        }

        private static float RoundMult(float v)
        {
            return Mathf.Round(v * 100f) / 100f;
        }

        private static bool SafeKeyInputAvailable()
        {
            try
            {
                return UIWindowManager.KeyInputAvailable;
            }
            catch
            {
                return true;
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (m_configPath == null || !File.Exists(m_configPath))
                {
                    return;
                }

                foreach (string raw in File.ReadAllLines(m_configPath))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "multiplier":
                            float m;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                            {
                                m_multiplier = Mathf.Clamp(m, MinMult, MaxMult);
                            }
                            break;
                        case "fastModeScale":
                            float fm;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out fm))
                            {
                                m_fastModeScale = Mathf.Clamp(fm, 1f, 10f);
                            }
                            break;
                        case "fastScouting":
                            m_fastScouting = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "holdKey":
                            m_holdKey = ParseKey(val, m_holdKey);
                            break;
                        case "toggleKey":
                            m_toggleKey = ParseKey(val, m_toggleKey);
                            break;
                        case "menuKey":
                            m_menuKey = ParseKey(val, m_menuKey);
                            break;
                        case "legacyMenu":
                            m_legacyMenu = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "skipIntros":
                            m_skipIntros = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "disableTutorials":
                            m_disableTutorials = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "skipNewGameIntro":
                            m_skipNewGameIntro = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "throttleFootsteps":
                            m_throttleFootsteps = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "closeZoomEnabled":
                            m_closeZoomEnabled = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "minZoom":
                            float z;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                            {
                                m_minZoom = Mathf.Clamp(z, ToolkitMinZoomFloor, VanillaMinZoom);
                            }
                            break;
                        case "chargenPoints":
                            m_chargenPoints = ParseIntSetting(val, m_chargenPoints, -9999, 9999);
                            m_chargenPointsText = m_chargenPoints.ToString(CultureInfo.InvariantCulture);
                            break;
                        case "chargenStatMaximum":
                            m_chargenStatMaximum = ParseIntSetting(val, m_chargenStatMaximum, 1, 9999);
                            m_chargenStatMaximumText = m_chargenStatMaximum.ToString(CultureInfo.InvariantCulture);
                            break;
                        case "skillBonus":
                            m_skillBonus = ParseIntSetting(val, m_skillBonus, -9999, 9999);
                            m_skillBonusText = m_skillBonus.ToString(CultureInfo.InvariantCulture);
                            break;
                        case "unclipCursor":
                            m_unclipCursor = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "invulnerable":
                            m_invulnerable = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "oneHitKills":
                            m_oneHitKills = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                        case "maxFocusStart":
                            m_maxFocusStart = val == "1" || val.ToLowerInvariant() == "true";
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] load config: " + ex);
            }
        }

        private void SaveConfig()
        {
            try
            {
                if (m_configPath == null)
                {
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add("# Pillars1Toolkit settings (internal hook: LoomTimeAccelerator)");
                lines.Add("multiplier=" + m_multiplier.ToString("0.0#", CultureInfo.InvariantCulture));
                lines.Add("fastModeScale=" + m_fastModeScale.ToString("0.0#", CultureInfo.InvariantCulture));
                lines.Add("fastScouting=" + (m_fastScouting ? "1" : "0"));
                lines.Add("holdKey=" + m_holdKey);
                lines.Add("toggleKey=" + m_toggleKey);
                lines.Add("menuKey=" + m_menuKey);
                lines.Add("legacyMenu=" + (m_legacyMenu ? "1" : "0"));
                lines.Add("skipIntros=" + (m_skipIntros ? "1" : "0"));
                lines.Add("disableTutorials=" + (m_disableTutorials ? "1" : "0"));
                lines.Add("skipNewGameIntro=" + (m_skipNewGameIntro ? "1" : "0"));
                lines.Add("throttleFootsteps=" + (m_throttleFootsteps ? "1" : "0"));
                lines.Add("closeZoomEnabled=" + (m_closeZoomEnabled ? "1" : "0"));
                lines.Add("minZoom=" + m_minZoom.ToString("0.00", CultureInfo.InvariantCulture));
                lines.Add("chargenPoints=" + m_chargenPoints.ToString(CultureInfo.InvariantCulture));
                lines.Add("chargenStatMaximum=" + m_chargenStatMaximum.ToString(CultureInfo.InvariantCulture));
                lines.Add("skillBonus=" + m_skillBonus.ToString(CultureInfo.InvariantCulture));
                lines.Add("unclipCursor=" + (m_unclipCursor ? "1" : "0"));
                lines.Add("invulnerable=" + (m_invulnerable ? "1" : "0"));
                lines.Add("oneHitKills=" + (m_oneHitKills ? "1" : "0"));
                lines.Add("maxFocusStart=" + (m_maxFocusStart ? "1" : "0"));
                File.WriteAllLines(m_configPath, lines.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] save config: " + ex);
            }
        }

        private static KeyCode ParseKey(string s, KeyCode fallback)
        {
            try
            {
                return (KeyCode)Enum.Parse(typeof(KeyCode), s, true);
            }
            catch
            {
                return fallback;
            }
        }

        private static int ParseIntSetting(string s, int fallback, int min, int max)
        {
            int parsed;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return Mathf.Clamp(parsed, min, max);
            }
            return fallback;
        }
    }
}
