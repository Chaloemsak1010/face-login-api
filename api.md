# Face Attendance API Documentation

REST API for face registration and face-verification check-in, backed by an ONNX face embedding model (ArcFace, 512 dimensions) and PostgreSQL + pgvector.

- **Base URL (local dev)**: `http://localhost:5002`
- **Content type**: All request and response bodies are `application/json`.
- **Authentication**: None (proof of concept — do not expose publicly without adding auth + HTTPS).
- **Image format**: Images are sent as base64 strings. A data-URI prefix (`data:image/jpeg;base64,...`) is accepted and stripped automatically. Any format decodable by ImageSharp works (JPEG, PNG, BMP, GIF, WebP...). Images are resized to the model's input size (112×112) server-side.

---

## POST /api/register

Registers face images for a new or existing employee. Each image is converted to a 512-dimension embedding vector and stored in PostgreSQL (pgvector). If the employee code does not exist, the employee is created; if it exists, the new embeddings are **added** to the existing profile (multi-angle enrollment).

Employee code matching is case-insensitive and surrounding whitespace is trimmed.

### Request body

| Field | Type | Required | Description |
|---|---|---|---|
| `employeeCode` | string | Yes | Unique employee identifier (max 50 chars). |
| `firstName` | string | Yes | Employee first name (max 100 chars). Ignored if employee already exists. |
| `lastName` | string | Yes | Employee last name (max 100 chars). Ignored if employee already exists. |
| `faceImagesBase64` | string[] | Yes | One or more base64-encoded face images (different angles recommended). Empty/whitespace entries are skipped. |

```json
{
  "employeeCode": "EMP001",
  "firstName": "Jane",
  "lastName": "Doe",
  "faceImagesBase64": [
    "data:image/jpeg;base64,/9j/4AAQSkZJRg...",
    "/9j/4AAQSkZJRg..."
  ]
}
```

### Responses

**200 OK** — registration succeeded:

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

**400 Bad Request** — validation failure. Possible messages:

| Condition | `message` |
|---|---|
| Missing employee code | `Employee Code is required.` |
| Missing name | `First Name and Last Name are required.` |
| Empty image list | `At least one face image must be provided.` |
| Malformed base64 | `Image at index {i} is not valid base64.` |
| Bytes are not an image | `Image at index {i} is not a valid or supported image format.` |
| All entries blank | `No usable face images were provided.` |

**500 Internal Server Error** — unexpected failure (database down, model error):

```json
{ "message": "An error occurred during registration.", "error": "..." }
```

---

## POST /api/checkin

Verifies a check-in face image against the registered embeddings of the given employee. The embedding is extracted from the submitted image and the **highest cosine similarity** against all registered angles is computed inside PostgreSQL (pgvector `<=>` operator). Every attempt — matched or not — is written to the `checkinlogs` audit table.

The check-in is considered successful when `similarity >= FaceModel:Threshold` (default **0.70**, configurable in `appsettings.json`).

### Request body

| Field | Type | Required | Description |
|---|---|---|---|
| `employeeCode` | string | Yes | Employee identifier (case-insensitive). |
| `faceImageBase64` | string | Yes | Base64-encoded check-in image. |

```json
{
  "employeeCode": "EMP001",
  "faceImageBase64": "data:image/jpeg;base64,/9j/4AAQSkZJRg..."
}
```

### Responses

**200 OK** — verification performed (both match and non-match return 200; check `success`):

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

| Field | Description |
|---|---|
| `success` | `true` when similarity ≥ threshold. |
| `similarity` | Best cosine similarity across registered angles, clamped to `[0, 1]`. |
| `similarity_percentage` | `similarity × 100`, rounded to 2 decimals. |
| `threshold` | Threshold used for the decision. |
| `status` | `SUCCESS` or `FAILED_MATCH_UNDER_THRESHOLD` (also stored in the audit log). |

**400 Bad Request**

| Condition | `message` |
|---|---|
| Missing employee code | `Employee Code is required.` |
| Missing image | `Face check-in image is required.` |
| Malformed base64 | `Face check-in image is not valid base64.` |
| Bytes are not an image | `Face check-in image is not a valid or supported image format.` |
| Employee has no enrolled faces | `Employee exists, but has no registered face embeddings. Please register faces first.` |

**404 Not Found** — unknown employee code:

```json
{ "message": "Employee with code 'EMP999' not found." }
```

**500 Internal Server Error**

```json
{ "message": "An error occurred during check-in processing.", "error": "..." }
```

---

## GET /api/employees

Lists all registered employees with the number of enrolled face angles. Intended as a helper for frontend UIs.

### Responses

**200 OK**

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

## Configuration reference

| Setting | Default | Description |
|---|---|---|
| `FaceModel:Path` | `ml-models/arc.onnx` | Path to the ONNX embedding model. |
| `FaceModel:Threshold` | `0.70` | Cosine-similarity threshold for a successful check-in. |
| `ConnectionStrings:DefaultConnection` | — | PostgreSQL connection string (pgvector extension required). |

## Notes & limitations

- No face **detection/alignment** is performed — the full image is resized and embedded, so tightly cropped face images give the best accuracy.
- No liveness detection; a photo of a photo will match.
- No authentication or rate limiting — POC only.
- Very large base64 payloads are limited by the default Kestrel body size limit (~30 MB).
