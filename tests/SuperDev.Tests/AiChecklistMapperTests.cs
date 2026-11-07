using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class AiChecklistMapperTests
{
    [Fact]
    public void Maps_priority_notes_and_work_type_onto_checklist_items()
    {
        var request = new AiChecklistRequest
        {
            Platform = DevPlatform.Android,
            Framework = DevFramework.Maui,
            WorkKind = AiWorkKind.BugFix
        };
        var items = AiChecklistMapper.ToItems(new GeneratedChecklist
        {
            Title = "MAUI Android Bug Fix",
            Sections =
            [
                new GeneratedSection
                {
                    Name = "Investigation",
                    Items =
                    [
                        new GeneratedCheckItem
                        {
                            Title = "Reproduce the issue",
                            Description = "Reproduce the issue consistently on the target device.",
                            Type = "Investigation",
                            Priority = "High",
                            AcceptanceCriteria = "The issue can be reproduced consistently.",
                            Logs = "crash after rotate",
                            PullRequestLinks = "https://example.com/pull/4"
                        },
                        new GeneratedCheckItem
                        {
                            Title = "Check a related screen",
                            Description = "Look at the nearest flow.",
                            Type = "Optional",
                            Priority = "Medium",
                            AcceptanceCriteria = ""
                        }
                    ]
                }
            ]
        });

        Assert.Equal(WorkType.Bug, AiChecklistMapper.ToWorkType(AiWorkKind.BugFix));
        Assert.Equal(WorkType.Custom, AiChecklistMapper.ToWorkType(AiWorkKind.SecurityReview));
        Assert.Equal("AI · .NET MAUI · Bug Fix", AiChecklistMapper.TemplateName(request));
        Assert.Equal(".NET MAUI · Bug Fix", AiChecklistMapper.VisibleTemplateName(AiChecklistMapper.TemplateName(request)));
        Assert.Equal("Bug Fix", AiChecklistMapper.VisibleTemplateName("Bug Fix"));
        Assert.Equal(string.Empty, AiChecklistMapper.VisibleTemplateName("  AI ·  "));
        Assert.Equal("Investigation", items[0].Category);
        Assert.Equal(ChecklistPriority.Critical, items[0].Priority);
        Assert.Equal("Reproduce the issue consistently on the target device.", items[0].Notes);
        Assert.Equal("The issue can be reproduced consistently.", items[0].Acceptance);
        Assert.Equal("Investigation", items[0].ItemType);
        Assert.Equal("crash after rotate", items[0].Logs);
        Assert.Equal("https://example.com/pull/4", items[0].PullRequestLinks);
        Assert.Null(items[0].TestReports);
        Assert.Equal(ChecklistPriority.Required, items[1].Priority);
        Assert.Equal("Look at the nearest flow.", items[1].Notes);
        Assert.Null(items[1].Acceptance);
        Assert.Null(items[1].ItemType);
    }

    [Fact]
    public void Priority_ignores_item_type()
    {
        Assert.Equal(ChecklistPriority.Critical, AiChecklistMapper.ToPriority(Item("Documentation", "Critical")));
        Assert.Equal(ChecklistPriority.Required, AiChecklistMapper.ToPriority(Item("Optional", "Required")));
        Assert.Equal(ChecklistPriority.Conditional, AiChecklistMapper.ToPriority(Item("Implementation", "Conditional")));
        Assert.Equal(ChecklistPriority.Review, AiChecklistMapper.ToPriority(Item("Investigation", "Review")));
        Assert.Equal(ChecklistPriority.Critical, AiChecklistMapper.ToPriority(Item("Implementation", "High")));
        Assert.Equal(ChecklistPriority.Required, AiChecklistMapper.ToPriority(Item("Implementation", "")));
        Assert.Equal(ChecklistPriority.Conditional, AiChecklistMapper.ToPriority(Item("Implementation", "Low")));
    }

    private static GeneratedCheckItem Item(string type, string priority) => new()
    {
        Title = "Check",
        Type = type,
        Priority = priority
    };
}
