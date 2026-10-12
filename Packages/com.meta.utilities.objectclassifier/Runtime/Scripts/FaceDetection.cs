// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Profiling;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Meta.Utilities.ObjectClassifier
{

    public class FaceDetection : MonoBehaviour
    {
        public Texture2D InputTexture;
        public ModelAsset ModelAsset;
        public ModelAsset CompiledModelAsset;
        private Model m_runtimeModel;
        private RenderTexture m_texture;
        public float ScoreThreshold;
        private float[] m_scoreResults;

        public int ImageSize = 640;
        public BackendType BackendType = BackendType.CPU;

        public QuantizationTypeOrNone QuantizationType = QuantizationTypeOrNone.None;
        public bool UseNMS = false;

        private IEnumerable<float> PriorBoxForward(int imageWidth, int imageHeight)
        {
            var minSizesAll = new[]
            {
            new[] {16, 32},
            new[] {64, 128},
            new[] {256, 512},
        };
            var steps = new[] { 8, 16, 32 };
            for (var k = 0; k < steps.Length; k++)
            {
                var step = steps[k];
                var hsize = (int)Math.Ceiling((double)imageHeight / step);
                var wsize = (int)Math.Ceiling((double)imageWidth / step);
                var minSizes = minSizesAll[k];
                for (var i = 0; i < hsize; i++)
                {
                    var cy = (i + 0.5f) * step;
                    for (var j = 0; j < wsize; j++)
                    {
                        var cx = (j + 0.5f) * step;
                        foreach (var minSize in minSizes)
                        {
                            yield return cx / ImageSize;
                            yield return cy / ImageSize;
                            yield return minSize / (float)ImageSize;
                            yield return minSize / (float)ImageSize;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Timing for a single <see cref="RunBlurring"/> call.
        /// </summary>
        public readonly struct BlurStats
        {
            /// <summary>Wall-clock time for the whole call, model load included.</summary>
            public long TotalMilliseconds { get; }

            /// <summary>
            /// Time spent loading and compiling the detection model. Only the first call pays
            /// this, so it is reported apart from the recurring blur cost.
            /// </summary>
            public long ModelLoadMilliseconds { get; }

            /// <summary>Blur cost on its own, with any one-time model load taken out.</summary>
            public long BlurMilliseconds => TotalMilliseconds - ModelLoadMilliseconds;

            /// <summary>True when this call was the one that loaded the model.</summary>
            public bool IncludedModelLoad { get; }

            /// <summary>Source image dimensions, which drive most of the cost.</summary>
            public int Width { get; }
            public int Height { get; }

            internal BlurStats(long totalMilliseconds, long modelLoadMilliseconds, bool includedModelLoad, int width, int height)
            {
                TotalMilliseconds = totalMilliseconds;
                ModelLoadMilliseconds = modelLoadMilliseconds;
                IncludedModelLoad = includedModelLoad;
                Width = width;
                Height = height;
            }
        }

        /// <summary>Timing for the most recent blur, or null if none has run.</summary>
        public BlurStats? LastBlur { get; private set; }

        /// <summary>
        /// Raised after each blur completes. Lets callers report timing without having to wrap
        /// every <see cref="RunBlurring"/> call site themselves.
        /// </summary>
        public event Action<BlurStats> BlurCompleted;

        public async Awaitable<RenderTexture> RunBlurring()
        {
            var total = Stopwatch.StartNew();
            long modelLoadMilliseconds = 0;
            var includedModelLoad = false;

            if (m_runtimeModel == null)
            {
                var modelLoad = Stopwatch.StartNew();
                Profiler.BeginSample("FaceDetection.LoadModel");
                if (CompiledModelAsset != null)
                    m_runtimeModel = ModelLoader.Load(CompiledModelAsset);
                else
                    LoadAndCompileModel();
                Profiler.EndSample();

                await Task.Yield();

                modelLoad.Stop();
                modelLoadMilliseconds = modelLoad.ElapsedMilliseconds;
                includedModelLoad = true;
            }

            m_texture = new RenderTexture(InputTexture.width, InputTexture.height, 32);

            await Task.Yield();

            await FaceBlur.BlurFaces(m_runtimeModel, BackendType, InputTexture, m_texture, UseNMS);

            total.Stop();
            var stats = new BlurStats(total.ElapsedMilliseconds, modelLoadMilliseconds, includedModelLoad, InputTexture.width, InputTexture.height);
            LastBlur = stats;
            BlurCompleted?.Invoke(stats);

            return m_texture;
        }


        public enum QuantizationTypeOrNone
        {
            Float16 = Unity.InferenceEngine.QuantizationType.Float16,
            Uint8 = Unity.InferenceEngine.QuantizationType.Uint8,
            None
        }

        private void LoadAndCompileModel()
        {
            var sourceModel = ModelLoader.Load(ModelAsset);

            var graph = new FunctionalGraph();

            var inputs = graph.AddInputs(sourceModel);
            inputs[0] = AdjustPixels(inputs[0], ImageSize);

            var outputs = Functional.Forward(sourceModel, inputs);
            var loc = outputs[0][0, .., ..]; // (16800, 4)
            var boxes = ConvertLocToBoxes(loc);
            var scoreValues = outputs[1][0, .., ..];
            var scores = scoreValues[.., 1];

            if (!UseNMS)
            {
                graph.AddOutputs(boxes, scores);
            }
            else
            {
                var indices = Functional.NMS(boxes, scores, 0.01f, ScoreThreshold)[..32];
                var coords = Functional.IndexSelect(boxes, 0, indices);
                graph.AddOutputs(coords);
            }

            Profiler.BeginSample("FaceDetection.CompileGraph");
            m_runtimeModel = graph.Compile();
            Profiler.EndSample();

            if (QuantizationType is not QuantizationTypeOrNone.None)
            {
                Profiler.BeginSample("FaceDetection.QuantizeWeights");
                ModelQuantizer.QuantizeWeights((QuantizationType)QuantizationType, ref m_runtimeModel);
                Profiler.EndSample();
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Compile and save model")]
        public void CompileAndSaveModel()
        {
            LoadAndCompileModel();

            var path = UnityEditor.EditorUtility.SaveFilePanelInProject("Save compiled model", "FaceDetector", "sentis", "Save the compiled model.");
            ModelWriter.Save(path, m_runtimeModel);

            CompiledModelAsset = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) as ModelAsset;
        }
#endif

        private FunctionalTensor ConvertLocToBoxes(FunctionalTensor loc)
        {
            var priorBox = PriorBoxForward(ImageSize, ImageSize).ToArray();
            var priors = Functional.Constant(priorBox).Reshape(new[] { priorBox.Length / 4, 4 });
            var variances0 = 0.1f;
            var variances1 = 0.2f;
            var boxes = Functional.Concat(
                new[] {
                priors[.., ..2] + loc[.., ..2] * variances0 * priors[.., 2..],
                priors[.., 2..] * Functional.Exp(loc[.., 2..] * variances1)
                }, 1);
            boxes[.., ..2] -= boxes[.., 2..] / 2;
            boxes[.., 2..] += boxes[.., ..2];
            return boxes;
        }

        private static FunctionalTensor AdjustPixels(FunctionalTensor pixels, int imageSize)
        {
            var means = Functional.Constant(new float[] { 104, 117, 123 });
            means = means.Reshape(new[] { -1, 1, 1 }).BroadcastTo(new[] { 3, imageSize, imageSize }).Reshape(new[] { 1, 3, imageSize, imageSize });
            return pixels * 255.0f - means;
        }

        // private void OnGUI()
        // {
        //     GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), m_texture);
        //     static void Draw(float4 rect, int index)
        //     {
        //         rect *= new float4(Screen.width, Screen.height, Screen.width, Screen.height);
        //         GUI.Box(new Rect(rect.x, rect.y, rect.z - rect.x, rect.w - rect.y), $"{index}");
        //     }
        //     for (var i = 0; i != Results.Length; i += 4)
        //         Draw(new(Results[i], Results[i + 1], Results[i + 2], Results[i + 3]), i / 4);
        // }
    }
}
