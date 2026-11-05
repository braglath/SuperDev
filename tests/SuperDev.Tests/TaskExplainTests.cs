using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class TaskExplainTests
{
    private readonly TaskExplainPromptBuilder _prompts = new();
    private readonly TaskExplainValidator _validator = new();

    [Fact]
    public void Prompt_explains_one_check_and_keeps_its_notes()
    {
        var prompt = _prompts.Build(new TaskExplainRequest
        {
            ChecklistTitle = "Login rotation crash",
            TaskDescription = "Crash when rotating the screen",
            Platform = "Android",
            Framework = "Flutter",
            WorkType = WorkType.Bug,
            Title = "Reproduce the crash",
            Category = "Investigation",
            Priority = "Critical",
            Notes = "Rotate from portrait to landscape on the login screen.",
            Acceptance = "The crash is visible in the log.",
            ItemType = "Verification",
            PullRequestLinks = "https://example.com/pull/4",
            Subtasks =
            [
                new TaskExplainChild { Title = "Use a phone", IsCompleted = true },
                new TaskExplainChild { Title = "Use a tablet", IsCompleted = false }
            ],
            Links =
            [
                new TaskExplainChild { Title = "Device matrix", IsCompleted = false, Optional = true }
            ]
        });

        Assert.Contains("JSON only", prompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("Reproduce the crash", prompt.System, StringComparison.Ordinal);
        Assert.Contains("the behavior already exists", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Login rotation crash", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Crash when rotating the screen", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Android", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Flutter", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Bug Fix", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Reproduce the crash", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Investigation", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Critical", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Status: open", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Rotate from portrait to landscape", prompt.User, StringComparison.Ordinal);
        Assert.Contains("The crash is visible in the log.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Type: Verification", prompt.User, StringComparison.Ordinal);
        Assert.Contains("PR links: https://example.com/pull/4", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Use a phone (done)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Use a tablet (open)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Device matrix (optional, open)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Explain this check only.", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_notes_are_truncated()
    {
        var notes = new string('a', TaskExplainPromptBuilder.MaxNotes) + "SECRET-TAIL";
        var prompt = _prompts.Build(new TaskExplainRequest
        {
            WorkType = WorkType.Testing,
            Title = "Retest login",
            Notes = notes
        });

        Assert.DoesNotContain("SECRET-TAIL", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Do not turn it into implementation work.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Subtasks: none", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Status: open", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_check_is_marked_done()
    {
        var prompt = _prompts.Build(new TaskExplainRequest
        {
            WorkType = WorkType.Feature,
            Title = "Empty state",
            IsCompleted = true
        });

        Assert.Contains("Status: completed", prompt.User, StringComparison.Ordinal);
        Assert.Contains("do not tell them to do it again", prompt.System, StringComparison.Ordinal);
        Assert.Contains("A skipped check was set aside", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Notes: No notes.", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Skipped_check_is_not_described_as_open()
    {
        var prompt = _prompts.Build(new TaskExplainRequest
        {
            WorkType = WorkType.Bug,
            Title = "Rotate the screen",
            Priority = "Critical",
            IsSkipped = true,
            IsCompleted = true
        });

        Assert.Contains("Status: skipped", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Status: completed", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Status: open", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_splits_manual_subtasks_from_links()
    {
        var item = new ChecklistItem
        {
            Title = "  Ship the fix  ",
            Category = " Verification ",
            Priority = ChecklistPriority.Required,
            Notes = "Cover the crash.\n\nAcceptance: The crash is gone.",
            ItemType = "verification",
            Logs = "crash after rotate",
            IsCompleted = true,
            Subtasks =
            [
                new ChecklistSubtask { Title = "Write the test", IsCompleted = false },
                new ChecklistSubtask
                {
                    Title = "QA checklist",
                    IsCompleted = true,
                    LinkedFilePath = "/tmp/qa.json",
                    LinkOptional = true
                }
            ]
        };

        var request = TaskExplainMapper.Create(
            " Login ",
            WorkType.Bug,
            "  ",
            " Android ",
            null,
            item);

        Assert.Equal("Login", request.ChecklistTitle);
        Assert.Equal("Ship the fix", request.Title);
        Assert.Equal("Verification", request.Category);
        Assert.Equal("Required", request.Priority);
        Assert.Equal("Cover the crash.", request.Notes);
        Assert.Equal("The crash is gone.", request.Acceptance);
        Assert.Equal("Verification", request.ItemType);
        Assert.Equal("crash after rotate", request.Logs);
        Assert.True(request.IsCompleted);
        Assert.Null(request.TaskDescription);
        Assert.Equal("Android", request.Platform);
        Assert.Null(request.Framework);
        var subtask = Assert.Single(request.Subtasks);
        Assert.Equal("Write the test", subtask.Title);
        Assert.False(subtask.IsCompleted);
        var link = Assert.Single(request.Links);
        Assert.Equal("QA checklist", link.Title);
        Assert.True(link.Optional);
        Assert.True(link.IsCompleted);
    }

    [Fact]
    public void Reads_a_valid_explanation()
    {
        const string json = """
            {
              "summary": "Reproduce the login crash by rotating the phone.",
              "whyItMatters": "The fix cannot be confirmed until the crash is seen.",
              "steps": [
                "1. Open the login screen.",
                "Rotate from portrait to landscape."
              ],
              "doneWhen": "The crash happens on rotate, or you can say it no longer does."
            }
            """;

        var inspection = _validator.Read(json);

        Assert.True(inspection.IsValid);
        Assert.Equal("Reproduce the login crash by rotating the phone.", inspection.Explanation!.Summary);
        Assert.Equal("The fix cannot be confirmed until the crash is seen.", inspection.Explanation.WhyItMatters);
        Assert.Equal(["Open the login screen.", "Rotate from portrait to landscape."], inspection.Explanation.Steps);
        Assert.Contains("no longer does", inspection.Explanation.DoneWhen, StringComparison.Ordinal);
        Assert.Empty(inspection.Warnings);
    }

    [Fact]
    public void Reads_json_wrapped_in_markdown_and_a_step_string()
    {
        const string json = """
            <think>Look at the check.</think>
            ```json
            {
              "summary": "Confirm the login still works.",
              "why_it_matters": "A rotation fix can break sign-in.",
              "steps": "Open login\n- Sign in with a test account",
              "done_when": "Sign-in succeeds."
            }
            ```
            """;

        var inspection = _validator.Read(json);

        Assert.True(inspection.IsValid);
        Assert.Equal("Confirm the login still works.", inspection.Explanation!.Summary);
        Assert.Equal("A rotation fix can break sign-in.", inspection.Explanation.WhyItMatters);
        Assert.Equal(["Open login", "Sign in with a test account"], inspection.Explanation.Steps);
        Assert.Equal("Sign-in succeeds.", inspection.Explanation.DoneWhen);
    }

    [Fact]
    public void Extra_steps_are_cut_and_a_missing_summary_is_rejected()
    {
        var inspection = _validator.Read("""
            {
              "summary": "Check the crash.",
              "steps": ["one", "two", "three", "four", "five", "six", "seven"]
            }
            """);

        Assert.True(inspection.IsValid);
        Assert.Equal(6, inspection.Explanation!.Steps.Count);
        Assert.Equal("six", inspection.Explanation.Steps[5]);
        Assert.Contains(TaskExplainValidator.TruncatedWarning, inspection.Warnings);

        var missing = _validator.Read("""{ "steps": ["Look again."] }""");
        Assert.False(missing.IsValid);
        Assert.Equal(AiFailureKind.InvalidChecklist, missing.FailureKind);

        var empty = _validator.Read("   ");
        Assert.Equal(AiFailureKind.EmptyResponse, empty.FailureKind);

        var broken = _validator.Read("not json");
        Assert.Equal(AiFailureKind.InvalidJson, broken.FailureKind);
    }
}
