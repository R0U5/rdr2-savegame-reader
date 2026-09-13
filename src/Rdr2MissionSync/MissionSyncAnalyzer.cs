using System.Buffers.Binary;
using System.Security.Cryptography;
using Rdr2SaveResearch.Persistence;

namespace Rdr2MissionSync;

/// <summary>
/// Reads the host's current story-mode mission from a PC SRDR save and reduces
/// it to a <see cref="MissionFingerprint"/>. The analysis is intentionally
/// read-only and defensive: any save that cannot be decoded or does not expose
/// a mission record yields <c>null</c> instead of throwing.
/// </summary>
public static class MissionSyncAnalyzer
{
    private static readonly uint MissionFieldHash = RageJoaat.Compute("mission");
    private static readonly uint CompletedFieldHash = RageJoaat.Compute("completed");
    private static readonly uint ProgressFieldHash = RageJoaat.Compute("progress");

    /// <summary>
    /// Analyzes an encrypted PC SRDR save and returns the fingerprint of the
    /// first mission record that is not marked completed, or <c>null</c> when
    /// the save is corrupt, undecodable, or contains no in-progress mission.
    /// </summary>
    public static MissionFingerprint? Analyze(byte[] saveBytes)
    {
        ArgumentNullException.ThrowIfNull(saveBytes);

        Rdr2PcSaveDocument document;
        try
        {
            document = Rdr2PcSaveCodec.Decode(saveBytes);
        }
        catch (Rdr2PcSaveCodecException)
        {
            return null;
        }

        Rdr2RsavContentReport report;
        try
        {
            report = Rdr2RsavContentAnalyzer.Analyze(document);
        }
        catch (Rdr2RsavContentException)
        {
            return null;
        }

        var active = FindActiveMission(report);
        if (active is null)
        {
            return null;
        }

        return new MissionFingerprint(
            active.Value.MissionId,
            active.Value.ProgressStage,
            Convert.ToHexString(SHA256.HashData(saveBytes)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Collects every mission record across all PSO frames and returns the
    /// first record that is not completed, ordered by region then record
    /// index. A save whose records are all completed has no active mission.
    /// </summary>
    private static MissionCandidate? FindActiveMission(Rdr2RsavContentReport report)
    {
        var candidates = new List<MissionCandidate>();
        foreach (var frame in report.PsoFrames)
        {
            if (frame.MissionRecords is null)
            {
                continue;
            }
            foreach (var record in frame.MissionRecords)
            {
                if (InterpretRecord(record) is { } candidate)
                {
                    candidates.Add(candidate with { RegionIndex = frame.RegionIndex });
                }
            }
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        foreach (var candidate in candidates
                     .OrderBy(static value => value.RegionIndex)
                     .ThenBy(static value => value.RecordIndex))
        {
            if (!candidate.Completed)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Interprets one located mission record. A record without a mission field
    /// is not a mission record and is skipped. The mission id is read as a
    /// big-endian 32-bit value, matching the byte order used by the PSO schema
    /// analyzer for every other multi-byte field.
    /// </summary>
    private static MissionCandidate? InterpretRecord(Rdr2PsoMissionRecord record)
    {
        uint? missionId = null;
        var completed = false;
        var progress = 0u;
        foreach (var field in record.Fields)
        {
            if (field.FieldHash == MissionFieldHash)
            {
                missionId = ReadUInt32BigEndian(field.ValueHex);
            }
            else if (field.FieldHash == CompletedFieldHash)
            {
                completed = ReadFirstByte(field.ValueHex) != 0;
            }
            else if (field.FieldHash == ProgressFieldHash)
            {
                progress = ReadUInt16BigEndian(field.ValueHex);
            }
        }
        if (missionId is null)
        {
            return null;
        }
        return new MissionCandidate(
            0,
            record.RecordIndex,
            missionId.Value,
            completed,
            progress);
    }

    private static uint ReadUInt32BigEndian(string valueHex)
    {
        var bytes = Convert.FromHexString(valueHex);
        if (bytes.Length == 0)
        {
            return 0;
        }
        if (bytes.Length >= 4)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        }
        if (bytes.Length >= 2)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }
        return bytes[0];
    }

    private static uint ReadUInt16BigEndian(string valueHex)
    {
        var bytes = Convert.FromHexString(valueHex);
        if (bytes.Length == 0)
        {
            return 0;
        }
        if (bytes.Length >= 2)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2));
        }
        return bytes[0];
    }

    private static byte ReadFirstByte(string valueHex)
    {
        var bytes = Convert.FromHexString(valueHex);
        return bytes.Length == 0 ? (byte)0 : bytes[0];
    }

    private readonly record struct MissionCandidate(
        int RegionIndex,
        int RecordIndex,
        uint MissionId,
        bool Completed,
        uint ProgressStage);
}