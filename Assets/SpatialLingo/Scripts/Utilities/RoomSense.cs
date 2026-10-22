// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using Meta.XR.MRUtilityKit;
using Meta.XR.Samples;
using UnityEngine;

namespace SpatialLingo.Utilities
{
    [MetaCodeSample("SpatialLingo")]
    public class RoomSense : MonoBehaviour
    {
        [Header("Spawn Position Finder")]
        [SerializeField] private FindSpawnPositions m_spawnPositionFinder;
        private Coroutine m_spawnRoutine;

        [MetaCodeSample("SpatialLingo")]
        public class SpawnPositionResult
        {
            public Vector3 Position { get; private set; }
            public bool Success { get; private set; }

            public SpawnPositionResult(Vector3 position, bool success)
            {
                Position = position;
                Success = success;
            }
        }
        public delegate void FindSpawnPositionEvent(SpawnPositionResult result);
        public FindSpawnPositionEvent FindSpawnPosition;

        public void FindSpawnPositions()
        {
            if (m_spawnRoutine != null) StopCoroutine(m_spawnRoutine);
            m_spawnRoutine = StartCoroutine(SpawnCoroutine(m_spawnPositionFinder));
        }

        private IEnumerator SpawnCoroutine(FindSpawnPositions spawnPositionFinder)
        {
            // Room discovery can fail when an old room cannot be localized. Keep
            // onboarding usable instead of waiting forever with only passthrough.
            var deadline = Time.realtimeSinceStartup + 8.0f;
            while (MRUK.Instance == null || MRUK.Instance.GetCurrentRoom() == null)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Debug.LogWarning("RoomSense - No localized room after 8 seconds; spawning companion in front of the headset.");
                    FindSpawnPosition?.Invoke(null);
                    yield break;
                }
                yield return null;
            }
            var room = MRUK.Instance.GetCurrentRoom();
            spawnPositionFinder.StartSpawn(room);

            var spawned = FindFirstObjectByType<RoomSenseSpawnObject>();
            if (spawned == null)
            {
                Debug.LogWarning("RoomSense - Spawning - Initialize: Failed to find RoomSenseSpawnObject");
                FindSpawnPosition?.Invoke(null);
                yield break;
            }

            var position = spawned.transform.position;
            Destroy(spawned);
            FindSpawnPosition?.Invoke(new SpawnPositionResult(position, true));
        }
    }
}
