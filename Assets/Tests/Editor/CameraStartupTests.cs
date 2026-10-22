using System.Collections;
using System.Diagnostics;
using Meta.Utilities.CameraTaxonTracking;
using Meta.Utilities.CameraTracking;
using Meta.Utilities.ObjectClassifier;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class CameraStartupTests
{
    [UnityTest]
    public IEnumerator MissingCameraYieldsAndCanBeCancelled()
    {
        var root = new GameObject("CameraStartupTest");
        root.SetActive(false);
        var camera = root.AddComponent<WebCamTextureManager>();
        var classifier = root.AddComponent<ImageObjectClassifier>();
        var tracker = new CameraTaxonTracker(null, camera, classifier);
        try
        {
            var clock = Stopwatch.StartNew();
            tracker.StartPolling();
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(100), "Polling must not block the caller.");
            var frames = 0;
            while (clock.ElapsedMilliseconds < 350)
            {
                frames++;
                yield return null;
            }
            Assert.That(frames, Is.GreaterThan(1), "Frames must advance while the camera is unavailable.");
            Assert.That(tracker.IsIdle, Is.False);
            tracker.StopPolling();
            Assert.That(tracker.IsIdle, Is.True);
            tracker.StartPolling();
            tracker.Dispose();
            Assert.That(tracker.IsIdle, Is.True);
        }
        finally
        {
            tracker.Dispose();
            Object.DestroyImmediate(root);
        }
    }
}
