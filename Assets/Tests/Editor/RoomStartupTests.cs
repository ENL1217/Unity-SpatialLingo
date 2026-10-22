using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class RoomStartupTests
{
    private bool m_receivedFallback;
    private void OnSpawn(object result) => m_receivedFallback = result == null;

    [UnityTest]
    public IEnumerator MissingRoomCompletesOnboardingInsteadOfWaitingForever()
    {
        var host = new GameObject("Room startup regression");
        try
        {
            var type = Type.GetType("SpatialLingo.Utilities.RoomSense, Assembly-CSharp", true);
            var component = host.AddComponent(type);
            var callback = type.GetField("FindSpawnPosition");
            callback.SetValue(component, Delegate.CreateDelegate(callback.FieldType, this,
                GetType().GetMethod(nameof(OnSpawn), BindingFlags.NonPublic | BindingFlags.Instance)));
            // Drive the same coroutine directly: EditMode does not advance
            // MonoBehaviour coroutines automatically like a running player.
            var routine = (IEnumerator)type.GetMethod("SpawnCoroutine", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(component, new object[] { null });
            var deadline = Time.realtimeSinceStartup + 10;
            var frames = 0;
            LogAssert.Expect(LogType.Warning,
                "RoomSense - No localized room after 8 seconds; spawning companion in front of the headset.");
            while (Time.realtimeSinceStartup < deadline && routine.MoveNext())
            {
                frames++;
                yield return routine.Current;
            }
            Assert.That(m_receivedFallback, Is.True, "Missing room must allow companion setup to continue.");
            Assert.That(frames, Is.GreaterThan(2), "Room discovery must keep yielding frames.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    [Test]
    public void MissingFloorSpawnsWithinReachInFrontOfCurrentHeadset()
    {
        var host = new GameObject("Companion startup regression");
        var head = new GameObject("Moved headset");
        try
        {
            head.transform.SetPositionAndRotation(new Vector3(8, 3, -5), Quaternion.Euler(0, 90, 0));
            var type = Type.GetType("SpatialLingo.Characters.GollyGoshInteractionManager, Assembly-CSharp", true);
            var component = host.AddComponent(type);
            type.GetField("m_centerEyeAnchor", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(component, head.transform);
            var position = (Vector3)type.GetMethod("GetDefaultSpawnPosition", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(component, null);
            var offset = position - head.transform.position;
            Assert.That(Vector3.Dot(offset, head.transform.forward), Is.GreaterThan(0.5f));
            Assert.That(offset.magnitude, Is.LessThan(1.5f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(head);
        }
    }
}
