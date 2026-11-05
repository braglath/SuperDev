using System.Net;
using System.Text;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class ChecklistReviewTests
{
    private readonly ChecklistReviewValidator _validator = new();
    private readonly ChecklistReviewPromptBuilder _prompts = new();

    [Fact]
    public void Reads_a_valid_review_and_keeps_the_same_id()
    {
        var (request, first, _) = Sample();
        var json = Review(Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition()));

        var firstRead = _validator.Read(json, request);
        var secondRead = _validator.Read(json, request);

        Assert.True(firstRead.IsValid);
        var finding = Assert.Single(firstRead.Review!.Findings);
        Assert.Equal(ReviewCategory.Testing, finding.Category);
        Assert.Equal(ReviewSeverity.High, finding.Severity);
        Assert.Equal(ReviewRecommendationType.AddItem, finding.RecommendationType);
        Assert.Equal("Missing rotation regression", finding.Title);
        Assert.Equal("Add a rotation regression test", finding.SuggestedItem!.Title);
        Assert.False(string.IsNullOrWhiteSpace(finding.Id));
        Assert.Equal(finding.Id, secondRead.Review!.Findings[0].Id);
        Assert.NotEqual(first.ToString(), finding.Id);
    }

    [Fact]
    public void Reads_json_wrapped_in_markdown_and_think_tags()
    {
        var (request, _, _) = Sample();
        var json = """
            <think>Look at the checklist.</think>
            ```json
            { "findings": [] }
            ```
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        Assert.Empty(inspection.Review!.Findings);
    }

    [Fact]
    public void Recovers_an_add_item_when_the_model_puts_the_check_in_the_action()
    {
        var (request, _, _) = Sample();
        const string json = """
            {
              "findings": [
                {
                  "category": "Testing",
                  "severity": "High",
                  "title": "Missing regression coverage",
                  "explanation": "The rotation crash has no regression check.",
                  "whyItMatters": "The crash can return.",
                  "suggestedAction": "Add a new check item: 'Verify login after rotation.'",
                  "recommendationType": "AddItem"
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var finding = Assert.Single(inspection.Review!.Findings);
        Assert.Equal(ReviewRecommendationType.AddItem, finding.RecommendationType);
        Assert.Equal("Verify login after rotation", finding.SuggestedItem!.Title);
        Assert.Equal("The rotation crash has no regression check.", finding.SuggestedItem.Description);
    }

    [Fact]
    public void An_instruction_about_existing_checks_updates_those_checks()
    {
        var (items, first, second, request) = Items();
        const string json = """
            {
              "findings": [
                {
                  "category": "Completeness",
                  "severity": "Critical",
                  "title": "Missing functional and edge-case checks in testing scope",
                  "explanation": "Item i1 (Reproduce the crash) and Item i2 (Retest login) are marked as open, indicating a gap in testing coverage.",
                  "whyItMatters": "Failure modes can ship without a check.",
                  "suggestedAction": "Add missing functional and edge-case checks to Item i1 and i2 to ensure comprehensive testing coverage.",
                  "recommendationType": "AddItem"
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var finding = Assert.Single(inspection.Review!.Findings);
        Assert.Equal(ReviewRecommendationType.ImproveItem, finding.RecommendationType);
        Assert.Contains(first.Id, finding.RelatedItemIds);
        Assert.Contains(second.Id, finding.RelatedItemIds);
        Assert.True(string.IsNullOrWhiteSpace(finding.SuggestedItem!.Title));
        Assert.Contains("comprehensive testing coverage", finding.SuggestedItem.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Item i1", finding.SuggestedItem.Description, StringComparison.Ordinal);

        var result = ChecklistReviewApplicator.Apply(items, finding);
        Assert.True(result.Changed);

        Assert.Equal(2, items.Count);
        Assert.Equal(first.Id, items[0].Id);
        Assert.Equal("Reproduce the crash", items[0].Title);
        Assert.Contains("Rotate the screen.", items[0].Notes, StringComparison.Ordinal);
        Assert.Contains("comprehensive testing coverage", items[0].Notes, StringComparison.Ordinal);
        Assert.False(items[0].IsCompleted);
        Assert.Equal(second.Id, items[1].Id);
        Assert.Equal("Retest login", items[1].Title);
        Assert.Contains("Sign in still works.", items[1].Notes, StringComparison.Ordinal);
        Assert.True(items[1].IsCompleted);

        var again = ChecklistReviewMapper.CreateRequest(
            request.Title,
            request.WorkType,
            request.TaskDescription,
            request.Platform,
            request.Framework,
            null,
            items);
        var rerun = _validator.Read(json, again);
        Assert.True(rerun.IsValid);
        Assert.Empty(rerun.Review!.Findings);
        Assert.Contains(ChecklistReviewValidator.CoveredWarning, rerun.Warnings);

        var removed = ChecklistReviewApplicator.Revert(items, finding);
        Assert.True(removed.Changed);
        Assert.False(finding.Applied);
        Assert.Equal(2, items.Count);
        Assert.Equal("Rotate the screen.\n\nAcceptance: The crash happens on rotate.", items[0].Notes);
        Assert.Equal("Sign in still works.", items[1].Notes);
        Assert.False(items[0].IsCompleted);
        Assert.True(items[1].IsCompleted);
    }

    [Fact]
    public void Suggestions_for_the_same_check_become_one_review()
    {
        var (request, first, second) = Sample();
        var json = Review(
            Finding("Clarity", "Low", "Name the screen", "ImproveItem", related: "\"i1\"", suggested: "{\"description\":\"Name the login screen.\"}"),
            Finding("Testing", "High", "Cover rotation", "ImproveItem", related: "\"i1\"", suggested: "{\"description\":\"Rotate and sign in again.\"}"),
            Finding("Priority", "Medium", "Retest should be critical", "PriorityAdjustment", related: "\"i2\"", suggested: "{\"priority\":\"Critical\"}"));

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        Assert.Equal(2, inspection.Review!.Findings.Count);
        var combined = Assert.Single(inspection.Review.Findings, finding => finding.RelatedItemIds.Contains(first));
        Assert.Equal(ReviewSeverity.High, combined.Severity);
        Assert.Equal("Cover rotation", combined.Title);
        Assert.Contains("Name the screen", combined.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains("Rotate and sign in again.", combined.SuggestedItem!.Description, StringComparison.Ordinal);
        Assert.Contains("Name the login screen.", combined.SuggestedItem.Description, StringComparison.Ordinal);
        var other = Assert.Single(inspection.Review.Findings, finding => finding.RelatedItemIds.Contains(second));
        Assert.Equal(ReviewRecommendationType.PriorityAdjustment, other.RecommendationType);
    }

    [Fact]
    public void Removing_an_added_check_leaves_the_other_checks_unchanged()
    {
        var (items, first, second, request) = Items();
        var finding = One(Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition()), request);

        var applied = ChecklistReviewApplicator.Apply(items, finding);
        Assert.True(applied.Changed);
        Assert.Equal(3, items.Count);

        var removed = ChecklistReviewApplicator.Revert(items, finding);

        Assert.True(removed.Changed);
        Assert.False(finding.Applied);
        Assert.Equal(2, items.Count);
        Assert.Equal(first.Id, items[0].Id);
        Assert.Equal(second.Id, items[1].Id);
        Assert.False(items[0].IsCompleted);
        Assert.True(items[1].IsCompleted);
        Assert.Equal("Sign in still works.", items[1].Notes);
    }

    [Fact]
    public void Selection_is_offered_only_when_more_than_one_suggestion_can_be_applied()
    {
        var one = new ChecklistReviewResult
        {
            Findings =
            [
                new ChecklistReviewFinding
                {
                    RecommendationType = ReviewRecommendationType.AddItem,
                    Title = "Add a regression",
                    SuggestedItem = new SuggestedChecklistItem { Title = "Retest rotation" }
                }
            ]
        };
        Assert.False(one.AllowsBulkSelection);
        Assert.False(one.HasSelectedSuggestion);

        var many = new ChecklistReviewResult
        {
            Findings =
            [
                new ChecklistReviewFinding
                {
                    RecommendationType = ReviewRecommendationType.AddItem,
                    Title = "Add a regression",
                    SuggestedItem = new SuggestedChecklistItem { Title = "Retest rotation" },
                    Selected = true
                },
                new ChecklistReviewFinding
                {
                    RecommendationType = ReviewRecommendationType.ImproveItem,
                    Title = "Clarify the title"
                }
            ]
        };
        Assert.True(many.AllowsBulkSelection);
        Assert.True(many.HasSelectedSuggestion);
        many.Findings[0].Selected = false;
        Assert.False(many.HasSelectedSuggestion);
    }

    [Fact]
    public void Recovers_an_imperative_check_that_does_not_name_an_existing_item()
    {
        var (request, _, _) = Sample();
        const string json = """
            {
              "findings": [
                {
                  "category": "Completeness",
                  "severity": "Critical",
                  "title": "Crash on rotation needs handling",
                  "explanation": "The login screen crashes when the phone rotates.",
                  "whyItMatters": "The crash blocks sign-in.",
                  "suggestedAction": "Implement robust rotation handling for the login screen.",
                  "recommendationType": "AddItem"
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var finding = Assert.Single(inspection.Review!.Findings);
        Assert.Equal(ReviewRecommendationType.AddItem, finding.RecommendationType);
        Assert.Equal("Implement robust rotation handling for the login screen", finding.SuggestedItem!.Title);
    }

    [Fact]
    public void Keeps_a_readable_finding_when_no_check_can_be_built()
    {
        var (request, _, _) = Sample();
        const string json = """
            {
              "findings": [
                {
                  "category": "Testing",
                  "severity": "High",
                  "title": "Missing regression coverage",
                  "explanation": "The rotation crash has no regression check.",
                  "whyItMatters": "The crash can return.",
                  "suggestedAction": "The tests do not cover rotation.",
                  "recommendationType": "AddItem"
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var finding = Assert.Single(inspection.Review!.Findings);
        Assert.Equal(ReviewRecommendationType.Observation, finding.RecommendationType);
        Assert.Null(finding.SuggestedItem);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        var (request, _, _) = Sample();

        var inspection = _validator.Read("{not json", request);

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.InvalidJson, inspection.FailureKind);
        Assert.Null(inspection.Review);
    }

    [Fact]
    public void Accepts_an_empty_findings_array()
    {
        var (request, _, _) = Sample();

        var inspection = _validator.Read("""{"findings":[]}""", request);

        Assert.True(inspection.IsValid);
        Assert.Empty(inspection.Review!.Findings);
        Assert.Empty(inspection.Warnings);
    }

    [Fact]
    public void Reads_every_category_and_recommendation_type()
    {
        var (request, _, _) = Sample();
        var json = Review(
            Finding("Completeness", "Critical", "Missing reproduction", "AddItem", suggested: Addition("Reproduce on rotate")),
            Finding("Duplicates", "Medium", "These checks overlap", "PossibleDuplicate", related: "\"i1\",\"i2\""),
            Finding("Clarity", "Low", "Reproduction is vague", "ImproveItem", related: "\"i1\"", suggested: "{\"title\":\"Reproduce the rotation crash\",\"description\":\"Rotate the login screen and record the stack.\"}"),
            Finding("Testing", "High", "Missing regression", "AddItem", suggested: Addition()),
            Finding("Security", "High", "Token stored in logs", "Observation"),
            Finding("Performance", "Medium", "Layout pass on rotate", "Observation"),
            Finding("Priority", "Low", "Retest is required for a bug fix", "PriorityAdjustment", related: "\"i2\"", suggested: "{\"priority\":\"Critical\"}"),
            Finding("Risk", "High", "No failure path for rotation", "Observation"));

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var findings = inspection.Review!.Findings;
        Assert.Equal(8, findings.Count);
        foreach (var category in Enum.GetValues<ReviewCategory>())
            Assert.Contains(findings, finding => finding.Category == category);
        foreach (var recommendation in Enum.GetValues<ReviewRecommendationType>())
            Assert.Contains(findings, finding => finding.RecommendationType == recommendation);
    }

    [Fact]
    public void Rejects_an_unsupported_severity()
    {
        var (request, _, _) = Sample();
        var json = Review(Finding("Testing", "Urgent", "Missing regression", "AddItem", suggested: Addition()));

        var inspection = _validator.Read(json, request);

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.InvalidChecklist, inspection.FailureKind);
    }

    [Fact]
    public void Rejects_a_finding_that_is_missing_a_required_field()
    {
        var (request, _, _) = Sample();
        const string json = """
            {
              "findings": [
                {
                  "category": "Clarity",
                  "severity": "Medium",
                  "explanation": "The title does not say what to do.",
                  "whyItMatters": "The check cannot be completed.",
                  "suggestedAction": "Name the screen.",
                  "recommendationType": "Observation"
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.False(inspection.IsValid);
        Assert.Equal(AiFailureKind.InvalidChecklist, inspection.FailureKind);
    }

    [Fact]
    public void Drops_a_finding_that_points_at_a_missing_check()
    {
        var (request, _, _) = Sample();
        var json = Review(
            Finding("Clarity", "Medium", "Name the screen", "ImproveItem", related: "\"i99\"", suggested: "{\"title\":\"Reproduce the rotation crash\"}"),
            Finding("Security", "Low", "No secret is involved", "Observation"));

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        var finding = Assert.Single(inspection.Review!.Findings);
        Assert.Equal(ReviewRecommendationType.Observation, finding.RecommendationType);
        Assert.Contains(ChecklistReviewValidator.UnmatchedWarning, inspection.Warnings);
    }

    [Fact]
    public void Drops_duplicate_findings_and_duplicate_additions()
    {
        var (request, _, _) = Sample();
        var json = Review(
            Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition()),
            Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition()),
            Finding("Testing", "Medium", "Add the same regression again", "AddItem", suggested: Addition()));

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        Assert.Single(inspection.Review!.Findings);
        Assert.Contains(ChecklistReviewValidator.DuplicateWarning, inspection.Warnings);
    }

    [Fact]
    public void Schema_lists_the_supported_values()
    {
        var schema = ChecklistReviewValidator.Schema.GetRawText();

        foreach (var category in Enum.GetNames<ReviewCategory>())
            Assert.Contains(category, schema, StringComparison.Ordinal);
        foreach (var severity in Enum.GetNames<ReviewSeverity>())
            Assert.Contains(severity, schema, StringComparison.Ordinal);
        foreach (var recommendation in Enum.GetNames<ReviewRecommendationType>())
            Assert.Contains(recommendation, schema, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\": 10", schema, StringComparison.Ordinal);
        Assert.Contains("qualityScore", schema, StringComparison.Ordinal);
        Assert.Contains("summary", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_quality_score_and_summary()
    {
        var (request, _, _) = Sample();
        const string json = """
            {
              "qualityScore": 72,
              "summary": "Rotation is covered, but the login retest is vague and has no failure check.",
              "findings": [
                {
                  "category": "Potential risks",
                  "severity": "High",
                  "title": "No failure path for rotation",
                  "explanation": "The checks never say what happens when rotation fails.",
                  "whyItMatters": "A broken rotate can ship.",
                  "suggestedAction": "Add a check for the rotation failure path.",
                  "recommendationType": "AddItem",
                  "suggestedItem": {
                    "title": "Confirm the login screen recovers after a failed rotation",
                    "section": "Testing",
                    "priority": "Required",
                    "type": "Verification"
                  }
                }
              ]
            }
            """;

        var inspection = _validator.Read(json, request);

        Assert.True(inspection.IsValid);
        Assert.Equal(72, inspection.Review!.QualityScore);
        Assert.Contains("login retest is vague", inspection.Review.Summary, StringComparison.Ordinal);
        Assert.Equal(ReviewCategory.Risk, Assert.Single(inspection.Review.Findings).Category);
    }

    [Fact]
    public void Clamps_the_quality_score_and_derives_one_when_it_is_missing()
    {
        var (request, _, _) = Sample();
        var high = _validator.Read("""{"qualityScore":140,"summary":"Too high.","findings":[]}""", request);
        var missing = _validator.Read(
            Review(Finding(
                "Clarity",
                "Critical",
                "Reproduce is vague",
                "ImproveItem",
                related: "\"i1\"",
                suggested: "{\"description\":\"Name the screen and the crash.\"}")),
            request);
        var unreadable = _validator.Read("""{"qualityScore":"high","findings":[]}""", request);

        Assert.Equal(100, high.Review!.QualityScore);
        Assert.Equal("Too high.", high.Review.Summary);
        Assert.Equal(75, missing.Review!.QualityScore);
        Assert.Equal("", missing.Review.Summary);
        Assert.Equal(100, unreadable.Review!.QualityScore);
    }

    [Fact]
    public void Prompt_asks_for_gaps_and_a_quality_score()
    {
        var prompt = _prompts.Build(ChecklistReviewMapper.CreateRequest(
            "Rotate crash",
            WorkType.Bug,
            null,
            null,
            null,
            null,
            []));

        Assert.Contains("qualityScore", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Missing checks", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Duplicate checks", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Vague or incomplete", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Potential risks", prompt.System, StringComparison.Ordinal);
        Assert.Contains("Additional validation", prompt.System, StringComparison.Ordinal);
        Assert.Contains("qualityScore", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_uses_only_the_context_that_exists()
    {
        var items = new List<ChecklistItem>
        {
            new()
            {
                Title = "Reproduce the crash",
                Category = "Development",
                Priority = ChecklistPriority.Required,
                Notes = "Rotate the screen.\n\nAcceptance: The crash happens on rotate.",
                IsCompleted = true
            }
        };
        var request = ChecklistReviewMapper.CreateRequest(
            "Rotate crash",
            WorkType.Bug,
            taskDescription: null,
            platform: null,
            framework: null,
            templateName: "AI · Flutter · Bug Fix",
            items);

        var prompt = _prompts.Build(request);

        Assert.Contains("Rotate crash", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Task:", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform:", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Framework: Flutter", prompt.User, StringComparison.Ordinal);
        Assert.Contains("i1", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Reproduce the crash", prompt.User, StringComparison.Ordinal);
        Assert.Contains("done", prompt.User, StringComparison.Ordinal);
        Assert.Contains("The crash happens on rotate.", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Android", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TASK", prompt.User, StringComparison.Ordinal);
        Assert.Equal("i1", request.Items[0].Alias);
        Assert.Equal("Rotate the screen.", request.Items[0].Description);
        Assert.Equal("The crash happens on rotate.", request.Items[0].AcceptanceCriteria);
        Assert.Null(request.Items[0].ItemType);
    }

    [Fact]
    public void Review_reads_acceptance_and_type_stored_on_the_check()
    {
        var request = ChecklistReviewMapper.CreateRequest(
            "Login",
            WorkType.Feature,
            null,
            null,
            null,
            null,
            [
                new ChecklistItem
                {
                    Title = "Save the draft",
                    Category = "Development",
                    Notes = "Write the file.",
                    Acceptance = "The file reopens with the same text.",
                    ItemType = "Implementation",
                    PullRequestLinks = "https://example.com/pull/4"
                }
            ]);

        Assert.Equal("Write the file.", request.Items[0].Description);
        Assert.Equal("The file reopens with the same text.", request.Items[0].AcceptanceCriteria);
        Assert.Equal("Implementation", request.Items[0].ItemType);

        var prompt = _prompts.Build(request);
        Assert.Contains("The file reopens with the same text.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("type Implementation", prompt.User, StringComparison.Ordinal);
        Assert.Contains("https://example.com/pull/4", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_checklist_does_not_invent_a_framework()
    {
        var request = ChecklistReviewMapper.CreateRequest(
            "Ship the fix",
            WorkType.Development,
            taskDescription: "  ",
            platform: null,
            framework: null,
            templateName: "Bug Fix",
            items: []);

        var prompt = _prompts.Build(request);

        Assert.Null(request.Platform);
        Assert.Null(request.Framework);
        Assert.Null(request.TaskDescription);
        Assert.DoesNotContain("Framework:", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform:", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Task:", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Items:", prompt.User, StringComparison.Ordinal);
        Assert.Contains("none", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_add_item_inserts_one_check_in_its_section()
    {
        var (items, first, second, request) = Items();
        var finding = One(Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition()), request);

        var result = ChecklistReviewApplicator.Apply(items, finding);

        Assert.True(result.Changed);
        Assert.Equal(3, items.Count);
        Assert.Equal(second.Id, items[1].Id);
        Assert.Equal("Add a rotation regression test", items[2].Title);
        Assert.Equal("Testing", items[2].Category);
        Assert.Equal(ChecklistPriority.Required, items[2].Priority);
        Assert.False(items[2].IsCompleted);
        Assert.Contains("Rotate after login.", items[2].Notes, StringComparison.Ordinal);
        Assert.Equal("Login still works after rotation.", items[2].Acceptance);
        Assert.Equal("Verification", items[2].ItemType);
        Assert.DoesNotContain("Acceptance:", items[2].Notes, StringComparison.Ordinal);
        Assert.True(finding.Applied);
        Assert.Equal(first.Id, items[0].Id);
        Assert.False(items[0].IsCompleted);
        Assert.True(items[1].IsCompleted);
    }

    [Fact]
    public void Applying_improve_item_keeps_identity_completion_and_user_content()
    {
        var (items, first, second, request) = Items();
        items[0].Evidence.Add(new EvidenceFile { Path = "/shots/crash.png" });
        items[0].Subtasks.Add(new ChecklistSubtask { Title = "Capture the log", IsCompleted = true });
        var evidence = items[0].Evidence;
        var subtasks = items[0].Subtasks;
        var finding = One(Finding(
            "Clarity",
            "Medium",
            "Name the rotation",
            "ImproveItem",
            related: "\"i1\"",
            suggested: "{\"title\":\"Reproduce the rotation crash\",\"description\":\"Rotate the login screen.\"}"), request);

        var result = ChecklistReviewApplicator.Apply(items, finding);

        Assert.True(result.Changed);
        Assert.Equal(first.Id, items[0].Id);
        Assert.False(items[0].IsCompleted);
        Assert.Equal("Reproduce the rotation crash", items[0].Title);
        Assert.Equal("Development", items[0].Category);
        Assert.Equal(ChecklistPriority.Required, items[0].Priority);
        Assert.Contains("Rotate the login screen.", items[0].Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("Acceptance:", items[0].Notes, StringComparison.Ordinal);
        Assert.Equal("The crash happens on rotate.", items[0].Acceptance);
        Assert.Same(evidence, items[0].Evidence);
        Assert.Same(subtasks, items[0].Subtasks);
        Assert.True(items[0].Subtasks[0].IsCompleted);
        Assert.Equal(second.Id, items[1].Id);
        Assert.True(items[1].IsCompleted);
        Assert.Equal("Sign in still works.", items[1].Notes);
    }

    [Fact]
    public void Applying_priority_adjustment_changes_only_the_priority()
    {
        var (items, _, second, request) = Items();
        var notes = items[1].Notes;
        var finding = One(Finding(
            "Priority",
            "High",
            "Retest should be critical",
            "PriorityAdjustment",
            related: "\"i2\"",
            suggested: "{\"priority\":\"Critical\"}"), request);

        var result = ChecklistReviewApplicator.Apply(items, finding);

        Assert.True(result.Changed);
        Assert.Equal(second.Id, items[1].Id);
        Assert.True(items[1].IsCompleted);
        Assert.Equal(ChecklistPriority.Critical, items[1].Priority);
        Assert.Equal(notes, items[1].Notes);
        Assert.Equal("Retest login", items[1].Title);
        Assert.Equal(ChecklistPriority.Required, items[0].Priority);
    }

    [Fact]
    public void Dismissing_a_finding_does_not_change_the_checklist()
    {
        var (items, first, _, request) = Items();
        var finding = One(Finding("Testing", "Low", "No secret is involved", "Observation"), request);

        var result = ChecklistReviewApplicator.Dismiss(finding);
        var apply = ChecklistReviewApplicator.Apply(items, finding);

        Assert.Equal("Dismissed.", result.Message);
        Assert.True(finding.Dismissed);
        Assert.False(apply.Changed);
        Assert.Equal(2, items.Count);
        Assert.Equal(first.Title, items[0].Title);
    }

    [Fact]
    public void Information_findings_do_not_mutate_the_checklist()
    {
        var (items, _, _, request) = Items();
        var duplicate = One(Finding("Duplicates", "Medium", "These checks overlap", "PossibleDuplicate", related: "\"i1\",\"i2\""), request);
        var observation = One(Finding("Security", "Low", "No secret is involved", "Observation"), request);

        Assert.False(ChecklistReviewApplicator.Apply(items, duplicate).Changed);
        Assert.False(ChecklistReviewApplicator.Apply(items, observation).Changed);
        Assert.Equal(2, items.Count);
        Assert.False(duplicate.CanApply);
        Assert.False(observation.CanApply);
    }

    [Fact]
    public void Rerunning_a_review_does_not_apply_the_same_change_twice()
    {
        var (items, first, second, request) = Items();
        var addFinding = Finding("Testing", "High", "Missing rotation regression", "AddItem", suggested: Addition());
        var improveFinding = Finding(
            "Clarity",
            "Medium",
            "Name the rotation",
            "ImproveItem",
            related: "\"i1\"",
            suggested: "{\"title\":\"Reproduce the rotation crash\",\"description\":\"Rotate the login screen.\"}");
        var priorityFinding = Finding(
            "Priority",
            "High",
            "Retest should be critical",
            "PriorityAdjustment",
            related: "\"i2\"",
            suggested: "{\"priority\":\"Critical\"}");

        Assert.True(ChecklistReviewApplicator.Apply(items, One(addFinding, request)).Changed);
        Assert.True(ChecklistReviewApplicator.Apply(items, One(improveFinding, request)).Changed);
        Assert.True(ChecklistReviewApplicator.Apply(items, One(priorityFinding, request)).Changed);

        var again = ChecklistReviewMapper.CreateRequest("Rotate crash", WorkType.Bug, null, null, null, null, items);
        Assert.Empty(_validator.Read(Review(addFinding), again).Review!.Findings);
        Assert.Empty(_validator.Read(Review(improveFinding), again).Review!.Findings);
        Assert.Empty(_validator.Read(Review(priorityFinding), again).Review!.Findings);

        var repeated = new ChecklistReviewFinding
        {
            Title = "Missing rotation regression",
            RecommendationType = ReviewRecommendationType.AddItem,
            SuggestedItem = new SuggestedChecklistItem { Title = "Add a rotation regression test", Section = "Testing" }
        };
        Assert.False(ChecklistReviewApplicator.Apply(items, repeated).Changed);
        Assert.Equal(3, items.Count);
        Assert.Equal(first.Id, items[0].Id);
        Assert.False(items[0].IsCompleted);
        Assert.Equal("Reproduce the rotation crash", items[0].Title);
        Assert.Equal(second.Id, items[1].Id);
        Assert.True(items[1].IsCompleted);
        Assert.Equal(ChecklistPriority.Critical, items[1].Priority);
    }

    [Fact]
    public void Select_applicable_skips_information_and_dismissed_findings()
    {
        var review = new ChecklistReviewResult
        {
            Findings =
            [
                new ChecklistReviewFinding { RecommendationType = ReviewRecommendationType.AddItem, Title = "Add" },
                new ChecklistReviewFinding { RecommendationType = ReviewRecommendationType.Observation, Title = "Note" },
                new ChecklistReviewFinding { RecommendationType = ReviewRecommendationType.PossibleDuplicate, Title = "Overlap" },
                new ChecklistReviewFinding
                {
                    RecommendationType = ReviewRecommendationType.ImproveItem,
                    Title = "Clarify",
                    Dismissed = true
                }
            ]
        };

        Assert.Equal(1, review.SelectApplicable());
        Assert.True(review.Findings[0].Selected);
        Assert.False(review.Findings[1].Selected);
        Assert.False(review.Findings[2].Selected);
        Assert.False(review.Findings[3].Selected);
    }

    [Fact]
    public void Confirmation_shows_the_current_and_proposed_values()
    {
        var (items, _, _, request) = Items();
        var improve = One(Finding(
            "Clarity",
            "Medium",
            "Name the rotation",
            "ImproveItem",
            related: "\"i1\"",
            suggested: "{\"title\":\"Reproduce the rotation crash\"}"), request);
        var priority = One(Finding(
            "Priority",
            "High",
            "Retest should be critical",
            "PriorityAdjustment",
            related: "\"i2\"",
            suggested: "{\"priority\":\"Critical\"}"), request);

        var improveText = ChecklistReviewMapper.DescribeChange(improve, items[0]);
        var priorityText = ChecklistReviewMapper.DescribeChange(priority, items[1]);

        Assert.Contains("Reproduce the crash", improveText, StringComparison.Ordinal);
        Assert.Contains("Reproduce the rotation crash", improveText, StringComparison.Ordinal);
        Assert.Contains("Current: Required", priorityText, StringComparison.Ordinal);
        Assert.Contains("Suggested: Critical", priorityText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saves_ai_context_for_a_later_review()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checks.superdev.json");
        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Rotate crash",
                WorkType = WorkType.Bug,
                TaskDescription = "Crash when rotating the screen",
                AiPlatform = "Android",
                AiFramework = ".NET MAUI",
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items = [new ChecklistItem { Title = "Reproduce", Category = "Testing" }]
            };
            var files = new ChecklistFileService();
            await files.SaveAsync(path, document, replaceDifferentDocument: false);
            var loaded = await files.LoadAsync(path);

            Assert.Equal("Crash when rotating the screen", loaded.TaskDescription);
            Assert.Equal("Android", loaded.AiPlatform);
            Assert.Equal(".NET MAUI", loaded.AiFramework);
            var request = ChecklistReviewMapper.CreateRequest(
                loaded.Title,
                loaded.WorkType,
                loaded.TaskDescription,
                loaded.AiPlatform,
                loaded.AiFramework,
                loaded.TemplateName,
                loaded.Items);
            Assert.Equal("Android", request.Platform);
            Assert.Equal(".NET MAUI", request.Framework);
            Assert.Equal("Crash when rotating the screen", request.TaskDescription);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Opening_a_checklist_splits_acceptance_out_of_older_notes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checks.superdev.json");
        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Rotate crash",
                WorkType = WorkType.Bug,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items =
                [
                    new ChecklistItem
                    {
                        Title = "Reproduce",
                        Notes = "Rotate the screen.\n\nAcceptance: The crash happens on rotate."
                    },
                    new ChecklistItem
                    {
                        Title = "Retest",
                        Notes = "Keep this.",
                        Acceptance = "Already stored.",
                        ItemType = "Verification"
                    }
                ]
            };
            var files = new ChecklistFileService();
            await files.SaveAsync(path, document, replaceDifferentDocument: false);
            var loaded = await files.LoadAsync(path);

            Assert.Equal("Rotate the screen.", loaded.Items[0].Notes);
            Assert.Equal("The crash happens on rotate.", loaded.Items[0].Acceptance);
            Assert.Equal("Keep this.", loaded.Items[1].Notes);
            Assert.Equal("Already stored.", loaded.Items[1].Acceptance);
            Assert.Equal("Verification", loaded.Items[1].ItemType);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Review_service_hides_invalid_model_output()
    {
        const string secret = "SECRET-PAYLOAD-SHOULD-NOT-LEAK";
        using var service = new OllamaService(
            new HttpClient(new StubHandler((request, _) =>
            {
                var body = request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal)
                    ? """{"models":[{"name":"qwen3.5:0.8b"}]}"""
                    : "{\"message\":{\"role\":\"assistant\",\"content\":\"" + secret + "\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = OllamaService.LocalEndpoint,
                Timeout = Timeout.InfiniteTimeSpan
            },
            new PromptBuilder(),
            new ChecklistValidator(),
            OllamaService.DefaultModel,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
        var reviews = new ChecklistReviewService(service);
        string? sent = null;
        using var capturing = new OllamaService(
            new HttpClient(new StubHandler(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"models":[{"name":"qwen3.5:0.8b"}]}""", Encoding.UTF8, "application/json")
                    };
                }

                sent = await request.Content!.ReadAsStringAsync();
                var content = JsonEncoded("""{"findings":[]}""");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"message\":{\"role\":\"assistant\",\"content\":" + content + "}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }))
            {
                BaseAddress = OllamaService.LocalEndpoint,
                Timeout = Timeout.InfiniteTimeSpan
            },
            new PromptBuilder(),
            new ChecklistValidator(),
            OllamaService.DefaultModel,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
        var valid = new ChecklistReviewService(capturing);
        var request = ChecklistReviewMapper.CreateRequest("Rotate crash", WorkType.Bug, null, null, null, null, []);

        var invalid = await reviews.ReviewAsync(request);
        var empty = await valid.ReviewAsync(request);

        Assert.False(invalid.Succeeded);
        Assert.DoesNotContain(secret, invalid.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", invalid.Failure.Message, StringComparison.Ordinal);
        Assert.True(empty.Succeeded);
        Assert.Empty(empty.Review!.Findings);
        Assert.Contains("Rotate crash", sent, StringComparison.Ordinal);
        Assert.Contains("AddItem", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform:", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_ollama_review_returns_a_readable_result()
    {
        using var ollama = OllamaService.CreateLocal();
        var availability = await ollama.CheckAvailabilityAsync();
        if (availability.Reachability != OllamaReachability.Reachable)
            return;

        if (!await ollama.IsModelAvailableAsync(OllamaService.DefaultModel))
            return;

        var reviews = new ChecklistReviewService(ollama);
        var request = ChecklistReviewMapper.CreateRequest(
            "Rotate crash",
            WorkType.Bug,
            "The login screen crashes when the phone rotates.",
            "Android",
            ".NET MAUI",
            null,
            [
                new ChecklistItem
                {
                    Title = "Reproduce the crash",
                    Category = "Development",
                    Priority = ChecklistPriority.Required,
                    Notes = "Rotate the login screen."
                }
            ]);

        var outcome = await reviews.ReviewAsync(request);

        if (!outcome.Succeeded)
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome.Failure!.Message));
            Assert.DoesNotContain("{", outcome.Failure.Message, StringComparison.Ordinal);
            return;
        }

        Assert.NotNull(outcome.Review);
        Assert.True(outcome.Review!.Findings.Count <= 10);
        Assert.All(outcome.Review.Findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Title));
            Assert.False(string.IsNullOrWhiteSpace(finding.Id));
        });
    }

    private ChecklistReviewFinding One(string findingJson, ChecklistReviewRequest request)
    {
        var inspection = _validator.Read(Review(findingJson), request);
        Assert.True(inspection.IsValid, inspection.FailureKind?.ToString());
        return Assert.Single(inspection.Review!.Findings);
    }

    private static (List<ChecklistItem> Items, ChecklistItem First, ChecklistItem Second, ChecklistReviewRequest Request) Items()
    {
        var (request, _, _) = Sample();
        var first = new ChecklistItem
        {
            Id = request.Items[0].Id,
            Title = "Reproduce the crash",
            Category = "Development",
            Priority = ChecklistPriority.Required,
            Notes = "Rotate the screen.\n\nAcceptance: The crash happens on rotate.",
            IsCompleted = false
        };
        var second = new ChecklistItem
        {
            Id = request.Items[1].Id,
            Title = "Retest login",
            Category = "Testing",
            Priority = ChecklistPriority.Required,
            Notes = "Sign in still works.",
            IsCompleted = true
        };
        return ([first, second], first, second, request);
    }

    private static (ChecklistReviewRequest Request, Guid First, Guid Second) Sample()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = ChecklistReviewMapper.CreateRequest(
            "Rotate crash",
            WorkType.Bug,
            null,
            null,
            null,
            null,
            [
                new ChecklistItem
                {
                    Id = first,
                    Title = "Reproduce the crash",
                    Category = "Development",
                    Priority = ChecklistPriority.Required,
                    Notes = "Rotate the screen.\n\nAcceptance: The crash happens on rotate."
                },
                new ChecklistItem
                {
                    Id = second,
                    Title = "Retest login",
                    Category = "Testing",
                    Priority = ChecklistPriority.Required,
                    Notes = "Sign in still works.",
                    IsCompleted = true
                }
            ]);
        return (request, first, second);
    }

    private static string Review(params string[] findings) =>
        "{\"findings\":[" + string.Join(",", findings) + "]}";

    private static string Finding(
        string category,
        string severity,
        string title,
        string recommendation,
        string? related = null,
        string? suggested = null)
    {
        var relatedJson = related is null ? string.Empty : ",\"relatedItemIds\":[" + related + "]";
        var suggestedJson = suggested is null ? string.Empty : ",\"suggestedItem\":" + suggested;
        return "{\"category\":\"" + category
            + "\",\"severity\":\"" + severity
            + "\",\"title\":\"" + title
            + "\",\"explanation\":\"This gap is specific to the rotation crash.\""
            + ",\"whyItMatters\":\"The crash can ship without a check.\""
            + ",\"suggestedAction\":\"Add or clarify the rotation check.\""
            + ",\"recommendationType\":\"" + recommendation + "\""
            + relatedJson
            + suggestedJson
            + "}";
    }

    private static string Addition(string title = "Add a rotation regression test") =>
        "{\"title\":\"" + title + "\",\"section\":\"Testing\",\"description\":\"Rotate after login.\",\"acceptanceCriteria\":\"Login still works after rotation.\",\"priority\":\"Required\",\"type\":\"Verification\"}";

    private static string JsonEncoded(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
