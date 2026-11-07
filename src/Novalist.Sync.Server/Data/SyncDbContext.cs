using Microsoft.EntityFrameworkCore;

namespace Novalist.Sync.Server.Data;

public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<StoredUpdate> Updates => Set<StoredUpdate>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<FieldLease> FieldLeases => Set<FieldLease>();
    public DbSet<FieldLeaseAttempt> FieldLeaseAttempts => Set<FieldLeaseAttempt>();
    public DbSet<ProjectDeviceAuthorization> ProjectDeviceAuthorizations => Set<ProjectDeviceAuthorization>();
    public DbSet<ProjectEpochStage> ProjectEpochStages => Set<ProjectEpochStage>();
    public DbSet<ProjectEpochEnvelope> ProjectEpochEnvelopes => Set<ProjectEpochEnvelope>();
    public DbSet<ProjectEpochKeyGrant> ProjectEpochKeyGrants => Set<ProjectEpochKeyGrant>();
    public DbSet<ProjectEpochDeviceGrant> ProjectEpochDeviceGrants => Set<ProjectEpochDeviceGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEpochDeviceGrant>(entry =>
        {
            entry.HasKey(value => new { value.RotationId, value.DeviceId });
            entry.HasOne<ProjectEpochStage>().WithMany().HasForeignKey(value => value.RotationId);
        });
        modelBuilder.Entity<ProjectDeviceAuthorization>(entry =>
        {
            entry.HasKey(value => new { value.ProjectId, value.DeviceId });
            entry.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId);
            entry.HasOne<Device>().WithMany().HasForeignKey(value => value.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProjectEpochStage>(entry =>
        {
            entry.HasIndex(value => value.CreatedAt);
            entry.Property(value => value.Id).ValueGeneratedNever();
            entry.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId);
        });
        modelBuilder.Entity<ProjectEpochEnvelope>(entry =>
        {
            entry.HasKey(value => new { value.RotationId, value.Index });
            entry.HasIndex(value => new { value.RotationId, value.SenderSeq }).IsUnique();
            entry.HasOne<ProjectEpochStage>().WithMany().HasForeignKey(value => value.RotationId);
        });
        modelBuilder.Entity<ProjectEpochKeyGrant>(entry =>
        {
            entry.HasKey(value => new { value.ProjectId, value.DeviceId, value.Epoch });
            entry.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId);
        });
        ConfigureFieldLeases(modelBuilder);
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Id).ValueGeneratedNever();
            user.HasIndex(u => new { u.Issuer, u.Subject }).IsUnique();
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasOne<User>().WithMany().HasForeignKey(d => d.UserId);
            device.HasOne<Device>().WithMany().HasForeignKey(d => d.LinkedByDeviceId).OnDelete(DeleteBehavior.Restrict);
            device.HasIndex(d => d.SigningPublicKey).IsUnique();
            device.HasIndex(d => d.ReceivingPublicKey).IsUnique();
        });

        modelBuilder.Entity<Project>(project =>
        {
            project.Property(p => p.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.HasKey(m => new { m.ProjectId, m.UserId });
            membership.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId);
            membership.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
        });

        modelBuilder.Entity<StoredUpdate>(update =>
        {
            update.ToTable("updates");
            update.HasKey(u => new { u.ProjectId, u.ServerSeq });
            update.HasIndex(u => new { u.ProjectId, u.SenderDeviceId, u.SenderSeq }).IsUnique();
            update.HasOne<Project>().WithMany().HasForeignKey(u => u.ProjectId);
            update.HasOne<Device>().WithMany().HasForeignKey(u => u.SenderDeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.Property(i => i.Id).ValueGeneratedNever();
            invitation.HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId);
            invitation.HasOne<User>().WithMany().HasForeignKey(i => i.RedeemedBy)
                .OnDelete(DeleteBehavior.Restrict);
            invitation.HasOne<Device>().WithMany().HasForeignKey(i => i.ActivatedDeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureFieldLeases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FieldLeaseAttempt>(attempt =>
        {
            attempt.HasKey(value => new { value.ProjectId, value.DeviceId, value.LeaseId });
            attempt.HasIndex(value => value.ExpiresAt);
            attempt.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId);
            attempt.HasOne<Device>().WithMany().HasForeignKey(value => value.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FieldLease>(lease =>
        {
            lease.HasKey(value => new { value.ProjectId, value.ResourceId });
            lease.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId);
            lease.HasOne<Device>().WithMany().HasForeignKey(value => value.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
