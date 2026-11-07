using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperDev.Models;

namespace SuperDev.Services;

public sealed class ChecklistTemplateService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly List<ChecklistTemplate> _templates = [];

    public ChecklistTemplateService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Super Dev");
        _path = Path.Combine(folder, "checklist-templates.json");
        Load();
    }

    public IReadOnlyList<ChecklistTemplate> Templates => _templates;

    /// <summary>
    /// Fixed summary for a built-in template. Custom templates have none.
    /// </summary>
    public static string? PredefinedDescription(string? id) => id switch
    {
        "bug" => "Find the cause, fix it, and confirm the bug is gone.",
        "feature" => "Build the requested behavior and verify it works.",
        "development" => "Implement the change, test it, and prepare the review.",
        "testing" => "Cover the main flow, edge cases, and regressions.",
        "pullrequest" => "Review the diff, confirm the checks, and merge.",
        "daily" => "Track the work you plan to finish today.",
        "blank" => "Start from an empty checklist.",
        _ => null
    };

    public ChecklistTemplate? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _templates.Find(template => template.Id == id);

    public List<ChecklistItem> CreateItems(ChecklistTemplate template) =>
        template.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new ChecklistItem
            {
                Title = item.Title.Trim(),
                Category = item.Category.Trim(),
                Priority = item.Priority
            })
            .ToList();

    public List<ChecklistItem> GetTemplate(WorkType workType)
    {
        if (workType is WorkType.Daily or WorkType.Blank)
            return [];

        var match = _templates.Find(template =>
            template.WorkType == workType
            && template.Id is "bug" or "feature" or "development" or "testing" or "pullrequest");

        return match is null ? [] : CreateItems(match);
    }

    public void Replace(ChecklistTemplate template)
    {
        var index = _templates.FindIndex(item => item.Id == template.Id);
        var stored = template.Copy();
        if (index < 0)
            _templates.Add(stored);
        else
            _templates[index] = stored;

        Save();
    }

    public ChecklistTemplate AddCopy(ChecklistTemplate template)
    {
        var stored = template.Copy();
        stored.Id = Guid.NewGuid().ToString("N");
        stored.WorkType = WorkType.Custom;
        _templates.Add(stored);
        Save();
        return stored.Copy();
    }

    public void Delete(string id)
    {
        _templates.RemoveAll(template => template.Id == id);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Seed();
                Save();
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<ChecklistTemplate>>(File.ReadAllText(_path), Json);
            if (loaded is null || loaded.Count == 0 || loaded.Any(template => string.IsNullOrWhiteSpace(template.Id)))
            {
                Seed();
                Save();
                return;
            }

            _templates.AddRange(loaded.Where(template => template.CanEdit));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _templates.Clear();
            Seed();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_templates, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private void Seed()
    {
        _templates.Clear();
        _templates.Add(BuiltIn("bug", "Bug Fix", "🐛", WorkType.Bug, BugChecklist()));
        _templates.Add(BuiltIn("feature", "Feature", "✨", WorkType.Feature, FeatureChecklist()));
        _templates.Add(BuiltIn("development", "Development", "💻", WorkType.Development, DevelopmentChecklist()));
        _templates.Add(BuiltIn("testing", "Testing", "🧪", WorkType.Testing, TestingChecklist()));
        _templates.Add(BuiltIn("pullrequest", "Pull Request", "🔀", WorkType.PullRequest, PullRequestChecklist()));
    }

    private static ChecklistTemplate BuiltIn(
        string id,
        string title,
        string icon,
        WorkType workType,
        IEnumerable<ChecklistItem> items) => new()
    {
        Id = id,
        Title = title,
        Icon = icon,
        WorkType = workType,
        Items = items.Select(item => new TemplateCheck
        {
            Title = item.Title,
            Category = item.Category,
            Priority = item.Priority
        }).ToList()
    };

    private static List<ChecklistItem> BugChecklist() =>
    [
        Item("Understand the bug and reproduce it", "Analysis", ChecklistPriority.Critical),
        Item("Review related code and recent changes", "Analysis", ChecklistPriority.Required),
        Item("Identify the root cause", "Analysis", ChecklistPriority.Critical),
        Item("Check whether the bug affects other areas", "Analysis", ChecklistPriority.Required),
        Item("Define the expected behavior", "Analysis", ChecklistPriority.Required),
        Item("Implement the fix", "Development", ChecklistPriority.Critical),
        Item("Keep the change as small as possible", "Development", ChecklistPriority.Required),
        Item("Avoid unrelated refactoring", "Development", ChecklistPriority.Conditional),
        Item("Add or update unit tests", "Testing", ChecklistPriority.Critical),
        Item("Add regression coverage for the bug", "Testing", ChecklistPriority.Required),
        Item("Run the relevant automated tests", "Testing", ChecklistPriority.Critical),
        Item("Manually verify the original bug is fixed", "Testing", ChecklistPriority.Critical),
        Item("Check related functionality for side effects", "Testing", ChecklistPriority.Required),
        Item("Update comments or documentation if behavior changed", "Documentation", ChecklistPriority.Conditional),
        Item("Review the diff before opening a pull request", "Review", ChecklistPriority.Required),
        Item("Write a clear pull request description", "Review", ChecklistPriority.Required),
        Item("Link the bug or issue", "Review", ChecklistPriority.Required),
        Item("Address review feedback", "Review", ChecklistPriority.Required),
        Item("Confirm checks pass before merging", "Release", ChecklistPriority.Critical),
        Item("Verify the fix after deployment if applicable", "Release", ChecklistPriority.Conditional)
    ];

    private static List<ChecklistItem> FeatureChecklist() =>
    [
        Item("Confirm the feature requirements", "Planning", ChecklistPriority.Critical),
        Item("Identify affected areas of the codebase", "Planning", ChecklistPriority.Required),
        Item("Define acceptance criteria", "Planning", ChecklistPriority.Critical),
        Item("Consider edge cases and failure states", "Planning", ChecklistPriority.Required),
        Item("Implement the feature", "Development", ChecklistPriority.Critical),
        Item("Handle validation and error states", "Development", ChecklistPriority.Required),
        Item("Keep the implementation consistent with existing patterns", "Development", ChecklistPriority.Required),
        Item("Add or update automated tests", "Testing", ChecklistPriority.Critical),
        Item("Test the main user flow", "Testing", ChecklistPriority.Critical),
        Item("Test edge cases", "Testing", ChecklistPriority.Required),
        Item("Check for regressions in related functionality", "Testing", ChecklistPriority.Required),
        Item("Update user-facing documentation if needed", "Documentation", ChecklistPriority.Conditional),
        Item("Review the implementation", "Review", ChecklistPriority.Required),
        Item("Open a pull request with a clear description", "Review", ChecklistPriority.Required),
        Item("Confirm the feature is ready to merge", "Release", ChecklistPriority.Critical)
    ];

    private static List<ChecklistItem> DevelopmentChecklist() =>
    [
        Item("Understand the task and expected outcome", "Planning", ChecklistPriority.Critical),
        Item("Review the relevant code", "Planning", ChecklistPriority.Required),
        Item("Identify files and components to change", "Planning", ChecklistPriority.Required),
        Item("Implement the change", "Development", ChecklistPriority.Critical),
        Item("Follow existing project patterns", "Development", ChecklistPriority.Required),
        Item("Handle errors and edge cases", "Development", ChecklistPriority.Required),
        Item("Remove unused code introduced by the change", "Development", ChecklistPriority.Conditional),
        Item("Add or update tests", "Testing", ChecklistPriority.Required),
        Item("Run the relevant tests", "Testing", ChecklistPriority.Critical),
        Item("Manually verify the change", "Testing", ChecklistPriority.Required),
        Item("Review the diff", "Review", ChecklistPriority.Required),
        Item("Prepare the pull request", "Review", ChecklistPriority.Required),
        Item("Confirm the branch is ready to merge", "Release", ChecklistPriority.Critical)
    ];

    private static List<ChecklistItem> TestingChecklist() =>
    [
        Item("Review the requirements or change being tested", "Planning", ChecklistPriority.Critical),
        Item("Identify the main scenarios", "Planning", ChecklistPriority.Required),
        Item("Identify edge cases and failure cases", "Planning", ChecklistPriority.Required),
        Item("Prepare the test environment or test data", "Setup", ChecklistPriority.Required),
        Item("Execute the primary test flow", "Execution", ChecklistPriority.Critical),
        Item("Test invalid input and error handling", "Execution", ChecklistPriority.Required),
        Item("Test boundary conditions", "Execution", ChecklistPriority.Required),
        Item("Check related features for regressions", "Execution", ChecklistPriority.Required),
        Item("Record failures with clear reproduction steps", "Reporting", ChecklistPriority.Critical),
        Item("Retest fixed issues", "Reporting", ChecklistPriority.Required),
        Item("Confirm acceptance criteria are covered", "Reporting", ChecklistPriority.Critical),
        Item("Summarize the test result", "Reporting", ChecklistPriority.Required)
    ];

    private static List<ChecklistItem> PullRequestChecklist() =>
    [
        Item("Confirm the branch is up to date with the target branch", "Preparation", ChecklistPriority.Required),
        Item("Review the complete diff", "Preparation", ChecklistPriority.Critical),
        Item("Remove debug code, temporary logs, and unused changes", "Preparation", ChecklistPriority.Required),
        Item("Confirm tests cover the change", "Validation", ChecklistPriority.Required),
        Item("Run the relevant automated checks", "Validation", ChecklistPriority.Critical),
        Item("Manually verify the user-facing behavior", "Validation", ChecklistPriority.Required),
        Item("Write a clear summary of the change", "Pull Request", ChecklistPriority.Critical),
        Item("Explain how the change was tested", "Pull Request", ChecklistPriority.Required),
        Item("Link the related issue or task", "Pull Request", ChecklistPriority.Required),
        Item("Add screenshots or recordings for UI changes", "Pull Request", ChecklistPriority.Conditional),
        Item("Request review from the appropriate reviewers", "Review", ChecklistPriority.Required),
        Item("Address review comments", "Review", ChecklistPriority.Required),
        Item("Confirm all required checks pass", "Merge", ChecklistPriority.Critical),
        Item("Merge using the repository's expected strategy", "Merge", ChecklistPriority.Required),
        Item("Delete the feature branch if it is no longer needed", "Merge", ChecklistPriority.Conditional)
    ];

    private static ChecklistItem Item(string title, string category, ChecklistPriority priority) =>
        new()
        {
            Title = title,
            Category = category,
            Priority = priority
        };
}
