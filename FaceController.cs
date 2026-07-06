using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FaceAttendance.Api.Data;
using FaceAttendance.Api.Models;
using FaceAttendance.Api.Services;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace FaceAttendance.Api.Controllers;

[ApiController]
[Route("api")]
public class FaceController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IFaceEmbeddingService _embeddingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FaceController> _logger;

    public FaceController(AppDbContext dbContext, IFaceEmbeddingService embeddingService, IConfiguration configuration, ILogger<FaceController> logger)
    {
        _dbContext = dbContext;
        _embeddingService = embeddingService;
        _configuration = configuration;
        _logger = logger;
    }

    public class RegisterRequest
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public List<string> FaceImagesBase64 { get; set; } = new();
    }

    public class CheckInRequest
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public string FaceImageBase64 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Strips an optional data URI prefix ("data:image/jpeg;base64,...") and decodes the payload.
    /// </summary>
    private static byte[] DecodeBase64Image(string base64Str)
    {
        int commaIndex = base64Str.IndexOf(',');
        if (commaIndex >= 0)
        {
            base64Str = base64Str.Substring(commaIndex + 1);
        }
        return Convert.FromBase64String(base64Str);
    }

    /// <summary>
    /// Endpoint 1: /api/register
    /// Receives multi-angle face pictures, extracts vectors, maps to Pgvector Vector, and saves to database.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.EmployeeCode))
            return BadRequest(new { message = "Employee Code is required." });
        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new { message = "First Name and Last Name are required." });
        if (request.FaceImagesBase64 == null || request.FaceImagesBase64.Count == 0)
            return BadRequest(new { message = "At least one face image must be provided." });

        string employeeCode = request.EmployeeCode.Trim();
        string normalizedCode = employeeCode.ToUpperInvariant();

        try
        {
            // Find existing employee or create new
            var employee = await _dbContext.Employees
                .FirstOrDefaultAsync(e => e.EmployeeCode.ToUpper() == normalizedCode);

            if (employee == null)
            {
                employee = new Employee
                {
                    EmployeeCode = employeeCode,
                    FirstName = request.FirstName.Trim(),
                    LastName = request.LastName.Trim(),
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Employees.Add(employee);
            }

            var processedAngles = new List<object>();

            for (int i = 0; i < request.FaceImagesBase64.Count; i++)
            {
                string base64Str = request.FaceImagesBase64[i];
                if (string.IsNullOrWhiteSpace(base64Str)) continue;

                byte[] imageBytes;
                float[] embedding;
                try
                {
                    imageBytes = DecodeBase64Image(base64Str);
                    embedding = _embeddingService.GetEmbedding(imageBytes);
                }
                catch (FormatException)
                {
                    return BadRequest(new { message = $"Image at index {i} is not valid base64." });
                }
                catch (ImageFormatException)
                {
                    return BadRequest(new { message = $"Image at index {i} is not a valid or supported image format." });
                }

                // Create 1-to-many embedding record using Pgvector.Vector.
                // Linking via the navigation property lets EF resolve the FK in a single SaveChanges,
                // even when the employee row is new (no extra round trip to obtain the PK).
                var faceEmbedding = new EmployeeFaceEmbedding
                {
                    Employee = employee,
                    EmbeddingData = new Vector(embedding), // map float[] to Pgvector.Vector
                    ImagePath = "path_placeholder", // optional: store path to raw image
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.FaceEmbeddings.Add(faceEmbedding);
                processedAngles.Add(new { angle_index = i, embedding_size = embedding.Length });
            }

            if (processedAngles.Count == 0)
                return BadRequest(new { message = "No usable face images were provided." });

            await _dbContext.SaveChangesAsync();
            stopwatch.Stop();

            return Ok(new
            {
                message = "Employee face profiles registered successfully.",
                employee_id = employee.Id,
                employee_code = employee.EmployeeCode,
                first_name = employee.FirstName,
                last_name = employee.LastName,
                angles_registered = processedAngles,
                execution_time_ms = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for EmployeeCode {EmployeeCode}", employeeCode);
            return StatusCode(500, new { message = "An error occurred during registration.", error = ex.Message });
        }
    }

    /// <summary>
    /// Endpoint 2: /api/checkin
    /// Receives check-in face, extracts embedding, and compares similarity in-database via pgvector.
    /// </summary>
    [HttpPost("checkin")]
    public async Task<IActionResult> CheckIn([FromBody] CheckInRequest request)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.EmployeeCode))
            return BadRequest(new { message = "Employee Code is required." });
        if (string.IsNullOrWhiteSpace(request.FaceImageBase64))
            return BadRequest(new { message = "Face check-in image is required." });

        string normalizedCode = request.EmployeeCode.Trim().ToUpperInvariant();

        try
        {
            // 1. Fetch employee (read-only, no change tracking needed)
            var employee = await _dbContext.Employees
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EmployeeCode.ToUpper() == normalizedCode);

            if (employee == null)
            {
                return NotFound(new { message = $"Employee with code '{request.EmployeeCode}' not found." });
            }

            // 2. Extract the new check-in embedding vector
            float[] checkInEmbedding;
            try
            {
                byte[] checkInBytes = DecodeBase64Image(request.FaceImageBase64);
                checkInEmbedding = _embeddingService.GetEmbedding(checkInBytes);
            }
            catch (FormatException)
            {
                return BadRequest(new { message = "Face check-in image is not valid base64." });
            }
            catch (ImageFormatException)
            {
                return BadRequest(new { message = "Face check-in image is not a valid or supported image format." });
            }

            // 3. Compute the best match inside PostgreSQL using the pgvector cosine
            // distance operator (<=>). Only a single scalar crosses the wire instead
            // of every registered 512-dimension vector.
            var probeVector = new Vector(checkInEmbedding);
            double? minCosineDistance = await _dbContext.FaceEmbeddings
                .Where(fe => fe.EmployeeId == employee.Id)
                .MinAsync(fe => (double?)fe.EmbeddingData.CosineDistance(probeVector));

            if (minCosineDistance == null)
            {
                return BadRequest(new { message = "Employee exists, but has no registered face embeddings. Please register faces first." });
            }

            // cosine distance = 1 - cosine similarity; clamp to [0, 1] like the in-memory path did
            float maxSimilarity = Math.Max(0f, 1f - (float)minCosineDistance.Value);

            // 4. Match validation threshold (usually 0.70 for cosine similarity of face vectors)
            float threshold = _configuration.GetValue<float>("FaceModel:Threshold", 0.70f);
            bool isMatched = maxSimilarity >= threshold;
            string status = isMatched ? "SUCCESS" : "FAILED_MATCH_UNDER_THRESHOLD";

            // 5. Save check-in log entry
            var log = new CheckInLog
            {
                EmployeeId = employee.Id,
                CheckInTime = DateTime.UtcNow,
                SimilarityScore = maxSimilarity,
                Status = status
            };
            _dbContext.CheckInLogs.Add(log);
            await _dbContext.SaveChangesAsync();

            stopwatch.Stop();

            if (isMatched)
                _logger.LogInformation("Check-in SUCCESS for EmployeeCode: {EmployeeCode}, Similarity: {Similarity:F4}", employee.EmployeeCode, maxSimilarity);
            else
                _logger.LogWarning("Check-in FAILED for EmployeeCode: {EmployeeCode}, Similarity: {Similarity:F4}", employee.EmployeeCode, maxSimilarity);

            return Ok(new
            {
                success = isMatched,
                employeeCode = employee.EmployeeCode,
                employeeName = $"{employee.FirstName} {employee.LastName}",
                similarity = maxSimilarity,
                similarity_percentage = Math.Round(maxSimilarity * 100, 2),
                threshold = threshold,
                status = status,
                execution_time_ms = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check-in failed for EmployeeCode {EmployeeCode}", normalizedCode);
            return StatusCode(500, new { message = "An error occurred during check-in processing.", error = ex.Message });
        }
    }

    /// <summary>
    /// Endpoint 3: /api/employees
    /// Helper endpoint to fetch employees and registration status for frontend usage.
    /// </summary>
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees()
    {
        var result = await _dbContext.Employees
            .AsNoTracking()
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FirstName,
                e.LastName,
                e.CreatedAt,
                FaceAnglesCount = e.FaceEmbeddings.Count
            })
            .ToListAsync();

        return Ok(result);
    }
}
