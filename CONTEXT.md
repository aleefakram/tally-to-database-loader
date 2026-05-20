# Tally-to-Database Sync Utility Context

This context describes the background synchronization system that extracts financial records (ledgers, vouchers, etc.) from a running Tally Prime instance and loads them into a target relational database.

## Language

**Sync Job**:
A configured rule linking a specific Tally company to a target database profile, specifying the recurrence interval/daily time and the synchronization mode.
_Avoid_: Task, batch, synchronization profile

**Database Profile**:
A saved set of connection credentials and network details (technology, host, port, credentials) used to target a database engine instance.
_Avoid_: Connection string, database target, connection profile

**Sync Mode**:
The strategy used to keep the target database up-to-date with Tally: either Full Sync or Incremental Sync.
_Avoid_: Sync type

**AlterID**:
An internal sequential number maintained by Tally Prime representing the modification state of a master or transaction, used during Incremental Sync to isolate changed records.
_Avoid_: Version ID, update sequence number, change ID

## Relationships

- A **Sync Job** targets a single database catalog utilizing one **Database Profile**
- A **Sync Job** executes in a specified **Sync Mode** (either Full or Incremental)
- An Incremental **Sync Mode** utilizes the **AlterID** to fetch changes from Tally

## Example dialogue

> **Dev**: "When the user triggers a manual sync from the tray icon, do we run every **Sync Job** immediately?"
> **Domain expert**: "Yes, the manual sync should execute all active **Sync Jobs** right away, bypassing their regular schedules."
> **Dev**: "And does the **Incremental Sync** mode compare the database's last recorded **AlterID** with Tally's current value?"
> **Domain expert**: "Exactly. If they match, Tally has no new modifications, and we skip syncing that job's data."

## Flagged ambiguities

- "Connection Profile" was used interchangeably with **Database Profile** — resolved: we use **Database Profile** consistently.
