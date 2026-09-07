using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TrackMeBaby.Mcp;

/// <summary>
/// The recurring workflows from the plan (sections 17-19), exposed as MCP prompts so they are
/// one click in Claude Desktop or a slash command in VS Code rather than a paragraph retyped
/// every Friday.
/// </summary>
[McpServerPromptType]
public static class ReviewPrompts
{
    [McpServerPrompt(Name = "weekly_review")]
    [Description("Produce this week's performance summary from the tracked evidence.")]
    public static string WeeklyReview(
        [Description("Which week: this_week or last_week (default this_week).")] string period = "this_week") =>
        $"""
        Prepare my weekly performance summary for {period}.

        Steps:
        1. Call get_my_performance with period = "{period}".
        2. Call search_memory for the same period to pick up context the activity cannot show.
        3. If the sync looks stale or the period looks empty, say so instead of guessing.

        Structure the output as:
        - Completed this week
        - Major contributions (what actually mattered, not the longest list)
        - Problems solved
        - Reviews and help given to others
        - Currently in progress
        - Learnings
        - Next focus

        Rules: cite specific pull requests, issues and board items as evidence. Lead with impact
        and use counts only as support. Do not invent anything that is not in the tool results.

        Finish by asking whether I want this stored with save_performance_snapshot, and offer to
        record anything I explain in the conversation as a memory.
        """;

    [McpServerPrompt(Name = "monthly_review")]
    [Description("Produce a monthly performance summary and compare it against the previous month.")]
    public static string MonthlyReview(
        [Description("Which month: this_month or last_month (default last_month).")] string period = "last_month") =>
        $"""
        Prepare my monthly performance summary for {period}.

        Steps:
        1. Call get_my_performance with period = "{period}" and detail_limit = 120.
        2. Call list_performance_snapshots to read the weekly summaries inside this month.
        3. Call search_memory for the month.

        Structure the output as:
        - Headline: what this month was actually about
        - Volume: engineering activity, with the previous period's numbers alongside for context
        - Major contributions, ranked by impact
        - Problems solved and what caused them
        - Growth: what I can do now that I could not before
        - Collaboration: reviews, mentoring, unblocking others
        - Challenges and what slowed me down
        - Areas to improve
        - Evidence appendix: the pull requests, issues and memories behind each claim

        Use the previous-period metrics in the response to describe direction of travel, and be
        honest when something went down. Offer to save the result as a monthly snapshot.
        """;

    [McpServerPrompt(Name = "six_month_appraisal")]
    [Description("Produce an evidence-backed six-month appraisal document.")]
    public static string SixMonthAppraisal(
        [Description("Range to cover, e.g. last_6_months, or a start date like 2026-02-01.")] string period = "last_6_months") =>
        $"""
        Prepare my six-month performance review covering {period}. This is for a real appraisal
        conversation, so accuracy matters more than volume.

        Gather everything first:
        1. get_my_performance for the whole range with detail_limit = 300.
        2. list_performance_snapshots to reuse the weekly and monthly summaries already written.
        3. search_memory across the range, and again per theme once you can see the themes.
        4. get_repositories for the range to show project breadth.
        5. get_sync_status to confirm the underlying data is complete, and state any gap.

        Then produce, in this order:
        - Executive summary (5-8 lines a busy manager can read)
        - Major projects, each with my specific role
        - Major contributions
        - Problems solved, with cause and outcome
        - Technical growth
        - Leadership and ownership
        - Collaboration
        - Challenges
        - Impact
        - Areas for improvement
        - Next goals
        - Evidence: a table of the pull requests, issues, board items and memories behind each claim

        Rules:
        - Every claim traces to something in the tool results. No invented achievements.
        - Prefer "resolved the reporting outage caused by an unapplied migration" over "25 commits".
        - Where the evidence is thin, say the evidence is thin rather than padding it.
        - Point out anything I appear to have done that has no memory attached, so I can add the
          context before the appraisal.
        """;

    [McpServerPrompt(Name = "delivery_review")]
    [Description("Read the DORA and flow metrics for a period and explain what they say about how I work.")]
    public static string DeliveryReview(
        [Description("Period to cover (default last_90_days).")] string period = "last_90_days") =>
        $"""
        Walk me through my delivery performance for {period}.

        1. Call get_dora_metrics with period = "{period}".
        2. Call get_flow_metrics for the same period.
        3. Call get_my_performance if you need the evidence rows behind a claim.

        Then explain, per environment:
        - The cadence, and whether the four metrics agree with each other. Throughput rising while
          change failure rate rises is a different story from both improving.
        - Where the time actually goes: lead time versus time in review, and what the p90 says that
          the median does not.
        - What the work was made of, and whether the mix looks deliberate.
        - Whether anything is piling up: open work by age, board columns absorbing time.

        Rules:
        - Quote every median with its sample size. A median over four changes is an anecdote.
        - Name the band, but say plainly that it is a team benchmark applied to one person's slice,
          and never present it as a grade.
        - Pass on the notes from both tools. They say what is missing, and a metric whose caveat was
          dropped is worse than no metric.
        - Offer to run export_report if I want this as a file to send, or point me at /dashboard to
          look at it live.
        """;

    [McpServerPrompt(Name = "capture_context")]
    [Description("Walk through recent work and record the missing context as memories.")]
    public static string CaptureContext(
        [Description("Period to review (default last_7_days).")] string period = "last_7_days") =>
        $"""
        Help me capture the story behind my recent work for {period}.

        1. Call get_my_completed_work for the period.
        2. Call search_memory for the same period to see what is already recorded.
        3. List the finished work that has no memory attached.
        4. Ask me about those items a few at a time — what the problem was, what I decided, what
           the impact was, what I learned. Keep it short; do not interrogate me.
        5. Save each answer with save_memory, choosing the right kind and linking the related
           activity ids and repository/number so the memory stays attached to its evidence.

        This is the input that makes an appraisal readable six months from now, so favour
        specifics: causes, decisions, and consequences.
        """;
}
