using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public partial class GenerateChecklistWindow : Window
{
    private readonly ILocalAIService _ai;
    private readonly AIModelManager _models;
    private readonly ChecklistValidator _validator = new();
    private readonly List<SectionEditor> _sections = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _generation;
    private bool _busy;
    private bool _checking;
    private bool _hasPreview;
    private ModelStatus? _status;

    public GenerateChecklistWindow(ILocalAIService ai, AIModelManager models)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        InitializeComponent();

        PlatformBox.ItemsSource = AiCatalog.Platforms;
        PlatformBox.SelectedIndex = 0;
        WorkBox.ItemsSource = AiCatalog.WorkKinds;
        WorkBox.SelectedIndex = 0;
        ApplyFrameworks();
        PlatformBox.SelectionChanged += (_, _) => ApplyFrameworks();
        StatusModel.Text = "Model: " + _models.ModelName;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = RefreshStatusAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _generation?.Cancel();
        _lifetime.Cancel();
        base.OnClosed(e);
    }

    private void ApplyFrameworks()
    {
        if (PlatformBox.SelectedItem is not AiChoice<DevPlatform> platform)
            return;

        var options = AiCatalog.FrameworksFor(platform.Value);
        var current = FrameworkBox.SelectedItem as AiChoice<DevFramework>;
        FrameworkBox.ItemsSource = options;
        FrameworkBox.SelectedItem = options.FirstOrDefault(option => current is not null && option.Value == current.Value)
            ?? options[0];
    }

    private async void CheckAgain_Click(object? sender, RoutedEventArgs e) =>
        await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_busy || _checking)
            return;

        _checking = true;
        ApplyBusy();
        StatusConnection.Text = "Checking Ollama…";
        try
        {
            _status = await _models.DetectAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested)
                ShowStatus(_status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _status = null;
            StatusConnection.Text = "Ollama disconnected";
            StatusModelState.Text = "Not checked";
            StatusGuidance.Text = AiMessages.For(AiFailureKind.Unexpected, _models.ModelName);
            StatusDot.Fill = AppearanceService.Brush("DangerTextBrush");
        }
        finally
        {
            _checking = false;
            if (!_lifetime.IsCancellationRequested)
                ApplyBusy();
        }
    }

    private void ShowStatus(ModelStatus status)
    {
        StatusConnection.Text = status.ConnectionText;
        StatusModel.Text = "Model: " + status.ModelName;
        StatusModelState.Text = status.ModelStateText;
        StatusGuidance.Text = status.Guidance;
        StatusDot.Fill = status.Status switch
        {
            OllamaStatus.Ready => AppearanceService.Brush("InfoTextBrush"),
            OllamaStatus.ModelMissing => AppearanceService.Brush("WarningTextBrush"),
            _ => AppearanceService.Brush("DangerTextBrush")
        };
    }

    private async void Generate_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _status is not { CanGenerate: true })
            return;

        if (_hasPreview)
        {
            var confirm = new StyledDialog(
                "Regenerate checklist?",
                "This replaces the current preview, including your edits.",
                "Regenerate",
                "Cancel");
            if (await confirm.ShowDialog<bool>(this) != true)
                return;
        }

        await RunGenerationAsync("Generating a checklist…", async token =>
        {
            var result = await _ai.GenerateChecklistAsync(BuildRequest(), token);
            if (result.Failure is not null)
            {
                ShowGenerationMessage(result.Failure.Message);
                return;
            }

            if (result.Checklist is null)
            {
                ShowGenerationMessage(AiMessages.For(AiFailureKind.EmptyResponse, _models.ModelName));
                return;
            }

            var request = BuildRequest();
            result.Checklist.Summary = ChecklistSummary.ForChecklist(
                result.Checklist.Summary,
                request.Description,
                result.Checklist);
            ShowPreview(result.Checklist, result.Notice, result.NoticeIsAdjustment);
            ShowGenerationMessage("Review the checklist, then create it.");
        });
    }

    private async void CancelGeneration_Click(object? sender, RoutedEventArgs e)
    {
        _generation?.Cancel();
        ShowGenerationMessage("Cancelling…");
        await Task.CompletedTask;
    }

    private async void RegenerateSection_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: SectionEditor section })
            return;

        if (_status is not { CanGenerate: true })
        {
            ShowGenerationMessage(_status?.Guidance ?? AiMessages.For(AiFailureKind.NotRunning, _models.ModelName));
            return;
        }

        var name = section.Name.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowGenerationMessage("Name the section before regenerating it.");
            return;
        }

        var existing = ReadFromUi().Sections.SelectMany(item => item.Items).Select(item => item.Title).ToList();
        await RunGenerationAsync("Regenerating " + name + "…", async token =>
        {
            var result = await _ai.GenerateSectionAsync(BuildRequest(), name, existing, token);
            if (result.Failure is not null || result.Section is null)
            {
                ShowGenerationMessage(result.Failure?.Message
                    ?? AiMessages.For(AiFailureKind.InvalidChecklist, _models.ModelName));
                return;
            }

            if (_sections.Contains(section))
            {
                section.Name.Text = string.IsNullOrWhiteSpace(result.Section.Name) ? name : result.Section.Name;
                section.Items.Clear();
                section.ItemPanel.Children.Clear();
                foreach (var item in result.Section.Items)
                    AddItem(section, item);
                ShowNotice(result.Notice, result.NoticeIsAdjustment);
            }

            ShowGenerationMessage("Updated the " + name + " section.");
        });
    }

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || !_hasPreview)
            return;

        var checklist = ReadFromUi();
        if (string.IsNullOrWhiteSpace(checklist.Title))
        {
            ShowGenerationMessage("Enter a title before creating the checklist.");
            return;
        }

        var inspection = _validator.Inspect(checklist);
        if (!inspection.IsValid || inspection.Checklist is null || inspection.Checklist.Sections.Count == 0)
        {
            ShowGenerationMessage(inspection.Failure?.Message
                ?? "Add at least one checklist item before creating it.");
            return;
        }

        if (PlatformBox.SelectedItem is not AiChoice<DevPlatform> _
            || FrameworkBox.SelectedItem is not AiChoice<DevFramework> _
            || WorkBox.SelectedItem is not AiChoice<AiWorkKind> work)
        {
            ShowGenerationMessage("Choose a platform, framework, and work type.");
            return;
        }

        var request = BuildRequest();
        var items = AiChecklistMapper.ToItems(inspection.Checklist);
        if (items.Count == 0)
        {
            ShowGenerationMessage("Add at least one checklist item before creating it.");
            return;
        }

        var task = request.Description?.Trim();
        var summary = ChecklistSummary.ForChecklist(
            inspection.Checklist.Summary,
            task,
            inspection.Checklist);
        Close(new AiChecklistDraft
        {
            Title = inspection.Checklist.Title.Trim(),
            WorkType = AiChecklistMapper.ToWorkType(work.Value),
            TemplateName = AiChecklistMapper.TemplateName(request),
            Description = string.IsNullOrEmpty(summary) ? null : summary,
            Items = items,
            TaskDescription = string.IsNullOrEmpty(task) ? null : task,
            AiPlatform = AiCatalog.PlatformLabel(request.Platform),
            AiFramework = AiCatalog.FrameworkLabel(request.Framework)
        });
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        _generation?.Cancel();
        Close(null);
    }

    private void AddSection_Click(object? sender, RoutedEventArgs e)
    {
        if (!_hasPreview)
            return;

        AddSection(new GeneratedSection
        {
            Name = "New section",
            Items = [new GeneratedCheckItem { Type = "Required", Priority = "Medium" }]
        }, focus: true);
    }

    private async Task RunGenerationAsync(string status, Func<CancellationToken, Task> action)
    {
        _generation?.Cancel();
        _generation = new CancellationTokenSource();
        var token = _generation.Token;
        _busy = true;
        ApplyBusy();
        ShowGenerationMessage(status);
        try
        {
            await action(token);
        }
        catch (OperationCanceledException)
        {
            ShowGenerationMessage(AiMessages.For(AiFailureKind.Cancelled, _models.ModelName));
        }
        catch (Exception)
        {
            ShowGenerationMessage(AiMessages.For(AiFailureKind.Unexpected, _models.ModelName));
        }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
                ApplyBusy();
        }
    }

    private void ShowPreview(GeneratedChecklist checklist, string notice, bool adjustment)
    {
        _hasPreview = true;
        PreviewPanel.IsVisible = true;
        EmptyPreview.IsVisible = false;
        TitleBox.Text = checklist.Title;
        SummaryBox.Text = checklist.Summary;
        PreviewSections.Children.Clear();
        _sections.Clear();
        foreach (var section in checklist.Sections)
            AddSection(section, focus: false);
        ShowNotice(notice, adjustment);
        GenerateButton.Content = "Regenerate";
        ApplyBusy();
    }

    private void ShowNotice(string notice, bool adjustment)
    {
        PreviewNotice.IsVisible = !string.IsNullOrWhiteSpace(notice);
        PreviewNotice.Text = notice;
        PreviewNotice.Foreground = AppearanceService.Brush(adjustment ? "WarningTextBrush" : "TextMutedBrush");
    }

    private void ShowGenerationMessage(string message)
    {
        GenerationStatus.IsVisible = !string.IsNullOrWhiteSpace(message);
        GenerationStatus.Text = message;
    }

    private void ApplyBusy()
    {
        var canGenerate = !_busy && !_checking && _status is { CanGenerate: true };
        GenerateButton.IsEnabled = canGenerate;
        CreateButton.IsEnabled = !_busy && _hasPreview;
        CheckAgainButton.IsEnabled = !_busy && !_checking;
        CancelButton.IsVisible = _busy;
        GenerationProgress.IsVisible = _busy;
        PlatformBox.IsEnabled = !_busy;
        FrameworkBox.IsEnabled = !_busy;
        WorkBox.IsEnabled = !_busy;
        DescriptionBox.IsEnabled = !_busy;
        TitleBox.IsEnabled = !_busy;
        SummaryBox.IsEnabled = !_busy;
    }

    private AiChecklistRequest BuildRequest() => new()
    {
        Platform = PlatformBox.SelectedItem is AiChoice<DevPlatform> platform ? platform.Value : DevPlatform.CrossPlatform,
        Framework = FrameworkBox.SelectedItem is AiChoice<DevFramework> framework ? framework.Value : DevFramework.Custom,
        WorkKind = WorkBox.SelectedItem is AiChoice<AiWorkKind> work ? work.Value : AiWorkKind.Development,
        Description = DescriptionBox.Text
    };

    private GeneratedChecklist ReadFromUi()
    {
        var checklist = new GeneratedChecklist
        {
            Title = TitleBox.Text?.Trim() ?? string.Empty,
            Summary = SummaryBox.Text?.Trim() ?? string.Empty
        };

        foreach (var section in _sections)
        {
            var generated = new GeneratedSection
            {
                Name = section.Name.Text?.Trim() ?? string.Empty
            };
            foreach (var item in section.Items)
            {
                generated.Items.Add(new GeneratedCheckItem
                {
                    Title = item.Title.Text?.Trim() ?? string.Empty,
                    Description = item.Description.Text?.Trim() ?? string.Empty,
                    Type = item.Type.SelectedItem as string ?? ChecklistVocabulary.DefaultType,
                    Priority = item.Priority.SelectedItem as string ?? ChecklistVocabulary.DefaultPriority,
                    AcceptanceCriteria = item.Acceptance.Text?.Trim() ?? string.Empty,
                    TestReports = item.TestReports.Text?.Trim() ?? string.Empty,
                    Logs = item.Logs.Text?.Trim() ?? string.Empty,
                    CodeReferences = item.CodeReferences.Text?.Trim() ?? string.Empty,
                    ApiResponses = item.ApiResponses.Text?.Trim() ?? string.Empty,
                    PullRequestLinks = item.PullRequestLinks.Text?.Trim() ?? string.Empty
                });
            }

            checklist.Sections.Add(generated);
        }

        return checklist;
    }

    private void AddSection(GeneratedSection section, bool focus)
    {
        var name = new TextBox
        {
            Classes = { "section-name" },
            Text = section.Name,
            PlaceholderText = "Section"
        };
        var items = new StackPanel { Spacing = 12 };
        var editor = new SectionEditor(name, items);
        var regenerate = new Button
        {
            Classes = { "form-button", "secondary" },
            Content = "Regenerate section",
            Tag = editor
        };
        regenerate.Click += RegenerateSection_Click;
        var add = new Button
        {
            Classes = { "form-button", "secondary" },
            Content = "Add item"
        };
        add.Click += (_, _) => AddItem(editor, new GeneratedCheckItem(), focus: true);
        var remove = new Button
        {
            Classes = { "form-button", "cancel" },
            Content = "Remove section"
        };
        remove.Click += (_, _) => RemoveSection(editor);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { regenerate, add, remove }
        };
        var header = new StackPanel
        {
            Spacing = 10,
            Children = { name, actions }
        };

        var card = new Border
        {
            Classes = { "section-card" },
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { header, FieldLabels(), items }
            }
        };
        editor.Card = card;
        _sections.Add(editor);
        PreviewSections.Children.Add(card);
        foreach (var item in section.Items)
            AddItem(editor, item);

        if (focus)
            name.Focus();
    }

    private void AddItem(SectionEditor section, GeneratedCheckItem item, bool focus = false)
    {
        var title = Field(item.Title, "Item title");
        var description = Multiline(item.Description, "What to check or change");
        var acceptance = Multiline(item.AcceptanceCriteria, "How you know this item is done");
        var type = Choice(item.Type, ChecklistVocabulary.Types);
        var priority = Choice(item.Priority, ChecklistVocabulary.Priorities);
        priority.SelectionChanged += (_, _) => ApplyPriorityColor(priority);
        ApplyPriorityColor(priority);

        var remove = new Button
        {
            Classes = { "form-button", "cancel" },
            Content = "Remove",
            MinWidth = 72
        };
        var testReports = Multiline(item.TestReports, "Report, path, or link");
        var logs = Multiline(item.Logs, "Log excerpt or link");
        var codeReferences = Multiline(item.CodeReferences, "File, symbol, or link");
        var apiResponses = Multiline(item.ApiResponses, "Response excerpt or link");
        var pullRequests = Multiline(item.PullRequestLinks, "Pull request link");
        var editor = new ItemEditor(
            title,
            description,
            type,
            priority,
            acceptance,
            testReports,
            logs,
            codeReferences,
            apiResponses,
            pullRequests);
        remove.Click += (_, _) =>
        {
            section.Items.Remove(editor);
            section.ItemPanel.Children.Remove(editor.Card);
        };

        title.VerticalAlignment = VerticalAlignment.Center;
        type.VerticalAlignment = VerticalAlignment.Center;
        priority.VerticalAlignment = VerticalAlignment.Center;
        remove.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,148,148,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(type, 1);
        Grid.SetColumn(priority, 2);
        Grid.SetColumn(remove, 3);
        row.Children.Add(title);
        row.Children.Add(type);
        row.Children.Add(priority);
        row.Children.Add(remove);

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                row,
                Labeled("Description", description),
                Labeled("Acceptance criteria", acceptance)
            }
        };
        AddFilled(body, "Test reports", testReports);
        AddFilled(body, "Logs", logs);
        AddFilled(body, "Code references", codeReferences);
        AddFilled(body, "API responses", apiResponses);
        AddFilled(body, "PR links", pullRequests);
        var card = new Border
        {
            Classes = { "item-card" },
            Child = body
        };
        editor.Card = card;
        section.Items.Add(editor);
        section.ItemPanel.Children.Add(card);
        if (focus)
            title.Focus();
    }

    private void RemoveSection(SectionEditor section)
    {
        _sections.Remove(section);
        PreviewSections.Children.Remove(section.Card);
    }

    private static void ApplyPriorityColor(ComboBox priority)
    {
        var key = (priority.SelectedItem as string) switch
        {
            "Critical" => "DangerTextBrush",
            "Conditional" => "InfoTextBrush",
            "Review" => "ReviewTextBrush",
            _ => "WarningTextBrush"
        };
        priority.Foreground = AppearanceService.Brush(key);
    }

    private static Grid FieldLabels()
    {
        var item = Caption("Item");
        var type = Caption("Type");
        var priority = Caption("Priority");
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,148,148,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(14, 0, 14, 0)
        };
        Grid.SetColumn(type, 1);
        Grid.SetColumn(priority, 2);
        grid.Children.Add(item);
        grid.Children.Add(type);
        grid.Children.Add(priority);
        return grid;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Classes = { "label" }
    };

    private static TextBox Field(string text, string placeholder) => new()
    {
        Classes = { "field" },
        Text = text,
        PlaceholderText = placeholder
    };

    private static TextBox Multiline(string text, string placeholder) => new()
    {
        Classes = { "field" },
        Text = text,
        PlaceholderText = placeholder,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64
    };

    private static ComboBox Choice(string selected, IReadOnlyList<string> values)
    {
        var box = new ComboBox { Classes = { "field" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in values)
            box.Items.Add(value);
        box.SelectedItem = values.Contains(selected) ? selected : values[0];
        return box;
    }

    private static void AddFilled(StackPanel body, string label, TextBox field)
    {
        if (!string.IsNullOrWhiteSpace(field.Text))
            body.Children.Add(Labeled(label, field));
    }

    private static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, Classes = { "label" } },
            field
        }
    };

    private sealed class SectionEditor(TextBox name, StackPanel itemPanel)
    {
        public TextBox Name { get; } = name;

        public StackPanel ItemPanel { get; } = itemPanel;

        public List<ItemEditor> Items { get; } = [];

        public Border Card { get; set; } = new();
    }

    private sealed class ItemEditor(
        TextBox title,
        TextBox description,
        ComboBox type,
        ComboBox priority,
        TextBox acceptance,
        TextBox testReports,
        TextBox logs,
        TextBox codeReferences,
        TextBox apiResponses,
        TextBox pullRequestLinks)
    {
        public TextBox Title { get; } = title;

        public TextBox Description { get; } = description;

        public ComboBox Type { get; } = type;

        public ComboBox Priority { get; } = priority;

        public TextBox Acceptance { get; } = acceptance;

        public TextBox TestReports { get; } = testReports;

        public TextBox Logs { get; } = logs;

        public TextBox CodeReferences { get; } = codeReferences;

        public TextBox ApiResponses { get; } = apiResponses;

        public TextBox PullRequestLinks { get; } = pullRequestLinks;

        public Border Card { get; set; } = new();
    }
}
