using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public sealed class TaskExplainSession
{
    public required Guid ItemId { get; init; }

    public required Func<TaskExplainRequest> Capture { get; init; }

    public required Func<TaskExplainRequest, CancellationToken, Task<TaskExplainOutcome>> Explain { get; init; }
}

public partial class TaskExplainWindow : Window
{
    private TaskExplainSession _session;
    private CancellationTokenSource? _run;
    private int _generation;
    private bool _busy;
    private bool _hasExplanation;

    public TaskExplainWindow(TaskExplainSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
        _ = RunAsync();
    }

    public void Explain(TaskExplainSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ItemId == _session.ItemId && (_busy || _hasExplanation))
            return;

        _session = session;
        _ = RunAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _generation++;
        _run?.Cancel();
        base.OnClosed(e);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        _run?.Cancel();

    private void Close_Click(object? sender, RoutedEventArgs e) =>
        Close();

    private async Task RunAsync()
    {
        _run?.Cancel();
        var generation = ++_generation;
        _hasExplanation = false;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        SetBusy(true, "Explaining this check…");
        ExplanationBody.Children.Clear();
        ExplanationCard.IsVisible = false;

        TaskExplainRequest request;
        try
        {
            request = _session.Capture();
        }
        catch (Exception)
        {
            if (generation != _generation)
                return;

            SetBusy(false, TaskExplainMessages.For(AiFailureKind.Unexpected));
            return;
        }

        ShowRequest(request);

        TaskExplainOutcome outcome;
        try
        {
            outcome = await _session.Explain(request, token);
        }
        catch (OperationCanceledException)
        {
            outcome = Cancelled();
        }
        catch (Exception)
        {
            outcome = new TaskExplainOutcome
            {
                Failure = new AiFailure
                {
                    Kind = AiFailureKind.Unexpected,
                    Message = TaskExplainMessages.For(AiFailureKind.Unexpected)
                }
            };
        }

        if (generation != _generation)
            return;

        if (token.IsCancellationRequested && outcome.Failure?.Kind != AiFailureKind.Cancelled)
            outcome = Cancelled();

        SetBusy(false, string.Empty);
        if (!outcome.Succeeded || outcome.Explanation is null)
        {
            StatusText.Text = outcome.Failure?.Message
                ?? TaskExplainMessages.For(AiFailureKind.InvalidChecklist);
            return;
        }

        ShowExplanation(outcome.Explanation, outcome.Warnings);
    }

    private void ShowRequest(TaskExplainRequest request)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled check" : request.Title.Trim();
        CheckTitle.Text = title;
        Title = title;
        var category = string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim();
        var priority = string.IsNullOrWhiteSpace(request.Priority) ? "Required" : request.Priority.Trim();
        var status = request.IsSkipped ? "Skipped" : request.IsCompleted ? "Completed" : "Open";
        ContextText.Text = category + " · " + priority + " · " + status;
    }

    private void ShowExplanation(TaskExplanation explanation, IReadOnlyList<string> warnings)
    {
        ExplanationBody.Children.Clear();
        AddBlock("What this check is asking", explanation.Summary);
        if (!string.IsNullOrWhiteSpace(explanation.WhyItMatters))
            AddBlock("Why it matters", explanation.WhyItMatters);
        if (explanation.Steps.Count > 0)
            AddSteps(explanation.Steps);
        if (!string.IsNullOrWhiteSpace(explanation.DoneWhen))
            AddBlock("Done when", explanation.DoneWhen);

        ExplanationCard.IsVisible = ExplanationBody.Children.Count > 0;
        _hasExplanation = ExplanationCard.IsVisible;
        WarningText.Text = string.Join(" ", warnings);
        WarningText.IsVisible = warnings.Count > 0;
        StatusText.Text = "Explanation ready.";
    }

    private void AddBlock(string label, string text)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Label(label));
        panel.Children.Add(Body(text));
        ExplanationBody.Children.Add(panel);
    }

    private void AddSteps(IReadOnlyList<string> steps)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Label("How to do it"));
        for (var index = 0; index < steps.Count; index++)
            panel.Children.Add(Body((index + 1) + ".  " + steps[index]));
        ExplanationBody.Children.Add(panel);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = AppearanceService.Font(12),
        FontWeight = FontWeight.SemiBold,
        Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush)
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = AppearanceService.Font(14),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 22,
        Foreground = AppearanceService.Brush(AppearanceService.TextBodyBrush)
    };

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ExplainProgress.IsVisible = busy;
        CancelButton.IsVisible = busy;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText.Text = status;
        if (busy)
            WarningText.IsVisible = false;
    }

    private static TaskExplainOutcome Cancelled() => new()
    {
        Failure = new AiFailure
        {
            Kind = AiFailureKind.Cancelled,
            Message = TaskExplainMessages.For(AiFailureKind.Cancelled)
        }
    };
}
