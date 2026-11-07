using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class ChecklistSummaryTests
{
    [Fact]
    public void A_summary_that_is_not_the_task_is_kept()
    {
        var summary = ChecklistSummary.ForChecklist(
            "Diagnose the crash and confirm the surrounding flow.",
            "Crash when rotating the screen",
            Sample());

        Assert.Equal("Diagnose the crash and confirm the surrounding flow.", summary);
    }

    [Fact]
    public void A_copied_task_is_replaced_with_the_checks()
    {
        var summary = ChecklistSummary.ForChecklist(
            "Crash when rotating the screen",
            "Crash when rotating the screen",
            Sample());

        Assert.Equal(
            "Investigation: Reproduce the crash — Rotate the phone on the login screen.\nImplementation: Correct the layout — Keep the form visible after rotation.",
            summary);
        Assert.False(ChecklistSummary.MatchesTask(summary, "Crash when rotating the screen"));
    }

    [Fact]
    public void A_copy_that_differs_only_by_spacing_or_punctuation_is_replaced()
    {
        var summary = ChecklistSummary.ForChecklist(
            "  crash   WHEN rotating the screen. ",
            "Crash when rotating the screen",
            Sample());

        Assert.Contains("Reproduce the crash", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("crash WHEN rotating", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_task_cut_at_the_summary_limit_is_still_a_copy()
    {
        var task = new string('a', ChecklistValidator.MaxSummaryLength + 40);
        var copied = task[..ChecklistValidator.MaxSummaryLength];

        Assert.True(ChecklistSummary.MatchesTask(copied, task));

        var summary = ChecklistSummary.ForChecklist(copied, task, Sample());
        Assert.Contains("Reproduce the crash", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(copied, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_summary_is_written_from_the_checks()
    {
        var checklist = new GeneratedChecklist
        {
            Title = "Login",
            Sections =
            [
                new GeneratedSection
                {
                    Name = "Verification",
                    Items =
                    [
                        new GeneratedCheckItem
                        {
                            Title = "Retest login",
                            AcceptanceCriteria = "Sign-in still works."
                        }
                    ]
                }
            ]
        };

        var summary = ChecklistSummary.ForChecklist("  ", "Crash when rotating", checklist);

        Assert.Equal("Verification: Retest login — Sign-in still works.", summary);
    }

    [Fact]
    public void A_checklist_with_no_checks_does_not_keep_the_task_as_its_summary()
    {
        var summary = ChecklistSummary.ForChecklist(
            "Crash when rotating",
            "Crash when rotating",
            new GeneratedChecklist { Title = "Empty" });

        Assert.Equal(string.Empty, summary);
    }

    private static GeneratedChecklist Sample() => new()
    {
        Title = "Rotation crash",
        Sections =
        [
            new GeneratedSection
            {
                Name = "Investigation",
                Items =
                [
                    new GeneratedCheckItem
                    {
                        Title = "Reproduce the crash",
                        Description = "Rotate the phone on the login screen."
                    }
                ]
            },
            new GeneratedSection
            {
                Name = "Implementation",
                Items =
                [
                    new GeneratedCheckItem
                    {
                        Title = "Correct the layout",
                        Description = "Keep the form visible after rotation."
                    }
                ]
            }
        ]
    };
}
