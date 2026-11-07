using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class PromptBuilderTests
{
    private readonly PromptBuilder _builder = new();

    [Fact]
    public void Checklist_prompt_includes_platform_framework_work_and_task()
    {
        var prompt = _builder.BuildChecklist(new AiChecklistRequest
        {
            Platform = DevPlatform.Android,
            Framework = DevFramework.Flutter,
            WorkKind = AiWorkKind.BugFix,
            Description = "Crash when rotating the screen"
        });

        Assert.Contains("JSON only", prompt.System, StringComparison.Ordinal);
        Assert.Contains("6 to 12", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Investigation", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Critical", prompt.System, StringComparison.Ordinal);
        Assert.Contains("summary describes this checklist", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Do not copy the task text into the summary", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Do not repeat the task text", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Crash when rotating", prompt.System, StringComparison.Ordinal);
        Assert.Contains("the behavior already exists", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Android", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Flutter", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Bug Fix", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Crash when rotating the screen", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_description_uses_a_generic_task_line()
    {
        var prompt = _builder.BuildChecklist(new AiChecklistRequest
        {
            Platform = DevPlatform.Web,
            Framework = DevFramework.React,
            WorkKind = AiWorkKind.Testing
        });

        Assert.Contains("No extra task detail.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Do not add implementation work.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("React", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Testing", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_description_is_truncated()
    {
        var description = new string('a', PromptBuilder.MaxDescriptionLength) + "SECRET-TAIL";
        var prompt = _builder.BuildChecklist(new AiChecklistRequest
        {
            Platform = DevPlatform.Backend,
            Framework = DevFramework.AspNetCore,
            WorkKind = AiWorkKind.SecurityReview,
            Description = description
        });

        Assert.DoesNotContain("SECRET-TAIL", prompt.User, StringComparison.Ordinal);
        Assert.Contains("ASP.NET Core", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Security Review", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_prompt_names_the_section_and_existing_items()
    {
        var prompt = _builder.BuildSection(
            new AiChecklistRequest
            {
                Platform = DevPlatform.Ios,
                Framework = DevFramework.NativeIos,
                WorkKind = AiWorkKind.CodeReview,
                Description = "Review the camera permission change"
            },
            "Verification",
            ["Reproduce the issue", "Reproduce the issue"]);

        Assert.Contains("Verification", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Reproduce the issue", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Native iOS (Swift)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Code Review / PR", prompt.User, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(prompt.User, "Reproduce the issue"));
    }

    [Fact]
    public void Check_prompt_asks_for_one_item_on_the_open_checklist()
    {
        var prompt = _builder.BuildCheck(new CheckGenerationRequest
        {
            ChecklistTitle = "Login crash",
            WorkType = WorkType.Bug,
            DraftTitle = "Reproduce the crash",
            DraftNotes = "It happens after rotate",
            DraftAcceptance = "The crash happens on rotate",
            DraftItemType = "Investigation",
            DraftPullRequestLinks = "https://example.com/pull/4",
            DraftCategory = "Understand",
            ExistingTitles = ["Open the ticket", "Open the ticket"]
        });

        Assert.Contains("exactly one", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Understand", prompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("6 to 12", prompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("Login crash", prompt.System, StringComparison.Ordinal);
        Assert.Contains("the behavior already exists", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Login crash", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Bug Fix", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Reproduce the crash", prompt.User, StringComparison.Ordinal);
        Assert.Contains("It happens after rotate", prompt.User, StringComparison.Ordinal);
        Assert.Contains("The crash happens on rotate", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Draft type: Investigation", prompt.User, StringComparison.Ordinal);
        Assert.Contains("https://example.com/pull/4", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Do not invent a report", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Understand", prompt.User, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(prompt.User, "Open the ticket"));
    }

    [Fact]
    public void Check_prompt_hides_a_long_draft()
    {
        var notes = new string('a', PromptBuilder.MaxDescriptionLength) + "SECRET-TAIL";
        var prompt = _builder.BuildCheck(new CheckGenerationRequest
        {
            ChecklistTitle = "Payments",
            WorkType = WorkType.Feature,
            DraftNotes = notes
        });

        Assert.DoesNotContain("SECRET-TAIL", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TAIL", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Feature", prompt.User, StringComparison.Ordinal);
        Assert.Contains("None yet.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Draft acceptance: None.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Draft type: None.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Draft test reports: None.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Draft PR links: None.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("none", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Framework_choices_follow_the_platform()
    {
        var android = AiCatalog.FrameworksFor(DevPlatform.Android);
        Assert.Contains(android, choice => choice.Value == DevFramework.NativeAndroid);
        Assert.DoesNotContain(android, choice => choice.Value == DevFramework.AspNetCore);

        var web = AiCatalog.FrameworksFor(DevPlatform.Web);
        Assert.Contains(web, choice => choice.Value == DevFramework.React);
        Assert.Contains(web, choice => choice.Value == DevFramework.Angular);
        Assert.DoesNotContain(web, choice => choice.Value == DevFramework.Maui);

        var backend = AiCatalog.FrameworksFor(DevPlatform.Backend);
        Assert.Contains(backend, choice => choice.Value == DevFramework.Node);
        Assert.DoesNotContain(backend, choice => choice.Value == DevFramework.React);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
