// Copyright (c) Meta Platforms, Inc. and affiliates.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meta.Utilities.CameraTaxonTracking
{
    /// <summary>
    /// Represents a single (surface) sample of object
    /// </summary>
    public class TrackSample
    {
        public Texture2D CameraImage;
        public Vector3 CameraPosition;
        public bool[] IsEdges;
        public Vector3 Normal;
        public Vector3[] Points;
        public Vector3 Up;

        /// <summary>
        /// Width over height of the crop, recorded when the sample is made so the size estimate
        /// still works after the crop texture has been released.
        /// </summary>
        public float ImageAspect;

        public TrackSample(Vector3 normal, Vector3 up, Vector3[] points, bool[] edges, Texture2D cameraImage, Vector3 cameraCenter, float imageAspect = 0.0f)
        {
            Normal = normal;
            Up = up;
            Points = points;
            IsEdges = edges;
            CameraImage = cameraImage;
            CameraPosition = cameraCenter;
            ImageAspect = cameraImage != null && cameraImage.height > 0
                ? (float)cameraImage.width / cameraImage.height
                : imageAspect > 0.0f ? imageAspect : 1.0f;
            Timestamp = DateTime.Now;
        }

        public DateTime Timestamp
        {
            get;
        }
    }

    public class ImageSampleContext
    {
        public Texture2D Image;
        public Vector3 Normal;
        public Vector2 Size;
        public Vector3 Up;
    }

    public class CameraTrackedTaxon
    {
        // Settings
        public const float MINIMUM_POINT_COUNT_KEEP = 4;
        public const float MINIMUM_EXTENT = 0.05f; // 5 cm
        public const int MAXIMUM_SAMPLE_COUNT = 10; // Throw away oldest values at this point
        public const int MAX_MISS_COUNT_FOR_REMOVAL = 5; // Should have been visible but was not allowance
        public const float MAXIMUM_ANGLE_VISIBILITY_DEGREES = 35.0f; // Forgiving / narrow fov estimate
        public const float MINIMUM_DISTANCE_VISIBILITY = 0.50f; // Not too close
        public const float MAXIMUM_DISTANCE_VISIBILITY = 2.0f; // Not too far

        // Reliability floor for anything still being tracked. Never zero: reliability says how
        // well we know the object is there, not whether it is worth teaching - the AI decides
        // that when it evaluates the object, and it cannot do so for something already discarded.
        public const float MINIMUM_RELIABILITY = 0.1f;

        // What one miss costs, expressed in sightings. A miss erodes trust but must not erase an
        // object that has been seen repeatedly: the detector blinks often enough that consecutive
        // sightings of the same object are the exception, not the rule.
        public const int MISS_PENALTY_IN_SIGHTINGS = 2;

        // Reliability at which the position becomes eligible to be committed. Eligibility is not
        // enough on its own - see POSITION_LOCK_AGREEMENT_COUNT.
        public const float POSITION_LOCK_RELIABILITY = 0.5f;

        // Committing needs the estimate to have stopped moving, not just enough sightings. A
        // sighting count alone freezes an object on its sparsest evidence and then ignores every
        // ray hit that follows, leaving the lesson off-centre. Require this many consecutive
        // sightings to agree first.
        public const int POSITION_LOCK_AGREEMENT_COUNT = 3;

        // How far consecutive sightings may differ and still count as agreeing. Scales with the
        // object, so a keyboard is allowed more wander than a mouse, with a floor for small items.
        public const float POSITION_LOCK_AGREEMENT_FACTOR = 0.25f;
        public const float POSITION_LOCK_AGREEMENT_MINIMUM = 0.10f;

        // Source values
        private readonly List<TrackSample> m_surfaceSamples = new();

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        public CameraTrackedTaxon(string classification, TrackSample[] samples)
        {
            Taxa = classification;
            AddSamples(samples);
        }

        public string Taxa { get; }

        // This can be a list from merged observations
        public ImageSampleContext ImageContext => GetRepresentativeImage(null);

        private int m_observeCount = 0; // As object is re-observed increase this by 1
        private int m_missedCount = 0; // As object is expected but not seen increase this by 1

        // Consecutive sightings whose own centre landed close to the previous one. The position
        // is only committed once the estimate has demonstrably settled.
        private int m_stableCenterCount;
        private Vector3 m_previousSightingCenter;
        private bool m_hasPreviousSightingCenter;

        /// <summary>
        /// True once the object has been seen reliably and its sightings agree, after which its
        /// position is frozen: Center and Extent stop moving, so a lesson placed here stays put
        /// and a later stray depth hit cannot drag it.
        /// </summary>
        public bool IsPositionLocked { get; private set; }

        public Vector3 Center { get; private set; }

        public string Name => Taxa;

        public Vector3 Extent { get; private set; }

        /// <summary>
        /// A detached copy for a lesson that leaves tracking. Its crops are duplicates owned by
        /// the copy: this taxon destroys its own as they are pruned, so sharing them would leave
        /// the copy pointing at destroyed textures. Whoever holds the copy must call
        /// <see cref="ReleaseImages"/> when done with it.
        /// </summary>
        public CameraTrackedTaxon UntrackedCopy()
        {
            var samples = new List<TrackSample>();
            foreach (var sample in m_surfaceSamples)
            {
                var copy = new TrackSample(sample.Normal, sample.Up, sample.Points, sample.IsEdges, CopyTexture(sample.CameraImage), sample.CameraPosition, sample.ImageAspect);
                samples.Add(copy);
            }
            var taxon = new CameraTrackedTaxon(Taxa, samples.ToArray());

            // Carry the committed place across, rather than letting the copy re-derive it from
            // the samples and land somewhere slightly different.
            if (IsPositionLocked)
            {
                taxon.LockPositionAt(Center, Extent);
            }

            return taxon;
        }

        public DateTime OldestTimestamp
        {
            get
            {
                var timestamp = DateTime.Now;
                foreach (var sample in m_surfaceSamples)
                {
                    var stamp = sample.Timestamp;
                    timestamp = timestamp < stamp ? timestamp : stamp;
                }

                return timestamp;
            }
        }

        /// <summary>
        /// When this object was last actually seen: the newest sample's timestamp, or
        /// DateTime.MinValue when it has none. Starts from the minimum rather than from now,
        /// since every sample is in the past and starting from now would always report now.
        /// </summary>
        public DateTime NewestTimestamp
        {
            get
            {
                var timestamp = DateTime.MinValue;
                foreach (var sample in m_surfaceSamples)
                {
                    var stamp = sample.Timestamp;
                    timestamp = timestamp > stamp ? timestamp : stamp;
                }

                return timestamp;
            }
        }

        public List<(Vector3, bool)> SamplePoints
        {
            get
            {
                var list = new List<(Vector3, bool)>();
                foreach (var sample in m_surfaceSamples)
                {
                    var pointCount = sample.Points.Length;
                    for (var i = 0; i < pointCount; i++)
                    {
                        var point = sample.Points[i];
                        var edge = sample.IsEdges[i];
                        list.Add((point, edge));
                    }
                }

                return list;
            }
        }

        public List<Vector3> SampleNormals
        {
            get
            {
                var list = new List<Vector3>();
                foreach (var sample in m_surfaceSamples)
                {
                    list.Add(sample.Normal);
                }

                return list;
            }
        }

        /// <summary>
        /// How well this object's presence and position are known, in [MINIMUM_RELIABILITY, 1].
        ///
        /// Sightings accumulate and misses erode, rather than a miss wiping the total. The old
        /// behaviour reset the sighting count on any miss and returned exactly zero after two,
        /// which made a well-established object unusable the moment the detector blinked twice -
        /// and zero meant the selection pass dropped it even though it was still tracked and had
        /// already been paid for.
        /// </summary>
        public float Reliability
        {
            get
            {
                var effective = m_observeCount - m_missedCount * MISS_PENALTY_IN_SIGHTINGS;
                if (effective <= 0)
                {
                    // Seen at least once, but not yet proven. Still a candidate.
                    return MINIMUM_RELIABILITY;
                }

                // 1 = 0.50, 2 = 0.67, 3 = 0.75, 4 = 0.80 ...
                return Mathf.Clamp(1.0f - 1.0f / (1.0f + effective), MINIMUM_RELIABILITY, 1.0f);
            }
        }

        public bool ShouldRemove(DateTime observeTimestamp)
        {
            // Criteria:
            var pointCountTotal = 0;
            foreach (var sample in m_surfaceSamples)
            {
                var points = sample.Points.Length;
                pointCountTotal += points;
            }

            // Not enough points to approximate center & extent
            if (pointCountTotal < MINIMUM_POINT_COUNT_KEEP)
            {
                return true;
            }

            // Time since the last sighting is not a reason to remove an object on its own: one
            // that is out of view is simply not being seen, and it only counts as missed when it
            // should have been visible (m_missedCount below).

            // Should have seen & missed too many times
            if (m_missedCount > MAX_MISS_COUNT_FOR_REMOVAL)
            {
                return true;
            }

            // If size or position is changing a lot, it may be best to remove here.
            // It will take comparing tracking data and checking for various tolerances. 

            return false;
        }

        private bool ShouldBeVisible(Vector3 cameraCenter, Vector3 cameraDirection)
        {
            var cameraToCenter = Center - cameraCenter;
            var angle = Vector3.Angle(cameraDirection, cameraToCenter);
            // For more accuracy, an extra check can be added here for the image facing towards the user.
            if (angle < MAXIMUM_ANGLE_VISIBILITY_DEGREES)
            {
                var distance = cameraToCenter.magnitude;
                // Is in reasonable distance for image recognition
                if (distance is >= MINIMUM_DISTANCE_VISIBILITY and <= MAXIMUM_DISTANCE_VISIBILITY)
                {
                    return true;
                }
            }
            return false;
        }

        public void NoteCameraObservation(DateTime timestamp, Vector3 cameraCenter, Vector3 cameraDirection)
        {
            // If object was seen this round == contains the recent timestamp (merged)
            if (TimestampExists(timestamp))
            {
                m_observeCount += 1;
                m_missedCount = 0;

                // Seen enough times AND settled in one place before committing. Reliability
                // alone says the object keeps being recognised; it says nothing about whether the
                // depth estimate has stopped wandering.
                if (!IsPositionLocked
                    && Reliability >= POSITION_LOCK_RELIABILITY
                    && m_stableCenterCount >= POSITION_LOCK_AGREEMENT_COUNT)
                {
                    IsPositionLocked = true;
                }
                // If object was not seen but clearly should have been:
            }
            else if (ShouldBeVisible(cameraCenter, cameraDirection))
            {
                // Deliberately does not reset m_observeCount: a blink from the detector should
                // cost trust, not erase everything the object has earned.
                m_missedCount += 1;
            }
            // Else: gray area, eg: maybe not clearly visible => no action
        }

        private bool TimestampExists(DateTime timestamp)
        {
            // Most recent timestamps should be on end:
            if (m_surfaceSamples.Count > 0)
            {
                var last = m_surfaceSamples.Last();
                return last.Timestamp >= timestamp;
            }
            return false;
        }

        /// <summary>
        /// The sample best facing the given transform, with its orientation and estimated size.
        /// Image is null once the crops have been released; the geometry is still valid.
        /// Returns null only when there are no samples.
        /// </summary>
        public ImageSampleContext GetRepresentativeImage(Transform transform)
        {
            Texture2D bestImage = null;
            var bestAspect = 1.0f;
            var found = false;
            var bestNormal = new Vector3();
            var bestUp = new Vector3();
            var bestAngle = -1.0f;
            var fewestEdges = 0;
            foreach (var sample in m_surfaceSamples)
            {
                var edges = sample.IsEdges;
                var edgeCount = 0;
                foreach (var edge in edges)
                {
                    if (edge)
                    {
                        edgeCount += 1;
                    }
                }

                if (!found || edgeCount <= fewestEdges)
                {
                    var keep = false;
                    if (transform != null)
                    {
                        var angle = Vector3.Angle(transform.forward, bestNormal);
                        if (bestAngle < 0.0f || angle > bestAngle)
                        {
                            keep = true;
                            bestAngle = angle;
                        }
                    }
                    else
                    {
                        keep = true;
                    }

                    if (keep)
                    {
                        found = true;
                        fewestEdges = edgeCount;
                        bestImage = sample.CameraImage;
                        bestAspect = sample.ImageAspect;
                        bestNormal = sample.Normal;
                        bestUp = sample.Up;
                    }
                }
            }

            // can get the SIZE from estimated extents in directions orthogonal to normal
            if (!found)
            {
                return null;
            }

            // Estimate image size based on sample origin and current size estimation
            var center = Center;
            var dotSumUp = 0.0f;
            var dotSumRight = 0.0f;
            var bestRight = Quaternion.AngleAxis(90.0f, bestNormal) * bestUp;
            var pointCount = 0;
            foreach (var sample in m_surfaceSamples)
            {
                foreach (var point in sample.Points)
                {
                    var dir = point - center;
                    // Height: up-down
                    var dot = Vector3.Dot(dir, bestUp);
                    if (dot < 0.0f)
                    {
                        dot = -dot;
                    }
                    dotSumUp += dot;

                    // Width: right-left
                    dot = Vector3.Dot(dir, bestRight);
                    if (dot < 0.0f)
                    {
                        dot = -dot;
                    }
                    dotSumRight += dot;

                    // Samples used
                    pointCount += 1;
                }
            }

            if (pointCount == 0)
            {
                return null;
            }

            dotSumUp /= pointCount;
            dotSumRight /= pointCount;

            var widthOverHeight = bestAspect;
            var averageY = dotSumUp;
            var averageX = dotSumRight;
            // Preserve aspect ratio by averaging 2 size estimates
            var widthFromHeight = averageY * widthOverHeight;
            var heightFromWidth = averageX / widthOverHeight;
            // Take average of 2 estimates
            var scaleX = (averageX + widthFromHeight) * 0.5f;
            var scaleY = (averageY + heightFromWidth) * 0.5f;

            // Average is ~ 50% of expected size, samples are only in ~ interior portion of object
            var additionalScale = 2.0f / CameraTaxonTracker.TAXON_SAMPLE_EXTENT_FACTOR;
            scaleX *= additionalScale;
            scaleY *= additionalScale;

            var best = new ImageSampleContext
            {
                Image = bestImage,
                Normal = bestNormal,
                Up = bestUp,
                Size = new Vector2(scaleX, scaleY)
            };
            return best;
        }

        // Extents are boundary points in 3D that are derived from the planar projection of the image in 2D 
        public void AddSamples(TrackSample[] samples)
        {
            // Stability is judged per sighting, not on the running average. Averaging damps the
            // noise: a depth estimate swinging over 40cm still produces a mean that barely moves,
            // so the mean looks settled while the underlying readings are nowhere near agreeing.
            foreach (var sample in samples)
            {
                NoteSampleAgreement(sample);
                m_surfaceSamples.Add(sample);
            }

            FilterSamplesRepeatRANSAC();

            // Remove oldest samples first
            // Samples could also include direction prioritization here (for maximizing surface coverage)
            while (m_surfaceSamples.Count > MAXIMUM_SAMPLE_COUNT)
            {
                // The crop is a texture this taxon owns; dropping the sample without destroying
                // it leaks native memory with every detection past the cap.
                ReleaseImage(m_surfaceSamples[0], null);
                m_surfaceSamples.RemoveAt(0);
            }

            CalculateDerived();
        }

        /// <summary>
        /// Set by a listener that keeps using this taxon after the tracker drops it - a pinned
        /// lesson, whose activation effect still picks the crop best facing the player. The
        /// tracker then leaves the crops alone, and the listener releases them when done.
        /// </summary>
        public bool KeepImagesAfterRemoval { get; set; }

        /// <summary>
        /// Destroy the crops this taxon holds. Textures in <paramref name="keep"/> are left alone -
        /// after a merge, a dropped taxon still points at samples the surviving one now owns.
        /// </summary>
        public void ReleaseImages(ICollection<Texture2D> keep = null)
        {
            foreach (var sample in m_surfaceSamples)
            {
                ReleaseImage(sample, keep);
            }
        }

        /// <summary>Every crop this taxon still holds.</summary>
        public IEnumerable<Texture2D> HeldImages
        {
            get
            {
                foreach (var sample in m_surfaceSamples)
                {
                    if (sample.CameraImage != null)
                    {
                        yield return sample.CameraImage;
                    }
                }
            }
        }

        /// <summary>
        /// Whether this taxon holds any of the other's samples. That is what a merge leaves behind,
        /// since <see cref="MergeInto"/> moves the merged-away taxon's samples into the survivor, so
        /// it tells a real collision apart from a separate track that merely sits nearby.
        /// </summary>
        public bool SharesSamplesWith(CameraTrackedTaxon other)
        {
            if (other == null || other == this)
            {
                return false;
            }

            foreach (var sample in other.m_surfaceSamples)
            {
                if (m_surfaceSamples.Contains(sample))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ReleaseImage(TrackSample sample, ICollection<Texture2D> keep)
        {
            if (sample.CameraImage == null || (keep != null && keep.Contains(sample.CameraImage)))
            {
                return;
            }

            DestroyTexture(sample.CameraImage);
            sample.CameraImage = null;
        }

        /// <summary>An independent copy of a crop, owned by the caller. Null in, null out.</summary>
        public static Texture2D CopyTexture(Texture2D texture)
        {
            return texture != null ? UnityEngine.Object.Instantiate(texture) : null;
        }

        /// <summary>Destroy a crop texture; DestroyImmediate outside play mode, where Destroy is not allowed.</summary>
        public static void DestroyTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public void SetSamples(TrackSample[] samples)
        {
            m_surfaceSamples.Clear();
            AddSamples(samples);
        }

        private void FilterSamplesRepeatRANSAC()
        {
            var maxRansacIterations = 5;
            for (var i = 0; i < maxRansacIterations; i++)
            {
                var didRemove = FilterSamplesRANSAC();
                if (!didRemove)
                {
                    return;
                }
            }
        }

        private bool FilterSamplesRANSAC()
        {
            var didDropAnyPoints = false;
            var ransacPointCount = 5; // Minimum count for sample 
            var sigmaPointDrop = 2.5f; // Points further away from 2sigma (95%) 3sigma (99%) are dropped
            var allPoints = new List<Vector3>();
            foreach (var sample in m_surfaceSamples)
            {
                foreach (var point in sample.Points)
                {
                    allPoints.Add(point);
                }
            }

            var allPointCount = allPoints.Count;
            if (allPointCount > ransacPointCount)
            {
                // Get a random sample of points
                var indexes = new List<int>();
                var com = new Vector3();
                var allPointIndices = Enumerable.Range(0, allPointCount).ToList();
                for (var point = 0; point < ransacPointCount; point++)
                {
                    var randomIndex = UnityEngine.Random.Range(0, allPointIndices.Count);
                    indexes.Add(allPointIndices[randomIndex]);
                    allPointIndices.RemoveAt(randomIndex);
                    var potential = allPoints[randomIndex];
                    com += potential;
                }

                // Get center of mass of points
                com /= ransacPointCount;

                // Calculate sigma of distances from center
                var sigma = 0.0f;
                for (var point = 0; point < ransacPointCount; point++)
                {
                    var index = indexes[point];
                    var potential = allPoints[index];
                    var distance = Vector3.Distance(potential, com);
                    sigma += distance * distance;
                }

                sigma = Mathf.Sqrt(sigma / ransacPointCount);

                // Drop points too far from center
                sigma *= sigmaPointDrop;
                for (var i = 0; i < m_surfaceSamples.Count; i++)
                {
                    var sample = m_surfaceSamples[i];
                    var originalPoints = sample.Points;
                    var points = new List<Vector3>(originalPoints);
                    for (var j = 0; j < points.Count; j++)
                    {
                        var point = points[j];
                        var distance = Vector3.Distance(point, com);
                        if (distance > sigma)
                        {
                            points.RemoveAt(j);
                            didDropAnyPoints = true;
                            --j;
                        }
                    }
                    // Replace points if deemed outliers
                    if (originalPoints.Length != points.Count)
                    {
                        sample.Points = points.ToArray();
                    }
                    if (m_surfaceSamples.Count == 0)
                    {
                        m_surfaceSamples.RemoveAt(i);
                        --i;
                    }
                }
            }

            return didDropAnyPoints;
        }

        private void CalculateDerived()
        {
            _ = m_surfaceSamples.Count;
            var pointCountTotal = 0;
            var averageCenter = new Vector3();
            foreach (var sample in m_surfaceSamples)
            {
                foreach (var point in sample.Points)
                {
                    averageCenter += point;
                    pointCountTotal += 1;
                }
            }

            averageCenter /= pointCountTotal;

            float dotX;
            float dotY;
            float dotZ;
            Vector3 centerToPoint;
            var averageExtents = new Vector3();
            var maximumExtents = new Vector3();

            // Edge points could be handled differently, for accuracy. Not a direct part of average, but part of extent minimum
            foreach (var sample in m_surfaceSamples)
            {
                foreach (var point in sample.Points)
                {
                    centerToPoint = point - averageCenter;
                    dotX = Vector3.Dot(centerToPoint, Vector3.right);
                    dotY = Vector3.Dot(centerToPoint, Vector3.up);
                    dotZ = Vector3.Dot(centerToPoint, Vector3.forward);
                    averageExtents.x += Mathf.Abs(dotX);
                    averageExtents.y += Mathf.Abs(dotY);
                    averageExtents.z += Mathf.Abs(dotZ);
                    maximumExtents.x = Mathf.Max(maximumExtents.x, Mathf.Abs(dotX));
                    maximumExtents.y = Mathf.Max(maximumExtents.y, Mathf.Abs(dotY));
                    maximumExtents.z = Mathf.Max(maximumExtents.z, Mathf.Abs(dotZ));
                }
            }
            averageExtents /= pointCountTotal;

            var derivedExtents = (averageExtents + maximumExtents) * 0.5f;
            derivedExtents.x = Mathf.Max(derivedExtents.x, MINIMUM_EXTENT);
            derivedExtents.y = Mathf.Max(derivedExtents.y, MINIMUM_EXTENT);
            derivedExtents.z = Mathf.Max(derivedExtents.z, MINIMUM_EXTENT);

            // Once the position is committed it stops moving. The centre is a mean of depth-ray
            // hits, so a stray hit on background geometry can otherwise shift a trusted object by
            // half a metre - which is how the same mouse ended up on the desk and on the floor.
            if (IsPositionLocked)
            {
                return;
            }

            Center = averageCenter;
            Extent = derivedExtents;
        }

        /// <summary>
        /// Track whether successive sightings are landing in the same place.
        ///
        /// Each sighting's own centroid is compared with the one before it. Consecutive agreement
        /// means the depth estimate has settled; a single disagreement resets the run, so an
        /// object whose readings keep jumping never becomes eligible to commit.
        /// </summary>
        private void NoteSampleAgreement(TrackSample sample)
        {
            if (sample?.Points == null || sample.Points.Length == 0)
            {
                return;
            }

            var centroid = Vector3.zero;
            foreach (var point in sample.Points)
            {
                centroid += point;
            }

            centroid /= sample.Points.Length;

            // Scaled by this sighting's own footprint, so a keyboard gets more latitude than a
            // mouse, with a floor so small objects are not held to sub-centimetre agreement.
            var span = 0.0f;
            foreach (var point in sample.Points)
            {
                span = Mathf.Max(span, Vector3.Distance(point, centroid));
            }

            var tolerance = Mathf.Max(span * POSITION_LOCK_AGREEMENT_FACTOR, POSITION_LOCK_AGREEMENT_MINIMUM);
            m_stableCenterCount = m_hasPreviousSightingCenter
                                  && Vector3.Distance(m_previousSightingCenter, centroid) <= tolerance
                ? m_stableCenterCount + 1
                : 0;

            m_previousSightingCenter = centroid;
            m_hasPreviousSightingCenter = true;
        }

        /// <summary>Commit this object to a place, and stop deriving it from the samples.</summary>
        internal void LockPositionAt(Vector3 center, Vector3 extent)
        {
            Center = center;
            Extent = extent;
            IsPositionLocked = true;
        }

        public static bool Collides(CameraTrackedTaxon taxonA, CameraTrackedTaxon taxonB)
        {
            var sizeMarginRatioClosed = 0.5f; // 1 = 100% of cuboid hypotenuses
            var sizeMarginRatioOpen = 1.5f; // Search a lot further to join open edges
            if (taxonA.Name != taxonB.Name)
            {
                return false;
            }

            // if A's center is too close to B's center:
            var centerA = taxonA.Center;
            var centerB = taxonB.Center;
            var hypA = taxonA.Extent.magnitude;
            var hypB = taxonB.Extent.magnitude;

            var dirAtoB = centerB - centerA;

            var joinsOpenEdgeA = false;
            var joinsOpenEdgeB = false;


            // Consider each sample inside the 2 planes defined by A-B
            var samplesA = taxonA.SamplePoints;
            var samplesB = taxonB.SamplePoints;
            foreach (var sampleA in samplesA)
            {
                var pointA = sampleA.Item1;
                var edgeA = sampleA.Item2;
                var directionA = pointA - centerA;
                // Check if outside plane merger volume
                var dotA = Vector3.Dot(dirAtoB, directionA);
                if (dotA is > 1.0f or < 0.0f)
                {
                    continue;
                }

                var closestBEdge = false;
                var closestBPoint = new Vector3();
                var closestBDistance = -1.0f;
                foreach (var sampleB in samplesB)
                {
                    var pointB = sampleB.Item1;
                    var edgeB = sampleB.Item2;
                    var directionB = pointB - centerB;
                    // Check if outside plane merger volume
                    var dotB = Vector3.Dot(dirAtoB, directionB);
                    if (dotB is > 1.0f or < 0.0f)
                    {
                        continue;
                    }

                    // Find closest point that would join surfaces
                    var distanceAB = Vector3.Distance(pointA, pointB);
                    if (closestBDistance < 0 || distanceAB < closestBDistance)
                    {
                        closestBDistance = distanceAB;
                        closestBPoint = pointB;
                        closestBEdge = edgeB;
                    }
                }

                // If there is a closest point, check if closed-closed, open-closed x2, open-open
                if (closestBDistance > 0)
                {
                    joinsOpenEdgeA |= edgeA;
                    joinsOpenEdgeB |= closestBEdge;
                }
            }

            var sizeMarginRatioA = joinsOpenEdgeA ? sizeMarginRatioOpen : sizeMarginRatioClosed;
            var sizeMarginRatioB = joinsOpenEdgeB ? sizeMarginRatioOpen : sizeMarginRatioClosed;
            var centerDistance = dirAtoB.magnitude;
            var marginAllowedA = hypA * sizeMarginRatioA;
            var marginAllowedB = hypB * sizeMarginRatioB;
            var allowedDistance = marginAllowedA + marginAllowedB;
            return centerDistance < allowedDistance;
        }

        /// <summary>
        ///     This merges 2 objects into a single object
        /// </summary>
        /// <param name="taxonA">Object A - merge target</param>
        /// <param name="taxonB">Object B - other object to merge with</param>
        /// <returns>Object A is returned with updated internal representation</returns>
        public static CameraTrackedTaxon MergeInto(CameraTrackedTaxon taxonA, CameraTrackedTaxon taxonB)
        {
            var sampleList = new List<TrackSample>();
            sampleList.AddRange(taxonA.m_surfaceSamples);
            sampleList.AddRange(taxonB.m_surfaceSamples);
            // Oldest first. Pruning past the cap drops from the front, and TimestampExists checks
            // the last sample for this cycle's sighting, so both rely on time order. OrderBy returns
            // a new sequence; it used to be discarded, leaving the samples in list order.
            var samples = sampleList.OrderBy(s => s.Timestamp).ToArray();

            // Preserve a committed position across the merge - SetSamples would otherwise
            // recompute it from the combined hits and undo the commitment.
            var wasLocked = taxonA.IsPositionLocked || taxonB.IsPositionLocked;
            var lockedCenter = taxonA.IsPositionLocked ? taxonA.Center : taxonB.Center;
            var lockedExtent = taxonA.IsPositionLocked ? taxonA.Extent : taxonB.Extent;

            taxonA.SetSamples(samples);
            if (wasLocked)
            {
                taxonA.LockPositionAt(lockedCenter, lockedExtent);
            }

            return taxonA;
        }
    }
}