# FixFlow

## What This Solves
FixFlow addresses a common gap between trading desks and middle-office operations: incoming order and allocation messages are often inconsistent, incomplete, or mapped differently across parties, OMS/EMS, and Trade Allocation and Confirmation systems. That mismatch creates manual remediation, delayed allocations, and operational risk. The project provides a reliable bridge that normalizes, validates, and transforms trade allocations into a consistent, auditable flow that downstream systems can ingest without custom per-counterparty logic.

## What's Innovative About the Approach
Instead of hard-coding one-off mappings, FixFlow treats FIX transformation as a first-class, configurable workflow.

- It uses dictionary-driven parsing so validation and tag semantics stay aligned with FIX standards.
- It supports mapping rules and tag inference that can be tuned without rebuilding the app, making it practical to onboard new counterparties quickly.
- It emphasizes traceability, surfacing message history and validation results so Ops can explain exactly why a message was accepted, corrected, or rejected.
- It provides both an interactive GUI for operators and a CLI that can be deployed as an automated ETL service, giving teams flexibility without splitting logic across multiple tools.

## Architecture Overview
- Input ingestion:
Operators use the GUI to create the mapping first, then load allocation spreadsheet file (csv, xls, xlsx), validate messages, and inspect results.
The CLI runs as an ETL service and can process allocations directly from email attachments using the Microsoft 365 Graph API.
- Parsing & validation:
FIX dictionaries define message structures, required tags, and enum values to drive strict validation and consistent interpretation.
- Mapping & normalization:
Operator-created mapping rules transform counterparty-specific FIX into a normalized internal model.
- Output & audit:
Outputs are emitted for downstream systems and accompanied by logs/diagnostics for traceability and reconciliation.

## How to Use It Best
1. Create the mapping first (operator-defined rules per counterparty or message family).
2. Define or import the FIX dictionaries that match your counterparties and version requirements.
3. Use the GUI for interactive testing and live monitoring.
4. Deploy the CLI as an automated ETL service for scheduled or continuous processing, including email-based allocation flows via M365 Graph API.
5. Review validation feedback and message logs to refine mapping rules and reduce exceptions.

## Ideal Use Cases
- Normalizing allocation flows across multiple brokers or clients.
- Automating fixes for missing or inconsistent FIX tags.
- Building an auditable pipeline for trade and allocation ingestion.
- Reducing manual Ops intervention in post-trade workflows.
