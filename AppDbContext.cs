using Microsoft.EntityFrameworkCore;
using FaceAttendance.Api.Models;

namespace FaceAttendance.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeFaceEmbedding> FaceEmbeddings => Set<EmployeeFaceEmbedding>();
    public DbSet<CheckInLog> CheckInLogs => Set<CheckInLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Load the pgvector extension on the DbContext builder
        modelBuilder.HasPostgresExtension("vector");

        // Map Table structures according to 3NF schema specifications
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("employees");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(e => e.EmployeeCode).HasColumnName("employee_code").HasMaxLength(50).IsRequired();
            entity.Property(e => e.FirstName).HasColumnName("first_name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasColumnName("last_name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.EmployeeCode).IsUnique();
        });

        modelBuilder.Entity<EmployeeFaceEmbedding>(entity =>
        {
            entity.ToTable("employeefaceembeddings");
            entity.HasKey(fe => fe.Id);
            entity.Property(fe => fe.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(fe => fe.EmployeeId).HasColumnName("employee_id").IsRequired();
            
            // Map pgvector type using the extension properties
            entity.Property(fe => fe.EmbeddingData)
                  .HasColumnName("embedding_data")
                  .HasColumnType("vector(512)") // Specify ArcFace dimensions (512)
                  .IsRequired();

            entity.Property(fe => fe.ImagePath).HasColumnName("image_path").HasMaxLength(500).IsRequired();
            entity.Property(fe => fe.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Define 1-to-Many relationship with Cascade Delete on Employee removal
            entity.HasOne(fe => fe.Employee)
                  .WithMany(e => e.FaceEmbeddings)
                  .HasForeignKey(fe => fe.EmployeeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CheckInLog>(entity =>
        {
            entity.ToTable("checkinlogs");
            entity.HasKey(log => log.Id);
            entity.Property(log => log.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(log => log.EmployeeId).HasColumnName("employee_id").IsRequired();
            entity.Property(log => log.CheckInTime).HasColumnName("check_in_time").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(log => log.SimilarityScore).HasColumnName("similarity_score").IsRequired();
            entity.Property(log => log.Status).HasColumnName("status").HasMaxLength(50).IsRequired();

            entity.HasOne(log => log.Employee)
                  .WithMany()
                  .HasForeignKey(log => log.EmployeeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
