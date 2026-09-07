# Personal Performance Tracker — Complete Product & Functional Plan

## 1. Purpose

The **Personal Performance Tracker** is a private AI-powered system that continuously records my work activity and helps me understand and present my professional growth.

The main goal is **not to track activity for the sake of tracking it**.

The goal is to answer:

> **“What have I actually accomplished over the last 6–12 months?”**

This becomes useful during:

* Performance reviews
* Appraisals
* 1:1s
* Career planning
* Resume preparation
* Promotion discussions
* Understanding personal strengths and weaknesses

The system continuously collects factual evidence from my work systems and combines it with my own notes and memories.

---

# 2. Core Concept

```text
                    My Work
                       │
        ┌──────────────┼──────────────┐
        ↓              ↓              ↓
     GitHub       Project Board    My Notes
        │              │              │
        └──────────────┼──────────────┘
                       ↓
                 Data Collector
                       ↓
                  PostgreSQL
                       ↓
                Personal Memory
                       ↓
                  MCP Server
                       ↓
                    Claude
                       ↓
        ┌──────────────┼──────────────┐
        ↓              ↓              ↓
     Answers       Reports         Actions
```

The important architectural principle is:

> **GitHub is the source of work activity. PostgreSQL is the historical record. Claude is the reasoning layer. MCP is the interface between Claude and my system.**

---

# 3. How I Will Use It

I should not have to manually maintain the system every day.

I work normally.

For example:

```text
Write code
   ↓
Commit
   ↓
Create PR
   ↓
Review PR
   ↓
Merge PR
   ↓
Move story
   ↓
Complete task
```

The system automatically picks these activities up.

Then I can open **Claude Chat**, **Claude Desktop**, or use a **Claude agent in VS Code** connected to my MCP server and ask questions.

Examples:

> What did I accomplish this week?

> What did I work on yesterday?

> Show me my biggest contributions this month.

> What stories have I completed this quarter?

> What PRs did I contribute to?

> What problems did I solve?

> What have I accomplished in the last six months?

> Prepare my appraisal summary.

I can also give instructions:

> Remember that the reporting issue was caused by the EF Core migration not running in QA.

> Create a ticket for the migration work.

> Move the migration ticket to In Progress.

> What did I learn from the problems I solved this month?

---

# 4. System Responsibilities

The system has five major responsibilities.

### 1. Collect

Automatically collect work activity.

### 2. Preserve

Store historical raw facts permanently.

### 3. Remember

Store additional context that cannot be obtained from GitHub.

### 4. Understand

Use Claude to interpret the accumulated information.

### 5. Act

Allow Claude to perform actions on my behalf through controlled MCP tools.

---

# 5. Data Sources

The first version should focus on GitHub.

### GitHub data

Collect:

* Commits
* Pull requests
* PR reviews
* PR comments
* Issues
* Issue state changes
* Project board items
* Project board status changes
* Labels
* Milestones
* Comments
* Repository activity

Later sources can be added:

* Jira
* Slack
* Microsoft Teams
* Calendar
* Timesheet system
* Other internal work systems

The architecture should allow additional sources without changing the core system.

---

# 6. Automatic Synchronization

The system should use **scheduled polling initially**.

For example:

```text
Every 1 hour
     ↓
Sync Worker starts
     ↓
Read last successful sync time
     ↓
Ask GitHub for changes since that time
     ↓
Process changes
     ↓
Store raw facts
     ↓
Update sync state
```

This means I don't need to manually tell the system to update itself.

---

# 7. Last Sync Tracking

The system must maintain synchronization state.

For example:

```text
SyncState

Source: GitHub
LastSuccessfulSync: 2026-08-18 11:00:00 UTC
```

When the next sync runs:

```text
Current time:
2026-08-18 12:00:00 UTC

Last sync:
2026-08-18 11:00:00 UTC
```

The collector asks GitHub for relevant changes between:

```text
11:00 → 12:00
```

Then it stores the results.

After successful processing:

```text
LastSuccessfulSync = 12:00
```

### Important

The system should update `LastSuccessfulSync` **only after the synchronization completes successfully**.

If the GitHub API fails:

```text
LastSuccessfulSync = 11:00
```

It should **not** move forward to 12:00.

The next run can retry:

```text
11:00 → current time
```

This prevents data loss.

---

# 8. Incremental Synchronization

The system should not download everything from GitHub every hour.

Instead:

```text
Last Sync
    ↓
Fetch changes
    ↓
Process only new/updated records
```

This keeps synchronization efficient.

However, APIs may have limitations around timestamps and updates.

Therefore, the implementation can use a small overlap window.

For example:

```text
Last successful sync = 11:00

Actually request:
10:55 → current time
```

The database must use unique identifiers/upserts so duplicated records don't create duplicate activity.

This makes the synchronization more reliable.

---

# 9. Polling vs Webhooks

### Version 1 — Polling

Use:

```text
Background Worker
+
Scheduled Job
```

Example:

```text
Every 1 hour
```

This is simple and reliable enough for a personal system.

### Version 2 — Webhooks

Later GitHub can notify the system immediately:

```text
GitHub
   ↓
Webhook
   ↓
My API
   ↓
Process Event
   ↓
Database
```

Then polling can remain as a backup/reconciliation mechanism.

### Recommended approach

```text
V1:
Polling

V2:
Webhooks + periodic reconciliation
```

Do not overcomplicate the first version with webhooks.

---

# 10. Raw Activity Storage

The database should maintain the actual facts independently of Claude.

For example:

```text
Activity
--------------------------------
Source
SourceId
ActivityType
Repository
Author
Timestamp
Payload
CreatedAt
UpdatedAt
```

Examples:

```text
Commit
PR Created
PR Merged
PR Reviewed
Issue Created
Issue Closed
Issue Updated
Project Item Moved
```

The raw information should be retained so that Claude can later reason over historical activity.

---

# 11. Memory

Raw GitHub activity isn't enough.

For example:

GitHub tells us:

> PR #123 was merged.

But GitHub may not tell us:

> This PR solved the production reporting problem and I spent two days investigating the root cause.

Therefore, the system needs **personal memory**.

Memory can contain:

### Decisions

> We decided to use PostgreSQL for the service.

### Problems solved

> Reporting failed because the EF Core migration wasn't applied in QA.

### Learnings

> Learned how X behaves in production.

### Accomplishments

> Took ownership of the migration work.

### Goals

> Improve system-design skills.

### Context

> This project was particularly important because...

---

# 12. Activity vs Memory vs Insight

These should remain separate.

### Activity

A factual event.

> PR #123 was merged.

### Memory

Context about the event.

> PR #123 completed the migration work.

### Insight

Claude's interpretation.

> The migration was one of the major contributions this quarter.

This separation is important because **Claude-generated conclusions should not be treated as original facts**.

---

# 13. MCP Architecture

Claude should **not directly receive unrestricted access to GitHub APIs**.

Instead:

```text
Claude
   ↓
MCP
   ↓
My Application
   ↓
PostgreSQL / GitHub
```

The MCP server exposes high-level tools.

### Read tools

Examples:

```text
get_my_activity()
get_my_commits()
get_my_pull_requests()
get_my_reviews()
get_my_completed_work()
get_my_open_work()
get_my_projects()
get_my_performance()
search_memory()
get_memory()
```

### Write tools

Examples:

```text
create_ticket()
update_ticket()
move_ticket()
add_ticket_comment()
save_memory()
update_memory()
```

Claude should work with these business-level tools instead of arbitrary SQL.

---

# 14. Read Flow

Suppose I ask Claude:

> What did I accomplish last week?

Claude does something like:

```text
User
 ↓
Claude
 ↓
get_my_performance(last_week)
 ↓
MCP
 ↓
PostgreSQL
 ↓
Activity + Memory
 ↓
MCP response
 ↓
Claude
 ↓
Human-readable summary
```

Claude does not need to call GitHub for every question.

The database already contains the historical record.

---

# 15. Action Flow

Suppose I say:

> Create a ticket for the API migration.

Flow:

```text
User
 ↓
Claude
 ↓
create_ticket()
 ↓
MCP
 ↓
My Application
 ↓
GitHub API
 ↓
Create Issue
 ↓
Add to Project
 ↓
Return result
```

Then the next synchronization cycle sees the new GitHub activity and stores it.

---

# 16. Claude Desktop / VS Code

The MCP server should be built as a **standard MCP server**, rather than something tightly coupled to one Claude client.

That allows it to be connected to supported MCP clients such as:

### Claude Desktop

Use Claude Desktop as the conversational interface.

```text
Claude Desktop
      ↓
   MCP Server
      ↓
Personal Tracker
```

### Claude in VS Code

Connect the same MCP server to the Claude/agent environment available in VS Code.

This gives a very useful workflow:

```text
Coding in VS Code
       ↓
Claude
       ↓
Personal Performance MCP
       ↓
"My PRs"
"My tasks"
"My previous work"
"My memories"
```

So the same backend/MCP server becomes the **single source of AI-accessible personal work context**.

---

# 17. Weekly Workflow

I don't need to do anything special.

The system collects activity during the week.

At any time I can ask:

> Give me my weekly performance.

Claude gathers:

```text
Commits
+
PRs
+
Reviews
+
Completed stories
+
Tasks
+
Memories
```

Then produces:

```text
Weekly Summary

Completed:
...

Major contributions:
...

Problems solved:
...

Reviews/help provided:
...

Current work:
...

Important learnings:
...

Next focus:
...
```

The weekly report can optionally be stored as a snapshot.

---

# 18. Monthly Workflow

At the end of the month:

> Give me my monthly performance.

Claude compares the month against previous months.

Possible output:

```text
August

Completed:
42 work items

Engineering:
31 PRs
94 commits
18 reviews

Major contributions:
...

Growth:
...

Challenges:
...

Areas to improve:
...
```

This gives me a historical progression.

---

# 19. Six-Month Appraisal Workflow

This is the most important workflow.

I ask:

> Prepare my six-month performance review.

The system retrieves:

```text
6 months of raw activity
+
Weekly summaries
+
Monthly summaries
+
Personal memories
+
Major projects
```

Claude organizes it into:

```text
Executive Summary

Major Projects

Major Contributions

Problems Solved

Technical Growth

Leadership / Ownership

Collaboration

Challenges

Impact

Areas for Improvement

Next Goals

Evidence
```

The important point is:

> **The appraisal isn't based on what I can remember that day.**

It is based on six months of accumulated evidence.

---

# 20. Performance Should Not Be Reduced to Numbers

The system should show numbers, but numbers aren't the whole story.

For example:

```text
25 commits
```

is less meaningful than:

> Investigated and resolved a production issue that was blocking reporting.

Therefore performance reports should combine:

```text
Quantitative evidence
+
Qualitative context
+
Impact
```

---

# 21. Suggested Database Areas

Keep the initial database simple.

```text
Sources
Repositories
Activities
Projects
ProjectItems
SyncStates
Memories
PerformanceSnapshots
```

### SyncStates

Important fields:

```text
Source
LastSuccessfulSync
LastAttemptedSync
Status
Error
UpdatedAt
```

---

# 22. Background Worker

The backend should contain a small background synchronization worker.

```text
Application
├── API
├── MCP Server
└── Background Worker
       │
       ├── GitHub Sync
       └── Future Source Syncs
```

The worker periodically executes:

```text
sync(source)
```

For GitHub:

```text
Read SyncState
      ↓
Determine from/to timestamp
      ↓
Call GitHub APIs
      ↓
Normalize events
      ↓
Upsert activities
      ↓
Update SyncState
```

---

# 23. Error Handling

If synchronization fails:

```text
GitHub API
   ↓
ERROR
   ↓
Do not advance LastSuccessfulSync
   ↓
Log error
   ↓
Retry next cycle
```

The system should also show:

```text
GitHub Sync

Status: ⚠️ Failed
Last successful sync: 11:00
Last attempt: 12:00
Reason: GitHub API unavailable
```

---

# 24. Security

Because this contains personal work information:

* Keep it private.
* Use GitHub tokens securely.
* Never expose tokens to Claude.
* MCP tools should have limited permissions.
* Separate read and write capabilities.
* Don't expose arbitrary SQL execution.
* Store secrets outside the database.
* Log important write operations.

For V1, running the system locally is a good choice.

---

# 25. Recommended V1 Architecture

```text
                    ┌────────────────────┐
                    │ Claude Desktop     │
                    │ / Claude in VSCode │
                    └─────────┬──────────┘
                              │
                             MCP
                              │
                    ┌─────────▼──────────┐
                    │ Personal Tracker   │
                    │ MCP Server         │
                    └─────────┬──────────┘
                              │
                    ┌─────────▼──────────┐
                    │ PostgreSQL         │
                    │                    │
                    │ Activities         │
                    │ Memories           │
                    │ Sync State         │
                    │ Reports            │
                    └─────────▲──────────┘
                              │
                         Sync Worker
                              │
                    ┌─────────▼──────────┐
                    │ GitHub API         │
                    └────────────────────┘
```

---

# 26. V1 Functional Requirements

### FR-01 — GitHub Authentication

The system must securely connect to my GitHub account.

### FR-02 — Activity Synchronization

The system must collect my GitHub activity.

### FR-03 — Incremental Synchronization

The system must synchronize activity from the last successful sync time until the current time.

### FR-04 — Sync State

The system must store the last successful synchronization timestamp.

### FR-05 — Failure Recovery

The system must not advance the successful sync timestamp when synchronization fails.

### FR-06 — Deduplication

The system must not create duplicate activities when synchronization overlaps or retries.

### FR-07 — Historical Storage

The system must preserve historical activity.

### FR-08 — Memory

The system must allow me to save personal work context and memories.

### FR-09 — MCP Read Tools

Claude must be able to query my activities, work, projects and memories.

### FR-10 — MCP Write Tools

Claude must be able to create/update/move work items through controlled tools.

### FR-11 — Performance Summary

Claude must be able to generate daily, weekly, monthly and six-month summaries.

### FR-12 — Evidence

Performance summaries should reference the underlying activity that supports the conclusion.

### FR-13 — Multiple MCP Clients

The MCP server should work with Claude Desktop and Claude/agent environments that support MCP in VS Code.

---

# 27. Non-Goals for V1

Don't build:

* Mobile application
* Complex dashboard
* Real-time event streaming
* Multiple integrations
* Advanced performance scoring
* Complex AI agents
* Automatic appraisal generation every month
* Sophisticated ML analytics

First prove one thing:

> **Can this reliably maintain my professional history and answer "What did I accomplish?" six months later?**

---

# 28. Build Phases

### Phase 1 — Foundation

```text
.NET backend
PostgreSQL
GitHub authentication
Database schema
Sync state
```

### Phase 2 — GitHub Collector

```text
Commits
PRs
Reviews
Issues
Projects
Incremental sync
```

### Phase 3 — MCP

Build:

```text
get_my_activity
get_my_work
get_my_prs
get_my_performance
search_memory
```

Connect it to Claude.

### Phase 4 — Actions

Add:

```text
create_ticket
update_ticket
move_ticket
add_comment
```

### Phase 5 — Memory

Add:

```text
save_memory
search_memory
update_memory
```

### Phase 6 — Performance

Add:

```text
weekly summary
monthly summary
six-month summary
```

Then start actually using it.

---

# 29. The Final User Experience

Ultimately, I want this experience:

I open Claude and say:

> **"What did I accomplish this week?"**

It knows.

> **"Why was last month's reporting work important?"**

It knows.

> **"What problems did I solve in the last six months?"**

It knows.

> **"Create a ticket for the task we discussed yesterday."**

It does it.

> **"Remember why we chose this architecture."**

It remembers.

> **"Prepare my appraisal based on everything I've done since February."**

It produces an evidence-backed summary.

That is the product.

---

## One-line definition

> **A private AI-powered professional memory system that continuously collects my work activity, preserves the history, understands the context through Claude, and helps me turn six months of everyday work into measurable evidence of growth and performance.**
