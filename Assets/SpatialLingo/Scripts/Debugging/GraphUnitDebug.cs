// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.Samples;
using SpatialLingo.AppSystems;
using SpatialLingo.Lessons;
using SpatialLingo.VisualScriptingUnits;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Tracks the current flow state and sends debug events to the app flow.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class GraphUnitDebug : MonoBehaviour
    {
        private const string DEBUG_FMT = "Current state: {0}";
        // Kept short: the button is 160 wide and a longer label wraps to two lines.
        private const string DEBUG_MODE_FMT = "DEBUG: {0}";

        [SerializeField] private TMP_Text m_debugText;
        [SerializeField] private Button m_skipButton;
        [SerializeField] private Button m_boundsButton;
        [SerializeField] private TMP_Text m_sessionText;

        [Tooltip("Toggles the room scan overlay, the lesson panel under the mic and the activity panel.")]
        [SerializeField] private Button m_debugModeButton;
        [SerializeField] private TMP_Text m_debugModeLabel;

        private void Start()
        {
            SkippableUnit.UnitEntered += OnGraphUnitChanged;
            m_skipButton?.onClick.AddListener(NextState);
            m_boundsButton?.gameObject.SetActive(false);
            OnGraphUnitChanged(SkippableUnit.CurrentName);

            m_debugModeButton?.onClick.AddListener(ToggleDebugMode);
            SpatialLingoDebug.Changed += OnDebugModeChanged;
            OnDebugModeChanged(SpatialLingoDebug.Enabled);
        }

        private void OnDestroy()
        {
            if (m_skipButton != null)
            {
                m_skipButton.onClick.RemoveListener(NextState);
            }

            if (m_boundsButton != null)
            {
                m_boundsButton.onClick.RemoveListener(NextState);
            }

            if (m_debugModeButton != null)
            {
                m_debugModeButton.onClick.RemoveListener(ToggleDebugMode);
            }

            SpatialLingoDebug.Changed -= OnDebugModeChanged;
            SkippableUnit.UnitEntered -= OnGraphUnitChanged;
        }

        private static void ToggleDebugMode() => SpatialLingoDebug.Toggle();

        private void OnDebugModeChanged(bool enabled)
        {
            if (m_debugModeLabel != null)
            {
                m_debugModeLabel.text = string.Format(DEBUG_MODE_FMT, enabled ? "ON" : "OFF");
            }
        }

        private void OnGraphUnitChanged(string unitName)
        {
            if (string.IsNullOrEmpty(unitName))
            {
                return;
            }

            if (m_boundsButton != null)
            {
                if (unitName.Contains(nameof(LessonWaitingState)))
                {
                    m_boundsButton.gameObject.SetActive(true);
                    if (m_boundsButton.onClick.GetPersistentEventCount() == 0)
                    {
                        m_boundsButton.onClick.AddListener(ToggleBounds);
                    }
                }
                else
                {
                    m_boundsButton.gameObject.SetActive(false);
                    m_boundsButton.onClick.RemoveListener(ToggleBounds);
                }
            }

            m_debugText.text = string.Format(DEBUG_FMT, unitName);
        }

        private void NextState()
        {
            EventBus.Trigger(ScriptEventNames.DEBUG_SKIP);
        }

        private void ToggleBounds()
        {
            Lesson3DInteractor.ShowDebugFeatures = !Lesson3DInteractor.ShowDebugFeatures;
        }

        private void Update()
        {
            m_sessionText.text = AppSessionData.GetDebugString();
        }
    }
}