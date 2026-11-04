using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

public enum DevPlatform
{
    CrossPlatform,
    Android,
    Ios,
    Windows,
    MacOs,
    Web,
    Backend
}

public enum DevFramework
{
    Maui,
    Flutter,
    NativeAndroid,
    NativeIos,
    ReactNative,
    React,
    Angular,
    AspNetCore,
    Node,
    Custom
}

public enum AiWorkKind
{
    BugFix,
    NewFeature,
    Development,
    Testing,
    CodeReview,
    Performance,
    Refactoring,
    Release,
    SecurityReview
}

public enum OllamaReachability
{
    Reachable,
    ConnectionRefused,
    TimedOut,
    Unavailable
}

public enum OllamaStatus
{
    Ready,
    ModelMissing,
    NotRunning,
    NotInstalled,
    Unavailable
}

public enum AiFailureKind
{
    NotInstalled,
    NotRunning,
    ConnectionRefused,
    ModelMissing,
    Timeout,
    Cancelled,
    InvalidJson,
    EmptyResponse,
    InvalidChecklist,
    MissingTitle,
    NoSections,
    NoValidItems,
    InsufficientResources,
    Unexpected
}

public sealed record OllamaAvailability(OllamaReachability Reachability);

public sealed record AiChoice<T>(T Value, string Label) where T : notnull
{
    public override string ToString() => Label;
}

public sealed class AiChecklistRequest
{
    public DevPlatform Platform { get; init; }

    public DevFramework Framework { get; init; }

    public AiWorkKind WorkKind { get; init; }

    public string? Description { get; init; }
}

public sealed class GeneratedChecklist
{
    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public List<GeneratedSection> Sections { get; set; } = [];
}

[JsonConverter(typeof(GeneratedSectionConverter))]
public sealed class GeneratedSection
{
    public string Name { get; set; } = string.Empty;

    public List<GeneratedCheckItem> Items { get; set; } = [];
}

public sealed class GeneratedCheckItem
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Type { get; set; } = ChecklistVocabulary.DefaultType;

    public string Priority { get; set; } = ChecklistVocabulary.DefaultPriority;

    public string AcceptanceCriteria { get; set; } = string.Empty;
}

/// <summary>
/// Preview values for a generated item. Type is the kind of work. Priority is the checklist priority.
/// </summary>
public static class ChecklistVocabulary
{
    public const string DefaultType = "Implementation";

    public const string DefaultPriority = "Required";

    public static readonly string[] Types = ["Investigation", "Implementation", "Verification", "Documentation"];

    public static readonly string[] Priorities = ["Critical", "Required", "Conditional", "Review"];

    public static string Notice(int filledOptional, int adjusted)
    {
        if (adjusted > 0)
        {
            return adjusted == 1
                ? "1 item was adjusted to match Super Dev's checklist format."
                : adjusted + " items were adjusted to match Super Dev's checklist format.";
        }

        if (filledOptional > 0)
            return "Some optional details were filled automatically.";

        return "Checklist generated successfully.";
    }
}

public static class CheckCategories
{
    public const string Default = "Development";

    public static readonly string[] All =
    [
        "Understand",
        "Development",
        "Testing",
        "Communication",
        "PR",
        "Merge",
        "Deployment",
        "Closure"
    ];
}

public sealed class CheckGenerationRequest
{
    public string ChecklistTitle { get; init; } = string.Empty;

    public WorkType WorkType { get; init; }

    public string? DraftTitle { get; init; }

    public string? DraftNotes { get; init; }

    public string? DraftCategory { get; init; }

    public IReadOnlyList<string> ExistingTitles { get; init; } = [];
}

[JsonConverter(typeof(GeneratedCheckConverter))]
public sealed class GeneratedCheck
{
    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Priority { get; set; } = ChecklistVocabulary.DefaultPriority;

    public string Description { get; set; } = string.Empty;

    public string AcceptanceCriteria { get; set; } = string.Empty;

    public List<string> Subtasks { get; set; } = [];
}

public sealed class AiChecklistDraft
{
    public required string Title { get; init; }

    public required WorkType WorkType { get; init; }

    public required string TemplateName { get; init; }

    public required List<ChecklistItem> Items { get; init; }

    public string? TaskDescription { get; init; }

    public string? AiPlatform { get; init; }

    public string? AiFramework { get; init; }
}

public sealed class AiFailure
{
    public AiFailureKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class ChecklistInspection
{
    public bool IsValid { get; init; }

    public GeneratedChecklist? Checklist { get; init; }

    public GeneratedSection? Section { get; init; }

    public GeneratedCheck? Check { get; init; }

    public AiFailure? Failure { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Notice { get; init; } = string.Empty;

    public bool NoticeIsAdjustment { get; init; }
}

public sealed class AiGenerationResult
{
    public GeneratedChecklist? Checklist { get; init; }

    public GeneratedSection? Section { get; init; }

    public GeneratedCheck? Check { get; init; }

    public AiFailure? Failure { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Notice { get; init; } = string.Empty;

    public bool NoticeIsAdjustment { get; init; }

    public bool Succeeded => Failure is null && (Checklist is not null || Section is not null || Check is not null);

    public static AiGenerationResult Fail(AiFailureKind kind, string model) => new()
    {
        Failure = new AiFailure
        {
            Kind = kind,
            Message = AiMessages.For(kind, model)
        }
    };

    public static AiGenerationResult FromChecklist(ChecklistInspection inspection, string model)
    {
        if (!inspection.IsValid || inspection.Checklist is null)
        {
            var kind = inspection.Failure?.Kind ?? AiFailureKind.InvalidChecklist;
            return new AiGenerationResult
            {
                Failure = new AiFailure
                {
                    Kind = kind,
                    Message = AiMessages.For(kind, model)
                },
                Warnings = inspection.Warnings,
                Notice = inspection.Notice,
                NoticeIsAdjustment = inspection.NoticeIsAdjustment
            };
        }

        return new AiGenerationResult
        {
            Checklist = inspection.Checklist,
            Warnings = inspection.Warnings,
            Notice = inspection.Notice,
            NoticeIsAdjustment = inspection.NoticeIsAdjustment
        };
    }

    public static AiGenerationResult FromSection(ChecklistInspection inspection, string model)
    {
        if (!inspection.IsValid || inspection.Section is null)
        {
            var kind = inspection.Failure?.Kind ?? AiFailureKind.InvalidChecklist;
            return new AiGenerationResult
            {
                Failure = new AiFailure
                {
                    Kind = kind,
                    Message = AiMessages.For(kind, model)
                },
                Warnings = inspection.Warnings,
                Notice = inspection.Notice,
                NoticeIsAdjustment = inspection.NoticeIsAdjustment
            };
        }

        return new AiGenerationResult
        {
            Section = inspection.Section,
            Warnings = inspection.Warnings,
            Notice = inspection.Notice,
            NoticeIsAdjustment = inspection.NoticeIsAdjustment
        };
    }

    public static AiGenerationResult FromCheck(ChecklistInspection inspection, string model)
    {
        if (!inspection.IsValid || inspection.Check is null)
        {
            var kind = inspection.Failure?.Kind ?? AiFailureKind.InvalidChecklist;
            return new AiGenerationResult
            {
                Failure = new AiFailure
                {
                    Kind = kind,
                    Message = AiMessages.For(kind, model)
                },
                Warnings = inspection.Warnings,
                Notice = inspection.Notice,
                NoticeIsAdjustment = inspection.NoticeIsAdjustment
            };
        }

        return new AiGenerationResult
        {
            Check = inspection.Check,
            Warnings = inspection.Warnings,
            Notice = inspection.Notice,
            NoticeIsAdjustment = inspection.NoticeIsAdjustment
        };
    }
}

public sealed class ModelStatus
{
    public OllamaStatus Status { get; init; }

    public string ModelName { get; init; } = string.Empty;

    public string ConnectionText { get; init; } = string.Empty;

    public string ModelStateText { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public bool CanGenerate => Status == OllamaStatus.Ready;
}

public static class AiMessages
{
    public static string For(AiFailureKind kind, string model) => kind switch
    {
        AiFailureKind.NotInstalled =>
            "Local AI is not set up yet. Choose Generate and Super Dev can download it onto this computer.",
        AiFailureKind.NotRunning =>
            "Ollama is installed but not running. Choose Generate to start it.",
        AiFailureKind.ConnectionRefused =>
            "Super Dev could not connect to the local AI service. Choose Generate to start it.",
        AiFailureKind.ModelMissing =>
            "The local model is not on this computer yet. Choose Generate to download " + model + ".",
        AiFailureKind.Timeout =>
            "The model took too long to respond. Try again with a shorter task description.",
        AiFailureKind.Cancelled =>
            "Generation was cancelled.",
        AiFailureKind.InvalidJson =>
            "The model returned a response that was not valid JSON. Try generating again.",
        AiFailureKind.EmptyResponse =>
            "The model returned an empty response. Try generating again.",
        AiFailureKind.MissingTitle =>
            "The checklist title is missing. Enter a title or generate again.",
        AiFailureKind.NoSections =>
            "The checklist did not include any sections.",
        AiFailureKind.NoValidItems =>
            "The checklist did not include any items with a title.",
        AiFailureKind.InvalidChecklist =>
            "The checklist could not be validated. Generate again.",
        AiFailureKind.InsufficientResources =>
            "Ollama could not finish generation. This machine may be low on memory. Close other apps and try again.",
        _ =>
            "Checklist generation failed. Check that Ollama is running and try again."
    };
}
