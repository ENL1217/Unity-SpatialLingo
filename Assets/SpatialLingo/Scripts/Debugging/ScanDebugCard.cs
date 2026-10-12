// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Text;
using Meta.XR.Samples;
using TMPro;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// One tracked object's debug readout: a translucent box on the object itself, the camera
    /// image the AI was given, and a world-space card saying where the object is in the pipeline
    /// and whether it was picked as an exercise.
    ///
    /// The image and card billboard toward the headset; the box stays axis-aligned on the object
    /// so it reads as the tracked volume rather than a label.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class ScanDebugCard : MonoBehaviour
    {
        // Alpha for the box. Low enough to read the real object through it.
        private const float BOX_ALPHA = 0.22f;

        // How far to the side of the object the image and card sit, in meters.
        private const float PANEL_SIDE_OFFSET = 0.30f;

        private static readonly Color s_colorPending = new(0.55f, 0.62f, 0.70f);
        private static readonly Color s_colorAcquiring = new(0.30f, 0.60f, 0.95f);
        private static readonly Color s_colorSelected = new(0.35f, 0.85f, 0.40f);
        private static readonly Color s_colorRejected = new(0.95f, 0.62f, 0.20f);
        private static readonly Color s_colorFailed = new(0.92f, 0.30f, 0.30f);

        // Only ever one object at a time, so it gets a colour nothing else uses.
        private static readonly Color s_colorProcessing = new(0.20f, 0.95f, 0.95f);

        // Sent out as a berry and currently in play.
        private static readonly Color s_colorDispatched = new(0.65f, 0.95f, 0.45f);

        // Extra alpha on the active object's box so it stands out across the room.
        private const float BOX_ALPHA_PROCESSING = 0.40f;

        // Threshold for flagging a position as suspect. A starting estimate, meant to be replaced
        // by whatever the logged distribution actually shows: a hit spread far wider than a
        // desk-sized object.
        private const float POSITION_SUSPECT_SPREAD_METERS = 0.40f;

        [Tooltip("Cube renderer drawn over the tracked object, scaled to its extent.")]
        [SerializeField] private MeshRenderer m_boxRenderer;

        [Tooltip("Root holding the image and the info text. Billboards toward the headset.")]
        [SerializeField] private Transform m_panelRoot;

        [Tooltip("Quad showing the camera image the AI request was built from.")]
        [SerializeField] private MeshRenderer m_imageRenderer;

        [Tooltip("Shown instead of the image quad before any image is available.")]
        [SerializeField] private GameObject m_imagePlaceholder;

        [SerializeField] private TextMeshPro m_infoText;

        [Tooltip("Shader texture property on the image material. URP Unlit uses _BaseMap.")]
        [SerializeField] private string m_imageTextureProperty = "_BaseMap";

        [Tooltip("Shader color property on the box material. URP Lit and Unlit use _BaseColor.")]
        [SerializeField] private string m_boxColorProperty = "_BaseColor";

        private Transform m_headset;
        private ScanCandidateDebugInfo m_info;
        private MaterialPropertyBlock m_boxBlock;
        private MaterialPropertyBlock m_imageBlock;
        private Texture2D m_appliedImage;

        public void Initialize(ScanCandidateDebugInfo info, Transform headset)
        {
            m_info = info;
            m_headset = headset;
            m_boxBlock = new MaterialPropertyBlock();
            m_imageBlock = new MaterialPropertyBlock();
            Refresh();
        }

        /// <summary>
        /// Pull the current values out of the debug record. Cheap enough to call on every
        /// registry update; the texture upload is skipped unless the image actually changed.
        /// </summary>
        public void Refresh()
        {
            if (m_info == null)
            {
                return;
            }

            var color = StateColor();

            transform.position = m_info.Position;

            if (m_boxRenderer != null)
            {
                // Extent is the full size of the tracked volume, and the box mesh is a unit cube.
                m_boxRenderer.transform.localScale = m_info.Extent;
                var alpha = m_info.IsProcessing ? BOX_ALPHA_PROCESSING : BOX_ALPHA;
                m_boxRenderer.GetPropertyBlock(m_boxBlock);
                m_boxBlock.SetColor(m_boxColorProperty, new Color(color.r, color.g, color.b, alpha));
                m_boxRenderer.SetPropertyBlock(m_boxBlock);
            }

            ApplyImage();

            if (m_infoText != null)
            {
                m_infoText.text = BuildInfoText();
                m_infoText.color = color;
            }
        }

        private void ApplyImage()
        {
            var image = m_info.Image;
            var hasImage = image != null;

            if (m_imagePlaceholder != null)
            {
                m_imagePlaceholder.SetActive(!hasImage);
            }

            if (m_imageRenderer == null)
            {
                return;
            }

            m_imageRenderer.gameObject.SetActive(hasImage);
            if (!hasImage || ReferenceEquals(image, m_appliedImage))
            {
                return;
            }

            m_appliedImage = image;
            m_imageRenderer.GetPropertyBlock(m_imageBlock);
            m_imageBlock.SetTexture(m_imageTextureProperty, image);
            m_imageRenderer.SetPropertyBlock(m_imageBlock);

            // Keep the source aspect ratio, fitting inside the authored quad height.
            var authored = m_imageRenderer.transform.localScale;
            var aspect = image.height > 0 ? image.width / (float)image.height : 1.0f;
            m_imageRenderer.transform.localScale = new Vector3(authored.y * aspect, authored.y, authored.z);
        }

        private Color StateColor()
        {
            // "Being worked on right now" is the most useful thing to spot across a room, so it
            // outranks everything else.
            if (m_info.IsProcessing)
            {
                return s_colorProcessing;
            }

            // Being in play outranks any selection verdict: the object is a live lesson.
            if (m_info.IsDispatched)
            {
                return s_colorDispatched;
            }

            // The decision is the more interesting fact once it exists, so it wins over the
            // pipeline state for coloring.
            if (m_info.State == ScanCandidateState.Failed)
            {
                return s_colorFailed;
            }

            return m_info.Decision switch
            {
                ScanDecision.Selected => s_colorSelected,
                ScanDecision.Rejected => s_colorRejected,
                _ => m_info.State == ScanCandidateState.Acquiring ? s_colorAcquiring : s_colorPending,
            };
        }

        private string StatusLabel()
        {
            if (m_info.IsDispatched)
            {
                return "IN LESSON";
            }

            if (m_info.IsProcessing)
            {
                return ">> PROCESSING";
            }

            if (m_info.State == ScanCandidateState.Failed)
            {
                return "FAILED";
            }

            return m_info.Decision switch
            {
                ScanDecision.Selected => "SELECTED",
                ScanDecision.Rejected => "REJECTED",
                _ => m_info.State == ScanCandidateState.Acquiring ? "ACQUIRING" : "PENDING",
            };
        }

        private string BuildInfoText()
        {
            var text = new StringBuilder();
            _ = text.Append('[').Append(m_info.Classification).Append("]  ").Append(StatusLabel()).Append('\n');
            _ = text.Append($"conf {m_info.Confidence:F2}\n");

            // Position quality. The centre is an unweighted mean of depth-ray hits with no check
            // that a ray landed on the object, so a wide hit spread is the signature of a
            // misplaced object.
            var spread = m_info.SampleSpread;
            var suspect = spread > POSITION_SUSPECT_SPREAD_METERS;
            _ = text.Append(suspect ? "! " : "  ");
            _ = text.Append($"spread {spread:F2}m  pts {m_info.SamplePointCount}\n");

            // Acquisition is single-file, so a waiting object is waiting on the queue, not on
            // anything about itself. Say how far back it is.
            if (m_info.QueuePosition > 0)
            {
                _ = text.Append($"queued #{m_info.QueuePosition} of {m_info.QueueLength}\n");
            }

            if (!string.IsNullOrEmpty(m_info.StageLabel))
            {
                _ = text.Append(m_info.StageLabel).Append('\n');
            }

            if (!string.IsNullOrEmpty(m_info.FailureReason))
            {
                _ = text.Append(m_info.FailureReason).Append('\n');
            }

            if (!string.IsNullOrEmpty(m_info.DecisionReason))
            {
                _ = text.Append("reason: ").Append(m_info.DecisionReason).Append('\n');
            }

            if (m_info.ModelCallCount > 0)
            {
                var calls = m_info.ModelCallCount == 1 ? "call" : "calls";
                _ = text.Append($"{m_info.ModelCallCount} {calls}  {m_info.TotalRoundTripMilliseconds / 1000.0:F1}s  {m_info.TotalTokens} tok");
            }
            else
            {
                _ = text.Append("no requests yet");
            }

            return text.ToString();
        }

        private void LateUpdate()
        {
            if (m_info == null)
            {
                return;
            }

            // Track the object: it keeps moving as the tracker refines its estimate.
            transform.position = m_info.Position;

            if (m_headset == null || m_panelRoot == null)
            {
                return;
            }

            // Park the panel to the user's right of the object so it does not sit on top of the
            // thing being inspected, then face it at the headset.
            var toHeadset = m_headset.position - transform.position;
            toHeadset.y = 0.0f;
            if (toHeadset.sqrMagnitude < 0.0001f)
            {
                return;
            }

            toHeadset.Normalize();
            var side = Vector3.Cross(Vector3.up, toHeadset);
            var halfWidth = Mathf.Max(m_info.Extent.x, m_info.Extent.z) * 0.5f;
            m_panelRoot.position = transform.position + side * (halfWidth + PANEL_SIDE_OFFSET);
            m_panelRoot.rotation = Quaternion.LookRotation(-toHeadset, Vector3.up);
        }
    }
}
