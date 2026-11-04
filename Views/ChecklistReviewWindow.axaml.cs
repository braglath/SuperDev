using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public sealed class ChecklistReviewSession
{
    public ChecklistReviewResult? Result { get; set; }

    public required Func<ChecklistReviewRequest> Capture { get; init; }

    public required Func<ChecklistReviewRequest, CancellationToken, Task<ChecklistReviewOutcome>> Review { get; init; }

    public required Func<ChecklistReviewFinding, ReviewApplyResult> Apply { get; init; }

    public required Func<ChecklistReviewFinding, ReviewApplyResult> Revert { get; init; }

    public required Action<ChecklistReviewFinding> Dismiss { get; init; }

    public required Func<Guid, bool> Reveal { get; init; }

    public required Func<Guid, ChecklistItem?> Find { get; init; }

    public required Func<bool> CanApply { get; init; }
}

public partial class ChecklistReviewWindow : Window
{
    private static readonly ReviewCategory[] CategoryOrder =
    [
        ReviewCategory.Completeness,
        ReviewCategory.Duplicates,
        ReviewCategory.Clarity,
        ReviewCategory.Testing,
        ReviewCategory.Security,
        ReviewCategory.Performance,
        ReviewCategory.Priority
    ];

    private readonly ChecklistReviewSession _session;
    private CancellationTokenSource? _run;
    private int _generation;
    private bool _busy;
    private CheckDetailWindow? _view;
    private IReadOnlyList<string> _warnings = [];

    public ChecklistReviewWindow(ChecklistReviewSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ChecklistNameText.Text = _session.Capture().Title;
        if (_session.Result is null)
        {
            _ = RunAsync();
            return;
        }

        ShowResult(_session.Result, []);
    }

    protected override void OnClosed(EventArgs e)
    {
        _generation++;
        _run?.Cancel();
        _view?.Close();
        base.OnClosed(e);
    }

    private async void Rerun_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync();

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        _run?.Cancel();

    private void Close_Click(object? sender, RoutedEventArgs e) =>
        Close();

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _session.Result is null)
            return;

        var selected = _session.Result.SelectApplicable();
        StatusText.Text = selected == 0
            ? "No suggestions can be applied."
            : selected + (selected == 1 ? " suggestion selected." : " suggestions selected.");
        Render(_session.Result);
    }

    private async void ApplySelected_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _session.Result is null)
            return;

        if (!_session.CanApply())
        {
            StatusText.Text = "Reopen the checklist before applying suggestions.";
            return;
        }

        var selected = _session.Result.Findings.Where(finding => finding.Selected && finding.CanApply).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select a suggestion first.";
            ApplyButton.IsEnabled = false;
            return;
        }

        var applied = 0;
        string? last = null;
        foreach (var finding in selected)
        {
            if (!await ConfirmAsync(finding))
                continue;

            var result = _session.Apply(finding);
            last = result.Message;
            if (!result.Changed)
                continue;

            applied++;
            if (result.ItemId is Guid itemId)
                _session.Reveal(itemId);
        }

        StatusText.Text = applied == 0
            ? last ?? "No suggestions were applied."
            : applied == 1 ? "Applied 1 suggestion." : "Applied " + applied + " suggestions.";
        Render(_session.Result);
    }

    private async Task RunAsync()
    {
        _run?.Cancel();
        var generation = ++_generation;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        SetBusy(true, "Reviewing this checklist…");
        ChecklistNameText.Text = _session.Capture().Title;

        ChecklistReviewOutcome outcome;
        try
        {
            outcome = await _session.Review(_session.Capture(), token);
        }
        catch (OperationCanceledException)
        {
            outcome = Cancelled();
        }
        catch (Exception)
        {
            outcome = new ChecklistReviewOutcome
            {
                Failure = new AiFailure
                {
                    Kind = AiFailureKind.Unexpected,
                    Message = ChecklistReviewMessages.For(AiFailureKind.Unexpected)
                }
            };
        }

        if (generation != _generation)
            return;

        SetBusy(false, string.Empty);
        if (token.IsCancellationRequested && (outcome.Failure is null || outcome.Failure.Kind != AiFailureKind.Cancelled))
            outcome = Cancelled();

        if (!outcome.Succeeded || outcome.Review is null)
        {
            StatusText.Text = outcome.Failure?.Message
                ?? ChecklistReviewMessages.For(AiFailureKind.InvalidChecklist);
            if (_session.Result is not null)
                Render(_session.Result);
            else
            {
                SelectButton.IsEnabled = false;
                ApplyButton.IsEnabled = false;
            }

            return;
        }

        _session.Result = outcome.Review;
        ShowResult(outcome.Review, outcome.Warnings);
    }

    private void ShowResult(ChecklistReviewResult review, IReadOnlyList<string> warnings)
    {
        _warnings = warnings;
        var active = review.ActiveCount;
        StatusText.Text = active == 0
            ? "Review complete. No findings."
            : active == 1
                ? "Review complete. 1 finding."
                : "Review complete. " + active + " findings.";
        WarningText.Text = string.Join(" ", warnings);
        WarningText.IsVisible = warnings.Count > 0;
        Render(review);
    }

    private void Render(ChecklistReviewResult review)
    {
        FindingsPanel.Children.Clear();
        var visible = review.Findings.Where(finding => !finding.Dismissed).ToList();
        var canApply = _session.CanApply();
        var bulk = review.AllowsBulkSelection;
        SelectButton.IsVisible = bulk;
        SelectButton.IsEnabled = !_busy && canApply && bulk;
        ApplyButton.IsEnabled = !_busy && canApply && review.HasSelectedSuggestion;

        if (visible.Count == 0)
        {
            FindingsPanel.Children.Add(new TextBlock
            {
                Classes = { "hint" },
                Text = review.Findings.Count == 0
                    ? "No findings. This checklist already covers the available task."
                    : "All findings in this review were dismissed. Rerun the review to look at the checklist again."
            });
            return;
        }

        foreach (var category in CategoryOrder)
        {
            var group = visible
                .Where(finding => finding.Category == category)
                .OrderBy(finding => SeverityRank(finding.Severity))
                .ToList();
            if (group.Count == 0)
                continue;

            FindingsPanel.Children.Add(new TextBlock
            {
                Classes = { "section-label" },
                Text = CategoryLabel(category) + " · " + group.Count,
                Margin = new Avalonia.Thickness(2, 4, 0, 0)
            });

            foreach (var finding in group)
                FindingsPanel.Children.Add(CreateCard(finding, canApply, bulk));
        }
    }

    private Control CreateCard(ChecklistReviewFinding finding, bool canApply, bool bulk)
    {
        var body = new StackPanel { Spacing = 8 };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10
        };

        var showSelect = bulk && finding.CanApply;
        if (showSelect)
        {
            var select = new CheckBox
            {
                IsChecked = finding.Selected,
                IsEnabled = !_busy && canApply,
                VerticalAlignment = VerticalAlignment.Center
            };
            select.IsCheckedChanged += (_, _) =>
            {
                finding.Selected = select.IsChecked == true;
                if (_session.Result is not null)
                    ApplyButton.IsEnabled = !_busy && _session.CanApply() && _session.Result.HasSelectedSuggestion;
            };
            ToolTip.SetTip(select, "Select this suggestion");
            header.Children.Add(select);
        }

        var titles = new StackPanel { Spacing = 2 };
        Grid.SetColumn(titles, showSelect ? 1 : 0);
        if (!showSelect)
            Grid.SetColumnSpan(titles, 2);
        titles.Children.Add(new TextBlock
        {
            Text = finding.Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        titles.Children.Add(new TextBlock
        {
            Text = SeverityLabel(finding.Severity) + " · " + RecommendationLabel(finding.RecommendationType)
                + (finding.Applied ? " · Applied" : string.Empty),
            Foreground = SeverityBrush(finding.Severity),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        });
        header.Children.Add(titles);
        body.Children.Add(header);
        body.Children.Add(Block("Explanation", finding.Explanation));
        body.Children.Add(Block("Why it matters", finding.WhyItMatters));
        body.Children.Add(Block("Proposed action", finding.SuggestedAction));

        if (finding.RecommendationType is ReviewRecommendationType.ImproveItem or ReviewRecommendationType.PriorityAdjustment)
        {
            var currents = RelatedItems(finding);
            if (currents.Count > 0)
                body.Children.Add(Block("Proposed change", ChecklistReviewMapper.DescribeChange(finding, currents)));
        }
        else if (finding.RecommendationType == ReviewRecommendationType.AddItem && finding.SuggestedItem is not null)
        {
            body.Children.Add(Block("Proposed check", DescribeAddition(finding.SuggestedItem)));
        }

        if (finding.RelatedItemIds.Count > 0)
            body.Children.Add(RelatedRow(finding));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };

        if (finding.Applied)
        {
            var remove = new Button
            {
                Content = "Remove",
                Classes = { "mini", "secondary" },
                IsEnabled = !_busy && canApply
            };
            remove.Click += async (_, _) => await RemoveOneAsync(finding);
            actions.Children.Add(remove);
        }
        else if (finding.CanApply)
        {
            var apply = new Button
            {
                Content = ApplyLabel(finding),
                Classes = { "mini", "primary" },
                IsEnabled = !_busy && canApply
            };
            apply.Click += async (_, _) => await ApplyOneAsync(finding);
            actions.Children.Add(apply);
        }

        var dismiss = new Button
        {
            Content = "Dismiss",
            Classes = { "mini", "secondary" },
            IsEnabled = !_busy
        };
        dismiss.Click += (_, _) =>
        {
            _session.Dismiss(finding);
            StatusText.Text = "Dismissed.";
            if (_session.Result is not null)
                Render(_session.Result);
        };
        actions.Children.Add(dismiss);
        body.Children.Add(actions);

        return new Border { Classes = { "finding" }, Child = body };
    }

    private StackPanel RelatedRow(ChecklistReviewFinding finding)
    {
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = "Related checks",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush("TextMutedBrush")
        });

        foreach (var id in finding.RelatedItemIds)
        {
            var item = _session.Find(id);
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            line.Children.Add(new TextBlock
            {
                Text = item?.Title ?? "That check is no longer on this checklist.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560
            });
            if (item is not null)
            {
                var view = new Button
                {
                    Content = "View",
                    Classes = { "mini", "secondary" },
                    Tag = id
                };
                view.Click += (_, _) =>
                {
                    if (!_session.Reveal(id))
                    {
                        StatusText.Text = "That check is no longer on this checklist.";
                        return;
                    }

                    ShowReadOnly(item);
                    StatusText.Text = "Showing \"" + item.Title + "\".";
                };
                line.Children.Add(view);
            }

            row.Children.Add(line);
        }

        return row;
    }

    private async Task ApplyOneAsync(ChecklistReviewFinding finding)
    {
        if (_busy)
            return;

        if (!_session.CanApply())
        {
            StatusText.Text = "Reopen the checklist before applying suggestions.";
            return;
        }

        if (!await ConfirmAsync(finding))
            return;

        var result = _session.Apply(finding);
        StatusText.Text = result.Message;
        if (result.ItemId is Guid itemId && result.Changed)
            _session.Reveal(itemId);
        if (_session.Result is not null)
            Render(_session.Result);
    }

    private async Task RemoveOneAsync(ChecklistReviewFinding finding)
    {
        if (_busy)
            return;

        if (!_session.CanApply())
        {
            StatusText.Text = "Reopen the checklist before changing suggestions.";
            return;
        }

        var dialog = new StyledDialog(
            "Remove this suggestion?",
            "This takes the applied change off the checklist.",
            "Remove",
            "Cancel",
            danger: true);
        if (!await dialog.ShowDialog<bool>(this))
            return;

        var result = _session.Revert(finding);
        StatusText.Text = result.Message;
        if (_session.Result is not null)
            Render(_session.Result);
    }

    private void ShowReadOnly(ChecklistItem item)
    {
        _view?.Close();
        var popup = new CheckDetailWindow();
        popup.Detail.ActionsVisible = false;
        popup.Detail.Show(item, canEdit: false);
        popup.Title = string.IsNullOrWhiteSpace(item.Title) ? "Check" : item.Title;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_view, popup))
                _view = null;
        };
        _view = popup;
        popup.Show(this);
    }

    private async Task<bool> ConfirmAsync(ChecklistReviewFinding finding)
    {
        if (finding.RecommendationType is not (ReviewRecommendationType.ImproveItem or ReviewRecommendationType.PriorityAdjustment))
            return true;

        var currents = RelatedItems(finding);
        if (currents.Count == 0)
            return true;

        var keepsTitle = finding.RecommendationType == ReviewRecommendationType.ImproveItem
            && string.IsNullOrWhiteSpace(finding.SuggestedItem?.Title);
        var many = currents.Count > 1;
        var title = finding.RecommendationType == ReviewRecommendationType.PriorityAdjustment
            ? many ? "Update these priorities?" : "Update priority?"
            : keepsTitle
                ? many ? "Update these checks?" : "Update this check?"
                : many ? "Replace these checks?" : "Replace this check?";
        var confirm = finding.RecommendationType == ReviewRecommendationType.PriorityAdjustment
            ? "Update priority"
            : keepsTitle
                ? "Update item"
                : "Replace item";
        var dialog = new StyledDialog(
            title,
            ChecklistReviewMapper.DescribeChange(finding, currents),
            confirm,
            "Cancel");
        dialog.Width = 560;
        dialog.Height = 460;
        return await dialog.ShowDialog<bool>(this);
    }

    private List<ChecklistItem> RelatedItems(ChecklistReviewFinding finding)
    {
        var items = new List<ChecklistItem>();
        foreach (var id in finding.RelatedItemIds)
        {
            var item = _session.Find(id);
            if (item is not null && !items.Contains(item))
                items.Add(item);
        }

        return items;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ReviewProgress.IsVisible = busy;
        CancelButton.IsVisible = busy;
        RerunButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        SelectButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText.Text = status;
        if (busy)
        {
            WarningText.IsVisible = false;
            return;
        }

        WarningText.Text = string.Join(" ", _warnings);
        WarningText.IsVisible = _warnings.Count > 0;
    }

    private static ChecklistReviewOutcome Cancelled() => new()
    {
        Failure = new AiFailure
        {
            Kind = AiFailureKind.Cancelled,
            Message = ChecklistReviewMessages.For(AiFailureKind.Cancelled)
        }
    };

    private static string DescribeAddition(SuggestedChecklistItem item)
    {
        var priority = ChecklistReviewMapper.TryPriority(item.Priority, out var parsed)
            ? parsed.ToString()
            : "Required";
        var section = string.IsNullOrWhiteSpace(item.Section) ? "(no section)" : item.Section.Trim();
        var notes = ChecklistReviewMapper.ComposeNotes(item.Description, item.AcceptanceCriteria);
        var type = string.IsNullOrWhiteSpace(item.Type) ? string.Empty : "\nType: " + item.Type.Trim();
        return item.Title.Trim() + "\nSection: " + section + "\nPriority: " + priority + type
            + "\nNotes: " + (string.IsNullOrWhiteSpace(notes) ? "No notes." : notes.Trim());
    }

    private static Control Block(string label, string text)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush("TextMutedBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppearanceService.Brush("TextBodyBrush")
        });
        return panel;
    }

    private static int SeverityRank(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => 0,
        ReviewSeverity.High => 1,
        ReviewSeverity.Medium => 2,
        _ => 3
    };

    private static IBrush SeverityBrush(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => AppearanceService.Brush("DangerTextBrush"),
        ReviewSeverity.High => AppearanceService.Brush("WarningTextBrush"),
        ReviewSeverity.Medium => AppearanceService.Brush("InfoTextBrush"),
        _ => AppearanceService.Brush("TextMutedBrush")
    };

    private static string SeverityLabel(ReviewSeverity severity) => severity.ToString();

    private static string CategoryLabel(ReviewCategory category) => category switch
    {
        ReviewCategory.Completeness => "Completeness",
        ReviewCategory.Duplicates => "Duplicates",
        ReviewCategory.Clarity => "Clarity",
        ReviewCategory.Testing => "Testing",
        ReviewCategory.Security => "Security",
        ReviewCategory.Performance => "Performance",
        _ => "Priority"
    };

    private static string RecommendationLabel(ReviewRecommendationType recommendation) => recommendation switch
    {
        ReviewRecommendationType.AddItem => "Add item",
        ReviewRecommendationType.ImproveItem => "Improve item",
        ReviewRecommendationType.PossibleDuplicate => "Possible duplicate",
        ReviewRecommendationType.PriorityAdjustment => "Priority adjustment",
        _ => "Observation"
    };

    private static string ApplyLabel(ChecklistReviewFinding finding)
    {
        if (finding.RecommendationType == ReviewRecommendationType.PriorityAdjustment)
            return "Update priority";

        if (finding.RecommendationType == ReviewRecommendationType.ImproveItem)
            return string.IsNullOrWhiteSpace(finding.SuggestedItem?.Title) ? "Update item" : "Replace item";

        return "Apply suggestion";
    }
}
