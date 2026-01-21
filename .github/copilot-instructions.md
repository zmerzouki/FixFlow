<!-- Copilot / AI agent guidance for FixFlow.TradeAllocBridge -->

# Purpose
Short, actionable guidance to get an AI coding agent productive in this repository.

# Quick architecture summary
- Projects: `src/FixFlow.TradeAllocBridge.Core` (business logic + FIX), `src/FixFlow.TradeAllocBridge.CLI` (headless runner), `src/FixFlow.TradeAllocBridge.WPF` (desktop UI), `tests/FixFlow.TradeAllocBridge.Tests` (unit tests).
- Primary flow: ingest allocations (CSV/XLSX) -> map columns via `configs/*_map.json` -> build FIX Allocation messages with `FixMessageBuilder` -> send via QuickFIXn initiator (`FixEngine` + `FixApp`) -> persisted session state in `store/` and runtime logs in `logs/`.
- FIX integration: QuickFIXn uses `cfg/FIX42.xml` (data dictionary) and `cfg/FIX42.cfg` (engine settings). `FixEngine` writes a default `[DEFAULT]` block and exposes `AppendSessionsIfMissing(...)` to add sessions safely.

# Important files (start here)
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixEngine.cs`  creates/updates `cfg/FIX42.cfg`, loads `cfg/FIX42.xml`, starts/stops initiator.
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixApp.cs`  QuickFIXn `IApplication` callbacks and message handling.
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixMessageBuilder.cs`  builds single/group Allocation (`MsgType=J`) messages; shows mapping usage and auto-fill logic (alloc counter, trade-date handling).
- `src/FixFlow.TradeAllocBridge.Core/Mapping/FixMappingRepository.cs`  loads `*_map.json` from a base `configs/` directory and provides `GetByClientId()`.
- `configs/`  mapping files named `<CLIENT>_map.json` (example: `RAYMONDJAMES_map.json`).
- `cfg/FIX42.xml`, `cfg/FIX42.cfg`  data dictionary and engine settings; `store/` and `logs/` are runtime artifacts.

# Developer workflows (concrete commands)
- Build solution: `dotnet build "FixFlow.TradeAllocBridge.sln"`
- Run CLI (headless): `dotnet run --project src/FixFlow.TradeAllocBridge.CLI`
- Run WPF UI: `dotnet run --project src/FixFlow.TradeAllocBridge.WPF`
- Run tests: `dotnet test` (solution root)
- VS Code tasks available: `build`, `publish`, `watch` (see workspace tasks for exact args).
- Helper scripts: `run-dotnet.ps1` (Windows helper) and `scripts/Start-Stunnel.ps1` (stunnel helper when network tunnelling is needed).

# Project-specific conventions & patterns
- Mapping files: use uppercase human column keys (e.g., `TRADE DATE`, `BROKER`) mapped to FIX tag numbers as strings. Filenames must match `<CLIENT>_map.json`.
- Mapping lookup: call `FixMappingRepository.GetByClientId(clientId)`; mapping objects expose `TradeAllocations` dictionary (column -> tag string).
- FIX sessions: prefer `FixEngine.AppendSessionsIfMissing(IEnumerable<(string Sender, string Target)>)` to add sessions rather than editing `cfg/FIX42.cfg` manually. After appending call `FixEngine.ReloadSettings(fixApp)` and `FixEngine.Start()`.
- Allocation IDs: `FixMessageBuilder` persists an `alloc_counter.chk` file to maintain monotonic AllocIDs  keep this file in repository root or ensure write access in runtime.
- Data dictionary changes: update `cfg/FIX42.xml` first and ensure `cfg/FIX42.cfg` references the correct path (FixEngine writes DataDictionary path automatically in default config).

# Integration & dependencies
- QuickFIXn: packages `QuickFix`, `QuickFix.DataDictionary`, and related NuGet packages used in `Core/Fix/*`.
- Excel/CSV: `ClosedXML`, `NPOI` used under `Core/Excel` for parsing files in `attachments/`.
- Email: Microsoft Graph client used; credentials are currently read via `appsettings.json` (treat as secret).

# Debugging and runtime tips
- Logs: Serilog writes to `logs/log-YYYYMMDD.txt`  the app uses emoji markers (``, ``, ``) in logs for key events.
- FIX session issues: confirm `cfg/FIX42.cfg` contains the `[SESSION]` block, `store/` contains corresponding session files (e.g., `FIX.4.2-CLIENT1-EXECUTOR.session`), then call `FixEngine.ReloadSettings()` to reinitialize.
- Unit tests: look under `tests/FixFlow.TradeAllocBridge.Tests` for examples of mapping and builder tests.

# Safety & hygiene
- `appsettings.json` may contain credentials  do not print or commit secrets. Prefer environment variables for CI.
- When changing FIX configuration, prefer appending sessions programmatically and include review notes about session IDs and ports.

# Common agent tasks (examples)
- Add a client mapping: create `configs/NEWCLIENT_map.json` with uppercase keys and tag values as strings; verify load via `FixMappingRepository.GetByClientId("NEWCLIENT")`.
- Add a FIX session: call `FixEngine.AppendSessionsIfMissing(new[] { ("SENDER","TARGET") })`, then `ReloadSettings()` and `Start()`; expect store files under `store/` named `FIX.4.2-SENDER-TARGET.*`.

---

If you'd like, I can add: a small JSON mapping example, a minimal `FixEngine` append snippet, or a sample test  tell me which to include.
