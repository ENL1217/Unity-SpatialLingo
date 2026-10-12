// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;
using SpatialLingo.Lessons;
using UnityEngine;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Where a scanned object is in the pipeline that turns it into an exercise.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public enum ScanCandidateState
    {
        // Tracked, but the AI acquisition has not picked it up yet.
        Pending,
        // Acquisition is running for this object right now.
        Acquiring,
        // Activity is complete and the object is eligible to be shown as a lesson.
        Ready,
        // Acquisition gave up after exhausting its retries.
        Failed,
    }

    /// <summary>
    /// Outcome of the eligibility pass in LessonsManager.NextLessonsInDistancePlaneXZ.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public enum ScanDecision
    {
        // Not considered yet - the selection pass has not run since this object became ready.
        None,
        // Picked as an exercise and sent a berry.
        Selected,
        // Considered and passed over. DecisionReason says why.
        Rejected,
    }

    /// <summary>
    /// Everything the scan overlay needs to explain one tracked object. Held per Lesson and
    /// mutated in place, so the card can refresh without rebuilding.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class ScanCandidateDebugInfo
    {
        public Lesson Lesson { get; }

        public ScanCandidateState State = ScanCandidateState.Pending;

        /// <summary>Sub-task currently running, plus its retry count, e.g. "wordcloud retry 2/5".</summary>
        public string StageLabel = string.Empty;

        /// <summary>Populated when State is Failed: which sub-task exhausted its retries.</summary>
        public string FailureReason = string.Empty;

        public ScanDecision Decision = ScanDecision.None;

        /// <summary>Populated when Decision is Rejected, e.g. "duplicate classification".</summary>
        public string DecisionReason = string.Empty;

        /// <summary>
        /// True while LessonsManager is acquiring this object. Several objects can be acquired at
        /// once while the room is being filled, so more than one candidate can have this set.
        /// </summary>
        public bool IsProcessing;

        /// <summary>
        /// True once this object has been sent out as a berry.
        ///
        /// ExerciseManager dispatches an untracked *copy* of the lesson, which the registry never
        /// sees, so without this the original looks like a lesson that was passed over - and the
        /// selection pass then labels it a duplicate of its own copy.
        /// </summary>
        public bool IsDispatched;

        /// <summary>
        /// 1-based place in the acquisition queue, or 0 when not waiting (already acquired, or
        /// currently being processed). Mirrors the order LessonsManager will actually pick in.
        /// </summary>
        public int QueuePosition;

        /// <summary>How many candidates are waiting in total, for context on QueuePosition.</summary>
        public int QueueLength;

        // Cumulative model cost for this object across every request its acquisition made,
        // including the ones that failed and were retried.
        public int ModelCallCount;
        public long TotalRoundTripMilliseconds;
        public int TotalTokens;

        public string Classification => Lesson?.Classification ?? "?";
        public float Confidence => Lesson?.Confidence ?? 0.0f;

        /// <summary>Furthest depth-ray hit from the derived centre, in metres.</summary>
        public float SampleSpread => Lesson?.SampleSpread ?? 0.0f;

        /// <summary>How many depth-ray hits the position was averaged from.</summary>
        public int SamplePointCount => Lesson?.SamplePointCount ?? 0;

        /// <summary>True once the position is committed and will no longer drift.</summary>
        public bool IsPositionLocked => Lesson?.IsPositionLocked ?? false;

        public Vector3 Position => Lesson?.Position ?? Vector3.zero;
        public Vector3 Extent => Lesson?.Extent ?? Vector3.one;
        public Texture2D Image => Lesson?.Activity?.ContextImage;

        public ScanCandidateDebugInfo(Lesson lesson) => Lesson = lesson;

        public void AddUsage(MetaModelRestApi.ModelUsage usage)
        {
            ModelCallCount += 1;
            if (usage == null)
            {
                return;
            }

            TotalRoundTripMilliseconds += usage.RoundTripMilliseconds;
            TotalTokens += usage.TotalTokens;
        }
    }

    /// <summary>
    /// Registry of the debug state for every object the room scan is tracking.
    ///
    /// LessonsManager writes here as objects are tracked, acquired and selected;
    /// RoomScanDebugVisualizer reads and draws. Recording is unconditional and cheap - a handful
    /// of fields per object, reusing the textures the Activity already holds - so that turning
    /// debug mode on mid-session shows the history rather than an empty room.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public static class ScanDebugRegistry
    {
        private static readonly Dictionary<Lesson, ScanCandidateDebugInfo> s_candidates = new();

        public static event Action<ScanCandidateDebugInfo> CandidateAdded;
        public static event Action<ScanCandidateDebugInfo> CandidateUpdated;
        public static event Action<ScanCandidateDebugInfo> CandidateRemoved;

        public static IEnumerable<ScanCandidateDebugInfo> Candidates => s_candidates.Values;

        public static ScanCandidateDebugInfo Track(Lesson lesson)
        {
            if (lesson == null)
            {
                return null;
            }

            if (s_candidates.TryGetValue(lesson, out var existing))
            {
                return existing;
            }

            var info = new ScanCandidateDebugInfo(lesson);
            s_candidates[lesson] = info;
            CandidateAdded?.Invoke(info);
            return info;
        }

        public static ScanCandidateDebugInfo Find(Lesson lesson)
        {
            if (lesson == null)
            {
                return null;
            }

            _ = s_candidates.TryGetValue(lesson, out var info);
            return info;
        }

        /// <summary>
        /// Apply a change and notify listeners. Returns silently for untracked lessons so callers
        /// in the gameplay path do not have to null check.
        /// </summary>
        public static void Update(Lesson lesson, Action<ScanCandidateDebugInfo> mutate)
        {
            // Track stays ungated so the set is complete the moment debug is switched on; only
            // the per-change work is skipped, since nothing is drawing the cards.
            if (!SpatialLingoDebug.Enabled)
            {
                return;
            }

            var info = Find(lesson);
            if (info == null)
            {
                return;
            }

            mutate?.Invoke(info);
            CandidateUpdated?.Invoke(info);
        }

        /// <summary>
        /// Called when the active lessons are torn down, so cards stop claiming to be in a lesson
        /// that no longer exists.
        /// </summary>
        public static void ClearAllDispatched()
        {
            foreach (var info in s_candidates.Values)
            {
                if (!info.IsDispatched)
                {
                    continue;
                }

                info.IsDispatched = false;
                CandidateUpdated?.Invoke(info);
            }
        }

        public static void Untrack(Lesson lesson)
        {
            if (lesson == null || !s_candidates.TryGetValue(lesson, out var info))
            {
                return;
            }

            _ = s_candidates.Remove(lesson);
            CandidateRemoved?.Invoke(info);
        }

        public static void Clear()
        {
            // Copy first: handlers commonly mutate scene state while this drains.
            var all = new List<ScanCandidateDebugInfo>(s_candidates.Values);
            s_candidates.Clear();
            foreach (var info in all)
            {
                CandidateRemoved?.Invoke(info);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_candidates.Clear();
            CandidateAdded = null;
            CandidateUpdated = null;
            CandidateRemoved = null;
        }
    }
}
