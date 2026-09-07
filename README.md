# track-me-baby

A private performance tracker: it collects your GitHub activity into PostgreSQL on a schedule,
lets you record the context GitHub cannot know, and exposes both to Claude through MCP so you can
ask *"what did I actually accomplish in the last six months?"* and get an evidence-backed answer.

Runs entirely on your machine. Nothing leaves it, and **[docker-compose.yml](docker-compose.yml) is
the only file you configure** — one place for your token, your time zone and what counts as a
deployment. Design background is in [docs/plan.md](docs/plan.md).

## What is running

Two things, and only two:

| Piece | What it is | Container |
| --- | --- | --- |
| PostgreSQL | Port **5433** on the host (so it cannot clash with a Postgres you already have) | `trackmebaby-db` |
| The tracker | One .NET process: MCP server **+** hourly sync worker **+** status page **+** dashboard, on **5199** | `trackmebaby` |

`docker compose up -d` starts both, and that is the whole system — Docker is the only thing you
need installed. The .NET SDK is needed only if you want to run the tracker on the host instead
(`./start.sh`), which works against the same database.

The plan's diagram shows API, MCP server and worker as three boxes. Here they are one process —
there is no separate API for Claude to go through, because MCP *is* the interface.

## Setup

### 1. Create a GitHub token

A **classic** personal access token ([github.com/settings/tokens](https://github.com/settings/tokens/new)) with:

| Scope | Why |
| --- | --- |
| `repo` | read your commits, PRs, reviews and issues in private repos |
| `read:org` | see organisation repositories |
| `read:user` | resolve your own login |
| `read:project` | read Projects V2 boards |
| `project` | only needed for the write tools (`create_ticket`, `move_ticket`) |

> **If your work repos are in an org with SAML/SSO**, click *Configure SSO* next to the token and
> authorise it for that org. Without this the API returns **empty results rather than an error** —
> the most confusing way this can fail. The status page shows which account resolved, so check it.

### 2. Configure

Everything lives in the **YOUR CONFIGURATION** block at the top of
[docker-compose.yml](docker-compose.yml). There is no second settings file, no `.env` to create and
nothing to copy into place. That file is tracked by git, so tell git to leave your copy alone first:

```bash
git update-index --skip-worktree docker-compose.yml
```

Now your token can never show up in `git status`, `git add .` or a pull request. Then edit two
values and you are done:

```yaml
Tracker__GitHub__Token: "ghp_your_token_here"
Tracker__TimeZone: "Asia/Kolkata"
```

**Set `TimeZone` explicitly** rather than leaving it as `UTC` if you are not in UTC. Relative
periods like `yesterday` and `last_week` are resolved in this zone, and a container's own zone is
always UTC — so leaving it wrong quietly shifts what "yesterday" means. IANA ids (`Asia/Kolkata`)
and Windows ids (`India Standard Time`) both work on either platform.

Worth a look while you are in there, though all optional: `Organizations` to narrow the search,
`Projects` to name org boards that auto-discovery cannot see, `DefaultRepository` and
`DefaultProject` for `create_ticket`, `Reports__DisplayName` for the cover of exported reports, and
[the delivery block](#defining-a-deployment) for what a deployment means in your pipeline.

If you would rather the token were not in a file at all, leave `Tracker__GitHub__Token` empty and
export it in your shell instead — compose passes it through:

```bash
export GITHUB_TOKEN=ghp_xxx        # or setx GITHUB_TOKEN ghp_xxx on Windows
```

### 3. Start

```bash
docker compose up -d --build
```

Or run `./start-docker.sh` (`start-docker.cmd` on Windows), which does that and prints the URLs.

```bash
docker compose logs -f tracker         # watch it work
docker compose up -d tracker           # after changing configuration
docker compose up -d --build tracker   # after changing C# code
docker compose down                    # stop (the database volume survives)
```

To run the tracker on the host instead — a quicker edit-run loop — use the other script, which
starts only the database and leaves the tracker container stopped so the two do not fight over
port 5199:

```bash
./start.sh          # start.cmd on Windows
```

Use the script rather than `dotnet run` directly. It does one thing bare `dotnet run` cannot: it
reads the `Tracker__*` lines out of `docker-compose.yml` and exports them, which is how the host
process ends up configured by the same single file as the container. `dotnet run` on its own, or a
launch from your IDE, picks up no configuration at all.

Either way, open **http://127.0.0.1:5199/**. The database schema is created automatically on first
run. Run one or the other, not both: they would fight over port 5199 (they would *not* corrupt
anything — sync is leased through the database).

The first sync backfills **180 days** and takes roughly **20–30 minutes** — search requests are
deliberately spaced about 2 seconds apart to stay inside GitHub's rate limits. Later syncs are
incremental and take **under 30 seconds**. Watch the status page or the console.

It is safe to interrupt. Progress is checkpointed after every weekly window, so Ctrl-C (or a reboot
mid-backfill) resumes from the last completed window rather than starting over.

### 4. Connect Claude

**Claude Code / VS Code** — [.mcp.json](.mcp.json) at the project root already points at the HTTP
endpoint, so it is picked up when you work in this folder. To register it globally instead:

```bash
claude mcp add --transport http track-me-baby http://127.0.0.1:5199/mcp
```

**Claude Desktop** — merge one entry from
[docs/claude-desktop-config.example.json](docs/claude-desktop-config.example.json) into your Claude
Desktop config (`%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/` on macOS, `~/.config/Claude/` on Linux), replace the
placeholder path with wherever you cloned this, and restart Claude Desktop. Both entries use the
stdio transport, so Claude Desktop launches its own copy of the server; it can run alongside the
HTTP host without conflict (they coordinate through the database so they never sync simultaneously).

Prefer the **Docker entry**: it runs `docker compose run --rm -T tracker --stdio`, which starts a
throwaway container against the same database, needs no build step, and — the point here — picks up
your configuration from `docker-compose.yml` like everything else. The native entry needs
`dotnet build -c Release` first and, because Claude Desktop launches the binary directly rather than
through a start script, gets no configuration from the compose file.

## Using it

Just work normally. Then ask:

> What did I accomplish this week?
> What PRs did I merge in July, and which one mattered most?
> What problems did I solve in the last six months?
> Prepare my appraisal based on everything since February.

Give it context it cannot collect:

> Remember that the reporting issue was caused by the EF Core migration not running in QA — I spent two days on the root cause.

And act:

> Create a ticket for the migration work and put it in Todo.
> Move the migration ticket to In Progress.

Five ready-made workflows are exposed as MCP prompts (slash commands in most clients):
`weekly_review`, `monthly_review`, `six_month_appraisal`, `delivery_review`, and `capture_context` —
the last one walks through recent finished work and asks you for the story behind anything with no
memory attached.

## Dashboard, DORA and reports

**http://127.0.0.1:5199/dashboard** — charts rather than prose. Pick a range along the top; every
chart carries a table view, a PNG button, and hover values.

| Section | What it shows |
| --- | --- |
| Headline | Changes shipped, median lead time, active days, review load |
| Delivery performance | The four DORA metrics, **per environment**, with band and cadence |
| Flow | Lead-time trend, weekly throughput, lead-time and review-time distributions, work mix, change size, repository mix, board columns |
| Open work | Everything still open, oldest first — age is the signal, not the count |
| Collaboration | Reviews given per teammate, and the activity calendar |
| Evidence | The pull requests, issues and memories the numbers come from |

### Defining a deployment

DORA cannot be computed without deciding what a deployment *is*, and most pipelines mean something
different at each gate. Each environment declares its own trigger, and gets its own four numbers —
they are never blended, because averaging a commit-to-dev with a tag-to-stage produces a figure that
describes nothing.

```yaml
# docker-compose.yml, section 4
Tracker__Delivery__Environments__0__Name: "dev"          # every push ships
Tracker__Delivery__Environments__0__Trigger: "commit"

Tracker__Delivery__Environments__1__Name: "qa"           # merge to main
Tracker__Delivery__Environments__1__Trigger: "merge"
Tracker__Delivery__Environments__1__Branches__0: "main"

Tracker__Delivery__Environments__2__Name: "stage"        # a tag is a cut
Tracker__Delivery__Environments__2__Trigger: "tag"
Tracker__Delivery__Environments__2__TagPattern: "v*"

Tracker__Delivery__Environments__3__Name: "production"   # published GitHub release
Tracker__Delivery__Environments__3__Trigger: "release"
```

- **`commit`** — commits are grouped by the hour, so six pushes in ten minutes is one deployment
  rather than six. Lead time is reported as *not applicable*: when the commit is the deployment
  there is no interval to measure.
- **`merge`** — one merge into `Branches` is one deployment. Lead time runs from the branch's first
  commit to the merge.
- **`tag`** / **`release`** — a cut carries every change merged since the previous cut in that
  repository, so lead time includes the wait for the release.

Number the environments from `0` with no gaps, and list all of them: declaring any one replaces the
built-in ladder outright rather than adding to it. Comment out every `Environments__*` line to fall
back to the shipped dev/qa/stage default.

**Failure and restore** come from reverts and hotfixes: a PR titled `Revert "..."`, a branch starting
`hotfix`, or an incident label marks the deployment before it as failed, and the gap between the two
is the time to restore. When a revert names the pull request it undoes, that pairing is used instead
of the preceding deployment, which makes the measurement exact rather than inferred.

Tags need one extra GraphQL call per repository per sync, only for tag- and release-triggered
environments. Turn it off with `Tracker__Delivery__SyncTags: "false"`.

### Exports

From the dashboard header, or directly:

| Route | What you get |
| --- | --- |
| `/export/report.html?period=last_90_days` | One self-contained file — inline SVG charts, no external anything. Opens on a machine that has never heard of this project, and **Ctrl-P gives a clean PDF** |
| `/export/report.md?period=...` | Markdown with every figure as a table, for pasting into a doc, a ticket or a wiki |
| `/export/data.zip?period=...` | Seven CSVs and the charts as standalone SVGs |

Or ask Claude: **"export my last quarter as a report"** runs `export_report`, which writes the whole
bundle — `report.html`, `report.md`, `charts/*.svg`, `data/*.csv` — to `./exports` (mounted to the
host in Docker) and tells you the path. Put your name and title in
`Tracker__Reports__DisplayName` and `Tracker__Reports__Role` to have them on the cover.

### Two things the numbers are not

- **DORA bands are team benchmarks.** Elite/High/Medium/Low describe delivery patterns across whole
  organisations; applied to one person's slice of the work they are context, not a grade. The
  dashboard says so on the page, and the MCP tools return the caveat alongside the figures.
- **Deployments are inferred from git, not observed from a pipeline.** They are exactly as accurate
  as the triggers you configured.

Lead time is measured only over changes whose commits are actually on record. A pull request opened
and self-merged ten seconds later would otherwise report a ten-second lead time, which describes the
merge rather than the work — so those are excluded and counted in the notes instead.

## Tool surface

**Reading** (all answered from PostgreSQL, never from GitHub, so six-month questions are instant)

`get_my_activity` · `get_my_commits` · `get_my_pull_requests` · `get_my_reviews` · `get_my_issues` ·
`get_my_completed_work` · `get_my_open_work` · `get_my_projects` · `get_project_items` ·
`get_repositories` · `get_my_performance` · `get_dora_metrics` · `get_flow_metrics` ·
`list_performance_snapshots` · `get_sync_status`

`get_my_performance` counts events; `get_flow_metrics` measures durations (lead time, time in
review, board cycle time, work-in-progress age, review load); `get_dora_metrics` reports the four
delivery metrics per environment.

**Memory**

`save_memory` · `search_memory` · `get_memory` · `update_memory` · `delete_memory` · `list_memory_kinds`

**Writing** (every call recorded in `audit_entries`)

`create_ticket` · `update_ticket` · `close_ticket` · `move_ticket` · `add_ticket_comment` ·
`add_ticket_to_project` · `save_performance_snapshot` · `export_report` · `sync_now`

Dates accept ISO values or plain language: `period: "last_month"`, `"last_6_months"`, `"yesterday"`,
`"this_quarter"`. Everything is resolved in your configured time zone, so "yesterday" means yours.

## How collection works

```
hourly tick -> read sync_states.LastSuccessfulSync
            -> search GitHub for changes in [last - 10 min, now]
            -> upsert activities, project items, board moves
            -> advance LastSuccessfulSync (only on success)
```

Rather than walking every repository, sync drives off the GitHub **search API** with
`author:`, `reviewed-by:`, `commenter:` and `assignee:` qualifiers. One set of queries covers every
repo you touch, public or private, so no repository configuration is needed.

- **Commits** come from the PRs they belong to, plus a commit-search pass that catches direct pushes.
- **Board moves** are detected by diffing each item's status against the last sync, since GitHub has
  no cheap "card moved" feed. Each change becomes a `project_item_moved` activity.
- **Deduplication** is a unique index on `(source, source_id, activity_type)`, so the overlap window
  and any retry upsert instead of double-counting.
- **Nothing is fetched twice.** Search results carry each item's `updated_at`; if what is already
  stored is that fresh, the detail requests are skipped entirely. This is why an hourly sync costs
  seconds even across 20+ repositories.
- **Search is throttled** to one request every 2.2s and retries rate-limit rejections with backoff.
  GitHub enforces an undocumented "secondary" limit on bursts that a naive backfill trips in seconds.
- **Failure** leaves `LastSuccessfulSync` at the last completed window; the next cycle resumes there.
  The status page shows the error and the failure streak. A board-sync problem is isolated — it can
  never discard a completed activity sync, only add a note.

## Design rules worth keeping

- **Facts, context and interpretation stay in separate tables.** `activities` is raw GitHub. `memories`
  is what you said. `performance_snapshots` is what Claude wrote. A summary is never mistaken for evidence.
- **`get_my_performance` returns numbers and rows, never prose.** Claude writes the narrative from
  them and cites specific PRs and issues, so every claim in an appraisal is traceable.
- **The token never reaches Claude.** It lives in `GitHubAccess`; tools only ever see typed results.
- **No arbitrary SQL or raw GitHub access is exposed.** Writes go through a handful of business-level
  tools, and each one is audited.

## Operating notes

- **Sync only runs while the process is running.** That is fine — it is timestamp-based, so after the
  machine is off for a week the next run simply fetches a bigger window. In Docker it starts itself:
  both services are `restart: unless-stopped`, so with Docker Desktop set to launch at login the
  tracker comes back after a reboot with nothing to remember. For the host process instead:

  ```powershell
  # Windows Task Scheduler: run at logon, no window
  schtasks /create /tn "track-me-baby" /tr "\"C:\path\to\track-me-baby\start.cmd\"" /sc onlogon /rl limited
  ```

  On macOS or Linux, a launchd plist or a systemd user unit pointing at `start-docker.sh` does the
  same job — or just leave Docker to it, which is simpler.

- **Changing settings** in `docker-compose.yml` needs `docker compose up -d tracker`, *not*
  `docker compose restart tracker`. `restart` reboots the existing container with the environment
  it was created with and would silently ignore your edit; `up -d` notices the file changed and
  recreates the container. Changing C# needs `docker compose up -d --build tracker`.

- **Exported reports** land in `./exports` on the host, via a compose volume. Delete the folder
  freely — nothing reads it back. The container runs as a non-root user, so on Linux hosts (where
  bind-mount permissions come from the host directory, unlike Docker Desktop) an `export_report`
  that fails to write just needs `chmod 777 exports` once.

- **Backfill further than 180 days**: `sync_now` with `since: "2026-01-01"`, or raise
  `InitialBackfillDays` before the first run. For long backfills lower `MaxWindowDays` (search caps
  any single query at 1000 results; the sync warns in `notes` if it hits the cap).
- **Rate limits**: the hourly sync uses a few dozen calls against a 5000/hour budget. The status page
  shows what is left.
- **Reachable only from this machine** and unauthenticated, which is appropriate for a local
  single-user system. On the host Kestrel binds `127.0.0.1`; in Docker it binds `0.0.0.0` *inside the
  container* and compose publishes to `127.0.0.1:5199` only, which amounts to the same thing. If
  you ever expose it, put authentication in front of `/mcp` first — it can write to your GitHub.

## Layout

```
docker-compose.yml            THE config file, plus Postgres 17 on 5433 and the tracker on 5199
Dockerfile                    builds the tracker (SDK build stage -> aspnet runtime, non-root)
start-docker.sh / .cmd        everything in Docker
start.sh / .cmd               Postgres in Docker, tracker on the host (reads compose config)
.mcp.json                     Claude Code / VS Code MCP registration
docs/plan.md                  the original product and functional plan
src/TrackMeBaby/
  Program.cs                  host wiring, routes; --stdio switches transport
  StatusPage.cs               the / and /status pages
  TrackerOptions.cs           the settings schema and every default
  appsettings.json            framework only: logging, Kestrel, connection string
  Data/                       entities, DbContext, migrations
  GitHub/GitHubAccess.cs      the only holder of the token (REST + GraphQL)
  Sync/                       incremental collector, Projects V2 collector, tags, worker
  Services/                   queries, periods, memory, performance, change log, DORA, flow
  Ui/                         theme, SVG charts, report sections, dashboard, exports
  Mcp/                        the tool and prompt definitions
```

`Services/ChangeLog.cs` is worth knowing about: the activity table is event-shaped, which is right
for "what happened when" and useless for "how long did it take". It is the one place those rows are
reassembled into changes, and both the DORA and flow layers read from it rather than re-deriving it.

## Not built (deliberately)

Webhooks, mobile, other data sources, automated scoring — V2 material per
[docs/plan.md §27](docs/plan.md). Polling every hour is enough for a personal record, and the source
abstraction is in place for when Jira or a timesheet needs adding.

Two measurements are missing because the data is not collected, rather than because they were
skipped: **time to first review on your own pull requests** (only reviews *you* gave are stored, so
the clock cannot be started) and **which files a change touched** (only the counts are kept, so there
is no subsystem-ownership or skill timeline). Both need a collector change, not a query.
