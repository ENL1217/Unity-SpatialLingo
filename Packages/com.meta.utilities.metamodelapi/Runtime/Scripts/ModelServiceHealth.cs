// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

namespace Meta.Utilities.MetaModelAPI
{
    /// <summary>How a model request ended, as far as the service's health is concerned.</summary>
    public enum ModelRequestOutcome
    {
        /// <summary>A reply came back and could be read.</summary>
        Success,

        /// <summary>No reply within the client timeout.</summary>
        Timeout,

        /// <summary>The request could not leave the device or the host was unreachable.</summary>
        NoNetwork,

        /// <summary>The service answered, but not with a usable reply.</summary>
        ServerError,
    }

    /// <summary>
    /// Whether the model service is answering.
    ///
    /// A single failed request means very little - the endpoint is slow and occasionally drops
    /// one, and every caller already retries. What matters to the player is the difference
    /// between that and the service having stopped answering altogether, which is worth showing
    /// on the UI because nothing they do will make progress until it comes back.
    ///
    /// So failures are only reported once several in a row have gone unanswered, and a single
    /// success clears it immediately: recovery should be as visible as the fault.
    /// </summary>
    public static class ModelServiceHealth
    {
        /// <summary>
        /// Consecutive unanswered requests before the player is told. Low enough to appear while
        /// they are still waiting, high enough that one dropped request is not an alarm.
        /// </summary>
        public const int FAILURES_BEFORE_DEGRADED = 3;

        private static int s_consecutiveFailures;
        private static ModelRequestOutcome s_lastFailure = ModelRequestOutcome.Success;

        /// <summary>
        /// Raised when the service stops answering, and again when it starts. Carries the kind of
        /// the most recent failure so the UI can tell "no network" from "server not answering".
        /// </summary>
        public static event Action<bool, ModelRequestOutcome> DegradedChanged;

        /// <summary>True while the service has failed enough consecutive requests to be reported.</summary>
        public static bool IsDegraded { get; private set; }

        /// <summary>The failure that tipped it over, meaningful only while degraded.</summary>
        public static ModelRequestOutcome LastFailure => s_lastFailure;

        /// <summary>Number of requests that have failed back to back.</summary>
        public static int ConsecutiveFailures => s_consecutiveFailures;

        /// <summary>
        /// Record how a request ended. Called for every model request, whichever endpoint it used,
        /// because the question being answered is whether the service as a whole is reachable.
        /// </summary>
        public static void Report(ModelRequestOutcome outcome)
        {
            if (outcome == ModelRequestOutcome.Success)
            {
                s_consecutiveFailures = 0;
                if (IsDegraded)
                {
                    IsDegraded = false;
                    Debug.Log($"{nameof(ModelServiceHealth)} - service is answering again");
                    DegradedChanged?.Invoke(false, ModelRequestOutcome.Success);
                }

                return;
            }

            s_consecutiveFailures += 1;
            s_lastFailure = outcome;

            if (IsDegraded || s_consecutiveFailures < FAILURES_BEFORE_DEGRADED)
            {
                return;
            }

            IsDegraded = true;
            Debug.LogWarning($"{nameof(ModelServiceHealth)} - {s_consecutiveFailures} requests in a row " +
                             $"went unanswered ({outcome}); reporting the service as down");
            DegradedChanged?.Invoke(true, outcome);
        }

        /// <summary>
        /// Forget the current state. For tests and for a fresh session; listeners are not notified,
        /// since there is nothing to report to a UI that has not been built yet.
        /// </summary>
        public static void Reset()
        {
            s_consecutiveFailures = 0;
            s_lastFailure = ModelRequestOutcome.Success;
            IsDegraded = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Reset();
            DegradedChanged = null;
        }
    }
}
