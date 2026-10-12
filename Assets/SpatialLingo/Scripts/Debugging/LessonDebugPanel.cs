// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Text;
using Meta.XR.Samples;
using SpatialLingo.AI;
using TMPro;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Debug readout under the microphone icon during a lesson: what was sent to the assistant,
    /// what it cost, and - when the attempt was rejected - the reason the model gave.
    ///
    /// Binds to the assistant rather than being pushed to, so it reports every evaluation
    /// including the stale ones ExerciseManager discards.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class LessonDebugPanel : MonoBehaviour
    {
        private const int MAX_TRANSCRIPT_LENGTH = 60;

        private static readonly Color s_colorPass = new(0.35f, 0.85f, 0.40f);
        private static readonly Color s_colorFail = new(0.95f, 0.62f, 0.20f);
        private static readonly Color s_colorIdle = new(0.65f, 0.70f, 0.76f);

        [Tooltip("Child object toggled with debug mode. Must NOT be this object: deactivating " +
                 "ourselves would stop OnDisable/OnEnable from ever hearing the toggle again.")]
        [SerializeField] private GameObject m_content;

        [SerializeField] private TextMeshPro m_text;

        private AssistantAI m_assistant;
        private string m_pendingTranscript;
        private float m_maxWidth;

        private void Awake()
        {
            if (m_text != null)
            {
                m_maxWidth = m_text.rectTransform.sizeDelta.x;
            }

            if (m_content == null && m_text != null)
            {
                m_content = m_text.gameObject;
            }

            if (m_content == gameObject)
            {
                Debug.LogWarning($"{nameof(LessonDebugPanel)} - content must be a child, not this object; " +
                                 "falling back to the text object so the toggle keeps working.");
                m_content = m_text != null ? m_text.gameObject : null;
            }
        }

        private void OnEnable()
        {
            SpatialLingoDebug.Changed += OnDebugModeChanged;
            OnDebugModeChanged(SpatialLingoDebug.Enabled);
        }

        private void OnDisable()
        {
            SpatialLingoDebug.Changed -= OnDebugModeChanged;
        }

        private void OnDestroy()
        {
            Unbind();
        }

        public void Bind(AssistantAI assistant)
        {
            if (m_assistant == assistant)
            {
                return;
            }

            Unbind();
            m_assistant = assistant;
            if (m_assistant != null)
            {
                m_assistant.EvaluateTranscriptionComplete += OnEvaluationComplete;
            }
        }

        private void Unbind()
        {
            if (m_assistant != null)
            {
                m_assistant.EvaluateTranscriptionComplete -= OnEvaluationComplete;
                m_assistant = null;
            }
        }

        /// <summary>
        /// Called when a transcription is handed to the assistant, before the round trip, so the
        /// panel shows the request while it is still in flight rather than only after it lands.
        /// </summary>
        public void ReportRequestSent(string transcript)
        {
            m_pendingTranscript = transcript;
            if (!SpatialLingoDebug.Enabled || m_text == null)
            {
                return;
            }

            m_text.color = s_colorIdle;
            m_text.text = $"SENT   {Shorten(transcript)}\n       waiting...";
            FitWidthToText();
        }

        public void Clear()
        {
            m_pendingTranscript = null;
            if (m_text != null)
            {
                m_text.text = string.Empty;
            }
        }

        private void OnDebugModeChanged(bool enabled)
        {
            if (m_content != null)
            {
                m_content.SetActive(enabled);
            }

            if (!enabled)
            {
                Clear();
            }
        }

        private void OnEvaluationComplete(WordCloudTranscriptPhraseResult result)
        {
            if (!SpatialLingoDebug.Enabled || m_text == null || result == null)
            {
                return;
            }

            var text = new StringBuilder();
            var transcript = string.IsNullOrEmpty(result.Transcript) ? m_pendingTranscript : result.Transcript;
            _ = text.Append("SENT   ").Append(Shorten(transcript)).Append('\n');
            _ = text.Append("       ").Append(FormatStats(result.Stats)).Append('\n');
            _ = text.Append('\n');

            if (result.IsPassing)
            {
                _ = text.Append("PASS");
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(result.FailReason)
                    ? "no reason returned"
                    : result.FailReason.Trim();
                _ = text.Append("FAIL   ").Append(reason);
            }

            _ = text.Append("\n\n").Append(FormatExpected(result));

            m_text.color = result.IsPassing ? s_colorPass : s_colorFail;
            m_text.text = text.ToString();
            FitWidthToText();
            m_pendingTranscript = null;
        }

        /// <summary>
        /// Shrink the text box to its widest line, capped at the authored width. The lines are
        /// left-aligned so the indented stats line up under the transcript, and in a box wider
        /// than the text that would push a short readout off to the left of the mic icon. The box
        /// pivots on its centre, so narrowing it keeps the readout centred under the icon.
        /// </summary>
        private void FitWidthToText()
        {
            if (m_maxWidth <= 0.0f)
            {
                return;
            }

            var rect = m_text.rectTransform;
            var preferred = m_text.GetPreferredValues(m_text.text, m_maxWidth, 0.0f).x;
            rect.sizeDelta = new Vector2(Mathf.Min(preferred, m_maxWidth), rect.sizeDelta.y);
        }

        /// <summary>
        /// Stats are null when the request produced no response at all, which is a different
        /// failure from the model answering "no" and worth distinguishing here.
        /// </summary>
        private static string FormatStats(Meta.Utilities.MetaModelAPI.MetaModelRestApi.ModelUsage usage)
        {
            if (usage == null)
            {
                return "no response";
            }

            return $"{usage.RoundTripMilliseconds / 1000.0:F2}s   {usage.TotalTokens} tok ({usage.PromptTokens}+{usage.CompletionTokens})";
        }

        /// <summary>
        /// The word lists the evaluation was graded against, so a rejection can be checked
        /// against what was actually on screen.
        /// </summary>
        private static string FormatExpected(WordCloudTranscriptPhraseResult result)
        {
            var parts = new StringBuilder("expects: ");
            _ = parts.Append(Join(result.Nouns));
            if (result.Adjectives is { Length: > 0 })
            {
                _ = parts.Append(" | ").Append(Join(result.Adjectives));
            }

            if (result.Verbs is { Length: > 0 })
            {
                _ = parts.Append(" | ").Append(Join(result.Verbs));
            }

            return parts.ToString();
        }

        private static string Join(string[] words) => words is { Length: > 0 } ? string.Join(", ", words) : "-";

        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return value.Length <= MAX_TRANSCRIPT_LENGTH
                ? $"\"{value}\""
                : $"\"...{value[^MAX_TRANSCRIPT_LENGTH..]}\"";
        }
    }
}
