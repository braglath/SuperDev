using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class ChecklistReviewValidator
{
    public const int MaxFindings = ChecklistReviewPromptBuilder.MaxFindings;

    public const string IncompleteWarning =
        "A finding was left out because it was incomplete or used an unsupported value.";

    public const string UnmatchedWarning =
        "A finding was left out because it pointed at a check that is not on this checklist.";

    public const string DuplicateWarning = "A duplicate finding was left out.";

    public const string CoveredWarning =
        "A suggestion was left out because that check is already covered.";

    public const string TruncatedWarning = "Only the first 10 findings are shown.";

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "findings": {
              "type": "array",
              "maxItems": 10,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "category": {
                    "type": "string",
                    "enum": ["Completeness", "Duplicates", "Clarity", "Testing", "Security", "Performance", "Priority"]
                  },
                  "severity": {
                    "type": "string",
                    "enum": ["Critical", "High", "Medium", "Low"]
                  },
                  "title": { "type": "string" },
                  "explanation": { "type": "string" },
                  "whyItMatters": { "type": "string" },
                  "suggestedAction": { "type": "string" },
                  "recommendationType": {
                    "type": "string",
                    "enum": ["AddItem", "ImproveItem", "PossibleDuplicate", "PriorityAdjustment", "Observation"]
                  },
                  "relatedItemIds": {
                    "type": "array",
                    "maxItems": 4,
                    "items": { "type": "string" }
                  },
                  "suggestedItem": {
                    "type": "object",
                    "properties": {
                      "title": { "type": "string" },
                      "section": { "type": "string" },
                      "description": { "type": "string" },
                      "acceptanceCriteria": { "type": "string" },
                      "priority": { "type": "string" },
                      "type": { "type": "string" }
                    }
                  }
                },
                "required": ["category", "severity", "title", "explanation", "whyItMatters", "suggestedAction", "recommendationType"]
              }
            }
          },
          "required": ["findings"]
        }
        """);

    public static JsonElement Schema => SchemaDocument.RootElement;

    public ChecklistReviewInspection Read(string? content, ChecklistReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(content))
            return Fail(AiFailureKind.EmptyResponse);

        var json = ChecklistValidator.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
            return Fail(AiFailureKind.InvalidJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return Fail(AiFailureKind.InvalidJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryProperty(root, "findings", out var findings))
                return Fail(AiFailureKind.InvalidChecklist);

            if (findings.ValueKind != JsonValueKind.Array)
                return Fail(AiFailureKind.InvalidChecklist);

            return Finish(findings, request);
        }
    }

    private static ChecklistReviewInspection Finish(JsonElement findings, ChecklistReviewRequest request)
    {
        var kept = new List<ChecklistReviewFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var invalid = 0;
        var filtered = 0;
        var raw = 0;

        var addedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in findings.EnumerateArray())
        {
            raw++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                invalid++;
                continue;
            }

            if (!TryRead(element, request, out var finding, out var reason))
            {
                if (reason == RejectReason.Invalid)
                {
                    invalid++;
                }
                else
                {
                    filtered++;
                    Warn(warnings, reason == RejectReason.Covered ? CoveredWarning : UnmatchedWarning);
                }

                continue;
            }

            if (!seen.Add(finding.Id)
                || (finding.RecommendationType == ReviewRecommendationType.AddItem
                    && finding.SuggestedItem is not null
                    && !addedTitles.Add(finding.SuggestedItem.Title.Trim())))
            {
                filtered++;
                Warn(warnings, DuplicateWarning);
                continue;
            }

            if (kept.Count >= MaxFindings)
            {
                filtered++;
                Warn(warnings, TruncatedWarning);
                continue;
            }

            kept.Add(finding);
        }

        kept = Consolidate(kept);

        if (raw > 0 && kept.Count == 0 && invalid > 0 && filtered == 0)
            return Fail(AiFailureKind.InvalidChecklist);

        if (invalid > 0)
            Warn(warnings, IncompleteWarning);

        return new ChecklistReviewInspection
        {
            IsValid = true,
            Review = new ChecklistReviewResult
            {
                ChecklistTitle = request.Title ?? string.Empty,
                Findings = kept
            },
            Warnings = warnings
        };
    }

    private enum RejectReason
    {
        Invalid,
        Unmatched,
        Covered
    }

    private static bool TryRead(
        JsonElement element,
        ChecklistReviewRequest request,
        out ChecklistReviewFinding finding,
        out RejectReason reason)
    {
        finding = new ChecklistReviewFinding();
        reason = RejectReason.Invalid;

        var categoryText = AiChecklistJson.FirstString(element, "category");
        var severityText = AiChecklistJson.FirstString(element, "severity");
        var recommendationText = AiChecklistJson.FirstString(element, "recommendationType", "recommendation_type");
        var title = Limit(AiChecklistJson.FirstString(element, "title"), 120);
        var explanation = Limit(AiChecklistJson.FirstString(element, "explanation"), 360);
        var why = Limit(AiChecklistJson.FirstString(element, "whyItMatters", "why_it_matters", "why"), 280);
        var action = Limit(AiChecklistJson.FirstString(element, "suggestedAction", "suggested_action", "action"), 280);

        if (!TryCategory(categoryText, out var category)
            || !TrySeverity(severityText, out var severity)
            || !TryRecommendation(recommendationText, out var recommendation)
            || title.Length == 0
            || explanation.Length == 0
            || why.Length == 0
            || action.Length == 0)
        {
            return false;
        }

        var suggested = ReadSuggested(element);
        var tokens = ReadIdTokens(element);
        var related = new List<Guid>();
        var unresolved = 0;
        foreach (var token in tokens)
        {
            if (request.Items.Count == 0)
            {
                unresolved++;
                continue;
            }

            var match = Resolve(token, request.Items);
            if (match is null)
                unresolved++;
            else if (!related.Contains(match.Value))
                related.Add(match.Value);
        }

        if (recommendation == ReviewRecommendationType.AddItem)
            suggested = CompleteAddition(suggested, title, action, explanation);

        if (recommendation == ReviewRecommendationType.AddItem
            && suggested is not null
            && NamesExistingItem(suggested.Title, request))
        {
            AttachMentions(suggested.Title, request, related);
            AttachMentions(action, request, related);
            AttachMentions(explanation, request, related);
            AttachMentions(title, request, related);
            var note = EditNote(action, explanation);
            if (related.Count > 0 && note.Length > 0)
            {
                recommendation = ReviewRecommendationType.ImproveItem;
                suggested = new SuggestedChecklistItem
                {
                    Description = note,
                    AppendNote = true
                };
            }
            else
            {
                suggested = null;
                recommendation = ReviewRecommendationType.Observation;
            }
        }

        if (recommendation == ReviewRecommendationType.AddItem
            && (suggested is null || string.IsNullOrWhiteSpace(suggested.Title)))
            recommendation = ReviewRecommendationType.Observation;

        var linkCount = Math.Max(tokens.Count, related.Count);
        if (!Accept(recommendation, suggested, related, unresolved, linkCount, request, out reason))
        {
            if (reason != RejectReason.Invalid)
                return false;

            recommendation = ReviewRecommendationType.Observation;
            suggested = null;
        }

        finding = new ChecklistReviewFinding
        {
            Category = category,
            Severity = severity,
            Title = title,
            Explanation = explanation,
            WhyItMatters = why,
            SuggestedAction = action,
            SuggestedItem = suggested,
            RelatedItemIds = related,
            RecommendationType = recommendation
        };
        finding.Id = StableId(finding);
        return true;
    }

    private static bool Accept(
        ReviewRecommendationType recommendation,
        SuggestedChecklistItem? suggested,
        List<Guid> related,
        int unresolved,
        int tokenCount,
        ChecklistReviewRequest request,
        out RejectReason reason)
    {
        reason = RejectReason.Invalid;
        switch (recommendation)
        {
            case ReviewRecommendationType.AddItem:
                if (suggested is null || string.IsNullOrWhiteSpace(suggested.Title))
                    return false;
                if (AlreadyCovered(suggested.Title, request))
                {
                    reason = RejectReason.Covered;
                    return false;
                }

                return true;

            case ReviewRecommendationType.ImproveItem:
                if (suggested is null || !HasImprovement(suggested))
                    return false;
                if (!RequireRelated(related, unresolved, tokenCount, minimum: 1, out reason))
                    return false;
                if (related.All(id => ImprovementAlreadyApplied(request, id, suggested)))
                {
                    reason = RejectReason.Covered;
                    return false;
                }

                return true;

            case ReviewRecommendationType.PriorityAdjustment:
                if (suggested is null || string.IsNullOrWhiteSpace(suggested.Priority))
                    return false;
                if (!ChecklistReviewMapper.TryPriority(suggested.Priority, out var priority))
                    return false;
                if (!RequireRelated(related, unresolved, tokenCount, minimum: 1, out reason))
                    return false;
                if (related.All(id => PriorityAlreadyApplied(request, id, priority)))
                {
                    reason = RejectReason.Covered;
                    return false;
                }

                return true;

            case ReviewRecommendationType.PossibleDuplicate:
                if (tokenCount < 2)
                    return false;
                return RequireRelated(related, unresolved, tokenCount, minimum: 2, out reason);

            default:
                return true;
        }
    }

    private static bool RequireRelated(
        List<Guid> related,
        int unresolved,
        int tokenCount,
        int minimum,
        out RejectReason reason)
    {
        reason = RejectReason.Invalid;
        if (tokenCount == 0)
            return false;

        if (related.Count >= minimum)
            return true;

        reason = RejectReason.Unmatched;
        return false;
    }

    private static bool ImprovementAlreadyApplied(
        ChecklistReviewRequest request,
        Guid itemId,
        SuggestedChecklistItem suggested)
    {
        var current = FindItem(request, itemId);
        if (current is null)
            return false;

        var title = string.IsNullOrWhiteSpace(suggested.Title) ? current.Title : suggested.Title.Trim();
        var section = string.IsNullOrWhiteSpace(suggested.Section) ? current.Section : suggested.Section.Trim();
        var description = ChecklistReviewMapper.MergeDescription(current.Description, suggested);
        var acceptance = string.IsNullOrWhiteSpace(suggested.AcceptanceCriteria)
            ? current.AcceptanceCriteria
            : suggested.AcceptanceCriteria.Trim();
        return Same(title, current.Title)
            && Same(section, current.Section)
            && Same(description, current.Description)
            && Same(acceptance, current.AcceptanceCriteria);
    }

    private static bool PriorityAlreadyApplied(
        ChecklistReviewRequest request,
        Guid itemId,
        ChecklistPriority priority)
    {
        var current = FindItem(request, itemId);
        return current is not null && Same(current.Priority, priority.ToString());
    }

    private static ChecklistReviewItem? FindItem(ChecklistReviewRequest request, Guid itemId)
    {
        foreach (var item in request.Items)
        {
            if (item.Id == itemId)
                return item;
        }

        return null;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static SuggestedChecklistItem? CompleteAddition(
        SuggestedChecklistItem? suggested,
        string findingTitle,
        string action,
        string explanation)
    {
        if (suggested is not null && !string.IsNullOrWhiteSpace(suggested.Title))
            return suggested;

        var recovered = Quoted(action) ?? Imperative(findingTitle) ?? Imperative(action);
        if (recovered is null)
            return suggested;

        suggested ??= new SuggestedChecklistItem();
        suggested.Title = Limit(recovered.Trim().TrimEnd('.'), 120);
        if (string.IsNullOrWhiteSpace(suggested.Description))
            suggested.Description = Limit(explanation, 220);
        if (string.IsNullOrWhiteSpace(suggested.Priority))
            suggested.Priority = ChecklistVocabulary.DefaultPriority;
        return suggested;
    }

    private static readonly Regex AliasPattern = new(
        @"\bi(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ItemPhrase = new(
        @"\b(?:to\s+)?items?\s+i\d+(?:\s*(?:,|and|&)\s*i\d+)*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool NamesExistingItem(string? text, ChecklistReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(text) || request.Items.Count == 0)
            return false;

        foreach (Match match in AliasPattern.Matches(text))
        {
            if (Resolve("i" + match.Groups[1].Value, request.Items) is not null)
                return true;
        }

        return false;
    }

    private static void AttachMentions(string? text, ChecklistReviewRequest request, List<Guid> related)
    {
        if (string.IsNullOrWhiteSpace(text) || request.Items.Count == 0)
            return;

        foreach (Match match in AliasPattern.Matches(text))
        {
            var id = Resolve("i" + match.Groups[1].Value, request.Items);
            if (id is not null && !related.Contains(id.Value))
                related.Add(id.Value);
        }
    }

    private static string EditNote(string action, string explanation)
    {
        var cleaned = CleanItemReferences(action);
        if (cleaned.Length >= 12)
            return Limit(cleaned, 220);

        return Limit(CleanItemReferences(explanation), 220);
    }

    private static string CleanItemReferences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = ItemPhrase.Replace(text, " ");
        cleaned = AliasPattern.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+([,.;:])", "$1");
        return cleaned.Trim(' ', ',', ';', ':');
    }

    private static List<ChecklistReviewFinding> Consolidate(List<ChecklistReviewFinding> findings)
    {
        var grouped = new List<ChecklistReviewFinding>();
        foreach (var finding in findings)
        {
            var match = grouped.FirstOrDefault(existing => SameTask(existing, finding));
            if (match is null)
                grouped.Add(finding);
            else
                Merge(match, finding);
        }

        return grouped;
    }

    private static bool SameTask(ChecklistReviewFinding left, ChecklistReviewFinding right) =>
        left.RelatedItemIds.Count == 1
        && right.RelatedItemIds.Count == 1
        && left.RelatedItemIds[0] == right.RelatedItemIds[0];

    private static void Merge(ChecklistReviewFinding target, ChecklistReviewFinding extra)
    {
        if (SeverityValue(extra.Severity) < SeverityValue(target.Severity))
        {
            (target.Severity, extra.Severity) = (extra.Severity, target.Severity);
            (target.Title, extra.Title) = (extra.Title, target.Title);
            (target.Category, extra.Category) = (extra.Category, target.Category);
            (target.RecommendationType, extra.RecommendationType) = (extra.RecommendationType, target.RecommendationType);
            (target.SuggestedItem, extra.SuggestedItem) = (extra.SuggestedItem, target.SuggestedItem);
        }

        if (extra.RecommendationType == ReviewRecommendationType.PriorityAdjustment
            || target.RecommendationType == ReviewRecommendationType.PriorityAdjustment)
            target.IncludesPriorityChange = true;

        if (target.RecommendationType == ReviewRecommendationType.PriorityAdjustment
            && extra.RecommendationType is ReviewRecommendationType.ImproveItem or ReviewRecommendationType.AddItem)
            target.RecommendationType = extra.RecommendationType;

        CopyMissingSuggestion(target, extra);
        target.Explanation = JoinUnique(target.Explanation, extra.Explanation);
        target.WhyItMatters = JoinUnique(target.WhyItMatters, extra.WhyItMatters);
        var extraAction = string.IsNullOrWhiteSpace(extra.Title)
            ? extra.SuggestedAction
            : extra.Title.Trim() + " — " + extra.SuggestedAction.Trim();
        target.SuggestedAction = JoinUnique(target.SuggestedAction, extraAction);
        target.Id = StableId(target);
    }

    private static void CopyMissingSuggestion(ChecklistReviewFinding target, ChecklistReviewFinding extra)
    {
        if (extra.SuggestedItem is null)
            return;

        target.SuggestedItem ??= new SuggestedChecklistItem();
        var into = target.SuggestedItem;
        var from = extra.SuggestedItem;
        if (string.IsNullOrWhiteSpace(into.Title))
            into.Title = from.Title;
        if (string.IsNullOrWhiteSpace(into.Section))
            into.Section = from.Section;
        if (string.IsNullOrWhiteSpace(into.Priority))
            into.Priority = from.Priority;
        if (string.IsNullOrWhiteSpace(into.AcceptanceCriteria))
            into.AcceptanceCriteria = from.AcceptanceCriteria;
        if (string.IsNullOrWhiteSpace(into.Description))
            into.Description = from.Description;
        else if (!string.IsNullOrWhiteSpace(from.Description))
            into.Description = JoinUnique(into.Description, from.Description);
        into.AppendNote = into.AppendNote || from.AppendNote;
    }

    private static int SeverityValue(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => 0,
        ReviewSeverity.High => 1,
        ReviewSeverity.Medium => 2,
        _ => 3
    };

    private static string JoinUnique(string left, string right)
    {
        var first = left?.Trim() ?? string.Empty;
        var second = right?.Trim() ?? string.Empty;
        if (second.Length == 0 || first.Contains(second, StringComparison.OrdinalIgnoreCase))
            return first;
        if (first.Length == 0 || second.Contains(first, StringComparison.OrdinalIgnoreCase))
            return second;

        var joined = first + "\n" + second;
        return joined.Length <= 500 ? joined : joined[..500].TrimEnd();
    }

    private static string? Quoted(string text)
    {
        foreach (var mark in new[] { '\'', '"' })
        {
            var start = text.IndexOf(mark);
            if (start < 0)
                continue;

            var end = text.IndexOf(mark, start + 1);
            if (end <= start + 1)
                continue;

            var inside = text[(start + 1)..end].Trim();
            if (inside.Length is >= 8 and <= 120)
                return inside;
        }

        return null;
    }

    private static string? Imperative(string text)
    {
        var trimmed = text.Trim().TrimEnd('.');
        if (trimmed.Length < 8)
            return null;
        if (trimmed.Length > 180)
            trimmed = trimmed[..180].TrimEnd();

        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        return verb is "add" or "verify" or "test" or "check" or "confirm" or "write" or "cover"
            or "reproduce" or "retest" or "inspect" or "review" or "implement" or "fix" or "prevent"
            or "ensure" or "handle" or "document" or "measure" or "validate"
            ? trimmed
            : null;
    }

    private static bool AlreadyCovered(string title, ChecklistReviewRequest request) =>
        request.Items.Any(item => item.Title.Trim().Equals(title.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool HasImprovement(SuggestedChecklistItem suggested) =>
        !string.IsNullOrWhiteSpace(suggested.Title)
        || !string.IsNullOrWhiteSpace(suggested.Section)
        || !string.IsNullOrWhiteSpace(suggested.Description)
        || !string.IsNullOrWhiteSpace(suggested.AcceptanceCriteria);

    private static SuggestedChecklistItem? ReadSuggested(JsonElement element)
    {
        if (!TryProperty(element, "suggestedItem", out var item) && !TryProperty(element, "suggested_item", out item))
            return null;

        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var suggested = new SuggestedChecklistItem
        {
            Title = Limit(AiChecklistJson.FirstString(item, "title", "name"), 120),
            Section = Limit(AiChecklistJson.FirstString(item, "section", "category", "section_title"), 60),
            Description = Limit(AiChecklistJson.FirstString(item, "description", "detail"), 220),
            AcceptanceCriteria = Limit(AiChecklistJson.FirstString(item, "acceptanceCriteria", "acceptance_criteria", "acceptance"), 180),
            Priority = Limit(AiChecklistJson.FirstString(item, "priority"), 40),
            Type = Limit(AiChecklistJson.FirstString(item, "type", "itemType", "item_type"), 40)
        };

        if (suggested.Title.Length == 0
            && suggested.Section.Length == 0
            && suggested.Description.Length == 0
            && suggested.AcceptanceCriteria.Length == 0
            && suggested.Priority.Length == 0
            && suggested.Type.Length == 0)
            return null;

        return suggested;
    }

    private static List<string> ReadIdTokens(JsonElement element)
    {
        JsonElement value = default;
        var found = false;
        foreach (var name in new[] { "relatedItemIds", "related_item_ids", "relatedItems" })
        {
            if (TryProperty(element, name, out value))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return [];

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (value.ValueKind != JsonValueKind.Array)
            return [];

        var tokens = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            var token = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Number => entry.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(token))
                tokens.Add(token.Trim());
            if (tokens.Count == 4)
                break;
        }

        return tokens;
    }

    private static Guid? Resolve(string token, IReadOnlyList<ChecklistReviewItem> items)
    {
        if (Guid.TryParse(token, out var id))
        {
            foreach (var item in items)
            {
                if (item.Id == id)
                    return id;
            }
        }

        foreach (var item in items)
        {
            if (item.Alias.Equals(token, StringComparison.OrdinalIgnoreCase))
                return item.Id;
        }

        return null;
    }

    public static string StableId(ChecklistReviewFinding finding)
    {
        var related = string.Join(",", finding.RelatedItemIds.OrderBy(id => id).Select(id => id.ToString("D")));
        var suggested = finding.SuggestedItem?.Title?.Trim() ?? string.Empty;
        var key = string.Join(
            "|",
            finding.RecommendationType,
            finding.Category,
            finding.Title.Trim().ToLowerInvariant(),
            suggested.ToLowerInvariant(),
            related);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "f-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static bool TryCategory(string raw, out ReviewCategory category)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "completeness":
                category = ReviewCategory.Completeness;
                return true;
            case "duplicates":
            case "duplicate":
                category = ReviewCategory.Duplicates;
                return true;
            case "clarity":
                category = ReviewCategory.Clarity;
                return true;
            case "testing":
            case "test":
                category = ReviewCategory.Testing;
                return true;
            case "security":
                category = ReviewCategory.Security;
                return true;
            case "performance":
                category = ReviewCategory.Performance;
                return true;
            case "priority":
                category = ReviewCategory.Priority;
                return true;
            default:
                category = default;
                return false;
        }
    }

    private static bool TrySeverity(string raw, out ReviewSeverity severity)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "critical":
                severity = ReviewSeverity.Critical;
                return true;
            case "high":
                severity = ReviewSeverity.High;
                return true;
            case "medium":
                severity = ReviewSeverity.Medium;
                return true;
            case "low":
                severity = ReviewSeverity.Low;
                return true;
            default:
                severity = default;
                return false;
        }
    }

    private static bool TryRecommendation(string raw, out ReviewRecommendationType recommendation)
    {
        var key = raw.Trim().Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        switch (key)
        {
            case "additem":
                recommendation = ReviewRecommendationType.AddItem;
                return true;
            case "improveitem":
                recommendation = ReviewRecommendationType.ImproveItem;
                return true;
            case "possibleduplicate":
                recommendation = ReviewRecommendationType.PossibleDuplicate;
                return true;
            case "priorityadjustment":
                recommendation = ReviewRecommendationType.PriorityAdjustment;
                return true;
            case "observation":
                recommendation = ReviewRecommendationType.Observation;
                return true;
            default:
                recommendation = default;
                return false;
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static string Limit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max].TrimEnd();
    }

    private static void Warn(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning))
            warnings.Add(warning);
    }

    private static ChecklistReviewInspection Fail(AiFailureKind kind) => new()
    {
        IsValid = false,
        FailureKind = kind
    };
}
