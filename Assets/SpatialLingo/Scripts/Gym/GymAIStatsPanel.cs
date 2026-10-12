// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.Utilities.ObjectClassifier;
using Meta.XR.Samples;
using SpatialLingo.AI;
using TMPro;
using UnityEngine;

namespace SpatialLingo.Gym
{
    /// <summary>
    /// Displays the model metrics for each kind of AI query the gym station can run. Every query
    /// type keeps one row, refreshed in place, so the panel stays a fixed size and the types can
    /// be compared against each other rather than scrolling away.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class GymAIStatsPanel : MonoBehaviour
    {
        // One row per query type the station can issue. Order here is the display order.
        private enum QueryKind
        {
            Translate = 0,
            Words = 1,
            Image = 2,
            Dialogue = 3,
        }

        private const int QUERY_KIND_COUNT = 4;

        private static readonly string[] s_rowLabels = { "Translate", "Words", "Image", "Dialogue" };

        // TMP has no monospace font here, so force uniform advance to keep the columns aligned.
        private const string MONOSPACE_TAG = "<mspace=0.55em>";

        [Tooltip("Station whose queries are reported. Defaults to the controller on this object.")]
        [SerializeField] private GymAIController m_controller;

        [Tooltip("Text field the stats table is written to")]
        [SerializeField] private TextMeshPro m_statsText;

        [Tooltip("Face blur step, reported on its own row. Defaults to the component on this object.")]
        [SerializeField] private FaceDetection m_faceBlur;

        // Latest metrics per query type. Null means that query has not completed yet.
        private MetaModelRestApi.ModelUsage[] m_latest = new MetaModelRestApi.ModelUsage[QUERY_KIND_COUNT];

        // Set when a query completed but reported no usage, so the row can say so rather than
        // staying blank as though it had never run.
        private bool[] m_failed = new bool[QUERY_KIND_COUNT];

        // Face blur is local work rather than a model query, so it gets its own row instead of
        // a slot in the table above.
        private FaceDetection.BlurStats? m_latestBlur;

        private AssistantAI m_assistant;

        private void Awake()
        {
            if (m_controller == null)
            {
                m_controller = GetComponent<GymAIController>();
            }

            if (m_faceBlur == null)
            {
                m_faceBlur = GetComponent<FaceDetection>();
            }
        }

        private void Start()
        {
            Redraw();

            if (m_faceBlur != null)
            {
                m_faceBlur.BlurCompleted += OnBlurCompleted;
            }

            if (m_controller == null)
            {
                Debug.LogWarning($"{nameof(GymAIStatsPanel)} - no {nameof(GymAIController)} assigned, no stats will be reported.");
                return;
            }

            // The controller builds its assistant only once the gym systems are ready, which can
            // land either side of this Start, so handle both orderings.
            if (m_controller.Assistant != null)
            {
                Bind(m_controller.Assistant);
            }
            else
            {
                m_controller.AssistantReady += Bind;
            }
        }

        private void Bind(AssistantAI assistant)
        {
            if (m_assistant != null)
            {
                return;
            }

            m_assistant = assistant;
            m_assistant.TranslationComplete += OnTranslationComplete;
            m_assistant.FindRelatedWordsComplete += OnFindRelatedWordsComplete;
            m_assistant.CollectImageSummaryComplete += OnImageSummaryComplete;
            m_assistant.GenerateDialogueComplete += OnGenerateDialogueComplete;
        }

        private void OnDestroy()
        {
            if (m_faceBlur != null)
            {
                m_faceBlur.BlurCompleted -= OnBlurCompleted;
            }

            if (m_controller != null)
            {
                m_controller.AssistantReady -= Bind;
            }

            if (m_assistant != null)
            {
                m_assistant.TranslationComplete -= OnTranslationComplete;
                m_assistant.FindRelatedWordsComplete -= OnFindRelatedWordsComplete;
                m_assistant.CollectImageSummaryComplete -= OnImageSummaryComplete;
                m_assistant.GenerateDialogueComplete -= OnGenerateDialogueComplete;
            }
        }

        private void OnTranslationComplete(TranslationResult result) => Record(QueryKind.Translate, result.Stats);

        private void OnFindRelatedWordsComplete(RelatedWordResult result) => Record(QueryKind.Words, result.Stats);

        private void OnImageSummaryComplete(ImageSummaryResult result) => Record(QueryKind.Image, result.Stats);

        private void OnGenerateDialogueComplete(DialogueResult result) => Record(QueryKind.Dialogue, result.Stats);

        private void OnBlurCompleted(FaceDetection.BlurStats stats)
        {
            m_latestBlur = stats;
            Redraw();
        }

        private void Record(QueryKind kind, MetaModelRestApi.ModelUsage usage)
        {
            var index = (int)kind;
            m_latest[index] = usage;
            m_failed[index] = usage == null;
            Redraw();
        }

        private void Redraw()
        {
            if (m_statsText == null)
            {
                return;
            }

            var text = new System.Text.StringBuilder();
            _ = text.Append("AI QUERY STATS\n\n");
            _ = text.Append(MONOSPACE_TAG);

            for (var i = 0; i < QUERY_KIND_COUNT; ++i)
            {
                _ = text.Append(FormatRow(s_rowLabels[i], m_latest[i], m_failed[i]));
                _ = text.Append('\n');
            }

            // Local preprocessing, not a model query, so it sits under the table rather than in
            // it. It runs both on its own button and as a step inside the image query.
            _ = text.Append(FormatBlurRow(m_latestBlur));
            _ = text.Append('\n');

            // Every row comes from the same client, so report the model once at the bottom
            // rather than repeating it on each line.
            var model = LatestModel();
            if (model != null)
            {
                _ = text.Append('\n').Append(model);
            }

            m_statsText.text = text.ToString();
        }

        /// <summary>
        /// Build one fixed-width row. Rows that have not run show a dash so the table keeps its
        /// shape from the first frame instead of growing as queries complete.
        /// </summary>
        private static string FormatRow(string label, MetaModelRestApi.ModelUsage usage, bool failed)
        {
            // ASCII only - the station's font atlas is static and has no typographic dashes.
            var paddedLabel = label.PadRight(10);
            if (usage == null)
            {
                return paddedLabel + (failed ? "failed" : "-");
            }

            var seconds = $"{usage.RoundTripMilliseconds / 1000.0:F2}s".PadLeft(7);
            var tokens = $"{usage.TotalTokens} tok".PadLeft(10);
            var split = $"({usage.PromptTokens}+{usage.CompletionTokens})";
            return $"{paddedLabel}{seconds}{tokens}  {split}";
        }

        /// <summary>
        /// Build the face blur row. Reports the recurring blur cost, and calls out the one-time
        /// model load separately so the first run does not read as the normal cost.
        /// </summary>
        private static string FormatBlurRow(FaceDetection.BlurStats? blur)
        {
            var paddedLabel = "Face blur".PadRight(10);
            if (blur == null)
            {
                return paddedLabel + "-";
            }

            var stats = blur.Value;
            var seconds = $"{stats.BlurMilliseconds / 1000.0:F2}s".PadLeft(7);
            var size = $"{stats.Width}x{stats.Height}".PadLeft(10);
            var row = $"{paddedLabel}{seconds}{size}";
            if (stats.IncludedModelLoad)
            {
                row += $"  +{stats.ModelLoadMilliseconds / 1000.0:F1}s load";
            }

            return row;
        }

        /// <summary>
        /// Model reported by the most recent query that returned one.
        /// </summary>
        private string LatestModel()
        {
            for (var i = QUERY_KIND_COUNT - 1; i >= 0; --i)
            {
                if (!string.IsNullOrEmpty(m_latest[i]?.Model))
                {
                    return m_latest[i].Model;
                }
            }

            return null;
        }
    }
}
