using System.Buffers;
using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceAttendance.Api.Services;

public interface IFaceEmbeddingService
{
    float[] GetEmbedding(byte[] imageBytes);
    float CalculateCosineSimilarity(float[] vectorA, float[] vectorB);
}

public class FaceEmbeddingService : IFaceEmbeddingService, IDisposable
{
    private readonly InferenceSession _onnxSession;
    private readonly string _inputNodeName;
    private readonly int _modelWidth;
    private readonly int _modelHeight;
    private readonly bool _isNhwc;
    private readonly int[] _inputShape;
    private readonly int _inputLength;

    // Loaded once at application start (registered as singleton); InferenceSession.Run is thread-safe.
    public FaceEmbeddingService(IConfiguration configuration)
    {
        // Load the path of the ONNX model from config, fallback to default name in current folder
        string modelPath = configuration["FaceModel:Path"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "arc.onnx");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"[FATAL] Face ONNX model not found at: {modelPath}. A valid ONNX model is required to run this application.");
        }

        _onnxSession = new InferenceSession(modelPath);
        _inputNodeName = _onnxSession.InputMetadata.Keys.First();
        var inputMeta = _onnxSession.InputMetadata[_inputNodeName];

        // Detect if model is NHWC (Channel-Last) or NCHW (Channel-First)
        if (inputMeta.Dimensions[3] == 3)
        {
            _isNhwc = true;
            _modelHeight = inputMeta.Dimensions[1] == -1 ? 112 : inputMeta.Dimensions[1];
            _modelWidth = inputMeta.Dimensions[2] == -1 ? 112 : inputMeta.Dimensions[2];
        }
        else
        {
            _isNhwc = false;
            _modelHeight = inputMeta.Dimensions[2] == -1 ? 112 : inputMeta.Dimensions[2];
            _modelWidth = inputMeta.Dimensions[3] == -1 ? 112 : inputMeta.Dimensions[3];
        }

        _inputShape = _isNhwc
            ? new[] { 1, _modelHeight, _modelWidth, 3 }
            : new[] { 1, 3, _modelHeight, _modelWidth };
        _inputLength = 3 * _modelHeight * _modelWidth;
    }

    public float[] GetEmbedding(byte[] imageBytes)
    {
        // 1. Decode and resize to the model's input size using SixLabors.ImageSharp
        using Image<Rgb24> image = Image.Load<Rgb24>(imageBytes);
        image.Mutate(x => x.Resize(_modelWidth, _modelHeight));

        // 2. Convert pixel data to a normalized float buffer.
        // The buffer (~150 KB for 112x112) would land on the Large Object Heap if
        // allocated per request, so rent it from the shared pool instead.
        float[] inputData = ArrayPool<float>.Shared.Rent(_inputLength);
        try
        {
            image.ProcessPixelRows(accessor =>
            {
                int channelSize = _modelHeight * _modelWidth;
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgb24> row = accessor.GetRowSpan(y);
                    int rowOffset = y * accessor.Width;

                    if (_isNhwc)
                    {
                        // H x W x C layout
                        for (int x = 0; x < row.Length; x++)
                        {
                            int baseIndex = (rowOffset + x) * 3;
                            inputData[baseIndex + 0] = (row[x].R - 127.5f) / 128f;
                            inputData[baseIndex + 1] = (row[x].G - 127.5f) / 128f;
                            inputData[baseIndex + 2] = (row[x].B - 127.5f) / 128f;
                        }
                    }
                    else
                    {
                        // C x H x W layout
                        for (int x = 0; x < row.Length; x++)
                        {
                            int pixelIndex = rowOffset + x;
                            inputData[pixelIndex] = (row[x].R - 127.5f) / 128f;
                            inputData[channelSize + pixelIndex] = (row[x].G - 127.5f) / 128f;
                            inputData[2 * channelSize + pixelIndex] = (row[x].B - 127.5f) / 128f;
                        }
                    }
                }
            });

            // 3. Create input tensor over the rented buffer (no copy)
            var tensor = new DenseTensor<float>(inputData.AsMemory(0, _inputLength), _inputShape);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputNodeName, tensor)
            };

            // 4. Run inference session
            using var results = _onnxSession.Run(inputs);
            if (results.First().Value is not DenseTensor<float> outputTensor)
            {
                throw new InvalidOperationException("Failed to retrieve outputs from the Face ONNX model.");
            }

            // Return extracted vector array (typically 128 or 512 dimensions)
            return outputTensor.ToArray();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(inputData);
        }
    }

    public float CalculateCosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length)
        {
            throw new ArgumentException("Vectors must have the same dimension size.");
        }

        // SIMD-accelerated cosine similarity (System.Numerics.Tensors)
        float cosineSimilarity = TensorPrimitives.CosineSimilarity(vectorA, vectorB);

        if (float.IsNaN(cosineSimilarity))
        {
            return 0.0f; // zero-magnitude vector
        }

        // Normalise range from [-1, 1] to matching percentage of [0, 1]
        // Cosine Similarity score is commonly between 0.0 (unrelated) and 1.0 (exact match)
        return Math.Max(0f, cosineSimilarity);
    }

    public void Dispose() => _onnxSession.Dispose();
}
