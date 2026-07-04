// Pillars1Speedhack — a configurable time-acceleration ("fast-forward") mod for
// Pillars of Eternity 1. See README.md and docs/HOW_IT_WORKS.md.
//
// The namespace / assembly is named "LoomTimeAccelerator" on purpose: it is the identifier
// the installer injects into GameState.Update(), so the sidecar DLL, the namespace, and the
// injected call must all agree. It is internal-only; the mod itself is "Pillars1Speedhack".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    public class Accelerator : MonoBehaviour
    {
        private enum Capturing { None, Hold, Toggle, Menu }

        private const float MinMult = 1.25f;
        private const float MaxMult = 10f;

        private float m_multiplier = 3f;

        // Separate bindings: hold-to-accelerate and toggle-acceleration.
        private KeyCode m_holdKey = KeyCode.None;
        private KeyCode m_toggleKey = KeyCode.Backslash;
        private KeyCode m_menuKey = KeyCode.F10;

        private bool m_toggled;   // sticky on/off driven by the toggle key
        private bool m_enabled;   // effective this frame (toggle OR hold)
        private bool m_active;    // actually multiplying this frame

        private bool m_menuOpen;
        private Capturing m_capturing = Capturing.None;
        private bool m_inputDisabledByUs;
        private Rect m_window = new Rect(60f, 60f, 340f, 0f);
        private string m_configPath;

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

        private void LateUpdate()
        {
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
                m_window = GUILayout.Window(0x54494D45, m_window, DrawWindow, "Time Accelerator");
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
                lines.Add("# Loom Time Accelerator settings");
                lines.Add("multiplier=" + m_multiplier.ToString("0.0#", CultureInfo.InvariantCulture));
                lines.Add("holdKey=" + m_holdKey);
                lines.Add("toggleKey=" + m_toggleKey);
                lines.Add("menuKey=" + m_menuKey);
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
    }
}
