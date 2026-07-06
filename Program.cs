using Microsoft.EntityFrameworkCore;
using FaceAttendance.Api.Data;
using FaceAttendance.Api.Services;

namespace FaceAttendance.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Allow CORS for Next.js frontend calls (locally from localhost:3000/any port)
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // Add services to the container.
        builder.Services.AddControllers();
        builder.Services.AddAuthorization();

        // Register Entity Framework DbContext with PostgreSQL and pgvector mappings
        string connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.UseVector()));

        // Register custom services
        builder.Services.AddSingleton<IFaceEmbeddingService, FaceEmbeddingService>();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        app.UseCors("AllowFrontend");
        app.UseAuthorization();
        app.MapControllers();

        // Run application
        app.Run();
    }
}
