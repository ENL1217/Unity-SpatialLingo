// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.Utilities.MetaModelAPI;
using Meta.Utilities.CameraTaxonTracking;
using Meta.Utilities.ObjectClassifier;
using Meta.XR.Samples;
using SpatialLingo.AI;
using SpatialLingo.AppSystems;
using SpatialLingo.Debugging;
using SpatialLingo.Settings;
using UnityEngine;

namespace SpatialLingo.Lessons
{
    [MetaCodeSample("SpatialLingo")]
    public class LessonUpdateResult
    {
        public Lesson Lesson { get; }

        public LessonUpdateResult(Lesson lesson) => Lesson = lesson;
    }

    [MetaCodeSample("SpatialLingo")]
    public class LessonsManager : IDisposable
    {
        public enum RequestStatus
        {
            Unknown,
            FailWifi,
            FailServer,
        }

        [MetaCodeSample("SpatialLingo")]
        public class RequestStatusResult
        {
            public RequestStatus Status { get; }

            public RequestStatusResult(RequestStatus status) => Status = status;
        }

        private const int RELATED_WORD_PARTS_OF_SPEECH_COUNT = 3;
        private const int WAIT_TIME_BETWEEN_REQUESTS_MS = 100;
        private const int ENDPOINT_MAX_RETRY_COUNT = 5;

        // How many lessons to hold at their last known position after losing tracking. Bounded so
        // a long session cannot accumulate the whole room. Sized for a desk scan, which turns up
        // 8-10 objects before the first acquisition finishes; too low and acquired lessons get
        // evicted to make room for candidates still waiting.
        private const int MAX_PINNED_LESSONS = 28;

        // Slack added to an object's own radius when deciding whether two detections of the same
        // classification are the same physical thing. Covers tracking jitter between observations.
        private const float SAME_OBJECT_TOLERANCE_METERS = 0.10f;

        // Floor under that radius, so the sightings of a small object still match each other
        // when its depth estimate wanders by more than its own size. Kept tight: erring large is
        // not cheap, because the detector gives unrelated things one classification - a mousepad
        // called "laptop" took over the real laptop 0.3m away and left it without a lesson.
        private const float SAME_OBJECT_MIN_REACH_METERS = 0.25f;

        // How far apart vertically two detections may be and still be one object, unless the
        // object is tall enough for half its height to allow more. Things in a room do not move
        // up and down, and the depth hits that used to land on the floor below are filtered out
        // by the tracker, so a big vertical gap means a different object stacked above or below.
        private const float SAME_OBJECT_MAX_VERTICAL_METERS = 0.15f;

        // Spacing between berries used to fill a stage when the caller's preferred spacing
        // cannot. Objects on a desk sit closer than that; this still keeps two berries from
        // ending up on top of each other.
        private const float FALLBACK_LESSON_SEPARATION_METERS = 0.10f;

        // Breather before picking the next object after one exhausts its retries. Keeps a
        // fast-failing endpoint (bad key, no network) from turning into a tight request loop.
        private const int WAIT_TIME_AFTER_FAILED_ACQUISITION_MS = 5000;

        // Ceiling for the pause after failures in a row, which doubles each time. A bad key or a
        // dead endpoint then settles to one attempt a minute instead of hammering it.
        private const int MAX_WAIT_TIME_AFTER_FAILED_ACQUISITION_MS = 60000;

        // Times one object may come back with an unusable answer (the wrong number of verbs or
        // adjectives, say) before it is given up on. Each attempt costs two requests.
        private const int MAX_INVALID_RESULTS_PER_OBJECT = 3;

        // Lessons in hand after which acquiring several at once stops being worth it. Getting the
        // first handful quickly is what makes the app usable; beyond that the extra requests
        // compete with the player's own for no visible gain.
        private const int PARALLEL_UNTIL_LESSON_COUNT = 5;

        // How long ago a repeat of a classification already acquired or in flight must have been
        // detected before it may take an extra parallel slot. A repeat is usually a second
        // detection of the same object, so the slot is held for something else to turn up first.
        // The time runs from the repeat's own detection, not from when the original's acquisition
        // started, and nothing waits for that acquisition to finish: a repeat detected longer ago
        // than this takes the slot straight away, alongside the original still in flight.
        private const float REPEAT_PARALLEL_WAIT_SECONDS = 10.0f;

        // Settling time after a lesson ends before picking up acquisition again. Stepping out of
        // one lesson and into the next resumes and re-suspends within a frame or two.
        private const int RESUME_DEBOUNCE_MS = 1500;

        // How often, and for how long at most, an acquisition already underway waits between its
        // two requests while a lesson runs. Bounded so an unbalanced suspend cannot wedge it.
        private const int SUBTASK_HOLD_POLL_MS = 250;
        private const int MAX_SUBTASK_HOLD_MS = 60000;

        // Queue ordering bonuses, largest first: what the user has been looking at (0.20), how long
        // the object has waited (0.15), then how recently it was seen and how near it is (0.10
        // each). Their total (0.55) must stay below SAME_CLASS_QUEUE_PENALTY, so a classification
        // with no lesson yet always goes ahead of a repeat however convenient the repeat is.
        // Re-check that sum before raising any of them.
        private const float IN_VIEW_SCORE_BONUS = 0.20f;
        private const float RECENTLY_SEEN_SCORE_WEIGHT = 0.10f;
        private const float PROXIMITY_SCORE_WEIGHT = 0.10f;
        private const float WAITING_SCORE_WEIGHT = 0.15f;
        private const float SAME_CLASS_QUEUE_PENALTY = 0.60f;

        // The in-view bonus is scaled by how much of this window the object spent in view, so it
        // goes to what the player has mostly been looking at rather than to whatever happens to
        // be in front of them the moment a slot frees up.
        private const float ATTENTION_WINDOW_SECONDS = 30.0f;
        private const float ATTENTION_SAMPLE_INTERVAL_SECONDS = 0.5f;

        // Waiting this long since detection earns the full waiting bonus. That bonus is smaller
        // than the in-view one, so a long wait lifts an object above those that have waited less
        // but does not guarantee it a turn: one outscored by things the player keeps looking at
        // can wait indefinitely.
        private const float WAITING_FULL_BONUS_SECONDS = 120.0f;

        // Matches the tracker's own visibility cone, so "in view" here means the same thing it
        // does when the tracker decides whether a missed detection counts against an object.
        private const float IN_VIEW_MAX_ANGLE_DEGREES = 35.0f;

        // How long after its last sighting an object still counts as freshly seen. Beyond this it
        // scores nothing for recency - it is not stale, just no longer the thing in front of you.
        private const float RECENTLY_SEEN_WINDOW_SECONDS = 60.0f;

        // Band over which proximity is scored, matching the tracker's usable range for
        // recognition. Nearer than the minimum scores full marks; beyond the maximum, nothing.
        private const float PROXIMITY_NEAR_METERS = 0.5f;
        private const float PROXIMITY_FAR_METERS = 3.0f;

        private CameraTaxonTracker m_taxonTracker;

        // The player's head, used only to rank the queue by what they are looking at. Optional:
        // without it the relevance terms contribute nothing.
        private readonly Transform m_userHead;
        private AssistantAI m_assistant;

        private AssistantAI.SupportedLanguage m_userLanguage;
        private AssistantAI.SupportedLanguage m_targetLanguage;

        public delegate void LessonUpdatedEvent(LessonUpdateResult result);
        public event LessonUpdatedEvent LessonAdded; // When a lesson and underlaying activity are ready to be used
        public event LessonUpdatedEvent LessonUpdated; // When a lesson is updated (tracking only)
        public event LessonUpdatedEvent LessonRemoved; // When a lesson is removed, or underlaying activity is no longer ready

        public delegate void LessonTrackingEvent();
        public event LessonTrackingEvent LessonTrackingChanged; // Any time a lesson's tracking or activity status changes

        public delegate void ActivityAcquisitionEvent(RequestStatusResult result);
        public event ActivityAcquisitionEvent ActivityAcquisitionFailed;

        private Dictionary<CameraTrackedTaxon, Lesson> m_lessons = new();
        // Non-generic lessons need to map 1:1 to a lesson
        private Dictionary<Lesson, Activity> m_activities = new();

        // Lessons the tracker has dropped but which are kept at their last known position. They
        // stay in m_activities, so they are still counted, selectable and acquirable.
        private readonly List<Lesson> m_pinnedLessons = new();

        /// <summary>
        /// One object being acquired. Held per run rather than on the manager, because several
        /// acquisitions can be in flight and each needs its own request id.
        /// </summary>
        private sealed class AcquisitionRun
        {
            public Activity Activity;
            public string RequestId;

            /// <summary>
            /// The target language the run was started for. A language change mid-flight makes
            /// the answer useless, so completion discards a run whose language no longer matches.
            /// </summary>
            public AssistantAI.SupportedLanguage TargetLanguage;
        }

        private readonly List<AcquisitionRun> m_activeRuns = new();

        /// <summary>
        /// Serialises the on-device image preparation. Resize and face blur share one detector
        /// instance and the global RenderTexture.active, so two crops prepared at once would read
        /// each other's pixels. Held only for the preparation, never across the request.
        /// </summary>
        private readonly SemaphoreSlim m_prepGate = new(1, 1);

        /// <summary>
        /// The run that started first. Only the unused legacy sub-tasks read it; the live ones
        /// are handed their own run.
        /// </summary>
        private Activity PrimaryAcquiringActivity => m_activeRuns.Count > 0 ? m_activeRuns[0].Activity : null;

        /// <summary>Whether a given activity is being acquired right now.</summary>
        private bool IsRunning(Activity activity)
        {
            foreach (var run in m_activeRuns)
            {
                if (run.Activity == activity)
                {
                    return true;
                }
            }

            return false;
        }

        // Set while the player is answering a lesson: no new acquisition starts, and one already
        // underway waits before its second request.
        private bool m_isAcquisitionSuspended;

        // An object that just exhausted its retries. Passed over for one pick so the objects
        // behind it get a turn instead of the same failure repeating straight away.
        private Activity m_skipOnNextPick;

        // No new acquisition starts before this time (Time.realtimeSinceStartup) after a failure.
        // Held here rather than only in the delayed pump, because every new detection also asks
        // for a pick and would otherwise start the next attempt at once.
        private float m_acquisitionCooldownUntil;
        private int m_consecutiveFailedAcquisitions;

        // Set while a pump is scheduled for when a held-back repeat may take an extra slot.
        private bool m_isRepeatPumpScheduled;

        // Attention samples within the last ATTENTION_WINDOW_SECONDS: when each was taken, and for
        // each waiting activity, the samples in which it was in view.
        private readonly Queue<float> m_attentionSampleTimes = new();
        private readonly Dictionary<Activity, Queue<float>> m_inViewSampleTimes = new();

        private bool m_isTaxonTracking = false;
        private FaceDetection m_faceBlur;

        // Numbers objects for debug readouts. Static so ids stay unique across a manager rebuild.
        private static int s_nextDebugId;

        // Start is called once before the first execution of Update after the MonoBehaviour is created

        public LessonsManager(CameraTaxonTracker tracker, AssistantAI assistant, AssistantAI.SupportedLanguage userLanguage, AssistantAI.SupportedLanguage targetLanguage, FaceDetection faceBlur, Transform userHead = null)
        {
            m_userHead = userHead;
            m_taxonTracker = tracker;
            m_assistant = assistant;
            m_userLanguage = userLanguage;
            m_targetLanguage = targetLanguage;
            m_faceBlur = faceBlur;

            m_taxonTracker.TaxonAdded += OnTaxonAddedEvent;
            m_taxonTracker.TaxonUpdated += OnTaxonUpdatedEvent;
            m_taxonTracker.TaxonRemoved += OnTaxonRemovedEvent;

            StartTracking();
            SampleAttention();
        }

        public void SetLayersPerFrame(int layersPerFrame)
        {
            m_taxonTracker.SetLayersPerFrame(layersPerFrame);
        }

        public void SetTargetLanguage(AssistantAI.SupportedLanguage targetLanguage)
        {
            if (m_targetLanguage != targetLanguage)
            {
                m_targetLanguage = targetLanguage;
                // Runs already in flight were started for the old language; each records the
                // language it started with and is discarded on completion if that changed.
                ResetActivities();
                CheckGetActivityComponents();
            }
        }

        public void Dispose()
        {
            // The tracking loop reads m_taxonTracker on every pass, so it has to stop before the
            // tracker is let go below.
            StopTracking();

            if (m_taxonTracker != null)
            {
                m_taxonTracker.TaxonAdded -= OnTaxonAddedEvent;
                m_taxonTracker.TaxonUpdated -= OnTaxonUpdatedEvent;
                m_taxonTracker.TaxonRemoved -= OnTaxonRemovedEvent;
                m_taxonTracker = null;
            }
        }

        private void StartTracking()
        {
            if (m_isTaxonTracking)
            {
                return;
            }

            m_isTaxonTracking = true;
            CheckRunTaxonTracking();
        }

        private async void CheckRunTaxonTracking()
        {
            while (m_isTaxonTracking)
            {
                if (m_taxonTracker.IsIdle && InferenceEngineUtilities.IsLoaded)
                {
                    m_taxonTracker.StartPolling();
                }
                else
                {
                    await Awaitable.WaitForSecondsAsync(0.1f);
                }
            }
        }

        private void StopTracking()
        {
            m_isTaxonTracking = false;
        }

        // Tracking results
        private void OnTaxonAddedEvent(CameraTaxonTracker.TaxonUpdateResult result)
        {
            var taxon = result.Taxon;
            var classification = taxon.Taxa;

            // Looking back at an object whose tracking lapsed rejoins the pinned lesson that
            // already holds its work, rather than starting a second lesson for the same thing.
            var pinned = FindPinnedLessonForSameObject(classification, taxon.Center, taxon.Extent);
            if (pinned != null)
            {
                RejoinPinnedLesson(pinned, taxon);
                return;
            }

            // Activities are unique to locations
            // Want to be able to choose a best example image for activity, not just the first one available
            var context = taxon.ImageContext;
            // The activity keeps its own copy of the crop it is acquired from. The tracker prunes
            // and destroys its crops as newer sightings arrive, and keeps the rest so the
            // activation effect can pick the one best facing the player.
            var image = CameraTrackedTaxon.CopyTexture(context.Image);
            var imageSize = context.Size;
            var activity = new Activity
            {
                DebugId = ++s_nextDebugId,
                DetectedAt = Time.realtimeSinceStartup,
                Classification = classification,
                ContextImage = image,
                ContextImageExtent = imageSize
            };

            var lesson = new Lesson(activity, taxon);
            m_activities.Add(lesson, activity);
            m_lessons.Add(taxon, lesson);
            _ = ScanDebugRegistry.Track(lesson);
            RefreshDebugQueuePositions();

            // Lesson may already be ready
            if (lesson.IsReady)
            {
                LessonAdded?.Invoke(new LessonUpdateResult(lesson));
                LessonTrackingChanged?.Invoke();
            }

            CheckGetActivityComponents();
        }

        private void OnTaxonUpdatedEvent(CameraTaxonTracker.TaxonUpdateResult result)
        {
            var taxon = result.Taxon;
            if (m_lessons.ContainsKey(taxon))
            {
                var lesson = m_lessons[taxon];
                LessonUpdated?.Invoke(new LessonUpdateResult(lesson));
                LessonTrackingChanged?.Invoke();
            }
            else
            {
                Debug.LogWarning("OnTaxonUpdatedEvent missing taxon key");
            }
        }

        private void OnTaxonRemovedEvent(CameraTaxonTracker.TaxonUpdateResult result)
        {
            var taxon = result.Taxon;
            // Remove lesson
            if (m_lessons.ContainsKey(taxon))
            {
                var lesson = m_lessons[taxon];

                // Losing sight of an object is not the same as the object going away. A lesson
                // that is acquired, mid-acquisition, or waiting with its crop already captured
                // represents real work and a real place in the room, so it is kept at its last
                // known position instead of being discarded.
                if (TryMoveOntoSurvivingTrack(taxon, lesson) || TryPinLesson(taxon, lesson))
                {
                    return;
                }

                // Remove activity
                if (m_activities.ContainsKey(lesson))
                {
                    var activity = m_activities[lesson];
                    _ = m_activities.Remove(lesson);

                    if (IsRunning(activity))
                    {
                        // Its run still uses it; the crop is freed when that run ends.
                        Debug.LogWarning($"LessonsManager - OnTaxonRemovedEvent - removing same activity that is in process: {activity}");
                    }
                    else
                    {
                        activity.Clear();
                    }
                }
                else
                {
                    Debug.LogWarning("OnTaxonRemovedEvent missing lesson key");
                }

                _ = m_lessons.Remove(taxon);
                ScanDebugRegistry.Untrack(lesson);
                RefreshDebugQueuePositions();
                LessonRemoved?.Invoke(new LessonUpdateResult(lesson));
                LessonTrackingChanged?.Invoke();
            }
            else
            {
                Debug.LogWarning("OnTaxonRemovedEvent missing taxon key");
            }
        }

        /// <summary>
        /// Every lesson, tracked or pinned. Materialised rather than lazy: callers raise events
        /// while iterating, and a handler that adds or removes a lesson would invalidate a live
        /// enumerator.
        /// </summary>
        private List<Lesson> AllLessons()
        {
            var all = new List<Lesson>(m_lessons.Count + m_pinnedLessons.Count);
            all.AddRange(m_lessons.Values);
            all.AddRange(m_pinnedLessons);
            return all;
        }

        /// <summary>
        /// Keep a lesson alive at its last known world position after the tracker drops it.
        ///
        /// The CameraTrackedTaxon outlives its removal from the tracker, and Lesson reads
        /// Position, Extent and Confidence straight off it, so the lesson simply freezes where the
        /// object was last seen. It stays selectable and, once dispatched, stays valid in place
        /// while the user looks around and answers.
        /// </summary>
        /// <returns>True when the lesson was pinned and must not be removed.</returns>
        private bool TryPinLesson(CameraTrackedTaxon taxon, Lesson lesson)
        {
            var isAcquired = lesson.IsReady;
            var isInFlight = m_activities.TryGetValue(lesson, out var activity) && IsRunning(activity);

            // Only work already paid for is kept. A candidate still waiting is let go: the tracker
            // mostly drops an object because it should have been seen at its estimated position
            // and was not, which usually means the estimate is wrong. Pinning waiting candidates
            // kept exactly those misplaced objects, and they went on to become lessons in the
            // wrong place.
            if (!isAcquired && !isInFlight)
            {
                return false;
            }

            // When two tracks of one object collide the tracker keeps one and drops the other.
            // The object is still tracked under the survivor, so pinning this one would only
            // leave a duplicate beside it. The same goes for a lesson of another classification
            // acquired under the same word. A separate track of the same classification nearby
            // is not enough: it may be a second object of the kind, so this one is pinned.
            foreach (var pair in m_lessons)
            {
                if (pair.Key != taxon && IsDuplicateOf(taxon, lesson, pair.Key, pair.Value))
                {
                    if (SpatialLingoDebug.Enabled)
                    {
                        Debug.Log($"LessonsManager - tracking lost for {DescribeForLog(lesson)}, dropped rather than pinned: " +
                                  $"still tracked as {DescribeForLog(pair.Value)} (acquired={isAcquired} inFlight={isInFlight})");
                    }

                    return false;
                }
            }

            if (m_pinnedLessons.Count >= MAX_PINNED_LESSONS)
            {
                // Finished or in-flight work always gets a place, displacing the oldest pin if it
                // has to. Only a waiting candidate is made to wait for room, since trading
                // acquired work for it would give up two model calls to save one.
                if (!TryEvictPinnedLesson(protectWork: !isAcquired && !isInFlight))
                {
                    return false;
                }
            }

            // Drop only the taxon key. The activity stays in m_activities so the lesson is still
            // counted, still selectable, and still visible on the debug overlay. Its crops stay
            // too, for the activation effect, and are released when the pin is evicted.
            taxon.KeepImagesAfterRemoval = true;
            _ = m_lessons.Remove(taxon);
            m_pinnedLessons.Add(lesson);
            Debug.Log($"LessonsManager - tracking lost for {lesson.Activity.DebugLabel}, pinned at {lesson.Position} " +
                      $"(acquired={isAcquired} inFlight={isInFlight} pinned={m_pinnedLessons.Count})");

            // Deliberately no LessonRemoved: the interactor and any berry stay put.
            RefreshDebugQueuePositions();
            LessonTrackingChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Keep a lesson's work when the tracker merges its track into another one. The tracker
        /// keeps the older of two colliding tracks, which is not always the one that was acquired,
        /// so the acquired lesson used to be thrown away while the survivor waited to be asked
        /// about all over again.
        ///
        /// The lesson moves onto the survivor if it has more work behind it than the survivor's
        /// own lesson, which is let go instead. Only a track holding this one's samples is taken
        /// over - one the tracker really merged it into - never a separate track that just sits
        /// nearby, which may be a different object of the same kind.
        /// </summary>
        /// <returns>True when the lesson moved and must not be removed.</returns>
        private bool TryMoveOntoSurvivingTrack(CameraTrackedTaxon taxon, Lesson lesson)
        {
            var work = WorkDone(lesson);
            if (work == 0)
            {
                return false;
            }

            CameraTrackedTaxon survivorTaxon = null;
            Lesson survivor = null;
            foreach (var pair in m_lessons)
            {
                if (pair.Key == taxon || !IsDuplicateOf(taxon, lesson, pair.Key, pair.Value))
                {
                    continue;
                }

                // Another lesson for this object already has as much done; it stays the one, and
                // the pin check drops this copy.
                if (WorkDone(pair.Value) >= work)
                {
                    return false;
                }

                if (survivor == null && pair.Value.Classification == lesson.Classification)
                {
                    survivorTaxon = pair.Key;
                    survivor = pair.Value;
                }
            }

            if (survivor == null)
            {
                return false;
            }

            _ = m_lessons.Remove(taxon);
            if (m_activities.TryGetValue(survivor, out var survivorActivity))
            {
                _ = m_activities.Remove(survivor);
                // A run still using it frees it when the run ends.
                if (!IsRunning(survivorActivity))
                {
                    survivorActivity.Clear();
                }
            }

            ScanDebugRegistry.Untrack(survivor);
            LessonRemoved?.Invoke(new LessonUpdateResult(survivor));

            lesson.MoveToTrack(survivorTaxon);
            m_lessons[survivorTaxon] = lesson;
            Debug.Log($"LessonsManager - {lesson.Activity.DebugLabel} merged into the track of " +
                      $"{survivor.Activity.DebugLabel} at {lesson.Position}, which had less done and is let go");
            RefreshDebugQueuePositions();
            LessonUpdated?.Invoke(new LessonUpdateResult(lesson));
            LessonTrackingChanged?.Invoke();
            return true;
        }

        /// <summary>How much is invested in a lesson: 2 acquired, 1 being acquired, 0 waiting.</summary>
        private int WorkDone(Lesson lesson)
        {
            return lesson.IsReady ? 2 : IsRunning(lesson.Activity) ? 1 : 0;
        }

        /// <summary>
        /// Whether a lesson whose track was dropped is a second copy of a live one. For one
        /// classification, only when the live track holds the dropped one's samples - the tracker
        /// merged them - since a separate track nearby may be a second object of the same kind.
        /// Tracks of different classifications never merge, so for those the same word within
        /// reach is the sign.
        /// </summary>
        private static bool IsDuplicateOf(CameraTrackedTaxon taxon, Lesson lesson, CameraTrackedTaxon liveTaxon, Lesson live)
        {
            return lesson.Classification == live.Classification
                ? liveTaxon.SharesSamplesWith(taxon)
                : IsLikelySameObject(lesson, live);
        }

        /// <summary>
        /// Make room in the pinned set, giving up the least valuable entry: waiting candidates
        /// before acquired lessons, since a candidate costs one model call to replace and an
        /// acquired lesson costs two plus the wait. Oldest first within each group, on the
        /// assumption the room has moved on. The lesson being acquired is never evicted.
        /// </summary>
        /// <param name="protectWork">When true, refuse to evict an acquired lesson.</param>
        private bool TryEvictPinnedLesson(bool protectWork)
        {
            var index = -1;
            for (var i = 0; i < m_pinnedLessons.Count; ++i)
            {
                var candidate = m_pinnedLessons[i];
                if (!candidate.IsReady && !IsRunning(candidate.Activity))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                if (protectWork)
                {
                    return false;
                }

                index = m_pinnedLessons.FindIndex(pinned => !IsRunning(pinned.Activity));
                if (index < 0)
                {
                    return false;
                }
            }

            var evicted = m_pinnedLessons[index];
            var wasAcquired = evicted.IsReady;
            m_pinnedLessons.RemoveAt(index);
            _ = m_activities.Remove(evicted);
            evicted.Activity.Clear();
            evicted.ReleaseTrackedImages(m_taxonTracker?.HeldImages());
            ScanDebugRegistry.Untrack(evicted);
            Debug.Log($"LessonsManager - pinned lessons full, evicted {evicted.Activity.DebugLabel} ({(wasAcquired ? "acquired" : "still queued")})");
            LessonRemoved?.Invoke(new LessonUpdateResult(evicted));

            return true;
        }

        /// <summary>
        /// Put a pinned lesson back on a live track: the object has been seen again.
        ///
        /// The pin's samples and crops are folded into the new track, so its position combines
        /// what was known with the fresh sighting rather than staying frozen where tracking was
        /// lost - a pin placed in the wrong spot gets corrected - and the lesson follows the
        /// track from now on. If tracking is lost again it is pinned again.
        /// </summary>
        private void RejoinPinnedLesson(Lesson lesson, CameraTrackedTaxon taxon)
        {
            var pinnedAt = lesson.Position;
            lesson.RejoinTracking(taxon);
            _ = m_pinnedLessons.Remove(lesson);
            m_lessons[taxon] = lesson;
            Debug.Log($"LessonsManager - {lesson.Activity.DebugLabel} seen again, tracking again at {lesson.Position} (was pinned at {pinnedAt})");
            RefreshDebugQueuePositions();
            LessonUpdated?.Invoke(new LessonUpdateResult(lesson));
            LessonTrackingChanged?.Invoke();
        }

        private Lesson FindPinnedLessonForSameObject(string classification, Vector3 position, Vector3 extent)
        {
            foreach (var pinned in m_pinnedLessons)
            {
                if (IsLikelySameObject(classification, position, extent, pinned))
                {
                    return pinned;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether a detection is plausibly the same physical object as a known lesson rather
        /// than a second one. Compares centre distance against the object's own size instead of
        /// a fixed threshold, because "close" means something very different for a mouse and for
        /// a TV, with a floor for small objects whose depth estimate wanders, and a tighter limit
        /// vertically, where a large gap means something stacked above or below.
        /// </summary>
        private static bool IsLikelySameObject(string classification, Vector3 position, Vector3 extent, Lesson known)
        {
            if (string.IsNullOrEmpty(classification) || known.Classification != classification)
            {
                return false;
            }

            return IsWithinSameObjectReach(position, extent, known);
        }

        /// <summary>
        /// Same as the classification overload, but also matches a lesson of another
        /// classification once both are acquired under the same word: the detector can call one
        /// monitor both "laptop" and "tv monitor", and the model names both "monitor".
        /// </summary>
        private static bool IsLikelySameObject(Lesson lesson, Lesson known)
        {
            return IsSameKind(lesson, known) && IsWithinSameObjectReach(lesson.Position, lesson.Extent, known);
        }

        private static bool IsWithinSameObjectReach(Vector3 position, Vector3 extent, Lesson known)
        {
            var reach = Mathf.Max(
                Mathf.Max(extent.magnitude, known.Extent.magnitude) * 0.5f + SAME_OBJECT_TOLERANCE_METERS,
                SAME_OBJECT_MIN_REACH_METERS);
            // Extent is roughly a quarter of the object's size - the tracker samples the inner half
            // of the detection box and averages distances from the centre - so twice it is about
            // half the object's height.
            var verticalReach = Mathf.Max(
                Mathf.Max(extent.y, known.Extent.y) * 2.0f,
                SAME_OBJECT_MAX_VERTICAL_METERS);
            return Vector3.Distance(position, known.Position) <= reach
                   && Mathf.Abs(position.y - known.Position.y) <= verticalReach;
        }

        /// <summary>
        /// Whether two lessons teach the same thing: one classification, or, once both are
        /// acquired, one word. Used wherever a second copy of a lesson should not count as a
        /// different one.
        /// </summary>
        private static bool IsSameKind(Lesson a, Lesson b)
        {
            if (a.Classification == b.Classification)
            {
                return true;
            }

            var word = a.Activity.ComparableWord;
            return word != null && word == b.Activity.ComparableWord;
        }

        /// <summary>
        /// Whether the user already completed this lesson's kind. Completion is recorded by
        /// classification, so another lesson with the same word whose classification was
        /// completed counts too.
        /// </summary>
        private static bool HasKindBeenCompleted(Lesson lesson, List<Lesson> allLessons)
        {
            if (AppSessionData.HasLessonClassificationBeenCompleted(lesson.Classification))
            {
                return true;
            }

            foreach (var other in allLessons)
            {
                if (other != lesson && IsSameKind(lesson, other) && AppSessionData.HasLessonClassificationBeenCompleted(other.Classification))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get the top K (ready) lessons at least N meters away from each other in the xz-plane
        /// Optionally allow lessons with same classification type
        /// </summary>
        /// <returns></returns>
        public Lesson[] NextLessonsInDistancePlaneXZ(List<Lesson> existingLessons, int kLessons, float minimumDistanceRestriction)
        {
            if (kLessons == 0)
            {
                return new Lesson[] { };
            }

            var startExistingCount = existingLessons.Count;
            var chosenList = new List<Lesson>();
            foreach (var lesson in existingLessons)
            {
                chosenList.Add(lesson);
            }

            var totalLessonsNeeded = startExistingCount + kLessons;

            var eligibleList = new List<(Lesson, float)>();

            // Sorted list based on score
            var allLessons = AllLessons();
            foreach (var lesson in allLessons)
            {
                if (lesson.IsReady)
                {
                    var score = lesson.Confidence;
                    eligibleList.Add((lesson, score));
                }
            }

            var comparer = new MyTupleComparer();
            eligibleList.Sort(comparer);

            // Map to complete vs incomplete lessions, for primary and secondary searches
            var incompletedList = new List<Lesson>();
            var completedList = new List<Lesson>();
            var skippedConfidenceLow = 0;
            foreach (var option in eligibleList)
            {
                var lesson = option.Item1;
                // Do not include lessons with low confidence
                if (lesson.Confidence <= 0)
                {
                    skippedConfidenceLow += 1;
                    continue;
                }
                if (HasKindBeenCompleted(lesson, allLessons))
                {
                    completedList.Add(lesson);
                }
                else
                {
                    incompletedList.Add(lesson);
                }
            }

            // Randomize the starting item in the list, if not already present
            if (chosenList.Count == 8)
            {
                if (incompletedList.Count > 0)
                {
                    var randomIndex = UnityEngine.Random.Range(0, incompletedList.Count);
                    var randomLesson = incompletedList[randomIndex];
                    chosenList.Add(randomLesson);
                    _ = incompletedList.Remove(randomLesson);
                }
                else if (completedList.Count > 0)
                {
                    var randomIndex = UnityEngine.Random.Range(0, completedList.Count);
                    var randomLesson = completedList[randomIndex];
                    chosenList.Add(randomLesson);
                    _ = completedList.Remove(randomLesson);
                }
            }

            var separation = minimumDistanceRestriction;
            var relaxedPicks = 0;
            while (chosenList.Count < totalLessonsNeeded)
            {
                // Try to get an incomplete lesson first
                var furthestLesson = FindFurthestLesson(chosenList, incompletedList, separation);
                // Otherwise try to get a complete lesson
                if (furthestLesson == null)
                {
                    furthestLesson = FindFurthestLesson(chosenList, completedList, separation);
                    if (furthestLesson == null)
                    {
                        // A desk packs its objects closer than the preferred spacing. Rather than
                        // send out fewer berries than the stage asks for, fill the rest with the
                        // tighter fallback spacing - the preferred one is still tried first.
                        if (separation > FALLBACK_LESSON_SEPARATION_METERS)
                        {
                            separation = FALLBACK_LESSON_SEPARATION_METERS;
                            continue;
                        }

                        // No more lessons satisfy constraints
                        break;
                    }
                }
                // Add lesson to chosen list, remove from putative lists
                chosenList.Add(furthestLesson);
                _ = completedList.Remove(furthestLesson);
                _ = incompletedList.Remove(furthestLesson);
                if (separation < minimumDistanceRestriction)
                {
                    relaxedPicks += 1;
                }
            }

            // Remove original lessons from beginning
            for (var i = 0; i < startExistingCount; ++i)
            {
                chosenList.RemoveAt(0);
            }

            var chosen = chosenList.ToArray();
            ReportSelectionDecisions(existingLessons, chosen, separation, minimumDistanceRestriction, relaxedPicks);
            return chosen;
        }

        /// <summary>
        /// Record on the scan overlay why each ready lesson was or was not picked. Runs after the
        /// selection and only reads its result, so it cannot change which lessons are chosen. The
        /// reasons mirror the rules above: confidence, repeated classification, then distance.
        /// </summary>
        /// <param name="minimumDistanceRestriction">The spacing the pick ended on, which rejections are judged by.</param>
        /// <param name="preferredSeparation">The spacing the caller asked for.</param>
        /// <param name="relaxedPicks">How many of the chosen lessons needed the fallback spacing.</param>
        private void ReportSelectionDecisions(List<Lesson> existingLessons, Lesson[] chosen, float minimumDistanceRestriction,
            float preferredSeparation, int relaxedPicks)
        {
            if (!SpatialLingoDebug.Enabled)
            {
                return;
            }

            var taken = new List<Lesson>(existingLessons);
            taken.AddRange(chosen);
            var passedOver = new List<string>();
            foreach (var lesson in AllLessons())
            {
                if (!lesson.IsReady)
                {
                    continue;
                }

                var info = ScanDebugRegistry.Find(lesson);
                // A dispatched original is represented in play by its untracked copy, so it would
                // read as a duplicate of itself. The card already shows it as in a lesson.
                if (info == null || info.IsDispatched)
                {
                    continue;
                }

                var isChosen = Array.IndexOf(chosen, lesson) >= 0;
                var reason = isChosen ? string.Empty : RejectionReason(lesson, taken, minimumDistanceRestriction);
                if (!isChosen)
                {
                    passedOver.Add($"{DescribeForLog(lesson)} ({reason})");
                }

                ScanDebugRegistry.Update(lesson, candidate =>
                {
                    candidate.Decision = isChosen ? ScanDecision.Selected : ScanDecision.Rejected;
                    candidate.DecisionReason = reason;
                });
            }

            // The pick repeats every few seconds while berries are missing; only the rounds that
            // send one out are worth a line.
            if (chosen.Length > 0)
            {
                var picked = new List<string>();
                foreach (var lesson in chosen)
                {
                    picked.Add(DescribeForLog(lesson));
                }

                var spacing = relaxedPicks > 0
                    ? $"spacing {preferredSeparation:F2}m, {FALLBACK_LESSON_SEPARATION_METERS:F2}m for the last {relaxedPicks}"
                    : $"spacing {preferredSeparation:F2}m";
                Debug.Log($"LessonsManager - tier {AppSessionData.Tier} pick, {existingLessons.Count} already out, {spacing}: " +
                          $"chose {string.Join(", ", picked)}; passed over {(passedOver.Count > 0 ? string.Join(", ", passedOver) : "nothing")}");
            }
        }

        /// <summary>
        /// One lesson for a debug log line: id, word, position, and whether its classification
        /// was already completed.
        /// </summary>
        private static string DescribeForLog(Lesson lesson)
        {
            var done = AppSessionData.HasLessonClassificationBeenCompleted(lesson.Classification) ? " done" : string.Empty;
            return $"{lesson.Activity.DebugLabel} \"{lesson.Activity.UserLanguageWord}\" at {lesson.Position}{done}";
        }

        private static string RejectionReason(Lesson lesson, List<Lesson> taken, float minimumDistanceRestriction)
        {
            if (lesson.Confidence <= 0)
            {
                return "low confidence";
            }

            foreach (var other in taken)
            {
                if (other.Classification == lesson.Classification)
                {
                    return "duplicate classification";
                }

                if (IsSameKind(lesson, other))
                {
                    return $"same word as {other.Activity.DebugLabel}";
                }
            }

            foreach (var other in taken)
            {
                if (Vector3.Distance(other.Position, lesson.Position) < minimumDistanceRestriction)
                {
                    return "too close to another lesson";
                }
            }

            return "enough lessons already";
        }

        private Lesson FindFurthestLesson(List<Lesson> existing, List<Lesson> potential, float minimumDistanceRestriction)
        {
            // No searching necessary if only a single or no entry
            if (existing.Count == 0)
            {
                return potential.Count == 0 ? null : potential[0];
            }

            Lesson closestLesson = null;
            var closestDistanceMax = 0.0f;

            // Find the next lesson furthest away from all other lessons
            foreach (var lessonPotential in potential)
            {
                var minDistance = -1.0f;
                var shouldSkip = false;
                foreach (var lessonExisting in existing)
                {
                    // No repeated types, by classification or by word
                    if (IsSameKind(lessonPotential, lessonExisting))
                    {
                        shouldSkip = true;
                        break;
                    }
                    // Keep track of closest location
                    var distance = Vector3.Distance(lessonPotential.Position, lessonExisting.Position);
                    minDistance = minDistance < 0 ? distance : Mathf.Min(minDistance, distance);
                }

                if (!shouldSkip && minDistance > closestDistanceMax)
                {
                    closestLesson = lessonPotential;
                    closestDistanceMax = minDistance;
                }
            }
            // Enforce minimum distance restriction
            return closestDistanceMax < minimumDistanceRestriction ? null : closestLesson;
        }

        private class MyTupleComparer : IComparer<(Lesson, float)>
        {
            public int Compare((Lesson, float) x, (Lesson, float) y)
            {
                if (x.Item2 < y.Item2)
                {
                    return 1;
                }
                if (x.Item2 > y.Item2)
                {
                    return -1;
                }
                return 0;
            }
        }

        public Lesson[] ReadyLessons()
        {
            var ready = new List<Lesson>();
            foreach (var lesson in AllLessons())
            {
                if (lesson.IsReady)
                {
                    ready.Add(lesson);
                }
            }
            return ready.ToArray();
        }

        public Lesson[] WaitingLessons()
        {
            var waiting = new List<Lesson>();
            foreach (var lesson in AllLessons())
            {
                if (!lesson.IsReady)
                {
                    waiting.Add(lesson);
                }
            }
            return waiting.ToArray();
        }

        public Activity[] Activities()
        {
            var list = new List<Activity>();
            foreach (var pair in m_activities)
            {
                var activity = pair.Value;
                list.Add(activity);
            }
            return list.ToArray();
        }

        public void AcquireActivityComponents()
        {
            CheckGetActivityComponents();
        }

        /// <summary>
        /// Stop starting new acquisitions while the user is answering.
        ///
        /// Acquisition and speech validation share one HttpClient and one main thread, and
        /// measured together the validation round trip went from ~1.5s to ~4.8s. An acquisition
        /// already in flight is left to finish - there is no clean way to cancel it - but it waits
        /// before its second request.
        /// </summary>
        public void SuspendAcquisition()
        {
            if (m_isAcquisitionSuspended)
            {
                return;
            }

            m_isAcquisitionSuspended = true;
            Debug.Log("LessonsManager - acquisition paused for lesson");
        }

        public void ResumeAcquisition()
        {
            if (!m_isAcquisitionSuspended)
            {
                return;
            }

            m_isAcquisitionSuspended = false;
            Debug.Log("LessonsManager - acquisition resumed");
            PumpAfterResume();
        }

        /// <summary>
        /// Pump the queue a moment after resuming rather than immediately. Stepping out of one
        /// lesson and into another produces a resume and a suspend in quick succession, and
        /// pumping on the spot would launch a minute-long acquisition that then runs for the
        /// whole of the next lesson. Waiting lets the re-suspend win.
        /// </summary>
        private async void PumpAfterResume()
        {
            try
            {
                await Task.Delay(RESUME_DEBOUNCE_MS);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"LessonsManager - PumpAfterResume delay interrupted: {e.Message}");
                return;
            }

            if (m_taxonTracker == null || m_isAcquisitionSuspended)
            {
                return;
            }

            CheckGetActivityComponents();
        }

        /// <summary>
        /// Park between an activity's two requests while a lesson is running, so an acquisition
        /// already underway does not fire its second request mid-lesson. Bounded so an
        /// unbalanced suspend cannot wedge the queue forever.
        /// </summary>
        private async Awaitable WaitWhileSuspendedBetweenSubTasks()
        {
            var waited = 0;
            while (m_isAcquisitionSuspended && m_taxonTracker != null && waited < MAX_SUBTASK_HOLD_MS)
            {
                await Task.Delay(SUBTASK_HOLD_POLL_MS);
                waited += SUBTASK_HOLD_POLL_MS;
            }
        }

        /// <summary>
        /// The objects waiting to be acquired, best first by a summed score (see
        /// <see cref="AcquisitionScore"/>): 1 for an object not yet completed and 0 for one that
        /// has, plus bonuses for how much the player has been looking at it, how long it has
        /// waited, how recently it was seen and how near it is, minus a penalty that puts a repeat
        /// of a classification that already has a lesson behind every one that does not. Nothing
        /// sets HasCompleted at present, so the completed term is the same for every object and
        /// has no effect on the order until something does. Ties keep the order objects were
        /// found in.
        ///
        /// Shared by the pick and the debug overlay, so the place in line a card shows is the
        /// order things would be taken in right now.
        /// </summary>
        private List<(Lesson lesson, Activity activity)> AcquisitionQueue()
        {
            var scored = new List<(Lesson lesson, Activity activity, float score, int order)>();
            foreach (var pair in m_activities)
            {
                var activity = pair.Value;
                if (activity.IsReady || IsRunning(activity) || activity.InvalidResultCount >= MAX_INVALID_RESULTS_PER_OBJECT)
                {
                    continue;
                }

                scored.Add((pair.Key, activity, AcquisitionScore(pair.Key, activity), scored.Count));
            }

            // List.Sort is not stable, so the found order breaks ties explicitly.
            scored.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.order.CompareTo(b.order));

            var queue = new List<(Lesson lesson, Activity activity)>(scored.Count);
            foreach (var (lesson, activity, _, _) in scored)
            {
                queue.Add((lesson, activity));
            }

            return queue;
        }

        private float AcquisitionScore(Lesson lesson, Activity activity)
        {
            var score = activity.HasCompleted ? 0.0f : 1.0f;
            score += RelevanceScore(lesson);
            score += WaitingScore(activity);

            // One mouse can be detected as several, and each acquisition costs about a minute.
            // A repeat of a classification that already has a lesson, or is being acquired right
            // now, is deferred, not dropped: it is still the best thing left once the fresh ones
            // run out. Counting running ones keeps two copies of one object from taking both
            // parallel slots.
            if (IsRepeatOfKnownClass(activity))
            {
                score -= SAME_CLASS_QUEUE_PENALTY;
            }

            return score;
        }

        /// <summary>
        /// Log who gets a free acquisition slot and the top of the queue at that moment, each with
        /// its score broken into the terms of <see cref="AcquisitionScore"/>, for debug mode.
        /// </summary>
        private void LogQueueAtPick(List<(Lesson lesson, Activity activity)> queue, Activity picked)
        {
            var top = new List<string>();
            for (var i = 0; i < queue.Count && i < 3; ++i)
            {
                var (lesson, activity) = queue[i];
                top.Add(DescribeScore(lesson, activity));
            }

            Debug.Log($"LessonsManager - slot {m_activeRuns.Count + 1} goes to {picked.DebugLabel}; queue top {top.Count} of {queue.Count}: {string.Join("; ", top)}");
        }

        private string DescribeScore(Lesson lesson, Activity activity)
        {
            var hasHead = m_userHead != null;
            var looked = hasHead ? AttentionScore(lesson) : 0.0f;
            var seen = hasHead ? RecentlySeenScore(lesson) : 0.0f;
            var near = hasHead ? ProximityScore(lesson) : 0.0f;
            var repeat = IsRepeatOfKnownClass(activity) ? $" - repeat {SAME_CLASS_QUEUE_PENALTY:F2}" : string.Empty;
            return $"{activity.DebugLabel} {AcquisitionScore(lesson, activity):F2} = " +
                   $"{(activity.HasCompleted ? "done 0.00" : "new 1.00")} + looked {looked:F2} ({AttentionShare(activity) * 100.0f:F0}% of {ATTENTION_WINDOW_SECONDS:F0}s)" +
                   $" + seen {seen:F2} + near {near:F2} + waited {WaitingScore(activity):F2}{repeat}";
        }

        /// <summary>
        /// Whether another activity of the same classification is already acquired or being
        /// acquired, so this one is most likely a second detection of the same object.
        /// </summary>
        private bool IsRepeatOfKnownClass(Activity activity)
        {
            foreach (var pair in m_activities)
            {
                var other = pair.Value;
                if (other != activity && (other.IsReady || IsRunning(other)) && other.Classification == activity.Classification)
                {
                    return true;
                }
            }

            // A run whose object was removed mid-acquisition still holds a slot for its class.
            foreach (var run in m_activeRuns)
            {
                if (run.Activity != activity && run.Activity.Classification == activity.Classification)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How relevant an object is to the player: looked at over the last while, recently seen,
        /// and near. Returns 0 when there is no head transform.
        /// </summary>
        private float RelevanceScore(Lesson lesson)
        {
            if (m_userHead == null || lesson == null)
            {
                return 0.0f;
            }

            return AttentionScore(lesson) + RecentlySeenScore(lesson) + ProximityScore(lesson);
        }

        /// <summary>The in-view bonus, scaled by the share of the attention window spent in view.</summary>
        private float AttentionScore(Lesson lesson)
        {
            return IN_VIEW_SCORE_BONUS * AttentionShare(lesson.Activity);
        }

        private float RecentlySeenScore(Lesson lesson)
        {
            // Read off the taxon, which stops advancing once a lesson is pinned, so a pinned
            // object reports when it was actually last seen rather than now.
            var secondsSinceSeen = lesson.SecondsSinceLastSeen;
            return secondsSinceSeen >= 0.0f
                ? RECENTLY_SEEN_SCORE_WEIGHT * Mathf.Clamp01(1.0f - secondsSinceSeen / RECENTLY_SEEN_WINDOW_SECONDS)
                : 0.0f;
        }

        private float ProximityScore(Lesson lesson)
        {
            if (m_userHead == null)
            {
                return 0.0f;
            }

            var distance = Vector3.Distance(lesson.Position, m_userHead.position);
            return PROXIMITY_SCORE_WEIGHT *
                   Mathf.Clamp01((PROXIMITY_FAR_METERS - distance) / (PROXIMITY_FAR_METERS - PROXIMITY_NEAR_METERS));
        }

        /// <summary>
        /// Grows linearly with time since the object was first detected, reaching the full
        /// waiting bonus at <see cref="WAITING_FULL_BONUS_SECONDS"/>. It is not reset when an
        /// acquisition fails, so an old object that keeps failing comes back with the full bonus.
        /// </summary>
        private static float WaitingScore(Activity activity)
        {
            if (activity.DetectedAt <= 0.0f)
            {
                return 0.0f;
            }

            var waited = Time.realtimeSinceStartup - activity.DetectedAt;
            return WAITING_SCORE_WEIGHT * Mathf.Clamp01(waited / WAITING_FULL_BONUS_SECONDS);
        }

        private bool IsInView(Lesson lesson)
        {
            var toObject = lesson.Position - m_userHead.position;
            return toObject.magnitude > Mathf.Epsilon && Vector3.Angle(m_userHead.forward, toObject) < IN_VIEW_MAX_ANGLE_DEGREES;
        }

        /// <summary>Share of the attention window this object spent in view, 0 to 1.</summary>
        private float AttentionShare(Activity activity)
        {
            if (m_attentionSampleTimes.Count == 0 || !m_inViewSampleTimes.TryGetValue(activity, out var times))
            {
                return 0.0f;
            }

            return Mathf.Clamp01((float)times.Count / m_attentionSampleTimes.Count);
        }

        /// <summary>
        /// Note, twice a second, which waiting objects are in view. Runs for the manager's lifetime
        /// when there is a head transform to sample.
        /// </summary>
        private async void SampleAttention()
        {
            try
            {
                while (m_taxonTracker != null && m_userHead != null)
                {
                    RecordAttentionSample(Time.realtimeSinceStartup);
                    await Awaitable.WaitForSecondsAsync(ATTENTION_SAMPLE_INTERVAL_SECONDS);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"LessonsManager - attention sampling stopped: {e.Message}");
            }
        }

        private void RecordAttentionSample(float now)
        {
            m_attentionSampleTimes.Enqueue(now);
            while (now - m_attentionSampleTimes.Peek() > ATTENTION_WINDOW_SECONDS)
            {
                _ = m_attentionSampleTimes.Dequeue();
            }

            foreach (var pair in m_activities)
            {
                var activity = pair.Value;
                if (activity.IsReady || !IsInView(pair.Key))
                {
                    continue;
                }

                if (!m_inViewSampleTimes.TryGetValue(activity, out var times))
                {
                    times = new Queue<float>();
                    m_inViewSampleTimes[activity] = times;
                }

                times.Enqueue(now);
            }

            // Age out samples older than the window, and forget activities that are acquired,
            // gone, or no longer in view at all within it.
            List<Activity> stale = null;
            foreach (var pair in m_inViewSampleTimes)
            {
                var times = pair.Value;
                while (times.Count > 0 && now - times.Peek() > ATTENTION_WINDOW_SECONDS)
                {
                    _ = times.Dequeue();
                }

                if (times.Count == 0 || pair.Key.IsReady || !m_activities.ContainsValue(pair.Key))
                {
                    (stale ??= new List<Activity>()).Add(pair.Key);
                }
            }

            if (stale != null)
            {
                foreach (var activity in stale)
                {
                    _ = m_inViewSampleTimes.Remove(activity);
                }
            }
        }

        /// <summary>
        /// How many objects may be acquired at once right now.
        ///
        /// Several at a time while the room is still being filled, since the wait is almost all
        /// network and overlapping it is the only way to reach a usable set of lessons quickly.
        /// Back to one once enough exist, so later discovery does not compete with the player.
        /// </summary>
        private int ConcurrentAcquisitionLimit()
        {
            var settings = SpatialLingoSettings.Instance;
            if (settings == null || !settings.ParallelAcquisitionEnabled)
            {
                return 1;
            }

            return ReadyActivityCount() >= PARALLEL_UNTIL_LESSON_COUNT
                ? 1
                : Mathf.Max(1, settings.ParallelAcquisitionCount);
        }

        /// <summary>Acquired activities, tracked or pinned, that could be taught.</summary>
        private int ReadyActivityCount()
        {
            var count = 0;
            foreach (var pair in m_activities)
            {
                if (pair.Value.IsReady)
                {
                    count += 1;
                }
            }

            return count;
        }

        private void CheckGetActivityComponents()
        {
            // Paused for a lesson, or cooling down after a failure. The delayed pump in
            // ContinueAfterFailedAcquisition picks up again once the cooldown ends.
            if (m_isAcquisitionSuspended || Time.realtimeSinceStartup < m_acquisitionCooldownUntil)
            {
                RefreshDebugQueuePositions();
                return;
            }

            // Fill the free slots. Each run is registered synchronously in GetActivityComponents
            // before its first await, so the next pick no longer sees it in the queue.
            var limit = ConcurrentAcquisitionLimit();
            while (m_activeRuns.Count < limit)
            {
                var queue = AcquisitionQueue();
                if (queue.Count == 0)
                {
                    break;
                }

                var nextActivity = PickNextActivity(queue);
                if (nextActivity == null)
                {
                    break;
                }

                if (SpatialLingoDebug.Enabled)
                {
                    LogQueueAtPick(queue, nextActivity);
                }

                var started = m_activeRuns.Count;
                GetActivityComponents(nextActivity);

                // Defensive: stop rather than spin if the run did not register.
                if (m_activeRuns.Count == started)
                {
                    break;
                }
            }

            m_skipOnNextPick = null;
            RefreshDebugQueuePositions();
        }

        /// <summary>
        /// The activity to start in a free slot, or null to leave the slot empty for now.
        ///
        /// The first slot takes the head of the queue. An extra slot, running alongside another
        /// acquisition, is not spent on a repeat of a classification already acquired or in
        /// flight while anything else is waiting. If only repeats are left, the slot is held until
        /// the repeat was detected <see cref="REPEAT_PARALLEL_WAIT_SECONDS"/> ago, giving
        /// something else the chance to turn up, and a pump is scheduled for then. That time runs
        /// from the repeat's own detection and does not wait for the acquisition already in
        /// flight, so a repeat detected longer ago than that takes the slot at once, alongside it.
        /// </summary>
        private Activity PickNextActivity(List<(Lesson lesson, Activity activity)> queue)
        {
            var candidates = new List<Activity>(queue.Count);
            foreach (var (_, activity) in queue)
            {
                candidates.Add(activity);
            }

            // Give the objects behind a fresh failure their turn, unless it is the only one left.
            if (candidates.Count > 1 && candidates[0] == m_skipOnNextPick)
            {
                candidates.RemoveAt(0);
                candidates.Add(m_skipOnNextPick);
            }

            if (m_activeRuns.Count == 0)
            {
                return candidates[0];
            }

            Activity firstRepeat = null;
            foreach (var candidate in candidates)
            {
                if (!IsRepeatOfKnownClass(candidate))
                {
                    return candidate;
                }

                firstRepeat ??= candidate;
            }

            var waited = Time.realtimeSinceStartup - firstRepeat.DetectedAt;
            if (waited >= REPEAT_PARALLEL_WAIT_SECONDS)
            {
                return firstRepeat;
            }

            PumpWhenRepeatMayStart(firstRepeat, REPEAT_PARALLEL_WAIT_SECONDS - waited);
            return null;
        }

        /// <summary>
        /// Pump the queue once a held-back repeat may take an extra slot. Nothing else would:
        /// pumps otherwise come from new detections and finished runs.
        /// </summary>
        private async void PumpWhenRepeatMayStart(Activity repeat, float delaySeconds)
        {
            if (m_isRepeatPumpScheduled)
            {
                return;
            }

            m_isRepeatPumpScheduled = true;
            if (SpatialLingoDebug.Enabled)
            {
                Debug.Log($"LessonsManager - holding the extra slot from {repeat.DebugLabel} for {delaySeconds:F1}s: " +
                          "its classification is already acquired or in flight");
            }

            try
            {
                await Task.Delay(Mathf.CeilToInt(delaySeconds * 1000.0f));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"LessonsManager - PumpWhenRepeatMayStart delay interrupted: {e.Message}");
                m_isRepeatPumpScheduled = false;
                return;
            }

            m_isRepeatPumpScheduled = false;
            if (m_taxonTracker == null)
            {
                return;
            }

            CheckGetActivityComponents();
        }

        /// <summary>
        /// Apply a change to the scan overlay entry of every lesson backed by this activity.
        /// </summary>
        private void UpdateDebugFor(Activity activity, Action<ScanCandidateDebugInfo> mutate)
        {
            if (activity == null || !SpatialLingoDebug.Enabled)
            {
                return;
            }

            foreach (var pair in m_activities)
            {
                if (pair.Value == activity)
                {
                    ScanDebugRegistry.Update(pair.Key, mutate);
                }
            }
        }

        /// <summary>
        /// Number the waiting objects in the order CheckGetActivityComponents will take them,
        /// from the same AcquisitionQueue the pick uses.
        /// </summary>
        private void RefreshDebugQueuePositions()
        {
            if (!SpatialLingoDebug.Enabled)
            {
                return;
            }

            var queue = new List<Lesson>();
            foreach (var (lesson, _) in AcquisitionQueue())
            {
                queue.Add(lesson);
            }

            foreach (var pair in m_activities)
            {
                var position = queue.IndexOf(pair.Key) + 1;
                ScanDebugRegistry.Update(pair.Key, info =>
                {
                    info.QueuePosition = position;
                    info.QueueLength = position > 0 ? queue.Count : 0;
                });
            }
        }

        private void ResetActivities()
        {
            var previousLessons = new List<Lesson>();
            foreach (var activityPair in m_activities)
            {
                var activity = activityPair.Value;
                if (!activity.IsReady)
                {
                    // Was not ready, don't need to invalidate
                    continue;
                }
                // Collect invalidated lessons
                foreach (var lesson in AllLessons())
                {
                    if (IsRunning(lesson.Activity))
                    {
                        previousLessons.Add(lesson);
                    }
                }
                activity.Reset();
                UpdateDebugFor(activity, info =>
                {
                    info.State = ScanCandidateState.Pending;
                    info.Decision = ScanDecision.None;
                    info.DecisionReason = string.Empty;
                });
                foreach (var lesson in previousLessons)
                {
                    LessonRemoved?.Invoke(new LessonUpdateResult(lesson));
                    LessonTrackingChanged?.Invoke();
                }
                previousLessons.Clear();
            }
        }

        private string ActivitySummary()
        {
            var totalActivities = m_activities.Count;
            var readyActivities = 0;
            var activityClasses = new List<string>();
            foreach (var activityPair in m_activities)
            {
                var activity = activityPair.Value;
                if (activity.IsReady)
                {
                    readyActivities++;
                }
                activityClasses.Add(activity.Classification);
            }

            var totalLessons = m_lessons.Count + m_pinnedLessons.Count;
            var readyLessons = 0;
            foreach (var lesson in AllLessons())
            {
                if (lesson.Activity.IsReady)
                {
                    readyLessons++;
                }
            }
            var classList = string.Join(",", activityClasses.ToArray());
            return $"LessonsManager - Activity Summary:\nLessons: {readyLessons}/{totalLessons}\nActivities: {readyActivities}/{totalActivities} - classifications:{classList}";
        }

        /// <summary>
        /// Turn a finished run's answer into a lesson. Synchronous, so runs that return together
        /// complete one at a time on the main thread rather than interleaving.
        /// </summary>
        private void ProcessActivityComplete(AcquisitionRun run)
        {
            var activity = run.Activity;
            var languageChanged = run.TargetLanguage != m_targetLanguage;

            var invalid = false;
            if (languageChanged)
            {
                activity.Reset();
            }
            else if (!activity.IsValidForUse())
            {
                // Counted, so an object the model keeps answering badly is given up on rather than
                // picked again straight away for ever.
                invalid = true;
                activity.InvalidResultCount += 1;
                activity.Reset();
            }
            else
            {
                // Completed loading atomic assets
                activity.IsAcquisitionComplete = true;
                m_consecutiveFailedAcquisitions = 0;
                foreach (var lesson in AllLessons())
                {
                    if (lesson.Activity == activity)
                    {
                        if (SpatialLingoDebug.Enabled)
                        {
                            Debug.Log($"LessonsManager - acquired {DescribeForLog(lesson)} / \"{activity.TargetLanguageWord}\"");
                        }

                        LessonAdded?.Invoke(new LessonUpdateResult(lesson));
                        LessonTrackingChanged?.Invoke();
                    }
                }
            }

            var acquired = activity.IsAcquisitionComplete;
            var abandoned = activity.InvalidResultCount >= MAX_INVALID_RESULTS_PER_OBJECT;
            var discardReason = languageChanged ? "reset before use"
                : abandoned ? $"result not usable x{activity.InvalidResultCount}, given up"
                : "result not usable, will retry";
            UpdateDebugFor(activity, info =>
            {
                info.State = acquired ? ScanCandidateState.Ready : abandoned ? ScanCandidateState.Failed : ScanCandidateState.Pending;
                info.IsProcessing = false;
                info.StageLabel = string.Empty;
                info.FailureReason = acquired ? string.Empty : discardReason;
            });

            // An unusable answer goes behind the other objects for one pick rather than being
            // asked about again immediately.
            if (invalid)
            {
                m_skipOnNextPick = activity;
            }

            _ = m_activeRuns.Remove(run);
            SettleAfterRun(activity);
            CheckGetActivityComponents();
        }

        /// <summary>
        /// Tidy up after a run ends, however it ended.
        ///
        /// A lesson pinned while its acquisition was in flight was kept as work in progress. If
        /// the run did not produce a lesson it is no longer that, and its position was never
        /// confirmed, so it is let go like any waiting candidate that lost tracking.
        ///
        /// An object removed while it was being acquired leaves its activity to the run still
        /// using it; once the run ends nothing else holds it, so its crop is freed.
        /// </summary>
        private void SettleAfterRun(Activity activity)
        {
            if (activity == null)
            {
                return;
            }

            if (!activity.IsReady)
            {
                var pinned = m_pinnedLessons.Find(lesson => lesson.Activity == activity);
                if (pinned != null)
                {
                    _ = m_pinnedLessons.Remove(pinned);
                    _ = m_activities.Remove(pinned);
                    pinned.ReleaseTrackedImages(m_taxonTracker?.HeldImages());
                    ScanDebugRegistry.Untrack(pinned);
                    Debug.Log($"LessonsManager - {activity.DebugLabel} was pinned mid-acquisition and produced no lesson, letting it go");
                    LessonRemoved?.Invoke(new LessonUpdateResult(pinned));
                }
            }

            if (!m_activities.ContainsValue(activity))
            {
                activity.Clear();
            }
        }

        /// <summary>
        /// Retry the task related to Activity Acquisition after a fail 
        /// If retry limit is reached, reset acquisition states and notify listeners
        /// Error could be underlying endpoint or WIFI in general 
        /// </summary>
        /// <param name="run">The acquisition this sub-task belongs to</param>
        /// <param name="task">Task to perform</param>
        /// <param name="stageName">Step name for the debug activity panel</param>
        /// <returns>true if success, false otherwise</returns>
        private async Awaitable<bool> RetryActivitySubTaskForSuccess(AcquisitionRun run, Func<Awaitable<bool>> task, string stageName)
        {
            var activity = run.Activity;
            // One panel row per sub-task, spanning its retries: that is the unit the app actually
            // waits on, and a row that sits at "..." for a minute is the thing worth seeing.
            var action = DebugActionLog.Begin(DebugActionKind.Discovery, activity.DebugLabel, stageName);
            var endpointRetryAttempts = 0;
            while (endpointRetryAttempts < ENDPOINT_MAX_RETRY_COUNT)
            {
                if (m_taxonTracker == null)
                {
                    return StopAfterDisposal();
                }

                var attempt = endpointRetryAttempts;
                UpdateDebugFor(activity, info =>
                    info.StageLabel = attempt == 0 ? stageName : $"{stageName} retry {attempt}/{ENDPOINT_MAX_RETRY_COUNT}");
                var resultSuccess = await ExecuteTask();
                if (m_taxonTracker == null)
                {
                    return StopAfterDisposal();
                }

                if (!resultSuccess)
                {
                    if (DateTime.UtcNow < MetaModelRestApi.RetryAfterUtc ||
                        MetaModelRestApi.SessionRequests >= MetaModelRestApi.SessionRequestLimit)
                        break;
                    Debug.LogWarning($"LessonsManager - RetryActivitySubTaskForSuccess unsuccessful task, will attempt retry:{endpointRetryAttempts}");
                    await Task.Delay(WAIT_TIME_BETWEEN_REQUESTS_MS);
                    if (m_taxonTracker == null)
                    {
                        return StopAfterDisposal();
                    }

                    ++endpointRetryAttempts;
                    continue;
                }

                DebugActionLog.Complete(action, DebugActionStatus.Success);
                return true;
            }

            DebugActionLog.Complete(action, DebugActionStatus.Failed, $"gave up after {ENDPOINT_MAX_RETRY_COUNT} attempts");
            UpdateDebugFor(activity, info =>
            {
                info.State = ScanCandidateState.Failed;
                info.IsProcessing = false;
                info.StageLabel = string.Empty;
                info.FailureReason = $"{stageName} failed x{ENDPOINT_MAX_RETRY_COUNT}";
            });
            Debug.LogWarning("LessonsManager - failed subtask, resetting activity");
            activity.Reset();
            _ = m_activeRuns.Remove(run);
            SettleAfterRun(activity);
            CallbackActivityFail();
            ContinueAfterFailedAcquisition(activity);
            return false;

            // Once the manager is disposed nobody is waiting on this run any more, so it stops
            // without retrying the network or reporting a failure that would queue another try.
            bool StopAfterDisposal()
            {
                DebugActionLog.Complete(action, DebugActionStatus.Interrupted, "manager disposed");
                _ = m_activeRuns.Remove(run);
                return false;
            }

            async Awaitable<bool> ExecuteTask()
            {
                try
                {
                    return await task();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    return false;
                }
            }
        }

        private void CallbackActivityFail()
        {
            RequestStatusResult result;
            var reachability = Application.internetReachability;
            result = reachability == NetworkReachability.NotReachable
                ? new RequestStatusResult(RequestStatus.FailWifi)
                : new RequestStatusResult(RequestStatus.FailServer);
            ActivityAcquisitionFailed?.Invoke(result);
        }

        private async Awaitable<bool> ActivitySubTaskGetUserWord()
        {
            var translationResult = await GetActivityUserWord();
            var value = translationResult.StatementTo;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            PrimaryAcquiringActivity.UserLanguageWord = value;

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskGetTargetWord()
        {
            var translationResult = await GetActivityTargetWord();
            var value = translationResult.StatementTo;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            PrimaryAcquiringActivity.TargetLanguageWord = value;

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskGetPartsOfSpeech()
        {
            var relatedResult = await GetActivityRelatedWords();
            var adjectives = relatedResult.Adjectives;
            var verbs = relatedResult.Verbs;
            if (adjectives == null || verbs == null || adjectives.Length < RELATED_WORD_PARTS_OF_SPEECH_COUNT || verbs.Length < RELATED_WORD_PARTS_OF_SPEECH_COUNT)
            {
                return false;
            }
            PrimaryAcquiringActivity.AdjectivesTargetLanguage = adjectives;
            PrimaryAcquiringActivity.VerbsTargetLanguage = verbs;

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskUserLanguageGetPartsOfSpeech()
        {
            if (PrimaryAcquiringActivity.AdjectivesTargetLanguage == null || PrimaryAcquiringActivity.VerbsTargetLanguage == null)
            {
                Debug.LogError("Null words, arrays were not populated. Check the AI service response");
                return false;
            }

            // Marshall the list of words
            List<string> wordList = new();
            foreach (var adj in PrimaryAcquiringActivity.AdjectivesTargetLanguage)
            {
                wordList.Add(adj);
            }
            foreach (var verb in PrimaryAcquiringActivity.VerbsTargetLanguage)
            {
                wordList.Add(verb);
            }

            // Get direct translations
            var result = await m_assistant.TranslateWordList(m_targetLanguage, m_userLanguage, wordList.ToArray(), null);
            var wordsTo = result.WordsTo;

            if (wordsTo == null || wordsTo.Length == 0 || wordsTo.Length != wordList.Count)
            {
                Debug.LogError("Empty or mismatch word counts");
                return false;
            }

            // Destinations:
            var resultAdjectives = new string[PrimaryAcquiringActivity.AdjectivesTargetLanguage.Length];
            var resultVerbs = new string[PrimaryAcquiringActivity.VerbsTargetLanguage.Length];

            for (var i = 0; i < wordsTo.Length; i++)
            {
                var wordTo = wordsTo[i];
                var index = i;
                if (index >= resultAdjectives.Length)
                {
                    index -= resultAdjectives.Length;
                    if (index >= resultVerbs.Length)
                    {
                        _ = resultVerbs.Length;
                        // N/A
                    }
                    else
                    {
                        resultVerbs[index] = wordTo;
                    }
                }
                else
                {
                    resultAdjectives[index] = wordTo;
                }
            }

            // Assign & done
            PrimaryAcquiringActivity.AdjectivesUserLanguage = resultAdjectives;
            PrimaryAcquiringActivity.VerbsUserLanguage = resultVerbs;

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskTargetLanguageSentence()
        {
            var dialogueResult = await GetActivityExamplePhrase();
            var examplePhrase = dialogueResult.Dialogue;

            if (string.IsNullOrWhiteSpace(examplePhrase))
            {
                return false;
            }
            PrimaryAcquiringActivity.ExamplePhrases = new string[] { examplePhrase };

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskGetWordCloudData(AcquisitionRun run)
        {
            string imageString = null;
            var image = run.Activity.ContextImage;
            if (image != null)
            {
                // Preparation shares one face detector and RenderTexture.active, so only one crop
                // is prepared at a time. The request itself is not held behind the gate.
                await m_prepGate.WaitAsync();
                try
                {
                    imageString = await AssistantAI.GetNetworkSafeImageString(image, m_faceBlur);
                }
                finally
                {
                    _ = m_prepGate.Release();
                }
            }
            var result = await m_assistant.GenerateWordCloudData(m_userLanguage, m_targetLanguage, run.Activity.Classification, imageString, run.RequestId);
            UpdateDebugFor(run.Activity, info => info.AddUsage(result.Stats));
            if (result.Wordcloud == null || result.Wordcloud.Wordclouds == null || result.Wordcloud.Wordclouds.Length == 0)
            {
                Debug.LogWarning($"ActivitySubTaskGetWordCloudData - result fail: {result}");
                return false;
            }

            var wordClouds = result.Wordcloud;
            var wordCloudUser = wordClouds.WordCloudForLanguage(m_userLanguage);
            var wordCloudTarget = wordClouds.WordCloudForLanguage(m_targetLanguage);

            if (wordCloudUser == null || wordCloudTarget == null)
            {
                Debug.LogWarning($"ActivitySubTaskGetWordCloudData - wordCloudUser ({wordCloudUser}) or wordCloudTarget ({wordCloudTarget}) null");
                return false;
            }

            run.Activity.UserLanguageWord = wordCloudUser.Word;
            run.Activity.VerbsUserLanguage = wordCloudUser.Verbs;
            run.Activity.AdjectivesUserLanguage = wordCloudUser.Adjectives;

            run.Activity.TargetLanguageWord = wordCloudTarget.Word;
            run.Activity.VerbsTargetLanguage = wordCloudTarget.Verbs;
            run.Activity.AdjectivesTargetLanguage = wordCloudTarget.Adjectives;

            return true;
        }

        private async Awaitable<bool> ActivitySubTaskTargetLanguagePhrases(AcquisitionRun run)
        {
            var word = run.Activity.TargetLanguageWord;
            var verbs = run.Activity.VerbsTargetLanguage;
            var adjectives = run.Activity.AdjectivesTargetLanguage;
            var result = await m_assistant.GenerateSpeechExamplesForWordCloud(m_targetLanguage, word, verbs, adjectives, run.RequestId);
            UpdateDebugFor(run.Activity, info => info.AddUsage(result.Stats));

            var phrases = result.Phrases;

            if (phrases == null)
            {
                Debug.LogWarning($"ActivitySubTaskTargetLanguagePhrases - phrases was null");
                return false;
            }

            if (string.IsNullOrWhiteSpace(phrases.Phrase0) ||
                string.IsNullOrWhiteSpace(phrases.Phrase1) ||
                string.IsNullOrWhiteSpace(phrases.Phrase2))
            {
                Debug.LogWarning($"ActivitySubTaskTargetLanguagePhrases - a phrases was empty or null");
                return false;
            }

            run.Activity.ExamplePhrases = new string[] { phrases.Phrase0, phrases.Phrase1, phrases.Phrase2 };

            return true;
        }

        private async void GetActivityComponents(Activity activity)
        {
            // Registered before the first await, so the caller's next pick no longer sees this
            // object in the queue.
            var run = new AcquisitionRun
            {
                Activity = activity,
                RequestId = Guid.NewGuid().ToString(),
                TargetLanguage = m_targetLanguage,
            };
            m_activeRuns.Add(run);

            // async void: anything escaping would skip the slot release below and hold it for
            // good, so the whole run is guarded.
            try
            {
                activity.EnglishWord = activity.Classification;
                var classification = activity.Classification;
                Debug.Log($"LessonsManager - acquiring {activity.DebugLabel} ({m_activeRuns.Count} of {ConcurrentAcquisitionLimit()} in flight)");
                UpdateDebugFor(activity, info =>
                {
                    info.State = ScanCandidateState.Acquiring;
                    info.IsProcessing = true;
                    info.FailureReason = string.Empty;
                });
                RefreshDebugQueuePositions();

                if (!await RetryActivitySubTaskForSuccess(run, () => ActivitySubTaskGetWordCloudData(run), "wordcloud"))
                {
                    Debug.LogWarning($"LessonsManager - failed subtask ActivitySubTaskGetPartsOfSpeech for: {classification}");
                    return;
                }

                await WaitWhileSuspendedBetweenSubTasks();

                // The wait above also ends when the manager is disposed, and then there is no one
                // left to hand a result to.
                if (m_taxonTracker == null)
                {
                    _ = m_activeRuns.Remove(run);
                    return;
                }

                if (!await RetryActivitySubTaskForSuccess(run, () => ActivitySubTaskTargetLanguagePhrases(run), "phrases"))
                {
                    Debug.LogWarning($"LessonsManager - failed subtask ActivitySubTaskTargetLanguageSentence for: {classification}");
                    return;
                }

                ProcessActivityComplete(run);
            }
            catch (Exception e)
            {
                Debug.LogException(e);

                // A throw from a listener after acquisition finished must not discard the result.
                if (!activity.IsAcquisitionComplete)
                {
                    UpdateDebugFor(activity, info =>
                    {
                        info.State = ScanCandidateState.Failed;
                        info.IsProcessing = false;
                        info.StageLabel = string.Empty;
                        info.FailureReason = $"error: {e.GetType().Name}";
                    });
                    activity.Reset();
                }

                // Completion may already have released this run's slot; only release it once.
                if (m_activeRuns.Remove(run))
                {
                    SettleAfterRun(activity);
                    ContinueAfterFailedAcquisition(activity);
                }
            }
        }

        /// <summary>
        /// Start the next object after one fails. Without this a failed object leaves the
        /// pipeline idle until the tracker happens to report a new one, which with a flaky
        /// endpoint makes the scan look frozen. The failed object was already reset and is back
        /// in the queue, so it is passed over for one pick to let the objects behind it through.
        /// </summary>
        private async void ContinueAfterFailedAcquisition(Activity failed)
        {
            m_skipOnNextPick = failed;

            // Pause all new acquisitions, doubling with each failure in a row, so a failing
            // endpoint is not retried as fast as objects are detected.
            m_consecutiveFailedAcquisitions += 1;
            var shift = Mathf.Min(m_consecutiveFailedAcquisitions - 1, 4);
            var waitMs = Mathf.Min(WAIT_TIME_AFTER_FAILED_ACQUISITION_MS << shift, MAX_WAIT_TIME_AFTER_FAILED_ACQUISITION_MS);
            m_acquisitionCooldownUntil = Mathf.Max(m_acquisitionCooldownUntil, Time.realtimeSinceStartup + waitMs / 1000.0f);
            try
            {
                await Task.Delay(waitMs);
            }
            catch (Exception e)
            {
                // async void: an escaping exception would take the app down rather than the queue.
                Debug.LogWarning($"LessonsManager - ContinueAfterFailedAcquisition delay interrupted: {e.Message}");
            }

            // Disposed while waiting - the tracker is gone and there is nothing left to drive.
            if (m_taxonTracker == null)
            {
                return;
            }

            CheckGetActivityComponents();
        }

        private async Awaitable<TranslationResult> GetActivityUserWord()
        {
            return await m_assistant.Translate(AssistantAI.SupportedLanguage.English, m_userLanguage, PrimaryAcquiringActivity.EnglishWord, null);
        }

        private async Awaitable<TranslationResult> GetActivityTargetWord()
        {
            return await m_assistant.Translate(AssistantAI.SupportedLanguage.English, m_targetLanguage, PrimaryAcquiringActivity.EnglishWord, null);
        }

        private async Awaitable<RelatedWordResult> GetActivityRelatedWords()
        {
            return await m_assistant.FindRelatedWords(m_targetLanguage, PrimaryAcquiringActivity.EnglishWord, RELATED_WORD_PARTS_OF_SPEECH_COUNT, null);
        }

        private async Awaitable<DialogueResult> GetActivityExamplePhrase()
        {
            var relatedWords = new List<string>();
            if (PrimaryAcquiringActivity.VerbsTargetLanguage != null && PrimaryAcquiringActivity.VerbsTargetLanguage.Length > 0)
            {
                relatedWords.Add(PrimaryAcquiringActivity.VerbsTargetLanguage[0]);
            }
            if (PrimaryAcquiringActivity.AdjectivesTargetLanguage != null && PrimaryAcquiringActivity.AdjectivesTargetLanguage.Length > 0)
            {
                relatedWords.Add(PrimaryAcquiringActivity.AdjectivesTargetLanguage[0]);
            }
            var dialogue = new DialogueContextFocusWord(m_targetLanguage, PrimaryAcquiringActivity.EnglishWord, relatedWords.ToArray(), 0.5f);
            return await m_assistant.GenerateExampleSentenceDialogue(dialogue, null);
        }
    }

    /// <summary>
    /// Object in the 3D world
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class Lesson
    {
        public Activity Activity { get; }
        public string Classification => Activity.Classification;
        public bool IsCompleted { get; private set; } = false;
        public bool IsReady => Activity.IsReady;

        // Tracked data source
        private CameraTrackedTaxon m_taxon;
        // Default values if no underlying tracker 
        private Vector3 m_position;
        private Vector3 m_extent;

        public Lesson(Activity activity, CameraTrackedTaxon taxon)
        {
            Activity = activity;
            m_taxon = taxon;
        }

        public Lesson(Activity activity, Vector3 position, Vector3 extent)
        {
            Activity = activity;
            m_position = position;
            m_extent = extent;
        }

        public List<(Vector3, bool)> SamplePoints => m_taxon != null ? m_taxon.SamplePoints : null;

        public List<Vector3> SampleNormals => m_taxon != null ? m_taxon.SampleNormals : null;

        public Vector3 Position => m_taxon != null ? m_taxon.Center : m_position;

        public Vector3 Extent => m_taxon != null ? m_taxon.Extent : m_extent;

        public float Confidence => m_taxon != null ? m_taxon.Reliability : 1.0f;

        /// <summary>
        /// Seconds since this object was last actually seen, or -1 when it has no tracker behind
        /// it. Read off the taxon, which stops advancing once the lesson is pinned - so a pinned
        /// object reports how long ago it was last in view, not how long ago it was asked about.
        /// </summary>
        public float SecondsSinceLastSeen
        {
            get
            {
                if (m_taxon == null)
                {
                    return -1.0f;
                }

                var lastSeen = m_taxon.NewestTimestamp;
                return lastSeen == DateTime.MinValue ? -1.0f : (float)(DateTime.Now - lastSeen).TotalSeconds;
            }
        }

        /// <summary>
        /// Furthest depth-ray hit from the derived centre, in metres, for the scan debug overlay.
        /// Builds the point list on every call, so it is meant for debug readouts only.
        /// </summary>
        public float SampleSpread
        {
            get
            {
                var points = SamplePoints;
                if (points == null)
                {
                    return 0.0f;
                }

                var center = Position;
                var worst = 0.0f;
                foreach (var (point, _) in points)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(point, center));
                }

                return worst;
            }
        }

        /// <summary>How many depth-ray hits the position was averaged from, for debug readouts.</summary>
        public int SamplePointCount => SamplePoints?.Count ?? 0;

        /// <summary>True once the tracker has committed this object's position, for debug readouts.</summary>
        public bool IsPositionLocked => m_taxon != null && m_taxon.IsPositionLocked;

        /// <summary>
        /// True for a copy made by <see cref="UntrackedCopy"/> for a dispatched lesson. Its crops
        /// are its own duplicates, released when its interactor is destroyed.
        /// </summary>
        public bool IsUntrackedCopy { get; private set; }

        /// <summary>
        /// Move this lesson onto a live track. The current taxon's samples, crops included, are
        /// merged into it first, so nothing the lesson knew about the object is lost.
        /// </summary>
        public void RejoinTracking(CameraTrackedTaxon live)
        {
            if (m_taxon != null && m_taxon != live)
            {
                _ = CameraTrackedTaxon.MergeInto(live, m_taxon);
            }

            m_taxon = live;
        }

        /// <summary>
        /// Move this lesson onto the track the tracker merged its own into. The survivor already
        /// holds this lesson's samples, so merging them again would count them twice.
        /// </summary>
        public void MoveToTrack(CameraTrackedTaxon live)
        {
            m_taxon = live;
        }

        /// <summary>
        /// Destroy the crops of this object's taxon, sparing any in <paramref name="keep"/>. The
        /// activity's own crop is separate and unaffected.
        /// </summary>
        public void ReleaseTrackedImages(ICollection<Texture2D> keep = null)
        {
            m_taxon?.ReleaseImages(keep);
        }

        public ImageSampleContext BestImageForView(Transform transform)
        {
            if (m_taxon != null)
            {
                return m_taxon.GetRepresentativeImage(transform);
            }
            return null;
        }

        public void MarkComplete()
        {
            IsCompleted = true;
        }

        public void MarkIncomplete()
        {
            IsCompleted = false;
        }

        public Lesson UntrackedCopy()
        {
            var untrackedActivity = Activity.UntrackedCopy();
            var copy = m_taxon != null
                ? new Lesson(untrackedActivity, m_taxon.UntrackedCopy())
                : new Lesson(untrackedActivity, Position, Extent);
            copy.IsUntrackedCopy = true;
            return copy;
        }
    }

    /// <summary>
    /// A single word
    /// </summary>
    [MetaCodeSample("SpatialLingo")]
    public class Activity
    {
        public bool IsReady => IsAcquisitionComplete;
        public string Classification;
        public string EnglishWord;
        public string[] ExamplePhrases;
        public bool HasCompleted = false;
        public bool IsAcquisitionComplete = false;

        // How many times acquisition came back with an answer that failed validation. Survives
        // Reset, which is what follows each such answer.
        public int InvalidResultCount;

        // Target-language words
        public string TargetLanguageWord;
        public string[] VerbsTargetLanguage;
        public string[] AdjectivesTargetLanguage;

        // User-language words for assistance:
        public string UserLanguageWord;
        public string[] VerbsUserLanguage;
        public string[] AdjectivesUserLanguage;

        // Image Sensing data:
        public Texture2D ContextImage; // Some representative image from the list
        public Vector2 ContextImageExtent; // 3D approximation of image 2D size

        /// <summary>
        /// Short stable number for debug readouts, so "mouse" detected eight times reads as
        /// mouse#3 rather than eight identical rows. Copied to untracked copies so a dispatched
        /// lesson keeps the identity it had during discovery.
        /// </summary>
        public int DebugId;

        /// <summary>When the object was first detected (Time.realtimeSinceStartup).</summary>
        public float DetectedAt;

        /// <summary>Name plus id, as shown on the debug activity panel.</summary>
        public string DebugLabel => $"{Classification}#{DebugId}";

        /// <summary>
        /// The model's word in the user's language, normalised for comparing two activities, or
        /// null until acquired. Not EnglishWord: that is only the detector's classification, so it
        /// cannot tell a "laptop" that is really a mousepad from one that is a laptop.
        /// </summary>
        public string ComparableWord => IsReady && !string.IsNullOrWhiteSpace(UserLanguageWord)
            ? UserLanguageWord.Trim().ToLowerInvariant()
            : null;

        /// <summary>
        /// A copy for a dispatched lesson. Its ContextImage is its own duplicate, so the original
        /// can destroy its crop whenever it is cleared, and the copy's holder releases the copy's
        /// with <see cref="ReleaseContextImage"/>.
        /// </summary>
        public Activity UntrackedCopy()
        {
            var activity = new Activity
            {
                DebugId = DebugId,
                Classification = Classification,
                EnglishWord = EnglishWord,
                ExamplePhrases = ExamplePhrases,
                HasCompleted = HasCompleted,
                IsAcquisitionComplete = IsAcquisitionComplete,

                TargetLanguageWord = TargetLanguageWord,
                VerbsTargetLanguage = VerbsTargetLanguage,
                AdjectivesTargetLanguage = AdjectivesTargetLanguage,

                UserLanguageWord = UserLanguageWord,
                VerbsUserLanguage = VerbsUserLanguage,
                AdjectivesUserLanguage = AdjectivesUserLanguage,

                ContextImage = CameraTrackedTaxon.CopyTexture(ContextImage),
                ContextImageExtent = ContextImageExtent
            };

            return activity;
        }

        public string Description()
        {
            var result = $"[Activity: ({IsReady}), Classification:{Classification}, EnglishWord:{EnglishWord}, UserLanguageWord:{UserLanguageWord}, TargetLanguageWord:{TargetLanguageWord} IsAcquisitionComplete: {IsAcquisitionComplete}, ContextImage:{ContextImage} ]";
            return result;
        }

        public bool IsValidForUse()
        {
            if (string.IsNullOrEmpty(Classification))
            {
                Debug.LogWarning("Activity ValidForUse: Classification was empty");
                return false;
            }

            if (string.IsNullOrEmpty(EnglishWord))
            {
                Debug.LogWarning("Activity ValidForUse: EnglishWord was empty");
                return false;
            }

            if (ExamplePhrases == null || ExamplePhrases.Length != 3)
            {
                Debug.LogWarning("Activity ValidForUse: EnglishWord was empty or had incorrect count");
                return false;
            }

            // This is set after validation: IsAcquisitionComplete

            if (string.IsNullOrEmpty(TargetLanguageWord))
            {
                Debug.LogWarning("Activity ValidForUse: TargetLanguageWord was empty");
                return false;
            }

            if (VerbsTargetLanguage == null || VerbsTargetLanguage.Length != 2)
            {
                Debug.LogWarning("Activity ValidForUse: VerbsTargetLanguage was empty or had incorrect count");
                return false;
            }

            if (AdjectivesTargetLanguage == null || AdjectivesTargetLanguage.Length != 3)
            {
                Debug.LogWarning("Activity ValidForUse: AdjectivesTargetLanguage was empty or had incorrect count");
                return false;
            }

            if (string.IsNullOrEmpty(UserLanguageWord))
            {
                Debug.LogWarning("Activity ValidForUse: UserLanguageWord was empty");
                return false;
            }

            if (VerbsUserLanguage == null || VerbsUserLanguage.Length != 2)
            {
                Debug.LogWarning("Activity ValidForUse: VerbsTargetLanguage was empty or had incorrect count");
                return false;
            }

            if (AdjectivesUserLanguage == null || AdjectivesUserLanguage.Length != 3)
            {
                Debug.LogWarning("Activity ValidForUse: AdjectivesUserLanguage was empty or had incorrect count");
                return false;
            }

            if (ContextImage == null)
            {
                Debug.LogWarning("Activity ValidForUse: ContextImage was empty");
                return false;
            }

            return true;
        }

        // Set dynamic data to blank
        public void Reset()
        {
            UserLanguageWord = null;
            TargetLanguageWord = null;
            VerbsTargetLanguage = null;
            AdjectivesTargetLanguage = null;
            HasCompleted = false;
            ExamplePhrases = null;
            VerbsUserLanguage = null;
            AdjectivesUserLanguage = null;
            IsAcquisitionComplete = false;
        }

        // Set all data to blank
        public void Clear()
        {
            Reset();
            ReleaseContextImage();
            ContextImageExtent = new Vector2();
        }

        /// <summary>
        /// Destroy the crop this activity owns. It is a copy no one else holds, so it is freed here
        /// rather than left for the finalizer.
        /// </summary>
        public void ReleaseContextImage()
        {
            CameraTrackedTaxon.DestroyTexture(ContextImage);
            ContextImage = null;
        }
    }
}
