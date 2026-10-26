using Microsoft.EntityFrameworkCore;
using SmartFactory.Services.TelemetryService.Domain.Entities;

namespace SmartFactory.Services.TelemetryService.Infrastructure.Data
{
    public class TelemetryDbContext : DbContext
    {
        public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : base(options) { }
        public DbSet<TelemetryRecord> TelemetryRecords { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var telemetry = modelBuilder.Entity<TelemetryRecord>();
            telemetry.HasIndex(record => record.IngestionId).IsUnique();
            telemetry.Property(record => record.DeliveryErrorCode).HasMaxLength(128);
            telemetry.Property(record => record.SensorType).HasMaxLength(64);
            telemetry.Property(record => record.SensorStatus).HasMaxLength(16);
        }
    }
}
