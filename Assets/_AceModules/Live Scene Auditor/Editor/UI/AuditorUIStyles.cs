using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    public static class AuditorUIStyles
    {
        private static GUIStyle _headerTitle;
        private static GUIStyle _cardBox;
        private static GUIStyle _cardHeader;
        private static GUIStyle _badgeGreen;
        private static GUIStyle _badgeYellow;
        private static GUIStyle _badgeRed;
        private static GUIStyle _metricLabel;
        private static GUIStyle _metricValue;
        private static GUIStyle _toolbarButton;
        private static GUIStyle _subtleText;

        public static readonly Color ColorGood = new Color(0.24f, 0.76f, 0.38f, 1f);
        public static readonly Color ColorWarning = new Color(0.96f, 0.71f, 0.16f, 1f);
        public static readonly Color ColorDanger = new Color(0.92f, 0.28f, 0.28f, 1f);
        public static readonly Color ColorCardBg = new Color(0.2f, 0.2f, 0.2f, 0.45f);
        public static readonly Color ColorBarBg = new Color(0.12f, 0.12f, 0.12f, 0.8f);

        public static GUIStyle HeaderTitle
        {
            get
            {
                if (_headerTitle == null)
                {
                    _headerTitle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 16,
                        normal = { textColor = new Color(0.92f, 0.94f, 0.96f) }
                    };
                }
                return _headerTitle;
            }
        }

        public static GUIStyle CardBox
        {
            get
            {
                if (_cardBox == null)
                {
                    _cardBox = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(12, 12, 10, 10),
                        margin = new RectOffset(4, 4, 6, 6)
                    };
                }
                return _cardBox;
            }
        }

        public static GUIStyle CardHeader
        {
            get
            {
                if (_cardHeader == null)
                {
                    _cardHeader = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        normal = { textColor = new Color(0.85f, 0.88f, 0.92f) }
                    };
                }
                return _cardHeader;
            }
        }

        public static GUIStyle SubtleText
        {
            get
            {
                if (_subtleText == null)
                {
                    _subtleText = new GUIStyle(EditorStyles.miniLabel)
                    {
                        fontSize = 10,
                        normal = { textColor = new Color(0.6f, 0.62f, 0.65f) }
                    };
                }
                return _subtleText;
            }
        }

        public static GUIStyle MetricValue
        {
            get
            {
                if (_metricValue == null)
                {
                    _metricValue = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 11,
                        alignment = TextAnchor.MiddleRight
                    };
                }
                return _metricValue;
            }
        }

        private class BarAnimState
        {
            public float currentValue;
            public double lastUpdateTime;
            public bool isAnimating;
        }

        private static readonly Dictionary<string, BarAnimState> _barAnimStates = new Dictionary<string, BarAnimState>();

        public static bool HasActiveAnimations
        {
            get
            {
                foreach (var s in _barAnimStates.Values)
                {
                    if (s.isAnimating) return true;
                }
                return false;
            }
        }

        public static float GetAnimatedValue(string key, float targetValue)
        {
            double now = EditorApplication.timeSinceStartup;

            if (!_barAnimStates.TryGetValue(key, out var state))
            {
                state = new BarAnimState { currentValue = targetValue, lastUpdateTime = now, isAnimating = false };
                _barAnimStates[key] = state;
                return targetValue;
            }

            float snapThreshold = Mathf.Max(0.1f, Mathf.Abs(targetValue) * 0.002f);
            float diff = targetValue - state.currentValue;
            if (Mathf.Abs(diff) <= snapThreshold)
            {
                state.currentValue = targetValue;
                state.lastUpdateTime = now;
                state.isAnimating = false;
                return targetValue;
            }

            float dt = (float)(now - state.lastUpdateTime);
            if (dt > 0.0001f)
            {
                dt = Mathf.Min(dt, 0.05f); // Prevent jump after long pauses
                state.lastUpdateTime = now;

                // Smooth exponential decay lerp: ~95% in 0.3s
                float t = 1f - Mathf.Exp(-9.0f * dt);
                state.currentValue = Mathf.Lerp(state.currentValue, targetValue, t);

                if (Mathf.Abs(targetValue - state.currentValue) <= snapThreshold)
                {
                    state.currentValue = targetValue;
                }
            }

            state.isAnimating = Mathf.Abs(targetValue - state.currentValue) > snapThreshold;
            if (state.isAnimating)
            {
                RequestAnimationRepaint();
            }

            return state.currentValue;
        }

        public static void RequestAnimationRepaint()
        {
            var window = LiveSceneAuditorWindow.GetActiveWindow();
            if (window != null)
            {
                window.Repaint();
            }
            SceneView.RepaintAll();
        }

        public static void DrawBudgetProgressBar(string label, float current, float max, string unit = "")
        {
            float animCurrent = GetAnimatedValue("Win_" + label, current);
            float ratio = max > 0 ? (animCurrent / max) : 0f;

            string percentText;
            if (ratio <= 0f) percentText = "0%";
            else if (ratio < 0.10f) percentText = $"{(ratio * 100f):F1}%";
            else percentText = $"{Mathf.Round(ratio * 100f):N0}%";

            Color barColor = ColorGood;
            if (ratio >= 1.0f)
                barColor = ColorDanger;
            else if (ratio >= 0.75f)
                barColor = ColorWarning;

            EditorGUILayout.BeginVertical();
            
            // Header line: Label and Current / Max (Percent)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(160));
            
            string valueText = $"{animCurrent:N0} / {max:N0} {unit} ({percentText})";
            if (current % 1 != 0 || animCurrent % 1 != 0)
            {
                valueText = $"{animCurrent:F1} / {max:F1} {unit} ({percentText})";
            }

            GUIStyle coloredValueStyle = new GUIStyle(MetricValue);
            coloredValueStyle.normal.textColor = barColor;
            EditorGUILayout.LabelField(valueText, coloredValueStyle);
            EditorGUILayout.EndHorizontal();

            // Progress bar
            Rect progressRect = GUILayoutUtility.GetRect(18, 14, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(progressRect, ColorBarBg);

            float fillWidth = Mathf.Clamp01(ratio) * progressRect.width;
            Rect fillRect = new Rect(progressRect.x, progressRect.y, fillWidth, progressRect.height);
            EditorGUI.DrawRect(fillRect, barColor);

            // Thin border
            Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.4f);
            Handles.DrawLine(new Vector3(progressRect.x, progressRect.y), new Vector3(progressRect.xMax, progressRect.y));
            Handles.DrawLine(new Vector3(progressRect.x, progressRect.yMax), new Vector3(progressRect.xMax, progressRect.yMax));

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        public static void DrawBadge(string text, Color bgColor, Color textColor)
        {
            GUIContent content = new GUIContent(text);
            GUIStyle badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 2, 2),
                normal = { textColor = textColor }
            };

            Vector2 size = badgeStyle.CalcSize(content);
            Rect rect = GUILayoutUtility.GetRect(size.x + 8, size.y + 4, GUILayout.ExpandWidth(false));
            
            EditorGUI.DrawRect(rect, bgColor);
            GUI.Label(rect, content, badgeStyle);
        }
    }
}
