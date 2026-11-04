using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class PromptBuilder
{
    public const int MaxDescriptionLength = 500;

    public string SystemPrompt { get; } =
        """
        Reply with JSON only. No markdown.
        Write 6 to 12 specific checklist items in 2 to 4 sections.
        Follow the work type. Stay on the task. Do not invent requirements or unrelated features.
        No duplicate items. No deployment work unless the task asks for it.
        Include a test or regression check when the work needs one.
        section_title names each section.
        type is Investigation, Implementation, Verification, or Documentation.
        priority is Critical, Required, Conditional, or Review.
        title is a short action. description and acceptanceCriteria are one short sentence.
        """;

    public Prompt BuildChecklist(AiChecklistRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = Describe(request.Description);
        var user = $"""
            {Focus(request.WorkKind)}
            Platform: {AiCatalog.PlatformLabel(request.Platform)}
            Framework: {AiCatalog.FrameworkLabel(request.Framework)}
            Work type: {AiCatalog.WorkLabel(request.WorkKind)}
            Task: {task}
            Name that behavior in every title.
            Do not add a screen, provider, or platform the task does not mention.
            Use Investigation to reproduce or find the cause, Implementation for the code change, and Verification for tests.
            Return title, summary, and sections.
            """;
        return new Prompt(SystemPrompt, user);
    }

    public Prompt BuildSection(AiChecklistRequest request, string sectionName, IReadOnlyList<string> existingTitles)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(sectionName) ? "General" : sectionName.Trim().Replace("\"", "'");
        var avoid = existingTitles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24);
        var avoidText = string.Join("; ", avoid);
        if (avoidText.Length == 0)
            avoidText = "none";

        var user = $"""
            {Focus(request.WorkKind)}
            Platform: {AiCatalog.PlatformLabel(request.Platform)}
            Framework: {AiCatalog.FrameworkLabel(request.Framework)}
            Work type: {AiCatalog.WorkLabel(request.WorkKind)}
            Task: {Describe(request.Description)}
            Rewrite only the "{name}" section.
            Name the task behavior in every title. Do not repeat these items: {avoidText}
            Return 3 to 5 new items in one section.
            """;
        return new Prompt(SystemPrompt, user);
    }

    public string CheckSystemPrompt { get; } =
        """
        Reply with JSON only. No markdown.
        Write exactly one checklist item for the checklist that is already open.
        category is Understand, Development, Testing, Communication, PR, Merge, Deployment, or Closure.
        priority is Critical, Required, Conditional, or Review.
        title is a short action. description and acceptanceCriteria are one short sentence.
        subtasks are 0 to 4 short steps for this check. Use an empty list when the check is one action.
        Do not invent a deadline, a link, or a second check.
        """;

    public Prompt BuildCheck(CheckGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = request.ExistingTitles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24);
        var avoid = string.Join("; ", existing);
        if (avoid.Length == 0)
            avoid = "none";

        var checklist = string.IsNullOrWhiteSpace(request.ChecklistTitle)
            ? "Untitled checklist"
            : Describe(request.ChecklistTitle);
        var draftTitle = string.IsNullOrWhiteSpace(request.DraftTitle)
            ? "None yet."
            : Describe(request.DraftTitle);
        var draftNotes = string.IsNullOrWhiteSpace(request.DraftNotes)
            ? "None."
            : Describe(request.DraftNotes);
        var category = string.IsNullOrWhiteSpace(request.DraftCategory)
            ? "Choose one."
            : Describe(request.DraftCategory);
        var user = $"""
            {Focus(ToWorkKind(request.WorkType))}
            Checklist: {checklist}
            Work type: {WorkLabel(request.WorkType)}
            Draft title: {draftTitle}
            Draft notes: {draftNotes}
            Preferred category: {category}
            Do not repeat these checks: {avoid}
            Write one check for this checklist. Keep the draft title when it already says what to do.
            Return title, category, priority, description, acceptanceCriteria, and subtasks.
            """;
        return new Prompt(CheckSystemPrompt, user);
    }

    private static AiWorkKind ToWorkKind(WorkType work) => work switch
    {
        WorkType.Bug => AiWorkKind.BugFix,
        WorkType.Feature => AiWorkKind.NewFeature,
        WorkType.Testing => AiWorkKind.Testing,
        WorkType.PullRequest => AiWorkKind.CodeReview,
        _ => AiWorkKind.Development
    };

    private static string WorkLabel(WorkType work) => work switch
    {
        WorkType.Bug => "Bug Fix",
        WorkType.Feature => "Feature",
        WorkType.Development => "Development",
        WorkType.Testing => "Testing",
        WorkType.PullRequest => "Pull Request",
        WorkType.Daily => "Daily",
        WorkType.Blank => "Blank",
        _ => "Custom"
    };

    private static string Focus(AiWorkKind kind) => kind switch
    {
        AiWorkKind.BugFix => "Bug fix: the behavior already exists. Reproduce it, find the cause in the current code, correct that code, and confirm the surrounding flow still works.",
        AiWorkKind.NewFeature => "New feature: add only the requested behavior, including its empty, error, and success cases.",
        AiWorkKind.Development => "Development: improve the described behavior. Do not add unrelated features.",
        AiWorkKind.Testing => "Testing: check the described behavior, including edge cases and regressions. Do not add implementation work.",
        AiWorkKind.CodeReview => "Code review: review the described change for correctness, regressions, and missing tests.",
        AiWorkKind.Performance => "Performance: measure the described path, change only what affects it, and compare the result.",
        AiWorkKind.Refactoring => "Refactoring: restructure the described code without changing its behavior, then verify it.",
        AiWorkKind.Release => "Release: prepare the described change, including the checks that must pass.",
        AiWorkKind.SecurityReview => "Security review: check the described behavior for auth, data, and input risks.",
        _ => "Stay on the described task."
    };

    private static string Describe(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "No extra task detail.";

        var compact = new StringBuilder(Math.Min(description.Length, MaxDescriptionLength));
        var pendingSpace = false;
        foreach (var character in description.Trim())
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (compact.Length + 1 >= MaxDescriptionLength)
                    break;
                compact.Append(' ');
                pendingSpace = false;
            }

            if (compact.Length + 1 > MaxDescriptionLength)
                break;

            compact.Append(character);
        }

        return compact.Length == 0 ? "No extra task detail." : compact.ToString();
    }
}

public readonly record struct Prompt(string System, string User);
