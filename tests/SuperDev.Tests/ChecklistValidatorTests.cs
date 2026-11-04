using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class ChecklistValidatorTests
{
    private readonly ChecklistValidator _validator = new();

    [Fact]
    public void Reads_a_checklist_and_ignores_unexpected_fields()
    {
        const string json = """
            {
              "title": "MAUI Android Bug Fix",
              "summary": "Checklist for diagnosing and fixing the reported issue.",
              "notes": "ignore me",
              "sections": [
                {
                  "name": "Investigation",
                  "owner": "nobody",
                  "items": [
                    {
                      "title": "Reproduce the issue",
                      "description": "Reproduce the issue consistently on the target device.",
                      "type": "Required",
                      "priority": "High",
                      "acceptanceCriteria": "The issue can be reproduced consistently.",
                      "commands": "do not run"
                    }
                  ]
                }
              ]
            }
            """;

        var inspection = _validator.ReadChecklist(json);

        Assert.True(inspection.IsValid);
        Assert.Null(inspection.Failure);
        var checklist = inspection.Checklist!;
        Assert.Equal("MAUI Android Bug Fix", checklist.Title);
        Assert.Equal("Investigation", checklist.Sections[0].Name);
        Assert.Equal("Implementation", checklist.Sections[0].Items[0].Type);
        Assert.Equal("Critical", checklist.Sections[0].Items[0].Priority);
        Assert.Equal("The issue can be reproduced consistently.", checklist.Sections[0].Items[0].AcceptanceCriteria);
    }

    [Fact]
    public void Reads_json_wrapped_in_markdown_and_think_tags()
    {
        const string content = """
            <think>The user wants a checklist.</think>
            ```json
            {
              "title": "Wrapped",
              "summary": "From a fence.",
              "sections": [
                {
                  "name": "Checks",
                  "items": [
                    {
                      "title": "Inspect the diff",
                      "description": "Read the changed files.",
                      "type": "required",
                      "priority": "medium",
                      "acceptance_criteria": "The diff matches the task."
                    }
                  ]
                }
              ]
            }
            ```
            """;

        var inspection = _validator.ReadChecklist(content);

        Assert.True(inspection.IsValid);
        Assert.Equal("Wrapped", inspection.Checklist!.Title);
        Assert.Equal("The diff matches the task.", inspection.Checklist.Sections[0].Items[0].AcceptanceCriteria);
        Assert.Equal("Implementation", inspection.Checklist.Sections[0].Items[0].Type);
        Assert.Equal("Required", inspection.Checklist.Sections[0].Items[0].Priority);
    }

    [Fact]
    public void Repairs_trailing_commas()
    {
        const string json = """
            {
              "title": "Trailing",
              "summary": "Still readable.",
              "sections": [
                {
                  "name": "Checks",
                  "items": [
                    {
                      "title": "Run the tests",
                      "description": "Run the project tests.",
                      "type": "Optional",
                      "priority": "Low",
                      "acceptanceCriteria": "The tests finish.",
                    },
                  ],
                },
              ],
            }
            """;

        var inspection = _validator.ReadChecklist(json);

        Assert.True(inspection.IsValid);
        Assert.Equal("Implementation", inspection.Checklist!.Sections[0].Items[0].Type);
        Assert.Equal("Conditional", inspection.Checklist.Sections[0].Items[0].Priority);
    }

    [Fact]
    public void Rejects_text_that_is_not_json()
    {
        var inspection = _validator.ReadChecklist("Sure, here is a checklist.");

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.InvalidJson, inspection.Failure!.Kind);
        Assert.DoesNotContain("Sure, here", inspection.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_empty_response()
    {
        var inspection = _validator.ReadChecklist("  ");

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.EmptyResponse, inspection.Failure!.Kind);
    }

    [Fact]
    public void Rejects_an_empty_title_and_empty_sections()
    {
        var missingTitle = _validator.ReadChecklist("""
            { "title": " ", "summary": "x", "sections": [ { "name": "A", "items": [ { "title": "Do it", "type": "Required", "priority": "High" } ] } ] }
            """);
        var missingSections = _validator.ReadChecklist("""
            { "title": "Named", "summary": "x", "sections": [] }
            """);

        Assert.Equal(AiFailureKind.MissingTitle, missingTitle.Failure!.Kind);
        Assert.Contains("title", missingTitle.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiFailureKind.NoSections, missingSections.Failure!.Kind);
        Assert.Contains("sections", missingSections.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Removes_duplicate_items_and_invalid_types()
    {
        const string json = """
            {
              "title": "Duplicates",
              "summary": "Checked.",
              "sections": [
                {
                  "name": "Investigation",
                  "items": [
                    { "title": "Reproduce the issue", "description": "First", "type": "Required", "priority": "High", "acceptanceCriteria": "Once" },
                    { "title": " reproduce the issue ", "description": "Second", "type": "Required", "priority": "Low", "acceptanceCriteria": "Twice" },
                    { "title": "Guess", "description": "No", "type": "Banana", "priority": "High", "acceptanceCriteria": "No" }
                  ]
                }
              ]
            }
            """;

        var inspection = _validator.ReadChecklist(json);

        Assert.True(inspection.IsValid);
        Assert.Equal(2, inspection.Checklist!.Sections[0].Items.Count);
        Assert.Equal("Implementation", inspection.Checklist.Sections[0].Items[1].Type);
        Assert.Equal("Critical", inspection.Checklist.Sections[0].Items[1].Priority);
        Assert.Contains("adjusted", inspection.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Banana", inspection.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Reproduce", inspection.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_type_and_priority_use_explicit_defaults_without_a_technical_warning()
    {
        var inspection = _validator.ReadSection("""
            {
              "name": "Verification",
              "items": [
                { "title": "Retest the fix", "description": "Run the same steps.", "acceptanceCriteria": "The bug stays fixed." }
              ]
            }
            """, "Verification");

        Assert.True(inspection.IsValid);
        var item = Assert.Single(inspection.Section!.Items);
        Assert.Equal("Verification", item.Type);
        Assert.Equal(ChecklistVocabulary.DefaultPriority, item.Priority);
        Assert.Equal("Some optional details were filled automatically.", inspection.Notice);
        Assert.DoesNotContain("Retest", inspection.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", inspection.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_drops_blank_titles_and_reports_duplicates()
    {
        var inspection = _validator.Inspect(new GeneratedChecklist
        {
            Title = "Edited",
            Summary = "",
            Sections =
            [
                new GeneratedSection
                {
                    Name = "Checks",
                    Items =
                    [
                        new GeneratedCheckItem { Title = "Run tests", Type = "Required", Priority = "Medium" },
                        new GeneratedCheckItem { Title = "Run tests", Type = "Optional", Priority = "Low" },
                        new GeneratedCheckItem { Title = "  ", Type = "Required", Priority = "High" }
                    ]
                }
            ]
        });

        Assert.True(inspection.IsValid);
        Assert.Single(inspection.Checklist!.Sections[0].Items);
        Assert.Equal(string.Empty, inspection.Notice);
    }

    [Fact]
    public void Reads_the_model_shape_with_section_title_and_implementation_type()
    {
        const string json = """
            {
              "title": "Flutter Login Page Implementation Checklist",
              "summary": "A comprehensive checklist.",
              "owner": "ignored",
              "sections": [
                {
                  "section_title": "Security & Authentication",
                  "notes": "ignored",
                  "items": [
                    {
                      "title": "Implement JWT or OAuth2 authentication",
                      "description": "Configure secure token handling.",
                      "type": "Implementation",
                      "priority": "High",
                      "commands": "do not run"
                    }
                  ]
                }
              ]
            }
            """;

        var inspection = _validator.ReadChecklist(json);

        Assert.True(inspection.IsValid);
        Assert.Equal("Flutter Login Page Implementation Checklist", inspection.Checklist!.Title);
        Assert.Equal("A comprehensive checklist.", inspection.Checklist.Summary);
        var section = Assert.Single(inspection.Checklist.Sections);
        Assert.Equal("Security & Authentication", section.Name);
        var item = Assert.Single(section.Items);
        Assert.Equal("Implement JWT or OAuth2 authentication", item.Title);
        Assert.Equal("Configure secure token handling.", item.Description);
        Assert.Equal("Implementation", item.Type);
        Assert.Equal("Critical", item.Priority);
        Assert.Equal(string.Empty, item.AcceptanceCriteria);

        var saved = AiChecklistMapper.ToItems(inspection.Checklist);
        Assert.Equal("Security & Authentication", saved[0].Category);
        Assert.Equal(ChecklistPriority.Critical, saved[0].Priority);
        Assert.Contains("Configure secure token handling.", saved[0].Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_name_when_section_title_is_absent()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Named sections",
              "sections": [
                {
                  "name": "Investigation",
                  "items": [
                    { "title": "Reproduce the crash", "type": "required", "priority": "HIGH" }
                  ]
                }
              ]
            }
            """);

        Assert.True(inspection.IsValid);
        Assert.Equal(string.Empty, inspection.Checklist!.Summary);
        Assert.Equal("Investigation", inspection.Checklist.Sections[0].Name);
        Assert.Equal("Implementation", inspection.Checklist.Sections[0].Items[0].Type);
        Assert.Equal("Critical", inspection.Checklist.Sections[0].Items[0].Priority);
        Assert.Equal(string.Empty, inspection.Checklist.Sections[0].Items[0].Description);
    }

    [Fact]
    public void Keeps_valid_items_when_another_item_or_section_is_empty()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Mixed",
              "summary": "Some items are incomplete.",
              "sections": [
                { "section_title": "Empty", "items": [] },
                {
                  "name": "Useful",
                  "items": [
                    { "description": "No title here", "type": "Optional", "priority": "Low" },
                    { "title": "Check the login form", "type": "oPtIoNaL", "priority": "mEdIuM" }
                  ]
                }
              ]
            }
            """);

        Assert.True(inspection.IsValid);
        var section = Assert.Single(inspection.Checklist!.Sections);
        Assert.Equal("Useful", section.Name);
        var item = Assert.Single(section.Items);
        Assert.Equal("Check the login form", item.Title);
        Assert.Equal("Implementation", item.Type);
        Assert.Equal("Required", item.Priority);
        Assert.Contains("adjusted", inspection.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No title here", inspection.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_no_valid_items_when_every_item_lacks_a_title()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Empty items",
              "sections": [
                { "section_title": "Security", "items": [ { "description": "Missing the title", "type": "Implementation", "priority": "High" } ] }
              ]
            }
            """);

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.NoValidItems, inspection.Failure!.Kind);
        Assert.Contains("title", inspection.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Missing the title", inspection.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_completed_items_when_the_json_is_cut_off_mid_string()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Flutter Login Page Implementation Checklist",
              "summary": "A comprehensive checklist.",
              "sections": [
                {
                  "section_title": "Security & Authentication",
                  "items": [
                    {
                      "title": "Implement JWT or OAuth2 authentication",
                      "description": "Configure secure token handling.",
                      "type": "Implementation",
                      "priority": "High"
                    },
                    {
                      "title": "Store tokens securely",
                      "description": "Use the platform keych
            """);

        Assert.True(inspection.IsValid);
        Assert.Contains("adjusted", inspection.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JSON", inspection.Notice, StringComparison.OrdinalIgnoreCase);
        var section = Assert.Single(inspection.Checklist!.Sections);
        Assert.Equal("Security & Authentication", section.Name);
        Assert.Equal(2, section.Items.Count);
        Assert.Equal("Implement JWT or OAuth2 authentication", section.Items[0].Title);
        Assert.Equal("Implementation", section.Items[0].Type);
        Assert.Equal("Critical", section.Items[0].Priority);
        Assert.Equal("Store tokens securely", section.Items[1].Title);
    }

    [Fact]
    public void Drops_an_unfinished_item_after_a_trailing_comma()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Flutter Login Page Implementation Checklist",
              "sections": [
                {
                  "section_title": "Security & Authentication",
                  "items": [
                    {
                      "title": "Implement JWT or OAuth2 authentication",
                      "description": "Configure secure token handling.",
                      "type": "Implementation",
                      "priority": "High"
                    },
            """);

        Assert.True(inspection.IsValid);
        var item = Assert.Single(inspection.Checklist!.Sections[0].Items);
        Assert.Equal("Implement JWT or OAuth2 authentication", item.Title);
        Assert.Contains("adjusted", inspection.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JSON", inspection.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_each_supported_type_and_priority()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Supported values",
              "sections": [
                {
                  "section_title": "Login email validation",
                  "items": [
                    { "title": "Reproduce the invalid email", "type": "Investigation", "priority": "Critical" },
                    { "title": "Correct the email check", "type": "Implementation", "priority": "Required" },
                    { "title": "Try empty and malformed addresses", "type": "Verification", "priority": "Conditional" },
                    { "title": "Note the accepted format", "type": "documentation", "priority": "review" }
                  ]
                }
              ]
            }
            """);

        Assert.True(inspection.IsValid);
        Assert.Equal("Checklist generated successfully.", inspection.Notice);
        Assert.False(inspection.NoticeIsAdjustment);
        var items = inspection.Checklist!.Sections[0].Items;
        Assert.Equal("Investigation", items[0].Type);
        Assert.Equal("Implementation", items[1].Type);
        Assert.Equal("Verification", items[2].Type);
        Assert.Equal("Documentation", items[3].Type);
        Assert.Equal("Critical", items[0].Priority);
        Assert.Equal("Required", items[1].Priority);
        Assert.Equal("Conditional", items[2].Priority);
        Assert.Equal("Review", items[3].Priority);
        Assert.Equal(ChecklistPriority.Critical, AiChecklistMapper.ToPriority(items[0]));
        Assert.Equal(ChecklistPriority.Required, AiChecklistMapper.ToPriority(items[1]));
        Assert.Equal(ChecklistPriority.Conditional, AiChecklistMapper.ToPriority(items[2]));
        Assert.Equal(ChecklistPriority.Review, AiChecklistMapper.ToPriority(items[3]));
    }

    [Fact]
    public void Rewrites_an_unsupported_type_without_naming_it()
    {
        var inspection = _validator.ReadChecklist("""
            {
              "title": "Adjusted",
              "sections": [
                {
                  "name": "Fix",
                  "items": [
                    { "title": "Inspect the validator", "type": "Banana", "priority": "Required" },
                    { "title": "Retest login", "type": "Verification", "priority": "Required" }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("Implementation", inspection.Checklist!.Sections[0].Items[0].Type);
        Assert.Equal("Verification", inspection.Checklist.Sections[0].Items[1].Type);
        Assert.Equal("1 item was adjusted to match Super Dev's checklist format.", inspection.Notice);
        Assert.True(inspection.NoticeIsAdjustment);
        Assert.DoesNotContain("Banana", inspection.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_one_check_and_keeps_known_values()
    {
        var inspection = _validator.ReadCheck("""
            {
              "title": "Retest the login",
              "category": "Testing",
              "priority": "Required",
              "description": "Run the same steps.",
              "acceptanceCriteria": "The crash is gone.",
              "subtasks": ["Open the screen", { "title": "Sign in" }]
            }
            """);

        Assert.True(inspection.IsValid);
        var check = inspection.Check!;
        Assert.Equal("Retest the login", check.Title);
        Assert.Equal("Testing", check.Category);
        Assert.Equal("Required", check.Priority);
        Assert.Equal(["Open the screen", "Sign in"], check.Subtasks);
        Assert.Equal("Checklist generated successfully.", inspection.Notice);
    }

    [Fact]
    public void Adjusts_a_check_into_the_form_categories()
    {
        var inspection = _validator.ReadCheck("""
            {
              "title": "Reproduce the crash",
              "category": "Investigation",
              "priority": "High",
              "description": "Open the screen and rotate it.",
              "subtasks": ["Open the screen", "", "Open the screen"]
            }
            """);

        Assert.True(inspection.IsValid);
        Assert.Equal("Understand", inspection.Check!.Category);
        Assert.Equal("Critical", inspection.Check.Priority);
        Assert.Equal(["Open the screen"], inspection.Check.Subtasks);
        Assert.True(inspection.NoticeIsAdjustment);
        Assert.DoesNotContain("Investigation", inspection.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_check_without_a_title()
    {
        var inspection = _validator.ReadCheck("""{ "description": "No title", "category": "Testing" }""");

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.NoValidItems, inspection.Failure!.Kind);
    }

    [Fact]
    public void Reads_the_first_item_when_the_model_returns_a_checklist()
    {
        var inspection = _validator.ReadCheck("""
            {
              "title": "Ignored checklist title",
              "sections": [
                {
                  "name": "Testing",
                  "items": [
                    {
                      "title": "Retest the fix",
                      "description": "Run the same steps.",
                      "priority": "Required",
                      "acceptanceCriteria": "The crash is gone."
                    }
                  ]
                }
              ]
            }
            """);

        Assert.True(inspection.IsValid);
        Assert.Equal("Retest the fix", inspection.Check!.Title);
        Assert.Equal("Testing", inspection.Check.Category);
        Assert.Equal("The crash is gone.", inspection.Check.AcceptanceCriteria);
    }
}
