using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Npgsql;

namespace Novalist.Sync.Server.Tests;

public sealed class MigrationUpgradeTests(DatabaseFixture database)
{
    [Fact]
    public async Task CollaborationMigrationPreservesPopulatedReleasedSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = $"novalist_upgrade_test_{Guid.NewGuid():N}"
        };
        await using var db = new SyncDbContext(new DbContextOptionsBuilder<SyncDbContext>()
            .UseNpgsql(connection.ConnectionString).UseSnakeCaseNamingConvention().Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261019010739_InitialCreate", ct);
            var user = Guid.NewGuid();
            var device = Guid.NewGuid();
            var project = Guid.NewGuid();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var envelope = TestData.Envelope(project, device, 7);
            var header = EnvelopeHeader.Parse(envelope.Header);
            var update = new StoredUpdate
            {
                ProjectId = project,
                ServerSeq = 1,
                ItemId = header.ItemId,
                Epoch = header.Epoch,
                SenderDeviceId = device,
                SenderSeq = (long)header.SenderSeq,
                Header = envelope.Header,
                Nonce = envelope.Nonce,
                Ciphertext = envelope.Ciphertext,
                Signature = envelope.Signature,
                ReceivedAt = now
            };
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users (id, issuer, subject, created_at) VALUES ({user}, 'https://migration.test', 'writer', {now});
                INSERT INTO devices (id, user_id, created_at) VALUES ({device}, {user}, {now});
                INSERT INTO projects (id, current_epoch, epoch_started_at, last_server_seq, created_at)
                    VALUES ({project}, 0, {now}, 1, {now});
                INSERT INTO memberships (project_id, user_id, role) VALUES ({project}, {user}, 1);
                INSERT INTO updates (project_id, server_seq, item_id, epoch, sender_device_id, sender_seq,
                    header, nonce, ciphertext, signature, received_at)
                    VALUES ({project}, {update.ServerSeq}, {update.ItemId}, {update.Epoch}, {device}, {update.SenderSeq},
                        {update.Header}, {update.Nonce}, {update.Ciphertext}, {update.Signature}, {now});
                """, ct);

            await db.Database.MigrateAsync(ct);

            Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync(ct)).Count());
            var restored = await db.Devices.SingleAsync(ct);
            Assert.Equal(device, restored.Id);
            Assert.Equal(user, restored.UserId);
            Assert.Null(restored.SigningPublicKey);
            Assert.Equal(MembershipRole.Owner, (await db.Memberships.SingleAsync(ct)).Role);
            Assert.Equal(project, (await db.Projects.SingleAsync(ct)).Id);
            Assert.Equal(1, await db.Projects.Select(value => value.LastServerSeq).SingleAsync(ct));
            Assert.Equivalent(update, await db.Updates.AsNoTracking().SingleAsync(ct), strict: true);
            var lease = Guid.NewGuid();
            db.FieldLeaseAttempts.Add(new()
            {
                ProjectId = project,
                DeviceId = device,
                LeaseId = lease,
                ExpiresAt = now.AddMinutes(1)
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            Assert.Equal(device, (await db.FieldLeaseAttempts.SingleAsync(ct)).DeviceId);

            await db.GetService<IMigrator>().MigrateAsync("20261019010739_InitialCreate", ct);
            Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM devices").SingleAsync(ct));
            Assert.Equal(user, await db.Users.Select(value => value.Id).SingleAsync(ct));
            Assert.Equal(MembershipRole.Owner, (await db.Memberships.AsNoTracking().SingleAsync(ct)).Role);
            Assert.Equal(user, (await db.Memberships.AsNoTracking().SingleAsync(ct)).UserId);
            Assert.Equal(1, await db.Projects.Select(value => value.LastServerSeq).SingleAsync(ct));
            Assert.Equivalent(update, await db.Updates.AsNoTracking().SingleAsync(ct), strict: true);
            await db.Database.MigrateAsync(ct);
            Assert.Equal(device, (await db.Devices.SingleAsync(ct)).Id);
            Assert.Equal(user, await db.Users.Select(value => value.Id).SingleAsync(ct));
            Assert.Equal(MembershipRole.Owner, (await db.Memberships.AsNoTracking().SingleAsync(ct)).Role);
            Assert.Equal(1, await db.Projects.Select(value => value.LastServerSeq).SingleAsync(ct));
            Assert.Equivalent(update, await db.Updates.AsNoTracking().SingleAsync(ct), strict: true);
            Assert.Empty(await db.FieldLeaseAttempts.ToListAsync(ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}
