// Text entry for names, notes and numbers: the Quest system keyboard (TouchScreenKeyboard, enabled through
// OVRProjectConfig.requiresSystemKeyboard), with a large on-panel keyboard as fallback when the system keyboard
// does not appear within 2 s (or on request).
using System;
using System.Text;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnKeyboard
    {
        private const float SystemTimeout = 2f;

        private readonly AnnotationMode m_mode;
        private TouchScreenKeyboard m_keyboard;
        private Action<string> m_done;
        private string m_title;
        private string m_initial;
        private bool m_numeric;
        private float m_openedAt;
        private bool m_seenVisible;
        private IAnnScreen m_returnTo;

        public AnnKeyboard(AnnotationMode mode) => m_mode = mode;

        public bool IsOpen => m_keyboard != null;

        /// <summary>Opens the Quest system keyboard (or the panel keyboard) and calls done with the trimmed text when confirmed.</summary>
        public void Open(string title, string initial, bool numeric, Action<string> done)
        {
            m_title = title;
            m_initial = initial ?? "";
            m_numeric = numeric;
            m_done = done;
            m_returnTo = m_mode.CurrentScreen;
            m_seenVisible = false;
            m_openedAt = Time.realtimeSinceStartup;
            m_keyboard = null;
            try
            {
                if (TouchScreenKeyboard.isSupported)
                {
                    m_keyboard = TouchScreenKeyboard.Open(m_initial,
                        numeric ? TouchScreenKeyboardType.NumbersAndPunctuation : TouchScreenKeyboardType.Default,
                        false, false, false, false, title);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"ann_keyboard_system_failed {e.Message}");
                m_keyboard = null;
            }

            Log.Info($"ann_keyboard_open title=\"{title}\" system={(m_keyboard != null)}");
            if (m_keyboard == null)
            {
                OpenPanelKeyboard();
            }
        }

        /// <summary>Per frame: watches the system keyboard (Done / Cancel / never appeared → panel keyboard).</summary>
        public void Update()
        {
            if (m_keyboard == null)
            {
                return;
            }

            switch (m_keyboard.status)
            {
                case TouchScreenKeyboard.Status.Visible:
                    m_seenVisible = true;
                    if (m_keyboard.active == false && Time.realtimeSinceStartup - m_openedAt > SystemTimeout)
                    {
                        Fallback();
                    }

                    break;
                case TouchScreenKeyboard.Status.Done:
                {
                    var text = m_keyboard.text;
                    m_keyboard = null;
                    Finish(text);
                    break;
                }
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus:
                    m_keyboard = null;
                    Log.Info("ann_keyboard_cancel");
                    break;
            }

            if (m_keyboard != null && !m_seenVisible && Time.realtimeSinceStartup - m_openedAt > SystemTimeout)
            {
                Fallback();
            }
        }

        /// <summary>Closes the system keyboard and shows the panel keyboard instead.</summary>
        private void Fallback()
        {
            Log.Warn("ann_keyboard_system_not_visible: using the panel keyboard");
            try
            {
                m_keyboard.active = false;
            }
            catch (Exception)
            {
                // ignore
            }

            m_keyboard = null;
            OpenPanelKeyboard();
        }

        /// <summary>Shows the on-panel keyboard; returns to the calling screen afterwards.</summary>
        private void OpenPanelKeyboard()
        {
            m_mode.Show(new PanelKeyboardScreen(m_mode, m_title, m_initial, m_numeric, text =>
            {
                m_mode.Show(m_returnTo);
                if (text != null)
                {
                    Finish(text);
                }
            }));
        }

        /// <summary>Hands the text to the caller and refreshes the panel.</summary>
        private void Finish(string text)
        {
            text = (text ?? "").Trim();
            Log.Info($"ann_keyboard_done title=\"{m_title}\" length={text.Length}");
            var done = m_done;
            m_done = null;
            done?.Invoke(text);
            m_mode.Refresh();
        }
    }

    /// <summary>Large on-panel keyboard (fallback). Returns null on cancel.</summary>
    public sealed class PanelKeyboardScreen : IAnnScreen
    {
        private static readonly string[] Letters = { "1234567890", "QWERTYUIOP", "ASDFGHJKL_", "ZXCVBNM-.," };
        private static readonly string[] Digits = { "789", "456", "123", "0.-" };

        private readonly AnnotationMode m_mode;
        private readonly string m_title;
        private readonly bool m_numeric;
        private readonly Action<string> m_done;
        private readonly StringBuilder m_text;
        private bool m_shift = true;
        private UnityEngine.UI.Text m_display;

        public PanelKeyboardScreen(AnnotationMode mode, string title, string initial, bool numeric, Action<string> done)
        {
            m_mode = mode;
            m_title = title;
            m_numeric = numeric;
            m_done = done;
            m_text = new StringBuilder(initial ?? "");
        }

        public string Title => m_title;

        /// <summary>Keyboard rows (letters or digits), Delete / Clear / Space, OK and Cancel.</summary>
        public void Build(AnnPanel p)
        {
            m_display = p.Label(Display(), 34, AnnPanel.Accent);
            foreach (var row in m_numeric ? Digits : Letters)
            {
                p.BeginRow();
                foreach (var ch in row)
                {
                    var c = ch;
                    var label = !m_numeric && char.IsLetter(c) && !m_shift ? char.ToLowerInvariant(c).ToString() : c.ToString();
                    p.Button(label, () => Type(label), true, null, 60, 30);
                }

                p.EndRow();
            }

            p.BeginRow();
            if (!m_numeric)
            {
                p.Button(m_shift ? "abc" : "ABC", () =>
                {
                    m_shift = !m_shift;
                    m_mode.Refresh();
                }, true, null, 60, 26);
                p.Button("Space", () => Type(" "), true, null, 60, 26);
            }

            p.Button("Delete", () =>
            {
                if (m_text.Length > 0)
                {
                    m_text.Length--;
                }

                UpdateDisplay();
            }, true, null, 60, 26);
            p.Button("Clear", () =>
            {
                m_text.Clear();
                UpdateDisplay();
            }, true, null, 60, 26);
            p.EndRow();
            p.BeginRow();
            p.Button("Cancel", () => m_done(null), true, AnnPanel.DangerButton);
            p.Button("OK", () => m_done(m_text.ToString()), true, AnnPanel.PrimaryButton);
            p.EndRow();
        }

        /// <summary>Appends a character (max 80).</summary>
        private void Type(string s)
        {
            if (m_text.Length < 80)
            {
                m_text.Append(s);
            }

            UpdateDisplay();
        }

        /// <summary>Updates the text line without rebuilding the panel.</summary>
        private void UpdateDisplay()
        {
            if (m_display != null)
            {
                m_display.text = Display();
            }
        }

        /// <summary>Text shown above the keys (with a cursor).</summary>
        private string Display() => m_text.Length == 0 ? "<color=#8A96A3>(empty)</color>" : m_text + "_";
    }
}
