====================
DICOM7 Installation
====================

DICOM7 is a suite of services for converting between DICOM and HL7 formats.
This installer lets you deploy only the components you need while keeping the
shared tooling consistent across environments.

Common Paths
------------
- Configuration files install to: %ProgramData%\Flux Inc\DICOM7\[Service]\config.yaml
- Shared runtime assets (cache, logs, HL7 templates) live under:
  %ProgramData%\Flux Inc\DICOM7\[Service]\ and %ProgramData%\Flux Inc\DICOM7\[Service]\logs\
- HL7 templates default to %ProgramData%\Flux Inc\DICOM7\DICOM2ORM\ormTemplate.hl7 and
  %ProgramData%\Flux Inc\DICOM7\DICOM2ORU\oruTemplate.hl7. The services create
  baseline templates if the files are missing.
- Pass `--path C:\Flux\DICOM2ORM` (or per-service equivalent) to override the base path.

Component Reference
-------------------

1. DICOM to ORM Service (DICOM2ORM)
-----------------------------------
Purpose: Queries a modality worklist (MWL) provider and emits HL7 ORM^O01 orders.

Runtime behavior
- Polls the configured DICOM SCP on the interval defined in Query.IntervalSeconds.
- Deduplicates orders via the cache folder before sending them onward.
- Uses `ormTemplate.hl7` to map DICOM tags into HL7 segments; the service will
  load the template from the common app folder and generate a default if absent.

Configuration (`config.yaml`)
- Cache: `Folder`, `RetentionDays`, and optional `KeepSentItems` (default true) govern
  where ORM messages are staged, how long they stay on disk, and whether sent
  payloads move into `cache\sent\`.
- Dicom: `ScuAeTitle`, `ScpHost`, `ScpPort`, `ScpAeTitle`. You can also override the base
  `AETitle` inherited from the shared configuration.
- Query: `ScheduledStationAeTitle`, `Modality`, and `ScheduledProcedureStepStartDate`.
  The `Mode` value supports `today`, `range`, or `specific` as implemented in
  DICOM2ORM/WorklistQuerier.cs. When `Mode` is `range`, `DaysBefore` and `DaysAfter`
  define the inclusive window; when `specific`, provide an ISO date string.
- HL7: `ReceiverHost`, `ReceiverPort`, plus the templated identifiers
  `SenderName`, `ReceiverName`, `ReceiverFacility` used by Shared/HL7Sender.cs
  to override HL7 MSH values.
- Retry: `RetryIntervalMinutes` controls how often pending messages and cache
  retries are revisited.

Usage tips
- Service name: DICOM7_DICOM2ORM
- Ensure the target MWL endpoint accepts the configured `ScuAeTitle`.
- Place site-specific ORM templates (v2.3/2.4) at the template path so the
  generated default does not get used in production.
- Cache cleanup honours `RetentionDays`; set `KeepSentItems` false if disk space
  is limited and downstream ACKs are reliable.

Tasks
- [ ] Update Dicom.ScpHost, ScpPort, and ScpAeTitle to your MWL provider.
- [ ] Confirm Query filters (station, modality, date mode) match site workflow.
- [ ] Point HL7.ReceiverHost/ReceiverPort to the RIS/HIS listener and validate MSH identifiers.
- [ ] Decide on Cache.Folder, RetentionDays, and KeepSentItems to satisfy audit policy.
- [ ] Review ormTemplate.hl7 placeholders against downstream HL7 expectations.

Configuration notes
- Set Cache.KeepSentItems to false if you prefer delivered messages deleted instead of archived
  (see DICOM2ORM/CacheManager.cs).

2. DICOM to ORU Service (DICOM2ORU)
-----------------------------------
Purpose: Operates a DICOM C-STORE SCP and converts received studies into HL7 ORU^R01 results.

Runtime behavior
- Runs a fo-dicom Store SCP using `Dicom.AETitle` and `Dicom.ListenPort`.
- Writes incoming payloads to the cache, renders HL7 messages through
  `oruTemplate.hl7`, and submits them using Shared/HL7Sender.
- Moves outbound messages into `cache\outgoing\` before transmission; pending
  items move between the retry queue and outgoing folder based on acknowledgments.

Configuration (`config.yaml`)
- Dicom: `AETitle`, `ListenPort`, with inherited defaults from Shared/Config/BaseDicomConfig.cs.
- HL7: `ReceiverHost`, `ReceiverPort`, `WaitForAck`, along with template identifiers
  `SenderName`, `ReceiverName`, `ReceiverFacility`.
- Cache: `Folder`, `RetentionDays`, optional `KeepSentItems` (default true) determine where
  processed SOP Instance UIDs are staged and whether archival copies are kept in `cache\sent\`.
- Retry: `RetryIntervalMinutes` drives both retry scheduling and the main processing loop delay.

Usage tips
- Service name: DICOM7_DICOM2ORU
- Configure the modality AE(s) to send studies to the selected ListenPort/AETitle.
- If the HL7 receiver does not provide ACKs, set `WaitForAck: false` so the sender
  treats transmissions as success without blocking.
- Monitor `%ProgramData%\Flux Inc\DICOM7\DICOM2ORU\cache\outgoing\` and `logs\`
  during validation to confirm ORU generation.

Tasks
- [ ] Update Dicom.AETitle and ListenPort to match modality routing tables.
- [ ] Point HL7.ReceiverHost/ReceiverPort to the LIS/RIS endpoint and decide on WaitForAck.
- [ ] Tune Cache.Folder, RetentionDays, KeepSentItems based on retention policy.
- [ ] Validate oruTemplate.hl7 to ensure proper OBX/OBR mappings for your site.
- [ ] Exercise retry handling by simulating an HL7 outage and verifying resend behavior.

Configuration notes
- Adjust Cache.KeepSentItems if the LIS/RIS does not require local copies of sent ORU payloads
  (see DICOM2ORU/CacheManager.cs).

3. ORM to DICOM Service (ORM2DICOM)
-----------------------------------
Purpose: Accepts inbound HL7 ORM^O01 orders and exposes them as a Modality Worklist SCP.

Runtime behavior
- Hosts an HL7 TCP listener using `HL7.ListenPort` (and optional `HL7.ListenIP`) to ingest orders.
- Persists active orders under `cache\Active\` and periodically prunes expired entries.
- Serves a DICOM MWL SCP on `Dicom.ListenPort`, translating cached ORMs into worklist datasets.

Configuration (`config.yaml`)
- HL7: `ListenPort`, `ListenIP`, `MaxORMsPerPatient`, plus template metadata inherited from the base config.
  The listener binds to the configured IP when `ListenIP` is not `0.0.0.0` (see ORM2DICOM/HL7Server.cs).
- Dicom: `AETitle` and `ListenPort`; the current implementation does not read a `ListenIP` field for
  the DICOM listener.
- Cache: `Folder`, `RetentionDays`, `KeepSentItems`, `AutoCleanup`, `CleanupIntervalMinutes` govern how
  cached ORM files are stored and pruned.
- Order: `ExpiryHours` determines when cached ORMs are discarded and removed from MWL responses.

Usage tips
- Service name: DICOM7_ORM2DICOM
- Ensure upstream HL7 routers target the configured HL7.ListenPort/IP while modalities query the DICOM
  endpoint defined by Dicom.ListenPort and AETitle.
- Leave `MaxORMsPerPatient` aligned with modality capacity; excess orders are trimmed in HL7Server.cs.
- When AutoCleanup is enabled the service schedules periodic cleanup using CleanupIntervalMinutes.

Tasks
- [ ] Set HL7.ListenPort (and ListenIP if binding to a specific interface) for inbound ORM feeds.
- [ ] Confirm Dicom.AETitle and ListenPort match modality worklist query settings.
- [ ] Decide on Cache retention, KeepSentItems, AutoCleanup, and CleanupIntervalMinutes.
- [ ] Adjust Order.ExpiryHours to match clinical retention requirements.
- [ ] Validate that new ORM messages appear in modality MWL queries after end-to-end testing.

Configuration notes
- The HL7 listener honours `ListenIP`; the DICOM worklist server currently binds on all interfaces
  exposed by the host.

Sample Configuration Audit
--------------------------
- Query.ScheduledProcedureStepStartDate supports only `today`, `range`, and `specific` modes as shown in
  DICOM2ORM/WorklistQuerier.GetScheduledDate; other values fall back to `today`.
- ORM2DICOM/HL7 ListenIP controls the bind address for the HL7 server; the DICOM worklist service listens
  on all interfaces (ORM2DICOM/Config.cs, ORM2DICOM/HL7Server.cs, ORM2DICOM/DICOMServerBackgroundService.cs).

For more information and support, please contact Flux Inc.

====================
