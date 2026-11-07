using SuperDev.Services;

namespace SuperDev.Tests;

public class TemplateDescriptionTests
{
    [Theory]
    [InlineData("bug", "Find the cause, fix it, and confirm the bug is gone.")]
    [InlineData("feature", "Build the requested behavior and verify it works.")]
    [InlineData("development", "Implement the change, test it, and prepare the review.")]
    [InlineData("testing", "Cover the main flow, edge cases, and regressions.")]
    [InlineData("pullrequest", "Review the diff, confirm the checks, and merge.")]
    [InlineData("daily", "Track the work you plan to finish today.")]
    [InlineData("blank", "Start from an empty checklist.")]
    public void Predefined_templates_keep_a_fixed_description(string id, string description)
    {
        Assert.Equal(description, ChecklistTemplateService.PredefinedDescription(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom")]
    public void Custom_templates_have_no_static_description(string? id)
    {
        Assert.Null(ChecklistTemplateService.PredefinedDescription(id));
    }
}
