namespace Rdr2MissionSync;

/// <summary>
/// Immutable snapshot of the host's current story-mode mission state as read
/// from a PC SRDR save. Value equality is structural so two fingerprints from
/// the same save compare equal regardless of when they were produced.
/// </summary>
public readonly record struct MissionFingerprint(
    uint MissionId,
    uint ProgressStage,
    string SaveHash,
    long Timestamp);