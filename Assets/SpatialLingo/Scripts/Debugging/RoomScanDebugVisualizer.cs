// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR.Samples;
using SpatialLingo.Lessons;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Spawns a <see cref="ScanDebugCard"/> for every object the room scan is tracking while
    /// debug mode is on, and tears them all down when it goes off.
    ///
    /// This reads <see cref="ScanDebugRegistry"/> rather than the tracker directly, so it covers
    /// objects that never reach a Lesson3DInteractor: ones still acquiring, ones that failed
    /// their retries, and ones the selection rules passed over.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class RoomScanDebugVisualizer : MonoBehaviour
    {
        [Tooltip("Card prefab instantiated once per tracked object.")]
        [SerializeField] private ScanDebugCard m_cardPrefab;

        [Tooltip("Headset transform the cards billboard toward. Falls back to the main camera.")]
        [SerializeField] private Transform m_headset;

        private readonly Dictionary<Lesson, ScanDebugCard> m_cards = new();
        private bool m_isShowing;

        public void SetHeadset(Transform headset)
        {
            m_headset = headset;
            foreach (var card in m_cards.Values)
            {
                card.Initialize(ScanDebugRegistry.Find(KeyOf(card)), headset);
            }
        }

        private void OnEnable()
        {
            SpatialLingoDebug.Changed += OnDebugModeChanged;
            ScanDebugRegistry.CandidateAdded += OnCandidateAdded;
            ScanDebugRegistry.CandidateUpdated += OnCandidateUpdated;
            ScanDebugRegistry.CandidateRemoved += OnCandidateRemoved;

            // Debug mode may already be on from the settings default.
            OnDebugModeChanged(SpatialLingoDebug.Enabled);
        }

        private void OnDisable()
        {
            SpatialLingoDebug.Changed -= OnDebugModeChanged;
            ScanDebugRegistry.CandidateAdded -= OnCandidateAdded;
            ScanDebugRegistry.CandidateUpdated -= OnCandidateUpdated;
            ScanDebugRegistry.CandidateRemoved -= OnCandidateRemoved;
            HideAll();
        }

        private void OnDebugModeChanged(bool enabled)
        {
            if (enabled == m_isShowing)
            {
                return;
            }

            m_isShowing = enabled;
            if (enabled)
            {
                ShowAll();
            }
            else
            {
                HideAll();
            }
        }

        /// <summary>
        /// Build cards for everything tracked so far. The registry records unconditionally, so
        /// turning debug mode on mid-session shows the objects already scanned rather than
        /// waiting for the next detection.
        /// </summary>
        private void ShowAll()
        {
            foreach (var info in ScanDebugRegistry.Candidates)
            {
                CreateCard(info);
            }
        }

        private void HideAll()
        {
            foreach (var card in m_cards.Values)
            {
                if (card != null)
                {
                    Destroy(card.gameObject);
                }
            }

            m_cards.Clear();
        }

        private void OnCandidateAdded(ScanCandidateDebugInfo info)
        {
            if (m_isShowing)
            {
                CreateCard(info);
            }
        }

        private void OnCandidateUpdated(ScanCandidateDebugInfo info)
        {
            if (m_isShowing && m_cards.TryGetValue(info.Lesson, out var card) && card != null)
            {
                card.Refresh();
            }
        }

        private void OnCandidateRemoved(ScanCandidateDebugInfo info)
        {
            if (!m_cards.TryGetValue(info.Lesson, out var card))
            {
                return;
            }

            if (card != null)
            {
                Destroy(card.gameObject);
            }

            _ = m_cards.Remove(info.Lesson);
        }

        private void CreateCard(ScanCandidateDebugInfo info)
        {
            if (m_cardPrefab == null)
            {
                Debug.LogWarning($"{nameof(RoomScanDebugVisualizer)} - no card prefab assigned, scan overlay will not draw.");
                return;
            }

            if (info?.Lesson == null || m_cards.ContainsKey(info.Lesson))
            {
                return;
            }

            var card = Instantiate(m_cardPrefab, transform);
            card.Initialize(info, m_headset != null ? m_headset : Camera.main != null ? Camera.main.transform : null);
            m_cards[info.Lesson] = card;
        }

        private Lesson KeyOf(ScanDebugCard card)
        {
            foreach (var pair in m_cards)
            {
                if (pair.Value == card)
                {
                    return pair.Key;
                }
            }

            return null;
        }
    }
}
