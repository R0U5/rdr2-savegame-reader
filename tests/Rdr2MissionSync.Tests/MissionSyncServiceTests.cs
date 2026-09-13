using Xunit;

namespace Rdr2MissionSync.Tests;

public sealed class MissionSyncServiceTests
{
    [Fact]
    public void Analyze_CachesFingerprintBySaveHash()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: false));
        var service = new MissionSyncService();

        var first = service.Analyze(save);
        var second = service.Analyze(save);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, service.CachedSaveCount);
    }

    [Fact]
    public void Analyze_SeventeenDistinctSaves_EvictsOldest()
    {
        var service = new MissionSyncService();
        for (var index = 1; index <= 17; index++)
        {
            var save = SyntheticSaveBuilder.BuildEncryptedSave(
                new MissionRecordSpec(MissionId: (uint)index));
            Assert.NotNull(service.Analyze(save));
        }

        Assert.Equal(MissionSyncService.CacheCapacity, service.CachedSaveCount);
    }

    [Fact]
    public void Analyze_RepeatedSave_DoesNotGrowCache()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1));
        var service = new MissionSyncService();

        for (var index = 0; index < 20; index++)
        {
            Assert.NotNull(service.Analyze(save));
        }

        Assert.Equal(1, service.CachedSaveCount);
    }

    [Fact]
    public void Analyze_UndeterminedSave_NotCached()
    {
        var emptySave = SyntheticSaveBuilder.BuildEncryptedSave();
        var service = new MissionSyncService();

        Assert.Null(service.Analyze(emptySave));
        Assert.Equal(0, service.CachedSaveCount);
    }

    [Fact]
    public void Analyze_NullInput_ThrowsArgumentNullException()
    {
        var service = new MissionSyncService();

        Assert.Throws<ArgumentNullException>(() => service.Analyze(null!));
    }

    [Fact]
    public void UpdateAfterMissionComplete_WithSaveReader_ReanalyzesFreshSave()
    {
        var initialSave = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true),
            new MissionRecordSpec(MissionId: 2, Completed: false));
        var nextSave = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true),
            new MissionRecordSpec(MissionId: 2, Completed: true),
            new MissionRecordSpec(MissionId: 3, Completed: false));
        var service = new MissionSyncService(() => nextSave);

        var initial = service.Analyze(initialSave);
        Assert.NotNull(initial);
        Assert.Equal(2u, initial.Value.MissionId);

        var next = service.UpdateAfterMissionComplete(2);

        Assert.NotNull(next);
        Assert.Equal(3u, next.Value.MissionId);
    }

    [Fact]
    public void UpdateAfterMissionComplete_WithoutSaveReader_UsesLastSave()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true),
            new MissionRecordSpec(MissionId: 2, Completed: false));
        var service = new MissionSyncService();

        var initial = service.Analyze(save);
        Assert.NotNull(initial);

        var next = service.UpdateAfterMissionComplete(2);

        Assert.NotNull(next);
        Assert.Equal(2u, next.Value.MissionId);
    }

    [Fact]
    public void UpdateAfterMissionComplete_MissionIdMismatch_Throws()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 2, Completed: false));
        var service = new MissionSyncService();
        service.Analyze(save);

        var exception = Assert.Throws<InvalidOperationException>(
            () => service.UpdateAfterMissionComplete(99));

        Assert.Contains("99", exception.Message);
        Assert.Contains("2", exception.Message);
    }

    [Fact]
    public void UpdateAfterMissionComplete_NoPriorAnalysis_ReturnsNull()
    {
        var service = new MissionSyncService();

        Assert.Null(service.UpdateAfterMissionComplete(1));
    }

    [Fact]
    public void UpdateAfterMissionComplete_AfterUndeterminedSave_KeepsLastKnown()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 2, Completed: false));
        var emptySave = SyntheticSaveBuilder.BuildEncryptedSave();
        var service = new MissionSyncService();

        var initial = service.Analyze(save);
        Assert.NotNull(initial);
        Assert.Null(service.Analyze(emptySave));

        var next = service.UpdateAfterMissionComplete(2);

        Assert.NotNull(next);
        Assert.Equal(2u, next.Value.MissionId);
    }

    [Fact]
    public void UpdateAfterMissionComplete_NewSaveAllCompleted_ReturnsNull()
    {
        var initialSave = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 2, Completed: false));
        var nextSave = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 2, Completed: true));
        var service = new MissionSyncService(() => nextSave);

        var initial = service.Analyze(initialSave);
        Assert.NotNull(initial);

        Assert.Null(service.UpdateAfterMissionComplete(2));
    }
}