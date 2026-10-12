// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using Meta.XR.Samples;

namespace SpatialLingo.Debugging
{
    /// <summary>
    /// Master switch for the testing-only debug features: the room scan overlay, the lesson panel
    /// under the microphone icon and the activity panel.
    ///
    /// This is deliberately separate from <see cref="Lessons.Lesson3DInteractor.ShowDebugFeatures"/>,
    /// which only toggles the bounding-box gizmo on lessons, and from
    /// <see cref="Lessons.Lesson3DInteractor.SetDebugStatus"/>, which is gameplay logic despite
    /// the name.
    ///
    /// The build default comes from SpatialLingoSettings; the menu on the debug canvas flips it
    /// at runtime.
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public static class SpatialLingoDebug
    {
        private static bool s_enabled;

        /// <summary>
        /// Raised whenever <see cref="Enabled"/> actually changes value. Listeners build their
        /// visuals on true and tear them down on false, so subscribing is enough - there is no
        /// need to poll.
        /// </summary>
        public static event Action<bool> Changed;

        public static bool Enabled
        {
            get => s_enabled;
            set
            {
                if (s_enabled == value)
                {
                    return;
                }

                s_enabled = value;
                Changed?.Invoke(s_enabled);
            }
        }

        public static void Toggle() => Enabled = !Enabled;

        /// <summary>
        /// Domain reload is disabled in player builds and can be disabled in the editor, so the
        /// static state has to be cleared explicitly rather than relying on it being fresh.
        /// Runs before the settings hook that applies the build default.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_enabled = false;
            Changed = null;
        }
    }
}
