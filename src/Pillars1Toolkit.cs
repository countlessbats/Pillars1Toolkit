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
using UnityEngine;

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
    public class Accelerator : MonoBehaviour
    {
        private enum Capturing { None, Hold, Toggle, Menu }

        private const float MinMult = 1.25f;
        private const float MaxMult = 10f;
        private const float VanillaMinZoom = 0.75f;
        private const float ToolkitMinZoomFloor = 0.10f;
        private const int DefaultAttributePoints = 15;
        private const int DefaultStatMaximum = 18;
        private const string EyelessFinaleConversation = "data/conversations/px2_04_eyeless_stronghold/px2_04_cv_abydon_finale.conversation";

        private float m_multiplier = 3f;
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

        // Separate bindings: hold-to-accelerate and toggle-acceleration.
        private KeyCode m_holdKey = KeyCode.None;
        private KeyCode m_toggleKey = KeyCode.Backslash;
        private KeyCode m_menuKey = KeyCode.F10;

        private bool m_toggled;   // sticky on/off driven by the toggle key
        private bool m_enabled;   // effective this frame (toggle OR hold)
        private bool m_active;    // actually multiplying this frame

        private bool m_skipIntros = true;   // auto-skip the startup logo movies
        private bool m_introHandled;        // stop looking once handled / past the intro window

        private bool m_menuOpen;
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
                m_configPath = Path.Combine(Application.persistentDataPath, "LoomTimeAccelerator.cfg");
            }
            catch
            {
                m_configPath = "LoomTimeAccelerator.cfg";
            }
            LoadConfig();
        }

        private void Update()
        {
            TrySkipIntro();
            ApplyZoomOverride();
            HandleCharacterCreation();
            ApplySkillBonusToParty();
            ApplyCursorUnclip();
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

            bool hold = keyInput && m_holdKey != KeyCode.None && Input.GetKey(m_holdKey);
            if (keyInput && m_toggleKey != KeyCode.None && Input.GetKeyDown(m_toggleKey))
            {
                m_toggled = !m_toggled;
            }

            m_enabled = m_toggled || hold;
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

            // Priority 1: unpause-first. SafePaused=false always unpauses (and never pauses). Gate on
            // player pause only (UiPaused covers menus/inventory/dialogue, which we leave alone).
            if (TimeController.Instance.Paused && !TimeController.Instance.UiPaused)
            {
                ConsumeSpace();
                TimeController.Instance.SafePaused = false;
                return;
            }

            // Priority 2: end our unit's turn in turn-based combat.
            if (TryEndTurnOnSpace())
            {
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
                if (mgr == null || mgr.TurnLocked)
                {
                    return false;
                }

                AIController who = mgr.WhoseTurn;
                if (who == null || !who.IsControllablePartyMember())
                {
                    return false; // enemy / environment / nobody's active turn -> Space may still pause
                }

                // It's our unit's turn: Space means End Turn and only End Turn.
                ConsumeSpace();
                if (CanEndTurn(who))
                {
                    mgr.FinishTurn(who, PassTurnStyle.UI);
                    m_pendingEndTurn = null;
                }
                else
                {
                    m_pendingEndTurn = who; // mid-move / mid-action: queue and retry, like the button does
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

            // Apply after TimeController.Update() has set the frame's base timescale.
            m_active = false;
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

        private void OnGUI()
        {
            if (m_capturing != Capturing.None)
            {
                HandleCaptureEvent();
            }

            if (m_active && !m_menuOpen)
            {
                DrawStatusBadge();
            }

            if (m_menuOpen)
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

            GUILayout.Space(10f);
            DrawZoomControls();

            GUILayout.Space(10f);
            DrawStatsControls();

            GUILayout.Space(10f);
            DrawTestingControls();

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

            GUILayout.Space(10f);
            DrawKeyRow("Hold to accelerate:", Capturing.Hold, m_holdKey);
            DrawKeyRow("Toggle acceleration:", Capturing.Toggle, m_toggleKey);
            if (GUILayout.Button("Clear both accelerate keys"))
            {
                m_holdKey = KeyCode.None;
                m_toggleKey = KeyCode.None;
                m_toggled = false;
                SaveConfig();
            }

            GUILayout.Space(8f);
            DrawKeyRow("Open this menu:", Capturing.Menu, m_menuKey);

            GUILayout.Space(10f);
            if (GUILayout.Button("Close"))
            {
                SetMenuOpen(false);
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawZoomControls()
        {
            bool closeZoom = GUILayout.Toggle(m_closeZoomEnabled, " Enable extra-close camera zoom");
            if (closeZoom != m_closeZoomEnabled)
            {
                m_closeZoomEnabled = closeZoom;
                ApplyZoomOverride(force: true);
                SaveConfig();
            }

            GUI.enabled = m_closeZoomEnabled;
            GUILayout.Label("Closest zoom:  " + m_minZoom.ToString("0.00", CultureInfo.InvariantCulture)
                + "  (lower = closer)");
            float zoom = GUILayout.HorizontalSlider(m_minZoom, ToolkitMinZoomFloor, VanillaMinZoom);
            if (Mathf.Abs(zoom - m_minZoom) > 0.001f)
            {
                m_minZoom = Mathf.Round(zoom * 100f) / 100f;
                ApplyZoomOverride(force: true);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Extreme")) { m_minZoom = 0.10f; ApplyZoomOverride(force: true); SaveConfig(); }
            if (GUILayout.Button("Close")) { m_minZoom = 0.20f; ApplyZoomOverride(force: true); SaveConfig(); }
            if (GUILayout.Button("Vanilla")) { m_minZoom = VanillaMinZoom; ApplyZoomOverride(force: true); SaveConfig(); }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
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

        private void DrawTestingControls()
        {
            GUILayout.Label("Testing");
            if (GUILayout.Button("Start Eyeless finale conversation"))
            {
                StartEyelessFinaleConversation(0);
            }

            if (GUILayout.Button("Jump to Eyeless tempering branch"))
            {
                StartEyelessFinaleConversation(145);
            }
        }

        private void StartEyelessFinaleConversation(int nodeId)
        {
            try
            {
                if (ConversationManager.Instance == null)
                {
                    AddToolkitMessage("conversation manager is not ready.", Color.yellow);
                    return;
                }

                GameObject owner = GetConversationOwner();
                if (owner == null)
                {
                    AddToolkitMessage("no player or party member is loaded.", Color.yellow);
                    return;
                }

                ConversationManager.Instance.KillAllBarkStrings();
                ConversationManager.Instance.StartConversation(EyelessFinaleConversation, nodeId, owner, FlowChartPlayer.DisplayMode.Standard);
                AddToolkitMessage("started Eyeless finale conversation at node " + nodeId.ToString(CultureInfo.InvariantCulture) + ".", Color.cyan);
            }
            catch (Exception ex)
            {
                Debug.LogError("[LoomTimeAccelerator] Eyeless finale test failed: " + ex);
                AddToolkitMessage("Eyeless finale test failed; see output_log.txt.", Color.yellow);
            }
        }

        private static GameObject GetConversationOwner()
        {
            if (GameState.s_playerCharacter != null)
            {
                return GameState.s_playerCharacter.gameObject;
            }

            if (PartyMemberAI.PartyMembers == null)
            {
                return null;
            }

            for (int i = 0; i < PartyMemberAI.PartyMembers.Length; i++)
            {
                PartyMemberAI member = PartyMemberAI.PartyMembers[i];
                if (member != null)
                {
                    return member.gameObject;
                }
            }

            return null;
        }

        private static void AddToolkitMessage(string message, Color color)
        {
            try
            {
                Console.AddMessage("Pillars1Toolkit: " + message, color);
            }
            catch
            {
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
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
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
        }

        private void SetMenuOpen(bool open)
        {
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
                        case "holdKey":
                            m_holdKey = ParseKey(val, m_holdKey);
                            break;
                        case "toggleKey":
                            m_toggleKey = ParseKey(val, m_toggleKey);
                            break;
                        case "menuKey":
                            m_menuKey = ParseKey(val, m_menuKey);
                            break;
                        case "skipIntros":
                            m_skipIntros = val == "1" || val.ToLowerInvariant() == "true";
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
                lines.Add("holdKey=" + m_holdKey);
                lines.Add("toggleKey=" + m_toggleKey);
                lines.Add("menuKey=" + m_menuKey);
                lines.Add("skipIntros=" + (m_skipIntros ? "1" : "0"));
                lines.Add("closeZoomEnabled=" + (m_closeZoomEnabled ? "1" : "0"));
                lines.Add("minZoom=" + m_minZoom.ToString("0.00", CultureInfo.InvariantCulture));
                lines.Add("chargenPoints=" + m_chargenPoints.ToString(CultureInfo.InvariantCulture));
                lines.Add("chargenStatMaximum=" + m_chargenStatMaximum.ToString(CultureInfo.InvariantCulture));
                lines.Add("skillBonus=" + m_skillBonus.ToString(CultureInfo.InvariantCulture));
                lines.Add("unclipCursor=" + (m_unclipCursor ? "1" : "0"));
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
