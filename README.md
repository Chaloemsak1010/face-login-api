# Face Attendance API (Proof of Concept)

A lightweight C# ASP.NET Core 8 Web API demonstrating face recognition, vector storage, and similarity verification. This POC integrates **ONNX Runtime** for local face embedding extraction using the **ArcFace (`arc.onnx`)** model and **PostgreSQL with pgvector** for persistent vector storage and in-database similarity search.

- Download model via: https://huggingface.co/garavv/arcface-onnx

> 📖 Full endpoint reference (request/response schemas, error tables): [API-Documentation.md](API-Documentation.md)

---

## 🌟 Key Features

- **Face Embedding Extraction**: Preprocesses images locally using `SixLabors.ImageSharp` and extracts 512-dimensional facial feature vectors using the `arc.onnx` model (ONNX Runtime). Supports both NCHW and NHWC model input layouts (auto-detected at startup).
- **Vector Database Storage**: Uses PostgreSQL combined with the `pgvector` extension to persist facial vectors.
- **In-Database Similarity Search**: Check-in verification computes cosine distance directly inside PostgreSQL using the pgvector `<=>` operator, so only a single scalar (the best score) crosses the wire — not every registered 512-dimension vector.
- **Multi-Angle Enrollment**: An employee can register multiple face images (different angles); check-in matches against the best-scoring one.
- **Audit Logging**: Every check-in attempt — matched or not — is recorded in the `checkinlogs` table with its similarity score and status.

---

## 🛠 Tech Stack

- **Framework**: ASP.NET Core 8 Web API
- **Machine Learning Inference**: Microsoft.ML.OnnxRuntime (v1.26.0)
- **Face Model**: ArcFace (`arc.onnx`) - 512-dimension output vector
- **Image Processing**: SixLabors.ImageSharp (v2.1.12)
- **Database Provider**: EF Core 8 with Npgsql (PostgreSQL)
- **Vector Search Support**: pgvector extension & Pgvector.EntityFrameworkCore
- **SIMD Math**: System.Numerics.Tensors (`TensorPrimitives.CosineSimilarity`) for local vector comparison

---

## 📂 Project Structure

All source files live flat in the project root:

```text
├── Program.cs                 # API services setup, middleware, and CORS configuration
├── FaceController.cs          # REST endpoints (/api/register, /api/checkin, /api/employees)
├── FaceEmbeddingService.cs    # IFaceEmbeddingService + ONNX inference & cosine similarity
├── AppDbContext.cs            # EF Core DbContext mapping pgvector extension and schemas
├── Models.cs                  # Database models (Employee, EmployeeFaceEmbedding, CheckInLog)
├── schema.sql                 # PostgreSQL schema (tables, indexes, pgvector notes)
├── test-api.ps1               # End-to-end PowerShell test script for all endpoints
├── API-Documentation.md       # Detailed REST API reference
├── ml-models/
│   └── arc.onnx               # Face embedding ONNX model (512 dimensions)
├── appsettings.json           # Application and DB settings
└── FaceAttendance.Api.http    # REST Client requests for quick manual testing
```

---

## 🔧 Installation & Prerequisites

### 1. PostgreSQL & pgvector Setup

Ensure a PostgreSQL instance is running with the [pgvector](https://github.com/pgvector/pgvector) extension installed, then create the schema:

```sql
-- 1. Enable the extension in your database
CREATE EXTENSION IF NOT EXISTS vector;
```

```bash
# 2. Create the tables and indexes
psql -h localhost -U your-username -d your-database -f schema.sql
```

> Note: there are no EF Core migrations in this project — the schema is created from `schema.sql`.

### 2. ONNX Model Setup

Place the ArcFace ONNX model at:

```text
ml-models/arc.onnx
```

The application throws a fatal `FileNotFoundException` at startup if the model file is missing. The model path is configurable via `FaceModel:Path` and is resolved relative to the working directory (run `dotnet run` from the project root).

---

## ⚙ Configuration (`appsettings.json`)

```json
{
  "FaceModel": {
    "Path": "ml-models/arc.onnx",
    "Threshold": 0.70
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=your-host;Port=5432;Database=your-db;Username=your-user;Password=your-password;Include Error Detail=true"
  }
}
```

- **`FaceModel:Path`**: File path to the ONNX model.
- **`FaceModel:Threshold`**: Cosine-similarity threshold (`0.0`–`1.0`). A check-in succeeds when the best similarity score is **≥** this value. Default `0.70` (70%).
- **`ConnectionStrings:DefaultConnection`**: PostgreSQL connection string (pgvector extension required).

---

## 🗄 Database Schema Design

See [`schema.sql`](schema.sql) for the full DDL.

### 1. `employees`
Employee master data.
- `id` (int, Primary Key)
- `employee_code` (varchar(50), Unique Index)
- `first_name` (varchar(100))
- `last_name` (varchar(100))
- `created_at` (timestamp, default: CURRENT_TIMESTAMP)

### 2. `employeefaceembeddings`
Registered facial vectors, 1-to-many per employee (multi-angle enrollment).
- `id` (int, Primary Key)
- `employee_id` (int, FK to `employees`, cascade delete)
- `embedding_data` (`vector(512)`, pgvector type)
- `image_path` (varchar(500)) — *currently stored as a placeholder; raw images are not persisted in this POC*
- `created_at` (timestamp, default: CURRENT_TIMESTAMP)

### 3. `checkinlogs`
Audit log of all check-in attempts.
- `id` (int, Primary Key)
- `employee_id` (int, FK to `employees`, cascade delete)
- `check_in_time` (timestamp, default: CURRENT_TIMESTAMP)
- `similarity_score` (float)
- `status` (varchar(50): `SUCCESS` or `FAILED_MATCH_UNDER_THRESHOLD`)

---

## 📡 API Endpoints

Base URL (local dev): `http://localhost:5002`. All bodies are JSON; images are base64 strings (an optional `data:image/...;base64,` prefix is stripped automatically).

Full details, validation rules, and error responses: **[API-Documentation.md](API-Documentation.md)**.

### 1. Register Employee Faces — `POST /api/register`

Registers one or more face images for a new **or existing** employee (new embeddings are added to an existing profile). Employee code matching is case-insensitive.

```json
{
  "employeeCode": "EMP001",
  "firstName": "Jane",
  "lastName": "Doe",
  "faceImagesBase64": [
    "data:image/jpeg;base64,/9j/4AAQSkZJRg...",
    "data:image/jpeg;base64,/9j/4AAQSkZJRg..."
  ]
}
```

Response `200 OK`:

```json
{
  "message": "Employee face profiles registered successfully.",
  "employee_id": 1,
  "employee_code": "EMP001",
  "first_name": "Jane",
  "last_name": "Doe",
  "angles_registered": [
    { "angle_index": 0, "embedding_size": 512 },
    { "angle_index": 1, "embedding_size": 512 }
  ],
  "execution_time_ms": 312
}
```

### 2. Employee Face Check-In — `POST /api/checkin`

Extracts the embedding from the submitted image and finds the **highest cosine similarity** across the employee's registered angles — computed inside PostgreSQL via the pgvector `<=>` (cosine distance) operator. Every attempt is written to `checkinlogs`.

```json
{
  "employeeCode": "EMP001",
  "faceImageBase64": "data:image/jpeg;base64,/9j/4AAQSkZJRg..."
}
```

Response `200 OK` (both match and non-match return 200 — check `success`):

```json
{
  "success": true,
  "employeeCode": "EMP001",
  "employeeName": "Jane Doe",
  "similarity": 0.8427,
  "similarity_percentage": 84.27,
  "threshold": 0.70,
  "status": "SUCCESS",
  "execution_time_ms": 145
}
```

Returns `404` for an unknown employee code, `400` if the employee has no registered embeddings or the image is invalid.

### 3. List Registered Employees — `GET /api/employees`

Helper endpoint returning all employees with their enrolled face-angle counts.

```json
[
  {
    "id": 1,
    "employeeCode": "EMP001",
    "firstName": "Jane",
    "lastName": "Doe",
    "createdAt": "2026-06-18T07:15:00Z",
    "faceAnglesCount": 2
  }
]
```

---

## 🏃 Running the Application

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

```bash
# 1. Restore dependencies
dotnet restore

# 2. Run the API (from the project root, so ml-models/arc.onnx resolves)
dotnet run
```

CORS is fully open (`AllowAnyOrigin`) to make local frontend development (e.g. Next.js on `localhost:3000`) friction-free.

---

## 🧪 Testing

An end-to-end PowerShell test suite is included:

```powershell
# Full suite with generated synthetic images
.\test-api.ps1

# Realistic accuracy test with real photos of the same person
.\test-api.ps1 -RegisterImages front.jpg,left.jpg -CheckInImage today.jpg
```

The script exercises all three endpoints, covering happy paths and the documented validation/error responses. The API and PostgreSQL must both be running. `FaceAttendance.Api.http` is also available for quick manual requests from VS Code / JetBrains REST clients.

---

## 🛡 Disclaimer & Known Limitations

This project is a **Proof of Concept (POC)**. Before any production use:

- **No face detection/alignment** — the full image is resized to 112×112 and embedded, so tightly cropped face images give the best accuracy.
- **Liveness detection** should be integrated to prevent spoofing (e.g. holding a printed photo or phone screen up to the camera).
- **Authentication, rate limiting, and HTTPS** must be added — endpoints are currently completely open.
- **Raw image storage** is not implemented (`image_path` is a placeholder).
- **Vector index** (HNSW/IVFFlat on pgvector) should be added for high-speed top-K similarity search as the user base scales — example DDL is included at the bottom of `schema.sql`.
