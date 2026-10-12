// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.Samples;
using TMPro;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Fits a translucent quad behind a world-space TextMeshPro so the readout stays legible
    /// over passthrough, where the background is whatever happens to be in the room.
    ///
    /// The quad is a child of the text transform, so it inherits the text's scale and only needs
    /// sizing in the text's own local units.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    [ExecuteAlways]
    public class DebugTextBackdrop : MonoBehaviour
    {
        [SerializeField] private TextMeshPro m_text;

        [Tooltip("Quad parented under the text transform, drawn behind the glyphs.")]
        [SerializeField] private Transform m_backdrop;

        [Tooltip("Margin around the text bounds, in the text's local units.")]
        [SerializeField] private Vector2 m_padding = new(3.0f, 2.0f);

        [Tooltip("Push behind the glyphs, in the text's local units, to avoid z-fighting.")]
        [SerializeField] private float m_depthOffset = 0.5f;

        private string m_lastText;
        private Vector2 m_lastRectSize;

        private void Reset()
        {
            m_text = GetComponent<TextMeshPro>();
        }

        private void OnEnable()
        {
            // Force a rebuild: the cached text may be stale from before the object was hidden.
            m_lastText = null;
            Refresh();
        }

        private void LateUpdate()
        {
            if (m_text == null || m_backdrop == null)
            {
                return;
            }

            // TMP raises a change event, but subscribing per instance is more machinery than a
            // string compare is worth for a debug overlay that updates a few times a second.
            var rectSize = m_text.rectTransform.rect.size;
            if (m_text.text == m_lastText && rectSize == m_lastRectSize)
            {
                return;
            }

            m_lastText = m_text.text;
            m_lastRectSize = rectSize;
            Refresh();
        }

        public void Refresh()
        {
            if (m_text == null || m_backdrop == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(m_text.text))
            {
                m_backdrop.gameObject.SetActive(false);
                return;
            }

            m_backdrop.gameObject.SetActive(true);

            // textBounds is only valid after the mesh has been regenerated for the current text.
            m_text.ForceMeshUpdate();
            var bounds = m_text.textBounds;

            // A degenerate bounds means TMP has not laid anything out yet this frame; leave the
            // previous size rather than collapsing the quad to nothing.
            if (bounds.size.x <= 0.0f || bounds.size.y <= 0.0f)
            {
                return;
            }

            m_backdrop.localScale = new Vector3(bounds.size.x + m_padding.x, bounds.size.y + m_padding.y, 1.0f);
            m_backdrop.localPosition = new Vector3(bounds.center.x, bounds.center.y, m_depthOffset);
            m_backdrop.localRotation = Quaternion.identity;
        }
    }
}
