using System;
using System.Reflection;
using UnityEngine;

namespace LoomTimeAccelerator
{
    /// <summary>
    /// Reusable runtime extension for PoE1's native options window. It clones the live page,
    /// tab, label, and checkbox widgets so fonts, sprites, audio, hover states, scaling, and
    /// controller behavior all come from the game rather than from an asset bundle.
    /// </summary>
    internal sealed class NativeOptionsPage
    {
        private readonly UIOptionsManager m_options;
        private readonly GameObject m_page;
        private readonly UIMultiSpriteImageButton m_tab;
        private readonly UIOptionsTag m_checkboxPrototype;
        private float m_nextY = 280f;
        private UILabel m_sliderValueLabel;

        private static readonly FieldInfo PageButtonsField = typeof(UIOptionsManager).GetField(
            "m_PageButtons", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PageButtonGridField = typeof(UIOptionsManager).GetField(
            "m_PageButtonGrid", BindingFlags.Instance | BindingFlags.NonPublic);

        public UIOptionsManager Options { get { return m_options; } }
        public bool IsActive { get { return m_page != null && m_page.activeSelf; } }
        public UILabel SliderValueLabel { get { return m_sliderValueLabel; } }

        public static bool IsReady(UIOptionsManager options)
        {
            return options != null && options.Pages != null && options.Pages.Length > 0 &&
                options.PageButtonPrefab != null && PageButtonsField != null &&
                PageButtonsField.GetValue(options) != null;
        }

        public NativeOptionsPage(UIOptionsManager options, string pageName, string tabText)
        {
            if (options == null || options.Pages == null || options.Pages.Length == 0)
            {
                throw new InvalidOperationException("UIOptionsManager pages are not initialized.");
            }
            if (options.PageButtonPrefab == null || PageButtonsField == null)
            {
                throw new InvalidOperationException("UIOptionsManager tab controls are not initialized.");
            }

            m_options = options;
            m_checkboxPrototype = FindCheckboxPrototype(options);
            if (m_checkboxPrototype == null)
            {
                throw new InvalidOperationException("No live native checkbox prototype was found.");
            }

            GameObject sourcePage = FindPagePrototype(options);
            m_page = UnityEngine.Object.Instantiate(sourcePage);
            m_page.name = pageName;
            m_page.transform.parent = sourcePage.transform.parent;
            m_page.transform.localPosition = sourcePage.transform.localPosition;
            m_page.transform.localRotation = sourcePage.transform.localRotation;
            m_page.transform.localScale = sourcePage.transform.localScale;
            ClearChildren(m_page.transform);
            m_page.SetActive(false);

            GameObject tabObject = NGUITools.AddChild(
                options.PageButtonPrefab.transform.parent.gameObject,
                options.PageButtonPrefab.gameObject);
            m_tab = tabObject.GetComponent<UIMultiSpriteImageButton>();
            m_tab.name = "Toolkit." + options.PageButtonPrefab.name;
            SetLabel(m_tab.Label, tabText);

            UIMultiSpriteImageButton[] oldButtons =
                (UIMultiSpriteImageButton[])PageButtonsField.GetValue(options);
            UIMultiSpriteImageButton[] newButtons = new UIMultiSpriteImageButton[oldButtons.Length + 1];
            Array.Copy(oldButtons, newButtons, oldButtons.Length);
            newButtons[newButtons.Length - 1] = m_tab;
            PageButtonsField.SetValue(options, newButtons);

            GameObject[] newPages = new GameObject[options.Pages.Length + 1];
            Array.Copy(options.Pages, newPages, options.Pages.Length);
            newPages[newPages.Length - 1] = m_page;
            options.Pages = newPages;

            m_tab.onClick = (UIEventListener.VoidDelegate)Delegate.Combine(
                m_tab.onClick, new UIEventListener.VoidDelegate(OnTabClicked));

            UIGrid grid = PageButtonGridField != null
                ? (UIGrid)PageButtonGridField.GetValue(options)
                : options.PageButtonGroup.GetComponent<UIGrid>();
            if (grid != null)
            {
                grid.Reposition();
            }
        }

        public void AddHeading(string text)
        {
            UILabel label = UnityEngine.Object.Instantiate(m_checkboxPrototype.Label);
            label.name = "Heading";
            label.transform.parent = m_page.transform;
            label.transform.localScale = Vector3.one;
            label.transform.localPosition = new Vector3(-210f, 335f, 0f);
            SetLabel(label, text);
        }

        public void AddCheckbox(string name, string text, bool initial, Action<bool> changed)
        {
            UIOptionsTag row = UnityEngine.Object.Instantiate(m_checkboxPrototype);
            row.name = name;
            row.enabled = false;
            row.transform.parent = m_page.transform;
            row.transform.localScale = Vector3.one;
            row.transform.localPosition = new Vector3(-210f, m_nextY, 0f);
            m_nextY -= 45f;

            UICheckbox checkbox = row.Checkbox;
            UILabel label = row.Label;
            SetLabel(label, text);
            checkbox.onStateChange = null;
            checkbox.onStateChangeUser = null;
            checkbox.eventReceiver = null;
            checkbox.functionName = string.Empty;
            checkbox.radioButtonRoot = null;
            checkbox.optionCanBeNone = true;
            checkbox.startsChecked = initial;
            checkbox.SetNoCallback(initial);
            checkbox.onStateChangeUser = delegate(GameObject sender, bool state)
            {
                changed(state);
            };

            if (label != null)
            {
                UIEventListener listener = UIEventListener.Get(label.gameObject);
                listener.onClick = delegate(GameObject sender)
                {
                    checkbox.isChecked = !checkbox.isChecked;
                    if (checkbox.onStateChangeUser != null)
                    {
                        checkbox.onStateChangeUser(checkbox.gameObject, checkbox.isChecked);
                    }
                };
            }
        }

        public UIMultiSpriteImageButton AddKeybind(string name, string text, string initial,
            UIEventListener.VoidDelegate clicked)
        {
            UILabel label = UnityEngine.Object.Instantiate(m_checkboxPrototype.Label);
            label.name = name + "Label";
            label.transform.parent = m_page.transform;
            label.transform.localScale = Vector3.one;
            label.transform.localPosition = new Vector3(-210f, m_nextY, 0f);
            SetLabel(label, text);

            UIMultiSpriteImageButton prototype = m_options.ApplyResolutionButton != null
                ? m_options.ApplyResolutionButton
                : m_options.DefControlsButton;
            if (prototype == null)
            {
                throw new InvalidOperationException("No live native keybind button prototype was found.");
            }
            UIMultiSpriteImageButton button = UnityEngine.Object.Instantiate(prototype);
            button.name = name;
            button.transform.parent = m_page.transform;
            button.transform.localPosition = new Vector3(125f, m_nextY, 0f);
            button.transform.localRotation = prototype.transform.localRotation;
            button.transform.localScale = prototype.transform.localScale;
            button.onClick = clicked;
            UILabel[] inheritedButtonLabels = button.GetComponentsInChildren<UILabel>(true);
            for (int i = 0; i < inheritedButtonLabels.Length; i++)
            {
                inheritedButtonLabels[i].gameObject.SetActive(false);
            }
            UILabel valueLabel = UnityEngine.Object.Instantiate(m_checkboxPrototype.Label);
            valueLabel.name = name + "Value";
            valueLabel.transform.parent = button.transform;
            valueLabel.transform.localPosition = new Vector3(0f, 0f, -1f);
            valueLabel.transform.localScale = Vector3.one;
            valueLabel.pivot = UIWidget.Pivot.Center;
            valueLabel.lineWidth = 260;
            SetLabel(valueLabel, initial);
            // Never assign this as button.Label: the button cached its ORIGINAL label's
            // position in Awake and snaps Label back there on hover/press, which teleports
            // a late-assigned label off the row. Keep the value label ours alone, drawn
            // above the button's sprites.
            UIWidget[] widgets = button.GetComponentsInChildren<UIWidget>(true);
            int maxDepth = 0;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (widgets[i] != null && widgets[i] != valueLabel && widgets[i].depth > maxDepth)
                {
                    maxDepth = widgets[i].depth;
                }
            }
            valueLabel.depth = maxDepth + 5;
            m_valueLabel = valueLabel;
            m_nextY -= 45f;
            return button;
        }

        // Value label of the most recent AddKeybind call; consumed by the menu builder.
        private UILabel m_valueLabel;
        public UILabel LastValueLabel { get { return m_valueLabel; } }

        public UIOptionsSliderGroup AddSlider(string name, string text, float minimum, float maximum,
            float step, float initial, Action<float> changed)
        {
            UIOptionsSliderGroup prototype = m_options.TooltipDelay != null
                ? m_options.TooltipDelay
                : m_options.GammaSlider;
            if (prototype == null || prototype.Slider == null)
            {
                throw new InvalidOperationException("No live native slider prototype was found.");
            }

            UIOptionsSliderGroup group = UnityEngine.Object.Instantiate(prototype);
            group.name = name;
            group.transform.parent = m_page.transform;
            group.transform.localScale = Vector3.one;
            group.transform.localPosition = new Vector3(35f, m_nextY, 0f);
            group.OnChanged = null;
            group.Slider.OnChanged = null;
            group.Slider.SpotTooltipStrings = new GUIDatabaseString[0];
            group.FormatString = default(GUIDatabaseString);
            group.NumberAdd = minimum;
            group.NumberMultiplier = step;
            group.Slider.Range = Mathf.RoundToInt((maximum - minimum) / step) + 1;

            UILabel titleLabel = null;
            UILabel[] inheritedLabels = group.GetComponentsInChildren<UILabel>(true);
            for (int i = 0; i < inheritedLabels.Length; i++)
            {
                if (inheritedLabels[i] != null && inheritedLabels[i] != group.NumberLabel)
                {
                    if (titleLabel == null)
                    {
                        titleLabel = inheritedLabels[i];
                        SetLabel(titleLabel, text);
                    }
                    else
                    {
                        inheritedLabels[i].gameObject.SetActive(false);
                    }
                }
            }
            if (group.NumberLabel != null)
            {
                group.NumberLabel.gameObject.SetActive(false);
            }
            UILabel valueLabel = UnityEngine.Object.Instantiate(m_checkboxPrototype.Label);
            m_sliderValueLabel = valueLabel;
            valueLabel.name = name + "Value";
            valueLabel.transform.parent = m_page.transform;
            valueLabel.transform.localScale = Vector3.one;
            valueLabel.pivot = UIWidget.Pivot.Left;
            SetLabel(valueLabel, "x" + initial.ToString("0.##"));
            if (group.Slider.Track != null)
            {
                float right = group.Slider.PuckMax;
                if (Mathf.Abs(right) < 0.001f)
                {
                    right = group.Slider.Track.transform.localPosition.x
                        + group.Slider.Track.transform.localScale.x;
                }
                Vector3 worldRight = group.Slider.transform.TransformPoint(new Vector3(right, 0f, 0f));
                Vector3 rightInPage = m_page.transform.InverseTransformPoint(worldRight);
                valueLabel.transform.localPosition = new Vector3(
                    rightInPage.x + 18f, rightInPage.y, rightInPage.z);
            }
            group.Setting = initial;
            UIOptionsSlider.OnSettingChanged applySliderSetting = delegate(UIOptionsSlider sender, int setting)
            {
                float rounded = minimum + setting * step;
                if (valueLabel != null)
                {
                    SetLabel(valueLabel, "x" + rounded.ToString("0.##"));
                }
                changed(rounded);
            };
            group.Slider.OnChanged = applySliderSetting;
            group.OnChanged = null;
            if (group.Slider.Puck != null)
            {
                UIEventListener.Get(group.Slider.Puck.gameObject).onDrag = delegate(GameObject sender, Vector2 delta)
                {
                    SetSliderFromMouse(group.Slider);
                };
            }
            if (group.Slider.Track != null)
            {
                UIEventListener.Get(group.Slider.Track.gameObject).onClick = delegate(GameObject sender)
                {
                    SetSliderFromMouse(group.Slider);
                };
            }
            if (group.Slider.DownArrow != null)
            {
                UIEventListener.Get(group.Slider.DownArrow.gameObject).onClick = delegate(GameObject sender)
                {
                    group.Slider.Setting--;
                };
            }
            if (group.Slider.UpArrow != null)
            {
                UIEventListener.Get(group.Slider.UpArrow.gameObject).onClick = delegate(GameObject sender)
                {
                    group.Slider.Setting++;
                };
            }
            m_nextY -= 75f;
            return group;
        }

        private static void SetSliderFromMouse(UIOptionsSlider slider)
        {
            if (slider == null || slider.Track == null || InGameUILayout.NGUICamera == null || slider.Range < 2)
            {
                return;
            }
            float minimum = slider.PuckMin;
            float maximum = slider.PuckMax;
            if (minimum == 0f && maximum == 0f)
            {
                minimum = slider.Track.transform.localPosition.x;
                maximum = minimum + slider.Track.transform.localScale.x;
            }
            float notch = (maximum - minimum) / (slider.Range - 1f);
            float mouseX = slider.transform.worldToLocalMatrix.MultiplyPoint3x4(
                InGameUILayout.NGUICamera.ScreenToWorldPoint(GameInput.MousePosition)).x;
            slider.Setting = Mathf.Clamp(
                Mathf.FloorToInt((mouseX - minimum - notch * 0.5f) / notch) + 1,
                0,
                slider.Range - 1);
        }

        public bool Show()
        {
            if (!m_options.WindowActive() && !m_options.ShowWindow())
            {
                return false;
            }
            Activate();
            return true;
        }

        public void Activate()
        {
            if (m_options.PageButtonGroup != null && m_tab != null)
            {
                m_options.PageButtonGroup.DoSelect(m_tab.gameObject);
            }
            for (int i = 0; i < m_options.Pages.Length; i++)
            {
                m_options.Pages[i].SetActive(m_options.Pages[i] == m_page);
            }
            if (m_tab != null)
            {
                m_tab.SetToggleStateNoCallback(true);
            }
        }

        private void OnTabClicked(GameObject sender)
        {
            Activate();
        }

        private static UIOptionsTag FindCheckboxPrototype(UIOptionsManager options)
        {
            UIOptionsTag[] tags = options.GetComponentsInChildren<UIOptionsTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] != null && tags[i].Checkbox != null && tags[i].Label != null &&
                    tags[i].BoolSuboption == GameOption.BoolOption.SCREEN_EDGE_SCROLLING)
                {
                    return tags[i];
                }
            }
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] != null && tags[i].Checkbox != null && tags[i].Label != null)
                {
                    return tags[i];
                }
            }
            return null;
        }

        private static GameObject FindPagePrototype(UIOptionsManager options)
        {
            for (int i = options.Pages.Length - 1; i >= 0; i--)
            {
                if (options.Pages[i] != null)
                {
                    return options.Pages[i];
                }
            }
            throw new InvalidOperationException("No native options page prototype was found.");
        }

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                GameObject child = root.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static void SetLabel(UILabel label, string text)
        {
            if (label == null)
            {
                return;
            }
            GUIStringLabel stringLabel = label.GetComponent<GUIStringLabel>();
            if (stringLabel != null)
            {
                stringLabel.enabled = false;
            }
            label.text = text;
        }
    }

    public partial class Accelerator
    {
        private static NativeOptionsPage s_nativeMenu;
        private static UIOptionsManager s_nativeOptions;
        private static bool s_nativeMenuFailed;
        private static float s_nativeRetryAt;
        private static UIMultiSpriteImageButton s_holdKeyButton;
        private static UIMultiSpriteImageButton s_toggleKeyButton;
        // Keybind value text lives in labels WE own, never registered as button.Label:
        // UIMultiSpriteImageButton caches its Label's localPosition in Awake (before ours
        // existed) and snaps Label back to that stale position on every hover/press, which
        // teleported the bind text off the button ("labels disappear on mouse-over").
        private static UILabel s_holdKeyLabel;
        private static UILabel s_toggleKeyLabel;
        private static UIOptionsSliderGroup s_multiplierSlider;
        private static UILabel s_multiplierValueLabel;
        private static UIOptionsSliderGroup s_fastModeSlider;
        private static UILabel s_fastModeValueLabel;
        private static UIMessageBox s_keybindPrompt;
        private static int s_captureArmedFrame;
        // Grace deadline after the prompt closes: the UIMessageBox confirms on Space/Return
        // (MB_CONFIRM) and can swallow the very keypress being bound, closing the dialog
        // before our scan runs. Instead of cancelling capture the moment the dialog dies,
        // keep scanning ~half a second — Input.GetKey still reports the key held.
        private static int s_captureCancelAtFrame;
        // Reentrancy guard: RefreshMultiplier pushes config -> slider every frame; without
        // the guard that programmatic set re-enters the user callback and can clobber the
        // configured multiplier (observed: 5.0 -> 1.25 = slider minimum, across the
        // native-menu deploy boundary in LoomCoop.log speed requests).
        private static bool s_syncingSlider;
        private static readonly KeyCode[] CaptureKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        internal static class NativeToolkitMenu
        {
            public static void Tick(Accelerator owner)
            {
                UIOptionsManager options = UIOptionsManager.Instance;
                if (options != s_nativeOptions)
                {
                    s_nativeMenu = null;
                    s_nativeOptions = options;
                    s_nativeMenuFailed = false;
                    s_nativeRetryAt = 0f;
                    s_holdKeyButton = null;
                    s_toggleKeyButton = null;
                    s_holdKeyLabel = null;
                    s_toggleKeyLabel = null;
                    s_multiplierSlider = null;
                    s_multiplierValueLabel = null;
                    s_fastModeSlider = null;
                    s_fastModeValueLabel = null;
                }

                if (options != null && s_nativeMenu == null && !s_nativeMenuFailed &&
                    Time.unscaledTime >= s_nativeRetryAt)
                {
                    TryCreate(owner, options);
                }

                if (!owner.m_legacyMenu && owner.m_menuOpen &&
                    (s_nativeMenu == null || !s_nativeMenu.Options.WindowActive()))
                {
                    owner.m_menuOpen = false;
                }
                else if (!owner.m_legacyMenu && s_nativeMenu != null && s_nativeMenu.IsActive &&
                    s_nativeMenu.Options.WindowActive())
                {
                    owner.m_menuOpen = true;
                }

                RefreshKeybindLabels(owner);
                RefreshSliders(owner);
            }

            public static bool HandleCaptureInput(Accelerator owner)
            {
                if (owner.m_capturing == Capturing.None)
                {
                    return false;
                }
                if (Time.frameCount < s_captureArmedFrame)
                {
                    return true;
                }
                // Prompt gone (user keypress or OK click closed it) and the grace window
                // has elapsed with no key seen: cancel the capture for real.
                if (s_captureCancelAtFrame > 0 && Time.frameCount > s_captureCancelAtFrame)
                {
                    s_captureCancelAtFrame = 0;
                    owner.m_capturing = Capturing.None;
                    RefreshKeybindLabels(owner);
                    return true;
                }
                for (int i = 0; i < CaptureKeys.Length; i++)
                {
                    KeyCode key = CaptureKeys[i];
                    if (key == KeyCode.None || (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) ||
                        !Input.GetKey(key))
                    {
                        continue;
                    }
                    Capturing target = owner.m_capturing;
                    owner.m_capturing = Capturing.None;
                    s_captureCancelAtFrame = 0;
                    if (key != KeyCode.Escape)
                    {
                        if (target == Capturing.Hold) { owner.m_holdKey = key; }
                        else if (target == Capturing.Toggle) { owner.m_toggleKey = key; }
                        Debug.Log("[Pillars1Toolkit] bound " + target + " key to " + key);
                    }
                    CloseKeybindPrompt();
                    owner.SaveConfig();
                    RefreshKeybindLabels(owner);
                    return true;
                }
                return true;
            }

            public static bool HandleMenuHotkey(Accelerator owner)
            {
                if (owner.m_menuKey == KeyCode.None || !Input.GetKeyDown(owner.m_menuKey))
                {
                    return false;
                }
                if (s_nativeMenu != null && s_nativeMenu.IsActive &&
                    s_nativeMenu.Options.WindowActive())
                {
                    Hide();
                    owner.m_menuOpen = false;
                }
                else
                {
                    owner.m_menuOpen = Show(owner);
                }
                return true;
            }

            public static void FinishCapture(Accelerator owner)
            {
                s_captureCancelAtFrame = 0;
                CloseKeybindPrompt();
                RefreshKeybindLabels(owner);
            }

            public static bool Show(Accelerator owner)
            {
                if (s_nativeMenu == null && UIOptionsManager.Instance != null)
                {
                    TryCreate(owner, UIOptionsManager.Instance);
                }
                if (s_nativeMenu == null)
                {
                    Debug.LogError("[Pillars1Toolkit] native menu is not ready; set legacyMenu=1 for the IMGUI fallback");
                    return false;
                }
                return s_nativeMenu.Show();
            }

            public static void Hide()
            {
                if (s_nativeMenu != null && s_nativeMenu.Options.WindowActive())
                {
                    s_nativeMenu.Options.HideWindow();
                }
            }

            private static void TryCreate(Accelerator owner, UIOptionsManager options)
            {
                if (!NativeOptionsPage.IsReady(options))
                {
                    s_nativeRetryAt = Time.unscaledTime + 0.5f;
                    return;
                }
                try
                {
                    NativeOptionsPage page = new NativeOptionsPage(
                        options, "Pillars1ToolkitPage", "Pillars1Toolkit");
                    s_fastModeSlider = page.AddSlider("ToolkitFastMode", "Built-in Fast mode speed",
                        MinFastModeScale, MaxFastModeScale, 0.1f, owner.m_fastModeScale, delegate(float value)
                        {
                            if (s_syncingSlider) { return; }
                            float rounded = Mathf.Round(value * 10f) / 10f;
                            if (Mathf.Abs(rounded - owner.m_fastModeScale) > 0.001f)
                            {
                                owner.m_fastModeScale = rounded;
                                owner.SaveConfig();
                            }
                        });
                    s_fastModeValueLabel = page.SliderValueLabel;
                    s_holdKeyButton = page.AddKeybind("ToolkitHoldKey", "Hold to accelerate",
                        FormatKey(owner.m_holdKey), delegate(GameObject sender)
                        {
                            BeginKeybindCapture(owner, Capturing.Hold, "Hold to accelerate");
                        });
                    s_holdKeyLabel = page.LastValueLabel;
                    s_toggleKeyButton = page.AddKeybind("ToolkitToggleKey", "Toggle acceleration",
                        FormatKey(owner.m_toggleKey), delegate(GameObject sender)
                        {
                            BeginKeybindCapture(owner, Capturing.Toggle, "Toggle acceleration");
                        });
                    s_toggleKeyLabel = page.LastValueLabel;
                    // The cloned controls' displayed value labels land on the opposite visual
                    // row in this options-page layout. Keep the binding targets unchanged, but
                    // flip the refresh handles so each key is painted beside its actual action.
                    UILabel keyLabelSwap = s_holdKeyLabel;
                    s_holdKeyLabel = s_toggleKeyLabel;
                    s_toggleKeyLabel = keyLabelSwap;
                    page.AddCheckbox("ToolkitFootsteps", "Limit footstep sounds to 1.5x normal rate",
                        owner.m_throttleFootsteps, delegate(bool value)
                        {
                            owner.m_throttleFootsteps = value;
                            s_throttleFootsteps = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitFastScouting", "Fast Scouting",
                        owner.m_fastScouting, delegate(bool value)
                        {
                            owner.m_fastScouting = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitInvulnerable", "Invulnerability (party never takes damage)",
                        owner.m_invulnerable, delegate(bool value)
                        {
                            owner.m_invulnerable = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitOneHitKills", "1-Hit Kills (any damage kills enemies)",
                        owner.m_oneHitKills, delegate(bool value)
                        {
                            owner.m_oneHitKills = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitMaxFocus", "Ciphers start fights at max focus",
                        owner.m_maxFocusStart, delegate(bool value)
                        {
                            owner.m_maxFocusStart = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitSkipStartup", "Skip intro movies at game start",
                        owner.m_skipIntros, delegate(bool value)
                        {
                            owner.m_skipIntros = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitSkipNewGame", "Skip New Game intro (adra pan + titles)",
                        owner.m_skipNewGameIntro, delegate(bool value)
                        {
                            owner.m_skipNewGameIntro = value;
                            s_skipNewGameIntro = value;
                            owner.SaveConfig();
                        });
                    page.AddCheckbox("ToolkitTutorials", "Disable tutorial pop-ups",
                        owner.m_disableTutorials, delegate(bool value)
                        {
                            owner.m_disableTutorials = value;
                            owner.ApplyTutorialSetting(true);
                            owner.SaveConfig();
                        });
                    s_nativeMenu = page;
                    s_nativeOptions = options;
                    Debug.Log("[Pillars1Toolkit] native options page installed");
                }
                catch (InvalidOperationException ex)
                {
                    s_nativeRetryAt = Time.unscaledTime + 1f;
                    Debug.LogWarning("[Pillars1Toolkit] native menu waiting for options UI: " + ex.Message);
                }
                catch (Exception ex)
                {
                    s_nativeMenuFailed = true;
                    Debug.LogError("[Pillars1Toolkit] native menu install failed: " + ex);
                }
            }

            private static void RefreshKeybindLabels(Accelerator owner)
            {
                if (s_holdKeyLabel != null)
                {
                    SetLabelText(s_holdKeyLabel,
                        owner.m_capturing == Capturing.Hold ? "Press a key..." : FormatKey(owner.m_holdKey));
                }
                if (s_toggleKeyLabel != null)
                {
                    SetLabelText(s_toggleKeyLabel,
                        owner.m_capturing == Capturing.Toggle ? "Press a key..." : FormatKey(owner.m_toggleKey));
                }
            }

            private static void RefreshSliders(Accelerator owner)
            {
                if (s_multiplierSlider != null && s_multiplierSlider.Slider != null
                    && Mathf.Abs(s_multiplierSlider.Setting - owner.m_multiplier) > 0.001f)
                {
                    s_syncingSlider = true;
                    try { s_multiplierSlider.Setting = owner.m_multiplier; }
                    finally { s_syncingSlider = false; }
                }
                if (s_multiplierValueLabel != null)
                {
                    SetLabelText(s_multiplierValueLabel,
                        "x" + owner.m_multiplier.ToString("0.##"));
                }
                if (s_fastModeSlider != null && s_fastModeSlider.Slider != null
                    && Mathf.Abs(s_fastModeSlider.Setting - owner.m_fastModeScale) > 0.001f)
                {
                    s_syncingSlider = true;
                    try { s_fastModeSlider.Setting = owner.m_fastModeScale; }
                    finally { s_syncingSlider = false; }
                }
                if (s_fastModeValueLabel != null)
                {
                    SetLabelText(s_fastModeValueLabel,
                        "x" + owner.m_fastModeScale.ToString("0.##"));
                }
            }

            private static void BeginKeybindCapture(Accelerator owner, Capturing target, string label)
            {
                CloseKeybindPrompt();
                owner.m_capturing = target;
                s_captureArmedFrame = Time.frameCount + 1;
                s_captureCancelAtFrame = 0;
                if (UIWindowManager.Instance != null)
                {
                    s_keybindPrompt = UIWindowManager.ShowMessageBox(
                        UIMessageBox.ButtonStyle.OK,
                        "Set keybind",
                        "Press a key for " + label + ".\nEscape cancels.");
                    s_keybindPrompt.OnDialogEnd = delegate(UIMessageBox.Result result, UIMessageBox sender)
                    {
                        // Don't cancel here: the box itself closes on Space/Return
                        // (MB_CONFIRM) and would eat that keypress as the bind. Give the
                        // Update scan a grace window; it still sees the key held.
                        if (owner.m_capturing == target)
                        {
                            s_captureCancelAtFrame = Time.frameCount + 30;
                        }
                        s_keybindPrompt = null;
                    };
                }
                RefreshKeybindLabels(owner);
            }

            private static void CloseKeybindPrompt()
            {
                if (s_keybindPrompt == null)
                {
                    return;
                }
                UIMessageBox prompt = s_keybindPrompt;
                s_keybindPrompt = null;
                prompt.OnDialogEnd = null;
                prompt.HideWindow(true);
            }

            private static string FormatKey(KeyCode key)
            {
                return key == KeyCode.None ? "Unbound" : key.ToString();
            }

            private static void SetLabelText(UILabel label, string text)
            {
                GUIStringLabel stringLabel = label.GetComponent<GUIStringLabel>();
                if (stringLabel != null)
                {
                    stringLabel.enabled = false;
                }
                label.text = text;
            }
        }
    }
}
