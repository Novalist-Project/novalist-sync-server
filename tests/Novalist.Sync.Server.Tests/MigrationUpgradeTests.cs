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
            Assert.Equal(1, await db.Projects.Select(value => value.HistoryStartsAt).SingleAsync(ct));
            Assert.Empty(await db.ProjectHistoryBoundaries.ToListAsync(ct));
            Assert.Empty(await db.UpdateReceipts.ToListAsync(ct));
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
            Assert.Empty(await db.UpdateReceipts.ToListAsync(ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EpochCompactionBackfillsHistoryForProjectsThatAlreadyRotated()
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
            await db.GetService<IMigrator>().MigrateAsync("20261024173741_FieldLeases", ct);
            var rotated = Guid.NewGuid();
            var untouched = Guid.NewGuid();
            var device = Guid.NewGuid();
            var other = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects (id, current_epoch, epoch_started_at, last_server_seq, created_at)
                    VALUES ({rotated}, 2, {now}, 20, {now}), ({untouched}, 0, {now}, 3, {now});
                INSERT INTO project_epoch_key_grants (project_id, device_id, epoch, first_server_seq, grantor_device_id, payload)
                    VALUES ({rotated}, {device}, 1, 6, {device}, '\x01'::bytea),
                           ({rotated}, {other}, 1, 6, {device}, '\x01'::bytea),
                           ({rotated}, {device}, 2, 15, {device}, '\x01'::bytea);
                """, ct);

            await db.Database.MigrateAsync(ct);

            Assert.Equal(15, await db.Projects.Where(value => value.Id == rotated).Select(value => value.HistoryStartsAt).SingleAsync(ct));
            Assert.Equal(1, await db.Projects.Where(value => value.Id == untouched).Select(value => value.HistoryStartsAt).SingleAsync(ct));
            Assert.Equal(new[] { 6L, 15L }, await db.ProjectHistoryBoundaries.Where(value => value.ProjectId == rotated)
                .OrderBy(value => value.FirstServerSeq).Select(value => value.FirstServerSeq).ToListAsync(ct));
            Assert.False(await db.ProjectHistoryBoundaries.AnyAsync(value => value.ProjectId == untouched, ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}
