# Big Pickle — Sprint Story 4: savegame-reader → Runtime Mission Sync

**Repo:** /tmp/rdr2-projects/savegame-reader/rdr2-savegame-reader-main (the C# savegame research repo)
**Branch:** story/mission-sync (cut from master; DO NOT PUSH — PM merges after QA)
**Work mode:** opencode run --agent big-pickle --auto (use opencode/nemotron-3-ultra-free if Zen quota hits)

## Objective
Promote the savegame-reader from a **research MVP** to a **runtime dependency** of the coop story. The coop story's `MissionProgressionJournal` needs to know, at runtime, which mission the host is on/should start next. The savegame-reader already has `CampaignCapabilityAnalyzer` that can fingerprint a save and produce capability candidates. We need to wire it as a library the sidecar calls at session start and after each mission.

## What you have (staged references)
- `src/Rdr2SaveResearch/` — the full savegame-reader:
  - `CampaignCapabilityAnalyzer.cs` — analyzes a save for capability candidates (mission progress, medals, recipes, etc.)
  - `CampaignSaveSync.cs` — syncs mission progress between saves
  - `CampaignMissionMerger.cs` — merges mission progression
  - `Rdr2PcSaveCodec.cs` — AES-256 ECB container codec (hardcoded key, exact round-trip)
  - `Rdr2RsavContentAnalyzer.cs` / `Rdr2RsavContentDiffer.cs` — structural analysis + diff
  - `Rdr2SaveResearch.csproj` — .NET 10 target
- `tests/Rdr2SaveResearch.SelfTest/` — 94 lines, thin

## Deliverables

### A. Runtime Mission Sync Library (`src/Rdr2MissionSync/` — NEW project)
Minimal .NET 10 library the coop story sidecar references:
1. **`MissionFingerprint.cs`** — immutable struct: `MissionId`, `ProgressStage`, `SaveHash`, `Timestamp`
2. **`MissionSyncAnalyzer`** — single public method:
   ```csharp
   public static MissionFingerprint Analyze(byte[] saveBytes)
   ```
   - Decodes save via `Rdr2PcSaveCodec.Decode`
   - Runs `CampaignCapabilityAnalyzer.AnalyzeAsync` (or sync variant) on the decoded document
   - Extracts the **active mission** (the one in progress / next available)
   - Returns `MissionFingerprint` or `null` if undetermined
3. **`MissionSyncService`** — optional higher-level:
   - Caches fingerprint per save hash (in-memory LRU, 16 entries)
   - `UpdateAfterMissionComplete(MissionId)` → re-analyzes and returns new fingerprint
3. **Zero external deps** — stdlib + the savegame-reader's internal code (add as project reference)

### B. Coop Story Integration (`src/CoopStory.Sidecar/Session/`)
1. **`MissionProgressionJournal.cs`** (existing — you already read it) — add:
   ```csharp
   public void SyncFromHostSave(byte[] hostSaveBytes) { ... }
   public MissionFingerprint CurrentMission { get; }
   ```
   Calls `MissionSyncAnalyzer.Analyze(hostSaveBytes)` at session start and after each mission completion event.
2. **`SidecarRuntime.cs`** — at session start, if Online mode and host, call `SyncFromHostSave` with the host's current save bytes (read from known RDR2 save path).

### C. Tests
- xUnit tests for `MissionSyncAnalyzer` (target 30+):
  - Real save round-trip: decode → analyze → fingerprint matches expected mission
  - Different saves → different fingerprints
  - Corrupted save → null (no crash)
- Integration test: spin up sidecar with mock host save → verify `MissionProgressionJournal.CurrentMission` populated correctly

### D. Coop Story Launcher Integration
- "Host Online" button: before launching sidecar, read host's current save (from `%LOCALAPPDATA%\RDR2CoopStory\saves\`), extract bytes, pass to sidecar via config/pipe.

## Constraints
- **Target framework**: .NET 10
- **Zero new external NuGet deps** — stdlib + project reference to savegame-reader
- Conventional commits on `story/mission-sync`. DO NOT PUSH. PM merges after QA.

## Report
DONE report: files/lines, test counts, integration smoke transcript (analyze real save → fingerprint → sidecar journal updated), .NET handoff notes.
DO NOT PUSH. PM merges after QA.