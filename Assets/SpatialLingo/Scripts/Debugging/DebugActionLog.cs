// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Meta.XR.Samples;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Which part of the app issued the action.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public enum DebugActionKind
    {
        // Turning a scanned object into an exercise.
        Discovery,
        // Judging what the player said during a lesson.
        Lesson,
    }

    [MetaCodeSample("SpatialLingo")]
    public enum DebugActionStatus
    {
        // Still waiting on it - this is what the panel is for.
        Running,
        Success,
        Failed,
        // Completed, but the result was thrown away: the object stopped being tracked, or a
        // newer request superseded this one.
        Interrupted,
    }

    /// <summary>
    /// One action the app took, and how it turned out.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class DebugActionEntry
    {
        public int Sequence { get; }

        /// <summary>Object the action was about, as "classification#id".</summary>
        public string ObjectLabel { get; }

        public DebugActionKind Kind { get; }

        /// <summary>Which step: "wordcloud", "phrases", "speech".</summary>
        public string Detail { get; }

        public DebugActionStatus Status { get; private set; } = DebugActionStatus.Running;

        /// <summary>Short explanation shown for anything that did not succeed.</summary>
        public string Note { get; private set; } = string.Empty;

        private readonly Stopwatch m_timer = Stopwatch.StartNew();

        /// <summary>Elapsed so far while running, frozen once finished.</summary>
        public double Seconds => m_timer.Elapsed.TotalSeconds;

        public bool IsRunning => Status == DebugActionStatus.Running;

        internal DebugActionEntry(int sequence, DebugActionKind kind, string objectLabel, string detail)
        {
            Sequence = sequence;
            Kind = kind;
            ObjectLabel = string.IsNullOrEmpty(objectLabel) ? "?" : objectLabel;
            Detail = detail ?? string.Empty;
        }

        internal void Finish(DebugActionStatus status, string note)
        {
            if (!IsRunning)
            {
                return;
            }

            m_timer.Stop();
            Status = status;
            Note = note ?? string.Empty;
        }
    }

    /// <summary>
    /// Running record of what the app is doing and waiting on, for the world-space debug panel.
    ///
    /// Recording is unconditional and cheap so that turning debug mode on mid-session shows the
    /// history rather than an empty list; only the panel itself is gated on debug mode.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public static class DebugActionLog
    {
        // Enough to cover a tier without growing without bound. Oldest finished entry is dropped.
        private const int MAX_ENTRIES = 80;

        private static readonly List<DebugActionEntry> s_entries = new();
        private static int s_nextSequence = 1;

        /// <summary>Raised when an entry is added or finishes. Not raised as time ticks.</summary>
        public static event Action Changed;

        public static IReadOnlyList<DebugActionEntry> Entries => s_entries;

        public static DebugActionEntry Begin(DebugActionKind kind, string objectLabel, string detail)
        {
            // Only the activity panel reads this, so with debug off it is a list nobody looks at.
            // Complete() tolerates the null, so callers need no extra check.
            if (!SpatialLingoDebug.Enabled)
            {
                return null;
            }

            var entry = new DebugActionEntry(s_nextSequence++, kind, objectLabel, detail);
            s_entries.Add(entry);

            // Trim from the front, but never drop something still in flight - an unfinished entry
            // is the most interesting thing on the panel.
            while (s_entries.Count > MAX_ENTRIES)
            {
                var removed = false;
                for (var i = 0; i < s_entries.Count; ++i)
                {
                    if (!s_entries[i].IsRunning)
                    {
                        s_entries.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }

                if (!removed)
                {
                    break;
                }
            }

            Changed?.Invoke();
            return entry;
        }

        public static void Complete(DebugActionEntry entry, DebugActionStatus status, string note = null)
        {
            if (entry == null)
            {
                return;
            }

            entry.Finish(status, note);
            Changed?.Invoke();
        }

        public static void Clear()
        {
            s_entries.Clear();
            Changed?.Invoke();
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_entries.Clear();
            s_nextSequence = 1;
            Changed = null;
        }
    }
}
