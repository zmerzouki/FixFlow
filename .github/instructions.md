<!-- Guidance for FixFlow.TradeAllocBridge -->

# Purpose
Short, actionable guidance to get productive in this repository.

Last reviewed: 2026-03-26  
This file replaces the auto-created 2026-01-12 draft and folds in the material repo changes since then.

# What changed since 2026-01-12
- The repo was renamed to `FixFlow.TradeAllocBridge` and now uses product names `FixFlowService` (headless service), `FixFlowClient` (desktop UI), and `FixFlowWeb` (web UI).
- The architecture expanded from Core + headless runner + desktop UI into a multi-surface system with:
  - `FixFlowService` for mailbox-driven automation
  - `FixFlowClient` for desktop operations
  - `FixFlowWeb` + `FixFlow.TradeAllocBridge.Web.Client` + `FixFlow.TradeAllocBridge.Web.Shared` for browser-based workflows and shared DTOs
- Operator tooling grew materially:
  - Message History was renamed to Message Log
  - Fix Dictionary lookup was added
  - Global Settings editing was added
  - Direct Ingestion, map validation, duplicate detection, delete confirmation, and result reporting were expanded
- Allocation processing is stricter now:
  - validation/reporting was improved for FIX send failures and numeric/value normalization errors
  - `DisableAllocationMerge` allows per-row allocation processing instead of symbol/side merging
  - validation report entries use `AllocID_TradeDate` formatting and deduping
- FIX metadata support expanded:
  - `cfg/FIX44.xml` and `cfg/REDUCED_FIX42.xml` were added
  - dictionary and map tooling now expose enum values and component-path metadata
- Runtime configuration is now more structured:
  - shared `appsettings.json` and `cfg/` assets are linked into app outputs
  - `SharedConfigResolver` supports shared appsettings discovery and `FIXFLOW_SHARED_APPSETTINGS`
  - session qualifiers are differentiated by host (`FixFlowService`, `FixFlowClient`, `FixFlowWeb`)
- Mapping deployment is no longer just static file loading:
  - `MappingStagingWatcher` watches an incoming mapping drop folder and notifies `FixMappingRepository` when active mappings change
- Local sample spreadsheets and duplicate cfg copies were removed from the repo; do not assume test spreadsheets or root-level sample data exist.

# Current architecture summary
- Projects:
  - `src/FixFlow.TradeAllocBridge.Core` - business logic, FIX integration, mapping, Excel parsing, reporting, shared config helpers
  - `src/FixFlow.TradeAllocBridge.CLI` - `FixFlowService`, the headless mailbox-driven/background runner
  - `src/FixFlow.TradeAllocBridge.WPF` - `FixFlowClient`, the desktop UI
  - `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web` - `FixFlowWeb` server host
  - `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web.Client` - browser UI
  - `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web.Shared` - DTOs/state models shared by web client/server
  - `tests/FixFlow.TradeAllocBridge.Tests` - unit tests
- Primary flows:
  - automated email ingestion in `FixFlowService`
  - interactive spreadsheet ingestion and validation in `FixFlowClient` / `FixFlowWeb`
  - mapping authoring and FIX dictionary lookup in UI surfaces
  - FIX Allocation (`MsgType=J`) message build/send through QuickFIXn
- Core runtime artifacts:
  - shared settings: repo-root `appsettings.json` or `FIXFLOW_SHARED_APPSETTINGS`
  - FIX dictionaries/config: `cfg/`
  - session state: `store/`
  - logs: `logs/`
  - downloaded attachments: `attachments/`

# Important files (start here)
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixEngine.cs`
  - creates/updates runtime `cfg/FIX42.cfg`
  - reloads QuickFIXn `SessionSettings`
  - filters sessions by `SessionQualifier`
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixMessageBuilder.cs`
  - builds Allocation (`35=J`) messages
  - handles alloc counters, trade-date formatting, header population, and tag normalization
- `src/FixFlow.TradeAllocBridge.Core/Fix/FixValueNormalizer.cs`
  - central FIX value normalization/validation
  - important for enum/tag handling and invalid value reporting
- `src/FixFlow.TradeAllocBridge.Core/Mapping/FixMappingRepository.cs`
  - loads active mappings from a resolved configs directory
  - used by service and UI flows
- `src/FixFlow.TradeAllocBridge.CLI/Program.cs`
  - `FixFlowService` entrypoint
  - mailbox processing, session setup, mapping watcher initialization, hosted/manual run modes
- `src/FixFlow.TradeAllocBridge.CLI/Services/MappingStagingWatcher.cs`
  - deploys staged mappings into the active configs folder and triggers repository refresh
- `src/FixFlow.TradeAllocBridge.WPF/ViewModels/AllocationProcessorViewModel.cs`
  - desktop ingestion/validation/send flow
- `src/FixFlow.TradeAllocBridge.WPF/ViewModels/MapEditorViewModel.cs`
  - desktop mapping editor, defaults, qualifier handling, and FIX metadata usage
- `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web/Controllers/IngestionController.cs`
  - web ingestion/preview/send logic
- `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web/Controllers/MapManagementController.cs`
  - web map CRUD, FIX metadata loading, session config generation
- `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web/Controllers/FixDictionaryController.cs`
  - dictionary lookup, enum-aware tag suggestions, component-path metadata
- `src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web/Controllers/SettingsController.cs`
  - web settings read/write flow

# Developer workflows (concrete commands)
- Build solution: `dotnet build "FixFlow.TradeAllocBridge.sln"`
- Run `FixFlowService`: `dotnet run --project src/FixFlow.TradeAllocBridge.CLI/FixFlow.TradeAllocBridge.CLI.csproj -- process-now`
- Run `FixFlowService` hosted loop: `dotnet run --project src/FixFlow.TradeAllocBridge.CLI/FixFlow.TradeAllocBridge.CLI.csproj -- run`
- Run `FixFlowClient`: `dotnet run --project src/FixFlow.TradeAllocBridge.WPF`
- Run `FixFlowWeb`: `dotnet run --project src/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web/FixFlow.TradeAllocBridge.Web.csproj`
- Run tests: `dotnet test`
- Helper scripts: `run-dotnet.ps1`, `build/PublishFixFlow.ps1`, `scripts/Start-Stunnel.ps1`

# Project-specific conventions & patterns
- Prefer product names in docs, comments, and examples:
  - `FixFlowService`
  - `FixFlowClient`
  - `FixFlowWeb`
- Do not use Raymond James-specific names or examples in code/docs/examples.
- Mapping files:
  - use `<CLIENT>_map.json` naming
  - map spreadsheet columns to FIX tag numbers as strings
  - active mapping location is runtime-resolved, not guaranteed to be a repo-root `configs/` folder
  - `FIXFLOW_CLI_CONFIGS` can override the active configs directory for `FixFlowService`
- FIX sessions:
  - prefer `FixEngine.AppendSessionsIfMissing(...)` and `ReloadSettings(...)` rather than manual `FIX42.cfg` edits
  - session routing now depends on `SenderCompID`, `TargetCompID`, and host-specific `SenderSubID` qualifier matching
  - session qualifiers come from `FixSessionQualifiers:Service`, `FixSessionQualifiers:Client`, and `FixSessionQualifiers:Web`
- Shared settings:
  - apps look for `appsettings.json` in runtime output first
  - fallback/shared resolution is handled through `SharedConfigResolver`
  - avoid hard-coding local config paths in runtime code
- Allocation processing:
  - `DisableAllocationMerge` changes the grouping key from symbol/side to per-row processing
  - missing required tags and invalid normalized values are surfaced in result/report entries, not only in console/log text
  - dry-run should mean validate/build without transmit, not "use local test data"
- Message log/reporting:
  - Message Log is the current feature name
  - direct ingestion and service runs emit report entries that are later read by Message Log surfaces

# Integration & dependencies
- QuickFIXn drives FIX session management and allocation message transmission in `Core/Fix/*`
- Excel/CSV parsing uses `ClosedXML` and `NPOI` under `Core/Excel`
- Email ingestion uses Microsoft Graph in `Core/Email/GraphEmailService.cs`
- Web and desktop mapping/dictionary tooling depend on shared FIX metadata models and dictionary XML parsing

# Debugging and runtime tips
- If `FixFlowService` fails to start, check that `appsettings.json` and `cfg/` were copied into the output folder.
- If no mappings are found, verify the resolved active configs directory and any `FIXFLOW_CLI_CONFIGS` override.
- If FIX sends fail, inspect:
  - `cfg/FIX42.cfg`
  - `store/`
  - host-specific session qualifiers
  - the generated report/message-log entries
- For UI debugging:
  - `FixFlowClient` and `FixFlowWeb` both expose Direct Ingestion, Map Management, Message Log, Fix Dictionary, and Settings flows
  - Fix Dictionary suggestions now include enum matches and component-path badges

# Safety & hygiene
- `appsettings.json` may contain secrets; do not print or commit credentials.
- Do not reintroduce local-only spreadsheets, hard-coded mailbox bypasses, or environment-specific sample data into runtime paths.
- When changing FIX session defaults, document sender/target IDs, qualifiers, and whether the change applies to service, client, web, or all three.
- When changing mappings or settings flows, account for both desktop and web implementations unless the change is intentionally surface-specific.

# Common user tasks (examples)
- Add a client mapping:
  - create `<CLIENT>_map.json`
  - verify it loads through `FixMappingRepository`
  - if using staged deployment, confirm `MappingStagingWatcher` moves it into the active configs folder
- Add FIX dictionary support:
  - place/update XML under `cfg/`
  - confirm `FixFlowClient` / `FixFlowWeb` dictionary surfaces can discover it
  - verify tag enums and component-path metadata still resolve
- Diagnose FIX session mismatches:
  - compare mapping predefined header values vs runtime `cfg/FIX42.cfg`
  - verify `SenderSubID`/`TargetSubID` and `SessionQualifier` alignment
- Extend ingestion behavior:
  - update both `AllocationProcessorViewModel` and `IngestionController` when desktop/web behavior should stay aligned
  - update shared DTOs in `FixFlow.TradeAllocBridge.Web.Shared` when web client/server payloads change
