# Face Attendance API (Proof of Concept)

A lightweight C# ASP.NET Core 8 Web API demonstrating face recognition, vector storage, and similarity verification. This POC integrates **ONNX Runtime** for local face embedding extraction using the **ArcFace (`arc.onnx`)** model and **PostgreSQL with pgvector** for persistent vector storage.

---

## 🌟 Key Features

- **Face Embedding Extraction**: Preprocesses images locally using `SixLabors.ImageSharp` and extracts 512-dimensional facial feature vectors using the `arc.onnx` model (ONNX Runtime).
- **Vector Database Storage**: Uses PostgreSQL combined with the `pgvector` extension to persist facial vectors.
- **Local Cosine Similarity Verification**: Compares face check-in request embeddings against registered embeddings using high-performance C# vector arithmetic.

---

## 🛠 Tech Stack

- **Framework**: ASP.NET Core 8 Web API
- **Machine Learning Inference**: Microsoft.ML.OnnxRuntime (v1.26.0)
- **Face Model**: ArcFace (Arc.onnx) - 512-dimension output vector
- **Image Processing**: SixLabors.ImageSharp (v2.1.12)
- **Database Provider**: EF Core with Npgsql (PostgreSQL)
- **Vector Search Support**: pgvector extension & Pgvector.EntityFrameworkCore
- **JSON Handler**: System.Text.Json (v10.0.9)

---

## 📂 Project Structure

```text
├── Data/
│   └── AppDbContext.cs            # EF Core DbContext mapping pgvector extension and schemas
├── Models/
│   └── Models.cs                  # Database models (Employee, EmployeeFaceEmbedding, CheckInLog)
├── Services/
│   ├── IFaceEmbeddingService.cs   # Service interface
│   └── FaceEmbeddingService.cs    # ONNX inference + Cosine Similarity calculations
├── Controllers/
│   └── FaceController.cs          # REST endpoints (/api/register, /api/checkin, /api/employees)
├── ml-models/
│   └── arc.onnx                   # Face embedding ONNX model (512 dimensions)
├── StoredFaces/                   # Local folder created dynamically to store raw uploaded pictures
├── appsettings.json               # Application and DB settings
└── Program.cs                     # API services setup, middleware, and CORS configuration
```

---

## 🔧 Installation & Prerequisites

### 1. PostgreSQL & pgvector Setup
Ensure that a PostgreSQL instance is running with the `pgvector` extension installed. Refer to the official [pgvector documentation](https://github.com/pgvector/pgvector) for installation and database initialization details.

### 2. ONNX Model Setup
Ensure that the ArcFace ONNX model (`arc.onnx`) is located in the `ml-models` directory of the application:
```text
ml-models/arc.onnx
```
*Note: The C# build configuration is set up to automatically copy this file to the build output directory (`CopyToOutputDirectory: PreserveNewest`).*

---

## ⚙ Configuration (`appsettings.json`)

Configure your database connection and verification thresholds in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "FaceModel": {
    "Path": "ml-models/arc.onnx",
    "Threshold": 0.70
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=your-database-host;Port=5432;Database=your-database-name;Username=your-username;Password=your-password;Include Error Detail=true"
  }
}
```

- **`FaceModel:Path`**: File path to the ONNX model.
- **`FaceModel:Threshold`**: Similarity score threshold (range `0.0` - `1.0`). If the similarity score is greater than or equal to this threshold, the check-in is successful. Default is `0.70` (70%).
- **`ConnectionStrings:DefaultConnection`**: Connection string pointing to your PostgreSQL instance.

---

## 🗄 Database Schema Design

The DB schema is structured as follows:

### 1. `employees`
Stores employee information.
- `id` (int, Primary Key)
- `employee_code` (varchar(50), Unique Index)
- `first_name` (varchar(100))
- `last_name` (varchar(100))
- `created_at` (timestamp, default: CURRENT_TIMESTAMP)

### 2. `employeefaceembeddings`
Stores registered facial vector profiles mapping back to the employee. An employee can have multiple registered angles (1-to-many relationship).
- `id` (int, Primary Key)
- `employee_id` (int, Foreign Key to `employees` with cascade delete)
- `embedding_data` (vector(512), PostgreSQL pgvector type)
- `image_path` (varchar(500), physical path where the raw file is stored)
- `created_at` (timestamp, default: CURRENT_TIMESTAMP)

### 3. `checkinlogs`
Audit logs of all check-in attempts.
- `id` (int, Primary Key)
- `employee_id` (int, Foreign key to `employees`)
- `check_in_time` (timestamp, default: CURRENT_TIMESTAMP)
- `similarity_score` (float)
- `status` (varchar(50), e.g. "SUCCESS", "FAILED_MATCH_UNDER_THRESHOLD")

---

## 📡 API Endpoints

### 1. Register Employee Faces
- **Endpoint**: `POST /api/register`
- **Description**: Registers a new or existing employee with one or more facial angles. Images are preprocessed and saved, their embeddings are generated, and vectors are persisted to the database.
- **Request Body**:
```json
{
  "EmployeeCode": "EMP001",
  "FirstName": "Jane",
  "LastName": "Doe",
  "FaceImagesBase64": [
    "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ...", 
    "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ..."
  ]
}
```
- **Response**:
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

### 2. Employee Face Check-In
- **Endpoint**: `POST /api/checkin`
- **Description**: Verifies a check-in image against all registered angles of the specified employee code. Generates embedding, computes the highest Cosine Similarity, and logs verification results.
- **Request Body**:
```json
{
  "EmployeeCode": "EMP001",
  "FaceImageBase64": "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ..."
}
```
- **Response (Success)**:
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
- **Response (Failure - Below Threshold)**:
```json
{
  "success": false,
  "employeeCode": "EMP001",
  "employeeName": "Jane Doe",
  "similarity": 0.5312,
  "similarity_percentage": 53.12,
  "threshold": 0.70,
  "status": "FAILED_MATCH_UNDER_THRESHOLD",
  "execution_time_ms": 139
}
```

### 3. List Registered Employees
- **Endpoint**: `GET /api/employees`
- **Description**: Helper endpoint returning details of all registered employees and the number of registered face angles.
- **Response**:
```json
[
  {
    "id": 1,
    "employee_code": "EMP001",
    "first_name": "Jane",
    "last_name": "Doe",
    "created_at": "2026-06-18T07:15:00Z",
    "faceAnglesCount": 2
  }
]
```

---

## 🏃 Running the Application

### 1. Running Locally with .NET Core CLI
Ensure you have [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) installed.

```bash
# 1. Restore dependencies
dotnet restore

# 2. Run EF Core database migrations (updates PostgreSQL schema and activates pgvector extension)
dotnet ef database update

# 3. Build & run the API
dotnet run
```
The application will spin up locally (typically bound to CORS-allowed ports). You can check the local URL in the console output.

---

## 🛡 Disclaimer
This project is a **Proof of Concept (POC)** designed to demonstrate localized face vector extraction and pgvector storage. In a full production implementation:
- **Liveness detection** should be integrated to prevent spoofing (e.g. holding a paper photo or phone screen up to the camera).
- **Authentication and HTTPS** must be implemented to secure endpoints and image transmissions.
- **Vector Search Indexing** (e.g. HNSW/IVFFlat index on pgvector) should be added to PostgreSQL for high-speed top-K similarity search as the user base scales.
