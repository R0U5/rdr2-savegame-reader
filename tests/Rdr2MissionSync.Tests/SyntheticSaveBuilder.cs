using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Rdr2SaveResearch.Persistence;

namespace Rdr2MissionSync.Tests;

/// <summary>
/// Describes one mission record to embed in a synthetic save. The record
/// layout mirrors the PSO structure the schema analyzer recognizes:
/// fud1 script hash (4), mission id (4), completed flag (1), progress (2).
/// </summary>
internal sealed record MissionRecordSpec(
    uint MissionId,
    bool Completed = false,
    uint Progress = 0,
    bool IncludeMissionField = true,
    bool IncludeCompletedField = true,
    bool IncludeProgressField = true,
    bool IncludeFud1Script = true);

/// <summary>
/// Builds synthetic PC SRDR saves that the real codec and schema analyzer can
/// decode. The save is a valid envelope (title + date + RSAV) containing one
/// or more CHKS-protected regions, each with a PSIN/PMAP/PSCH/PSIG frame that
/// declares a missions_owner structure and a mission_record structure.
/// </summary>
internal static class SyntheticSaveBuilder
{
    public const uint OwnerStructureHash = 0x8EA5C12A;        // joaat("missions_owner")
    public const uint RecordStructureHash = 0xA352665A;       // joaat("mission_record")
    public const uint MissionsFieldHash = 0xDCF649E3;         // joaat("missions")
    public const uint MissionsElementFieldHash = 0xFD93B64A;  // joaat("missions_element")
    public const uint Fud1FieldHash = 0x979E7766;             // joaat("fud1")
    public const uint MissionFieldHash = 0x8BC157A3;          // joaat("mission")
    public const uint CompletedFieldHash = 0x362E5B2F;        // joaat("completed")
    public const uint ProgressFieldHash = 0x40F57733;         // joaat("progress")

    public const int RecordLength = 16;

    private static readonly byte[] PcKey =
    [
        0x46, 0xED, 0x8D, 0x3F, 0x94, 0x35, 0xE4, 0xEC,
        0x12, 0x2C, 0xB2, 0xE2, 0xAF, 0x97, 0xC5, 0x7E,
        0x4C, 0x5A, 0x8C, 0x30, 0x92, 0xC7, 0x84, 0x4E,
        0x11, 0xC6, 0x86, 0xFF, 0x41, 0xDF, 0x41, 0x0F
    ];

    public static byte[] BuildEncryptedSave(params MissionRecordSpec[] records) =>
        Encrypt(BuildDecodedSave(records));

    public static byte[] BuildEncryptedSaveWithRegions(params MissionRecordSpec[][] regions) =>
        Encrypt(BuildDecodedSaveWithRegions(regions));

    public static byte[] BuildDecodedSave(params MissionRecordSpec[] records) =>
        BuildDecodedSaveWithRegions([records]);

    public static byte[] BuildDecodedSaveWithRegions(MissionRecordSpec[][] regions)
    {
        var frames = regions.Select(BuildFrame).ToArray();
        var payloadLength = frames.Sum(static frame => frame.Length);
        var dataLength = payloadLength + 20 * frames.Length;
        var decodedLength = Rdr2PcSaveCodec.EncryptedPayloadOffset + 4 + dataLength;
        decodedLength = (decodedLength + 15) & ~15;

        var decoded = new byte[decodedLength];
        decoded[3] = 4;
        Encoding.Unicode.GetBytes("SYNTHETIC MISSION SYNC SAVE").CopyTo(decoded, 4);
        "RSAV"u8.CopyTo(decoded.AsSpan(Rdr2PcSaveCodec.EncryptedPayloadOffset));

        var dataStart = Rdr2PcSaveCodec.EncryptedPayloadOffset + 4;
        var cursor = dataStart;
        foreach (var frame in frames)
        {
            frame.CopyTo(decoded, cursor);
            cursor += frame.Length;
            var chksOffset = cursor;
            "CHKS"u8.CopyTo(decoded.AsSpan(chksOffset));
            BinaryPrimitives.WriteUInt32BigEndian(decoded.AsSpan(chksOffset + 4, 4), 0x14);
            decoded.AsSpan(chksOffset + 8, 8).Clear();
            var regionLength = frame.Length + 20;
            var checksum = ComputeJooat(decoded.AsSpan(dataStart, regionLength));
            BinaryPrimitives.WriteUInt32BigEndian(decoded.AsSpan(chksOffset + 8, 4), (uint)regionLength);
            BinaryPrimitives.WriteUInt32BigEndian(decoded.AsSpan(chksOffset + 12, 4), checksum);
            cursor += 20;
            dataStart += regionLength;
        }

        UpdateDateChecksum(decoded);
        return decoded;
    }

    public static byte[] Encrypt(byte[] decoded)
    {
        var encrypted = (byte[])decoded.Clone();
        using var aes = Aes.Create();
        aes.Key = PcKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = aes.CreateEncryptor();
        transform.TransformFinalBlock(
            encrypted,
            Rdr2PcSaveCodec.EncryptedPayloadOffset,
            encrypted.Length - Rdr2PcSaveCodec.EncryptedPayloadOffset)
            .CopyTo(encrypted, Rdr2PcSaveCodec.EncryptedPayloadOffset);
        return encrypted;
    }

    /// <summary>
    /// Absolute offset of the mission field hash inside the PSCH record
    /// definition. Tests mutate this to remove the mission field from the
    /// schema and exercise the analyzer's skip path.
    /// </summary>
    public static int GetMissionFieldHashOffset(int recordCount)
    {
        var psinLength = 8 + 16 + recordCount * RecordLength;
        var pmapLength = 24 + 2 * 16;
        var pschHeaderAndIndex = 12 + 2 * 8;
        var ownerDefinitionSize = 12 + 2 * 12;
        var recordDefinition = pschHeaderAndIndex + ownerDefinitionSize;
        // descriptor(4) + structureLength(4) + fud1 field(12) + mission field hash.
        var missionFieldHashOffsetInPsch = recordDefinition + 24;
        return Rdr2PcSaveCodec.EncryptedPayloadOffset + 4 + psinLength + pmapLength + missionFieldHashOffsetInPsch;
    }

    private static byte[] BuildFrame(MissionRecordSpec[] records)
    {
        var psin = BuildPsin(records);
        var pmap = BuildPmap(records.Length);
        var psch = BuildPsch();
        var psig = BuildPsig();
        var frame = new byte[psin.Length + pmap.Length + psch.Length + psig.Length];
        psin.CopyTo(frame, 0);
        pmap.CopyTo(frame, psin.Length);
        psch.CopyTo(frame, psin.Length + pmap.Length);
        psig.CopyTo(frame, psin.Length + pmap.Length + psch.Length);
        return frame;
    }

    private static byte[] BuildPsin(MissionRecordSpec[] records)
    {
        var ownerBlock = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(ownerBlock.AsSpan(0, 2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ownerBlock.AsSpan(2, 2), (ushort)records.Length);

        var recordBlock = new byte[records.Length * RecordLength];
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            var offset = index * RecordLength;
            if (record.IncludeFud1Script)
            {
                BinaryPrimitives.WriteUInt32BigEndian(recordBlock.AsSpan(offset, 4), Fud1FieldHash);
            }
            if (record.IncludeMissionField)
            {
                BinaryPrimitives.WriteUInt32BigEndian(recordBlock.AsSpan(offset + 4, 4), record.MissionId);
            }
            if (record.IncludeCompletedField)
            {
                recordBlock[offset + 8] = record.Completed ? (byte)1 : (byte)0;
            }
            if (record.IncludeProgressField)
            {
                BinaryPrimitives.WriteUInt16BigEndian(recordBlock.AsSpan(offset + 9, 2), (ushort)record.Progress);
            }
        }

        var payload = new byte[ownerBlock.Length + recordBlock.Length];
        ownerBlock.CopyTo(payload, 0);
        recordBlock.CopyTo(payload, ownerBlock.Length);

        var part = new byte[8 + payload.Length];
        "PSIN"u8.CopyTo(part);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(4, 4), part.Length);
        payload.CopyTo(part, 8);
        return part;
    }

    private static byte[] BuildPmap(int recordCount)
    {
        const int count = 2;
        var part = new byte[24 + count * 16];
        "PMAP"u8.CopyTo(part);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(4, 4), part.Length);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(8, 4), 1); // rootIndex
        BinaryPrimitives.WriteUInt16BigEndian(part.AsSpan(16, 2), count);

        // Entry 1: owner block at data offset 0, length 16.
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(24, 4), OwnerStructureHash);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(28, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(36, 4), 16);

        // Entry 2: record block at data offset 16, length count * 16.
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(40, 4), RecordStructureHash);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(44, 4), 16);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(52, 4), recordCount * RecordLength);
        return part;
    }

    private static byte[] BuildPsch()
    {
        const int indexSize = 2 * 8;
        const int ownerDefinitionSize = 12 + 2 * 12;
        const int recordDefinitionSize = 12 + 4 * 12;
        var part = new byte[12 + indexSize + ownerDefinitionSize + recordDefinitionSize];
        "PSCH"u8.CopyTo(part);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(4, 4), part.Length);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(8, 4), 2);

        // Index entries: nameHash + definitionRelativeOffset. The index starts
        // after the 12-byte header, so the first definition is at offset 28.
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(12, 4), OwnerStructureHash);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(16, 4), 12 + indexSize);
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(20, 4), RecordStructureHash);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(24, 4), 12 + indexSize + ownerDefinitionSize);

        // Owner definition: descriptor(fieldCount=2, type=0) + structureLength + fields.
        var ownerDefinition = 12 + indexSize;
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(ownerDefinition, 4), 2);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(ownerDefinition + 4, 4), 16);
        WriteField(part, ownerDefinition + 12, MissionsFieldHash, 0x0D, 0, 0, 1);
        WriteField(part, ownerDefinition + 24, MissionsElementFieldHash, 0x0C, 0, 8, RecordStructureHash);

        // Record definition: descriptor(fieldCount=4, type=0) + structureLength + fields.
        var recordDefinition = 12 + indexSize + ownerDefinitionSize;
        BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(recordDefinition, 4), 4);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(recordDefinition + 4, 4), RecordLength);
        WriteField(part, recordDefinition + 12, Fud1FieldHash, 0x0B, 0, 0, 0);
        WriteField(part, recordDefinition + 24, MissionFieldHash, 0x0B, 0, 4, 0);
        WriteField(part, recordDefinition + 36, CompletedFieldHash, 0x00, 0, 8, 0);
        WriteField(part, recordDefinition + 48, ProgressFieldHash, 0x03, 0, 9, 0);
        return part;
    }

    private static void WriteField(
        Span<byte> target,
        int offset,
        uint fieldHash,
        byte dataType,
        byte subtype,
        short dataOffset,
        uint referenceKey)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset, 4), fieldHash);
        target[offset + 4] = dataType;
        target[offset + 5] = subtype;
        BinaryPrimitives.WriteInt16BigEndian(target.Slice(offset + 6, 2), dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset + 8, 4), referenceKey);
    }

    private static byte[] BuildPsig()
    {
        var part = new byte[8];
        "PSIG"u8.CopyTo(part);
        BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(4, 4), 8);
        return part;
    }

    private static uint ComputeJooat(ReadOnlySpan<byte> bytes) =>
        FinalizeHash(Update(0x3FAC7125, bytes));

    private static void UpdateDateChecksum(Span<byte> bytes)
    {
        Span<byte> iv = stackalloc byte[4];
        bytes[..4].CopyTo(iv);
        iv.Reverse();
        Span<byte> date = stackalloc byte[8];
        bytes.Slice(0x104, 8).CopyTo(date);
        date[..4].Reverse();
        date[4..].Reverse();
        var hash = FinalizeHash(Update(0, iv));
        hash = FinalizeHash(Update(hash, date));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.Slice(0x10C, 4), hash);
    }

    private static uint Update(uint hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            hash += unchecked((uint)(sbyte)value);
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        return hash;
    }

    private static uint FinalizeHash(uint hash)
    {
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}