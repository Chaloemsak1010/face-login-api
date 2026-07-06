-- ==========================================
-- HR Face Recognition Database Schema (3NF)
-- Optimized for CPU-based verification
-- ==========================================

-- ------------------------------------------
-- 1. Employees Table
-- Master data for employees.
-- ------------------------------------------
CREATE TABLE Employees (
    id SERIAL PRIMARY KEY,
    employee_code VARCHAR(50) NOT NULL UNIQUE,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Index for fast lookup when doing check-ins by employee code
CREATE INDEX idx_employees_employee_code ON Employees(employee_code);

-- ------------------------------------------
-- 2. EmployeeFaceEmbeddings Table
-- Stores the high-dimensional vector representation of the employee's face.
-- Features:
-- - 1-to-Many relationship with Employees.
-- - Stores embedding_data (as TEXT or JSON array by default, or vector type if using pgvector).
-- - Stores the registered raw image path.
-- ------------------------------------------
CREATE TABLE EmployeeFaceEmbeddings (
    id SERIAL PRIMARY KEY,
    employee_id INTEGER NOT NULL REFERENCES Employees(id) ON DELETE CASCADE,
    embedding_data vector(512) NOT NULL, -- Stored using pgvector extension for 512 dimensions
    image_path VARCHAR(500) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Index to quickly look up all face embeddings for a given employee
CREATE INDEX idx_face_embeddings_employee_id ON EmployeeFaceEmbeddings(employee_id);

-- ------------------------------------------
-- 3. CheckInLogs Table
-- Log attendance/check-in requests.
-- ------------------------------------------
CREATE TABLE CheckInLogs (
    id SERIAL PRIMARY KEY,
    employee_id INTEGER NOT NULL REFERENCES Employees(id) ON DELETE CASCADE,
    check_in_time TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    similarity_score REAL NOT NULL,
    status VARCHAR(50) NOT NULL, -- e.g., 'SUCCESS', 'FAILED_SCORE_TOO_LOW', 'MANUAL_VERIFICATION_REQUIRED'
    device_info VARCHAR(200) NULL
);

-- Index for fetching attendance history
CREATE INDEX idx_checkin_logs_employee_id ON CheckInLogs(employee_id);
CREATE INDEX idx_checkin_logs_time ON CheckInLogs(check_in_time);


-- =========================================================================
-- BONUS: PostgreSQL with pgvector extension configuration
-- =========================================================================
/*
-- Step 1: Enable the pgvector extension (requires pgvector installed on Postgres server)
CREATE EXTENSION IF NOT EXISTS vector;

-- Step 2: Define EmployeeFaceEmbeddings with the vector type
-- Change embedding_data to:
-- embedding_data vector(128) -- 128 dimensions (e.g. MobileFaceNet / FaceNet)
-- OR
-- embedding_data vector(512) -- 512 dimensions (e.g. ArcFace / ResNet)

CREATE TABLE EmployeeFaceEmbeddingsPgVector (
    id SERIAL PRIMARY KEY,
    employee_id INTEGER NOT NULL REFERENCES Employees(id) ON DELETE CASCADE,
    embedding_data vector(128) NOT NULL, -- pgvector data type
    image_path VARCHAR(500) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Step 3: Fast cosine similarity searches using pgvector
-- cosine distance is represented by the `<=>` operator.
-- Cosine Similarity = 1 - Cosine Distance.
-- To search for the closest matching face embedding:

SELECT 
    e.id AS employee_id, 
    e.employee_code, 
    e.first_name, 
    e.last_name,
    (1 - (efe.embedding_data <=> '[0.012, -0.045, 0.981, ... (128 floats) ...]'::vector)) AS similarity_score
FROM EmployeeFaceEmbeddingsPgVector efe
JOIN Employees e ON efe.employee_id = e.id
WHERE e.employee_code = 'EMP001'
ORDER BY efe.embedding_data <=> '[0.012, -0.045, 0.981, ... (128 floats) ...]'::vector ASC
LIMIT 1;

-- Step 4: Indexing for high performance vector searches (HNSW or IVFFlat)
-- HNSW is generally faster and more accurate for cosine distance:
CREATE INDEX ON EmployeeFaceEmbeddingsPgVector USING hnsw (embedding_data vector_cosine_ops);
*/
