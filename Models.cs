using System.Text.Json.Serialization;
using Pgvector;

namespace FaceAttendance.Api.Models;

public class Employee
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("employee_code")]
    public string EmployeeCode { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public List<EmployeeFaceEmbedding> FaceEmbeddings { get; set; } = new();
}

public class EmployeeFaceEmbedding
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("employee_id")]
    public int EmployeeId { get; set; }

    // Map embedding data to pgvector.Vector type
    [JsonPropertyName("embedding_data")]
    public Vector EmbeddingData { get; set; } = null!;

    [JsonPropertyName("image_path")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property back to employee
    [JsonIgnore]
    public Employee? Employee { get; set; }
}

public class CheckInLog
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("employee_id")]
    public int EmployeeId { get; set; }

    [JsonPropertyName("check_in_time")]
    public DateTime CheckInTime { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("similarity_score")]
    public float SimilarityScore { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUCCESS";

    // Navigation property back to employee
    [JsonIgnore]
    public Employee? Employee { get; set; }
}
