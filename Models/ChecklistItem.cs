using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

public class ChecklistSubtask
{
   public Guid Id { get; set; } = Guid.NewGuid();

   public string Title { get; set; } = string.Empty;

   public bool IsCompleted { get; set; }

   /// <summary>
   /// Set when this subtask points at another checklist. Completion follows that checklist.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public Guid? LinkedDocumentId { get; set; }

   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? LinkedFilePath { get; set; }

   /// <summary>
   /// A linked checklist marked optional does not have to be completed before this check can be.
   /// Missing on older files, which stay required.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
   public bool LinkOptional { get; set; }

   [JsonIgnore]
   public bool IsLink =>
       LinkedDocumentId is not null || !string.IsNullOrWhiteSpace(LinkedFilePath);

   [JsonIgnore]
   public bool IsManual => !IsLink;

   /// <summary>
   /// Manual subtasks always block the parent check. A link blocks only when it is required.
   /// </summary>
   [JsonIgnore]
   public bool BlocksParentCompletion => IsManual || !LinkOptional;

   [JsonIgnore]
   public string LinkLabel
   {
      get
      {
         var name = IsCompleted ? $"✓ {Title}" : Title;
         if (!IsLink)
            return name;

         return LinkOptional ? $"{name} · Optional" : $"{name} · Required";
      }
   }
}

public class ChecklistItem
{
   public Guid Id { get; set; } = Guid.NewGuid();

   public string Title { get; set; } = string.Empty;

   public string Category { get; set; } = string.Empty;

   public ChecklistPriority Priority { get; set; } =
       ChecklistPriority.Required;

   public string Notes { get; set; } = string.Empty;

   /// <summary>
   /// How you know this check is done. Missing until someone writes it.
   /// Older files that still say "Acceptance:" inside notes are split on open.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? Acceptance
   {
      get => _acceptance;
      set => _acceptance = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
   }

   /// <summary>
   /// Investigation, Implementation, Verification, or Documentation.
   /// Missing when the check has no single kind of work.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? ItemType
   {
      get => _itemType;
      set => _itemType = ChecklistVocabulary.CanonicalItemType(value);
   }

   /// <summary>Test report names, paths, or links. Missing until filled.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? TestReports
   {
      get => _testReports;
      set => _testReports = Clean(value);
   }

   /// <summary>Log excerpts or links. Missing until filled.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? Logs
   {
      get => _logs;
      set => _logs = Clean(value);
   }

   /// <summary>Files, symbols, or links. Missing until filled.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? CodeReferences
   {
      get => _codeReferences;
      set => _codeReferences = Clean(value);
   }

   /// <summary>API response excerpts or links. Missing until filled.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? ApiResponses
   {
      get => _apiResponses;
      set => _apiResponses = Clean(value);
   }

   /// <summary>Pull request links. Missing until filled.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? PullRequestLinks
   {
      get => _pullRequestLinks;
      set => _pullRequestLinks = Clean(value);
   }

   /// <summary>
   /// Optional calendar deadline. Missing on older files, and left unset until the user picks one.
   /// </summary>
   public DateOnly? Deadline { get; set; }

   /// <summary>
   /// Local minutes from midnight when a time was picked. Missing on older date-only deadlines.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public int? DeadlineMinute { get; set; }

   /// <summary>
   /// Null skips a deadline notification. Zero notifies at the deadline. A positive value is minutes before it.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public int? NotifyBeforeMinutes { get; set; }

   /// <summary>
   /// When set, this check's deadline notification is delivered without a sound.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
   public bool DeadlineSilent { get; set; }

   public List<ChecklistSubtask> Subtasks { get; set; } = [];

   /// <summary>
   /// Screenshots and videos chosen from a folder. Only the path is stored.
   /// </summary>
   public List<EvidenceFile> Evidence { get; set; } = [];

   [JsonIgnore]
   public bool HasNotes =>
       !string.IsNullOrWhiteSpace(Notes);

   [JsonIgnore]
   public bool HasAcceptance => _acceptance is not null;

   [JsonIgnore]
   public bool HasItemType => _itemType is not null;

   [JsonIgnore]
   public bool HasReferences =>
       _testReports is not null
       || _logs is not null
       || _codeReferences is not null
       || _apiResponses is not null
       || _pullRequestLinks is not null;

   [JsonIgnore]
   public IReadOnlyList<CheckReference> References
   {
      get
      {
         var title = string.IsNullOrWhiteSpace(Title) ? "Check" : Title;
         var detail = Detail;
         var list = new List<CheckReference>(5);
         AddReference(list, title, detail, "Test reports", "View test reports", _testReports);
         AddReference(list, title, detail, "Logs", "View logs", _logs);
         AddReference(list, title, detail, "Code references", "View code references", _codeReferences);
         AddReference(list, title, detail, "API responses", "View API responses", _apiResponses);
         AddReference(list, title, detail, "PR links", "View PR links", _pullRequestLinks);
         return list;
      }
   }

   public bool MatchesReference(string search) =>
       Hit(_testReports, search)
       || Hit(_logs, search)
       || Hit(_codeReferences, search)
       || Hit(_apiResponses, search)
       || Hit(_pullRequestLinks, search);

   public void CopyReferencesFrom(ChecklistItem source)
   {
      ArgumentNullException.ThrowIfNull(source);
      TestReports = source.TestReports;
      Logs = source.Logs;
      CodeReferences = source.CodeReferences;
      ApiResponses = source.ApiResponses;
      PullRequestLinks = source.PullRequestLinks;
   }

   [JsonIgnore]
   public bool HasEvidence =>
       Evidence is { Count: > 0 };

   [JsonIgnore]
   public bool HasSubtasks =>
       Subtasks.Count > 0;

   [JsonIgnore]
   public bool HasLinks =>
       Subtasks.Any(subtask => subtask.IsLink);

   [JsonIgnore]
   public bool HasManualSubtasks =>
       Subtasks.Any(subtask => subtask.IsManual);

   [JsonIgnore]
   public IEnumerable<ChecklistSubtask> LinkedSubtasks =>
       Subtasks.Where(subtask => subtask.IsLink);

   [JsonIgnore]
   public IEnumerable<ChecklistSubtask> ManualSubtasks =>
       Subtasks.Where(subtask => subtask.IsManual);

   /// <summary>
   /// A check can be completed once every manual subtask and every required link is done.
   /// Optional links do not block it.
   /// </summary>
   [JsonIgnore]
   public bool CanComplete =>
       !Subtasks.Any(subtask => !subtask.IsCompleted && subtask.BlocksParentCompletion);

   [JsonIgnore]
   public string Detail =>
       string.IsNullOrWhiteSpace(Category)
           ? Priority.ToString()
           : $"{Category} · {Priority}";

   public bool IsCompleted { get; set; }

   /// <summary>
   /// The user set this check aside. It is not completed.
   /// It is left out of progress and does not block completing the checklist, including when it is Critical or Required.
   /// Missing on older files, which stay included.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
   public bool IsSkipped { get; set; }

   /// <summary>
   /// An open Critical or Required check still has to be finished or skipped before the checklist can be completed.
   /// </summary>
   [JsonIgnore]
   public bool BlocksWorkspaceCompletion =>
       !IsSkipped &&
       !IsCompleted &&
       Priority is ChecklistPriority.Critical or ChecklistPriority.Required;

   public void MarkCompleted(bool completed)
   {
      if (completed)
         IsSkipped = false;

      IsCompleted = completed;
   }

   public void MarkSkipped(bool skipped)
   {
      IsSkipped = skipped;
      if (skipped)
         IsCompleted = false;
   }

   /// <summary>
   /// File name of the checklist this check was copied from. Missing when the check was created here.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? CopiedFrom
   {
      get => _copiedFrom;
      set => _copiedFrom = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
   }

   /// <summary>
   /// Full path of that checklist when it had been saved. Missing for an unsaved source.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? CopiedFromPath
   {
      get => _copiedFromPath;
      set => _copiedFromPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
   }

   [JsonIgnore]
   public bool HasCopiedFrom => CopiedFrom is not null;

   [JsonIgnore]
   public bool CanOpenCopiedFrom => CopiedFromPath is not null;

   [JsonIgnore]
   public bool CopiedFromIsPlain => HasCopiedFrom && !CanOpenCopiedFrom;

   private static string? Clean(string? value) =>
       string.IsNullOrWhiteSpace(value) ? null : value.Trim();

   private static bool Hit(string? value, string search) =>
       value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

   private static void AddReference(
       List<CheckReference> list,
       string title,
       string detail,
       string label,
       string viewLabel,
       string? text)
   {
      if (string.IsNullOrWhiteSpace(text))
         return;

      list.Add(new CheckReference
      {
         Title = title,
         Detail = detail,
         Label = label,
         ViewLabel = viewLabel,
         Text = text
      });
   }

   private string? _acceptance;
   private string? _itemType;
   private string? _testReports;
   private string? _logs;
   private string? _codeReferences;
   private string? _apiResponses;
   private string? _pullRequestLinks;
   private string? _copiedFrom;
   private string? _copiedFromPath;
}

public sealed class CheckReference
{
   public string Title { get; init; } = string.Empty;

   public string Detail { get; init; } = string.Empty;

   public string Label { get; init; } = string.Empty;

   public string ViewLabel { get; init; } = string.Empty;

   public string Text { get; init; } = string.Empty;
}

public enum ChecklistPriority
{
   Critical,
   Required,
   Conditional,
   Review
}

/// <summary>
/// Progress over the checks that still count. Skipped checks are left out of both numbers.
/// </summary>
public readonly record struct ChecklistProgress(int Completed, int Total, int Skipped)
{
   public double Percent => Total == 0 ? 0 : (double)Completed / Total * 100;

   public string Label =>
       Skipped == 0
           ? $"{Completed} / {Total} completed"
           : $"{Completed} / {Total} completed · {Skipped} skipped";

   public string ShareLabel =>
       Skipped == 0
           ? $"{Completed} of {Total} complete"
           : $"{Completed} of {Total} complete, {Skipped} skipped";
}

public static class ChecklistCounts
{
   public static ChecklistProgress Measure(IEnumerable<ChecklistItem> items)
   {
      var completed = 0;
      var total = 0;
      var skipped = 0;
      foreach (var item in items)
      {
         if (item.IsSkipped)
         {
            skipped++;
            continue;
         }

         total++;
         if (item.IsCompleted)
            completed++;
      }

      return new ChecklistProgress(completed, total, skipped);
   }

   public static int IncompleteRequired(IEnumerable<ChecklistItem> items) =>
       items.Count(item => item.BlocksWorkspaceCompletion);

   /// <summary>
   /// Every check that still counts is done. A list of only skipped checks has nothing finished.
   /// </summary>
   public static bool AllCountedComplete(IEnumerable<ChecklistItem> items)
   {
      var any = false;
      foreach (var item in items)
      {
         if (item.IsSkipped)
            continue;

         any = true;
         if (!item.IsCompleted)
            return false;
      }

      return any;
   }
}