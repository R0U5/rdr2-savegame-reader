using Rdr2SaveResearch.Persistence;
using Xunit;

namespace Rdr2MissionSync.Tests;

public sealed class MissionSyncAnalyzerTests
{
    [Fact]
    public void Analyze_ValidSaveWithActiveMission_ReturnsFingerprint()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true, Progress: 100),
            new MissionRecordSpec(MissionId: 2, Completed: false, Progress: 25));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(2u, fingerprint.Value.MissionId);
        Assert.Equal(25u, fingerprint.Value.ProgressStage);
        Assert.Equal(64, fingerprint.Value.SaveHash.Length);
        Assert.True(fingerprint.Value.Timestamp > 0);
    }

    [Fact]
    public void Analyze_FirstInProgressRecordWins()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: false),
            new MissionRecordSpec(MissionId: 2, Completed: false),
            new MissionRecordSpec(MissionId: 3, Completed: false));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(1u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_AllCompleted_ReturnsNull()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true),
            new MissionRecordSpec(MissionId: 2, Completed: true));

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_CompletedRecordWithProgress_IsStillSkipped()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true, Progress: 100),
            new MissionRecordSpec(MissionId: 2, Completed: false, Progress: 5));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(2u, fingerprint.Value.MissionId);
        Assert.Equal(5u, fingerprint.Value.ProgressStage);
    }

    [Fact]
    public void Analyze_NoMissionRecords_ReturnsNull()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave();

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_EmptySave_ReturnsNull()
    {
        var decoded = new byte[0x120];
        decoded[3] = 4;
        "RSAV"u8.CopyTo(decoded.AsSpan(Rdr2PcSaveCodec.EncryptedPayloadOffset));
        var save = SyntheticSaveBuilder.Encrypt(decoded);

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MissionSyncAnalyzer.Analyze(null!));
    }

    [Fact]
    public void Analyze_EmptyBytes_ReturnsNull()
    {
        Assert.Null(MissionSyncAnalyzer.Analyze(Array.Empty<byte>()));
    }

    [Fact]
    public void Analyze_TruncatedEnvelope_ReturnsNull()
    {
        var save = new byte[0x100];
        save[3] = 4;

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_NotRSAVPayload_ReturnsNull()
    {
        var decoded = new byte[0x120];
        decoded[3] = 4;
        var save = SyntheticSaveBuilder.Encrypt(decoded);

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_DifferentSaves_DifferentFingerprints()
    {
        var first = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: false));
        var second = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 2, Completed: false));

        var firstFingerprint = MissionSyncAnalyzer.Analyze(first);
        var secondFingerprint = MissionSyncAnalyzer.Analyze(second);

        Assert.NotNull(firstFingerprint);
        Assert.NotNull(secondFingerprint);
        Assert.NotEqual(firstFingerprint.Value.SaveHash, secondFingerprint.Value.SaveHash);
        Assert.NotEqual(firstFingerprint.Value.MissionId, secondFingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_SameSave_StableFingerprint()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 7, Completed: false, Progress: 42));

        var first = MissionSyncAnalyzer.Analyze(save);
        var second = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Value.MissionId, second.Value.MissionId);
        Assert.Equal(first.Value.ProgressStage, second.Value.ProgressStage);
        Assert.Equal(first.Value.SaveHash, second.Value.SaveHash);
    }

    [Fact]
    public void Analyze_RecordWithoutMissionField_IsSkipped()
    {
        var decoded = SyntheticSaveBuilder.BuildDecodedSave(
            new MissionRecordSpec(MissionId: 1));
        // Remove the mission field from the record schema so the record has no
        // mission id and must be skipped by the analyzer.
        var offset = SyntheticSaveBuilder.GetMissionFieldHashOffset(1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            decoded.AsSpan(offset, 4), 0xDEADBEEF);
        var save = SyntheticSaveBuilder.Encrypt(decoded);

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_RecordWithoutCompletedField_DefaultsToInProgress()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, IncludeCompletedField: false));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(1u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_RecordWithoutProgressField_ProgressZero()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, IncludeProgressField: false));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(0u, fingerprint.Value.ProgressStage);
    }

    [Fact]
    public void Analyze_RecordWithoutFud1Script_IsSkipped()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, IncludeFud1Script: false));

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_ProgressField_ReadsBigEndian()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Progress: 0x0102));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(0x0102u, fingerprint.Value.ProgressStage);
    }

    [Fact]
    public void Analyze_MissionId_ReadsBigEndian()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 0x01020304));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(0x01020304u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_ZeroMissionId_ReturnsFingerprint()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 0));

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(0u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_MultipleRegions_OrdersByRegionThenRecord()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSaveWithRegions(
            [new MissionRecordSpec(MissionId: 1, Completed: true)],
            [new MissionRecordSpec(MissionId: 2, Completed: false)]);

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(2u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_MultipleRegions_ActiveInEarlierRegionWins()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSaveWithRegions(
            [new MissionRecordSpec(MissionId: 1, Completed: false)],
            [new MissionRecordSpec(MissionId: 2, Completed: false)]);

        var fingerprint = MissionSyncAnalyzer.Analyze(save);

        Assert.NotNull(fingerprint);
        Assert.Equal(1u, fingerprint.Value.MissionId);
    }

    [Fact]
    public void Analyze_CountCapacityMismatch_ReturnsNull()
    {
        var decoded = SyntheticSaveBuilder.BuildDecodedSave(
            new MissionRecordSpec(MissionId: 1));
        // Owner block: count at dataStart+8, capacity at dataStart+10.
        // dataStart = 0x110 + 4, PSIN header is 8 bytes, so capacity is at
        // 0x11E..0x120. Zero the low byte to make capacity 0 while count is 1.
        decoded[0x11F] = 0;
        var save = SyntheticSaveBuilder.Encrypt(decoded);

        Assert.Null(MissionSyncAnalyzer.Analyze(save));
    }

    [Fact]
    public void Analyze_SyntheticSave_RoundTripsExactly()
    {
        var save = SyntheticSaveBuilder.BuildEncryptedSave(
            new MissionRecordSpec(MissionId: 1, Completed: true, Progress: 100),
            new MissionRecordSpec(MissionId: 2, Completed: false, Progress: 25));

        var verification = Rdr2PcSaveCodec.VerifyRoundTrip(save);

        Assert.True(verification.IsExactRoundTrip);
        Assert.Equal(1, verification.CheckCount);
    }
}