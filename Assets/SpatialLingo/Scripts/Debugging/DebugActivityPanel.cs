// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Text;
using Meta.XR.Samples;
using TMPro;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// World-space readout of every action the app has taken and is waiting on, parked to the
    /// player's right.
    ///
    /// Placed once, the first time debug mode turns on, and then left alone - it does not follow
    /// the head. A log that swims around is hard to read, and a fixed board can be glanced at and
    /// then ignored.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class DebugActivityPanel : MonoBehaviour
    {
        private const int MAX_ROWS = 22;

        // TMP has no monospace font here, so force uniform advance to keep the columns aligned.
        private const string MONOSPACE_TAG = "<mspace=0.5em>";

        // Running entries tick, so refresh on a timer rather than only on change.
        private const float REFRESH_INTERVAL_SECONDS = 0.25f;

        [Tooltip("Child holding the board. Must NOT be this object: deactivating ourselves would " +
                 "stop OnEnable/OnDisable ever hearing the toggle again.")]
        [SerializeField] private GameObject m_content;

        [SerializeField] private TextMeshPro m_text;

        [Tooltip("Headset transform the board is placed relative to. Falls back to the main camera.")]
        [SerializeField] private Transform m_headset;

        [Tooltip("Metres to the player's right.")]
        [SerializeField] private float m_rightOffset = 1.0f;

        [Tooltip("Metres in front of the player.")]
        [SerializeField] private float m_forwardOffset = 0.7f;

        [Tooltip("Height of the board's centre above the floor, in metres. Absolute rather than " +
                 "relative to the head, so the board sits at the same height whether the player " +
                 "is standing or seated. Assumes a floor-level tracking origin.")]
        [SerializeField] private float m_floorHeight = 1.5f;

        private bool m_isPlaced;
        private float m_nextRefresh;

        public void SetHeadset(Transform headset)
        {
            m_headset = headset;
        }

        private void Awake()
        {
            if (m_content == null && m_text != null)
            {
                m_content = m_text.gameObject;
            }

            if (m_content == gameObject)
            {
                Debug.LogWarning($"{nameof(DebugActivityPanel)} - content must be a child, not this object.");
                m_content = m_text != null ? m_text.gameObject : null;
            }
        }

        private void OnEnable()
        {
            SpatialLingoDebug.Changed += OnDebugModeChanged;
            DebugActionLog.Changed += Redraw;
            OnDebugModeChanged(SpatialLingoDebug.Enabled);
        }

        private void OnDisable()
        {
            SpatialLingoDebug.Changed -= OnDebugModeChanged;
            DebugActionLog.Changed -= Redraw;
        }

        private void OnDebugModeChanged(bool enabled)
        {
            if (m_content != null)
            {
                m_content.SetActive(enabled);
            }

            if (!enabled)
            {
                return;
            }

            // Placed the first time it is shown, so it lands wherever the player is standing when
            // they open it, and stays there afterwards.
            if (!m_isPlaced)
            {
                PlaceRelativeToPlayer();
            }

            Redraw();
        }

        /// <summary>
        /// Park the board to the player's right at eye level, turned to face where they are
        /// standing. Yaw only - a board tilted to match head pitch is unreadable.
        /// </summary>
        public void PlaceRelativeToPlayer()
        {
            var head = m_headset != null ? m_headset : Camera.main != null ? Camera.main.transform : null;
            if (head == null)
            {
                Debug.LogWarning($"{nameof(DebugActivityPanel)} - no headset transform, leaving the board where it is.");
                return;
            }

            var forward = head.forward;
            forward.y = 0.0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            // Cross(up, forward) is already +right in Unity's left-handed basis.
            var right = Vector3.Cross(Vector3.up, forward);

            var anchor = head.position + right * m_rightOffset + forward * m_forwardOffset;
            anchor.y = m_floorHeight;
            transform.position = anchor;

            // Face the player rather than copying their facing, so the board is readable from
            // where they are standing.
            var toPlayer = head.position - anchor;
            toPlayer.y = 0.0f;
            if (toPlayer.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.LookRotation(-toPlayer.normalized, Vector3.up);
            }

            m_isPlaced = true;
        }

        private void Update()
        {
            // Entries that are still running need their elapsed time to keep counting up; that is
            // the whole point of the board, so it cannot be redraw-on-change only.
            if (!SpatialLingoDebug.Enabled || Time.unscaledTime < m_nextRefresh)
            {
                return;
            }

            m_nextRefresh = Time.unscaledTime + REFRESH_INTERVAL_SECONDS;
            Redraw();
        }

        private void Redraw()
        {
            if (m_text == null || !SpatialLingoDebug.Enabled)
            {
                return;
            }

            var entries = DebugActionLog.Entries;
            var running = 0;
            foreach (var e in entries)
            {
                if (e.IsRunning)
                {
                    running += 1;
                }
            }

            var text = new StringBuilder();
            _ = text.Append($"ACTIVITY   {entries.Count} actions, {running} in flight\n");
            _ = text.Append(MONOSPACE_TAG);

            // Newest first: what the app is waiting on right now belongs at the top.
            var shown = 0;
            for (var i = entries.Count - 1; i >= 0 && shown < MAX_ROWS; --i, ++shown)
            {
                _ = text.Append(FormatRow(entries[i])).Append('\n');
            }

            if (entries.Count == 0)
            {
                _ = text.Append("<color=#8892a0>nothing yet</color>\n");
            }

            m_text.text = text.ToString();
        }

        private static string FormatRow(DebugActionEntry entry)
        {
            var kind = entry.Kind == DebugActionKind.Discovery ? "disc" : "less";
            var label = Truncate(entry.ObjectLabel, 14).PadRight(14);
            var detail = Truncate(entry.Detail, 10).PadRight(10);
            var seconds = $"{entry.Seconds,6:F1}s";

            var status = entry.Status switch
            {
                DebugActionStatus.Success => "OK",
                DebugActionStatus.Failed => "FAIL",
                DebugActionStatus.Interrupted => "INTR",
                _ => "...",
            };

            var colour = entry.Status switch
            {
                DebugActionStatus.Success => "#5ad96a",
                DebugActionStatus.Failed => "#eb4d4d",
                DebugActionStatus.Interrupted => "#f29e33",
                _ => "#33f0f0",
            };

            var row = $"{label} {kind} {detail} {seconds}  {status}";
            if (!string.IsNullOrEmpty(entry.Note))
            {
                row += "  " + Truncate(entry.Note, 28);
            }

            return $"<color={colour}>{row}</color>";
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= max ? value : value[..(max - 1)] + "~";
        }
    }
}
