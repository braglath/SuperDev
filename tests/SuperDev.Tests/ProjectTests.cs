using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Tests;

public class ProjectTests
{
    [Fact]
    public void Create_and_edit_keep_a_stable_id()
    {
        var library = Library();
        var created = library.Create(Draft("Manpower"));
        var edited = library.Update(created.Id, Draft("Manpower App", "Field crews", "iOS", "Swift", "Rocket", "#14B8A6"));

        Assert.NotNull(edited);
        Assert.Equal(created.Id, edited.Id);
        Assert.Equal(created.CreatedAt, edited.CreatedAt);
        Assert.Equal("Manpower App", edited.Name);
        Assert.Equal("Field crews", edited.Description);
        Assert.Equal("iOS", edited.Platform);
        Assert.Equal("Swift", edited.Framework);
        Assert.Equal("Rocket", edited.Icon);
        Assert.Equal("#14B8A6", edited.Color);
        Assert.Equal(ProjectStatus.Active, edited.Status);
        Assert.True(edited.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public void Archive_changes_status_without_removing_the_project()
    {
        var library = Library();
        var project = library.Create(Draft("Super Dev"));

        library.SetStatus(project.Id, ProjectStatus.Archived);

        var stored = library.Find(project.Id);
        Assert.NotNull(stored);
        Assert.Equal(ProjectStatus.Archived, stored.Status);
        Assert.Single(library.Projects);
    }

    [Fact]
    public async Task Associating_moving_and_removing_a_tasklist_updates_the_file_and_keeps_it()
    {
        var directory = TempDirectory();
        var library = Library(directory);
        var first = library.Create(Draft("Manpower"));
        var second = library.Create(Draft("Super Dev"));
        var path = Path.Combine(directory, "login.superdev.json");
        var files = new ChecklistFileService();
        var document = Sample("Login Feature");

        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        library.Note(document, path);
        Assert.Null(library.FindTasklist(document.Id)!.ProjectId);

        document.ProjectId = first.Id;
        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        library.Note(document, path);
        Assert.Equal(first.Id, library.FindTasklist(document.Id)!.ProjectId);
        Assert.Equal(document.Id, Assert.Single(library.ForProject(first.Id)).DocumentId);

        document.ProjectId = second.Id;
        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        library.Note(document, path);
        Assert.Empty(library.ForProject(first.Id));
        Assert.Equal(second.Id, library.FindTasklist(document.Id)!.ProjectId);

        document.ProjectId = null;
        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        library.Note(document, path);

        var loaded = await files.LoadAsync(path);
        Assert.Null(loaded.ProjectId);
        Assert.Equal("Login Feature", loaded.Title);
        Assert.Equal(2, loaded.Items.Count);
        Assert.True(File.Exists(path));
        Assert.Null(library.FindTasklist(document.Id)!.ProjectId);
    }

    [Fact]
    public async Task Deleting_a_project_keeps_the_tasklist_file_and_releases_it()
    {
        var directory = TempDirectory();
        var library = Library(directory);
        var project = library.Create(Draft("Manpower"));
        var path = Path.Combine(directory, "email.superdev.json");
        var files = new ChecklistFileService();
        var document = Sample("Email Validation");
        document.ProjectId = project.Id;
        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        library.Note(document, path);

        var released = library.Delete(project.Id);

        Assert.Null(library.Find(project.Id));
        Assert.Empty(library.Projects);
        var record = Assert.Single(released);
        Assert.Null(record.ProjectId);
        Assert.Equal(document.Id, record.DocumentId);
        Assert.True(File.Exists(path));

        var stillThere = await files.LoadAsync(path);
        Assert.Equal(project.Id, stillThere.ProjectId);
        Assert.Equal("Email Validation", stillThere.Title);
        Assert.Equal(document.Items.Count, stillThere.Items.Count);

        stillThere.ProjectId = null;
        await files.SaveAsync(path, stillThere, replaceDifferentDocument: false);
        library.Note(stillThere, path);
        var loaded = await files.LoadAsync(path);
        Assert.Null(loaded.ProjectId);
        Assert.Equal("Email Validation", loaded.Title);
        Assert.Contains(library.Tasklists, item => item.DocumentId == document.Id && item.ProjectId is null);
    }

    [Fact]
    public async Task A_checklist_without_a_project_omits_ProjectId_and_reloads()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "plain.superdev.json");
        var files = new ChecklistFileService();
        var document = Sample("Standalone");

        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("ProjectId", json, StringComparison.Ordinal);

        var loaded = await files.LoadAsync(path);
        Assert.Null(loaded.ProjectId);
        Assert.Equal(document.Id, loaded.Id);
    }

    [Fact]
    public async Task A_project_id_round_trips_on_the_checklist_file()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "linked.superdev.json");
        var files = new ChecklistFileService();
        var projectId = Guid.NewGuid();
        var document = Sample("Linked");
        document.ProjectId = projectId;

        await files.SaveAsync(path, document, replaceDifferentDocument: false);
        var loaded = await files.LoadAsync(path);

        Assert.Equal(projectId, loaded.ProjectId);
        Assert.Equal("Linked", loaded.Title);
    }

    [Fact]
    public void Projects_and_tasklists_survive_a_new_library_on_the_same_file()
    {
        var directory = TempDirectory();
        var store = Path.Combine(directory, "projects.json");
        var first = new ProjectLibrary(store);
        var project = first.Create(Draft("Super Dev", framework: "Avalonia"));
        var document = Sample("AI Review");
        document.ProjectId = project.Id;
        first.Note(document, Path.Combine(directory, "review.superdev.json"));

        var second = new ProjectLibrary(store);
        var stored = second.Find(project.Id);
        Assert.NotNull(stored);
        Assert.Equal("Super Dev", stored.Name);
        Assert.Equal("Avalonia", stored.Framework);
        Assert.Equal(project.Id, second.FindTasklist(document.Id)!.ProjectId);
    }

    [Fact]
    public void Progress_sums_checks_and_does_not_average_tasklist_percentages()
    {
        var halfway = TasklistProgressInput.FromItems(false,
        [
            Check("Done", completed: true),
            Check("Open", completed: false)
        ]);
        var mostly = TasklistProgressInput.FromItems(false,
        [
            Check("A", completed: true),
            Check("B", completed: true),
            Check("C", completed: true),
            Check("D", completed: true),
            Check("E", completed: true),
            Check("F", completed: true),
            Check("G", completed: true),
            Check("H", completed: true),
            Check("I", completed: true),
            Check("J", completed: false)
        ]);

        var report = ProjectProgress.Measure([halfway, mostly]);

        Assert.Equal(10, report.ChecksCompleted);
        Assert.Equal(12, report.ChecksTotal);
        Assert.Equal(10d / 12d * 100d, report.Percent);
        Assert.NotEqual(70d, report.Percent);
        Assert.Equal(2, report.TasklistCount);
        Assert.Equal(2, report.InProgress);
        Assert.Equal(0, report.Completed);
        Assert.Equal(0, report.NotStarted);
    }

    [Fact]
    public void Progress_follows_skipped_completed_and_open_checks()
    {
        var items = new List<ChecklistItem>
        {
            Check("Done", completed: true),
            Check("Set aside", completed: false, skipped: true),
            Check("Must ship", completed: false, priority: ChecklistPriority.Critical),
            Check("Maybe", completed: false, priority: ChecklistPriority.Conditional),
            new ChecklistItem
            {
                Title = "Parent",
                Priority = ChecklistPriority.Required,
                IsCompleted = true,
                Subtasks =
                [
                    new ChecklistSubtask
                    {
                        Title = "Optional link",
                        IsCompleted = false,
                        LinkOptional = true,
                        LinkedDocumentId = Guid.NewGuid()
                    }
                ]
            }
        };

        var report = ProjectProgress.Measure([TasklistProgressInput.FromItems(false, items)]);

        Assert.Equal(2, report.ChecksCompleted);
        Assert.Equal(4, report.ChecksTotal);
        Assert.Equal(1, report.ChecksSkipped);
        Assert.Equal(TasklistRunState.InProgress, ProjectProgress.StateOf(TasklistProgressInput.FromItems(false, items)));
        Assert.True(items[2].BlocksWorkspaceCompletion);
        Assert.False(items[3].BlocksWorkspaceCompletion);
    }

    [Fact]
    public void Empty_projects_and_empty_tasklists_report_zero_progress()
    {
        Assert.Equal(ProjectProgressReport.Empty, ProjectProgress.Measure([]));

        var empty = ProjectProgress.Measure([TasklistProgressInput.FromItems(false, [])]);
        Assert.Equal(0, empty.Percent);
        Assert.Equal(0, empty.ChecksTotal);
        Assert.Equal(1, empty.NotStarted);
        Assert.Equal(0, empty.InProgress);
        Assert.Equal(0, empty.Completed);

        var skippedOnly = ProjectProgress.Measure(
        [
            TasklistProgressInput.FromItems(false, [Check("Aside", completed: false, skipped: true)])
        ]);
        Assert.Equal(0, skippedOnly.Percent);
        Assert.Equal(1, skippedOnly.NotStarted);
    }

    [Fact]
    public void A_finished_checklist_counts_as_completed_even_when_the_flag_is_the_only_signal()
    {
        var flagged = new TasklistProgressInput(true, 0, 2, 0);
        var allChecks = new TasklistProgressInput(false, 2, 2, 0);
        var report = ProjectProgress.Measure([flagged, allChecks]);

        Assert.Equal(2, report.Completed);
        Assert.Equal(0, report.InProgress);
        Assert.Equal(2, report.ChecksCompleted);
        Assert.Equal(4, report.ChecksTotal);
    }

    [Fact]
    public void Opening_an_open_tasklist_keeps_the_existing_tab()
    {
        var documentId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(documentId, projectId, false)
        };

        Assert.False(TabSession.ShouldOpenNew(open, documentId));
        Assert.Equal(projectId, TabSession.ParentFor(alreadyOpen: true, projectId, requestedParent: null));
        Assert.True(TabSession.ShouldOpenNew(open, Guid.NewGuid()));
    }

    [Fact]
    public void A_standalone_open_is_not_attached_to_a_project()
    {
        Assert.Null(TabSession.ParentFor(alreadyOpen: false, existingParent: null, requestedParent: null));
    }

    [Fact]
    public void Closing_one_child_leaves_the_project_and_its_other_children()
    {
        var project = Guid.NewGuid();
        var login = Guid.NewGuid();
        var email = Guid.NewGuid();
        var forgot = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(login, project, false),
            new(email, project, false),
            new(forgot, project, false)
        };

        var focus = TabSession.AfterChecklistClosed(
            open,
            [project],
            new TabFocus(email, null),
            email);

        Assert.Equal(forgot, focus.DocumentId);
        Assert.Null(focus.ProjectId);
        Assert.Equal([login, forgot], TabSession.Children(
            open.Where(tab => tab.DocumentId != email).ToList(),
            project));
    }

    [Fact]
    public void Closing_the_last_child_activates_the_project_tab()
    {
        var project = Guid.NewGuid();
        var login = Guid.NewGuid();
        var open = new List<OpenChecklistTab> { new(login, project, false) };

        var focus = TabSession.AfterChecklistClosed(
            open,
            [project],
            new TabFocus(login, null),
            login);

        Assert.Equal(project, focus.ProjectId);
        Assert.Null(focus.DocumentId);
    }

    [Fact]
    public void Closing_a_project_closes_its_children_and_leaves_the_other_project()
    {
        var manpower = Guid.NewGuid();
        var superDev = Guid.NewGuid();
        var login = Guid.NewGuid();
        var email = Guid.NewGuid();
        var review = Guid.NewGuid();
        var engine = Guid.NewGuid();
        var standalone = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(login, manpower, false),
            new(email, manpower, false),
            new(review, superDev, false),
            new(engine, superDev, false),
            new(standalone, null, false)
        };

        var remaining = TabSession.ExceptProject(open, manpower);
        Assert.DoesNotContain(remaining, tab => tab.DocumentId == login || tab.DocumentId == email);
        Assert.Contains(remaining, tab => tab.DocumentId == review && tab.OpenedFromProjectId == superDev);
        Assert.Contains(remaining, tab => tab.DocumentId == engine && tab.OpenedFromProjectId == superDev);
        Assert.Contains(remaining, tab => tab.DocumentId == standalone && tab.OpenedFromProjectId is null);

        var focus = TabSession.AfterProjectClosed(
            open,
            [manpower, superDev],
            new TabFocus(null, manpower),
            manpower);

        Assert.Equal(superDev, focus.ProjectId);
        Assert.Null(focus.DocumentId);
    }

    [Fact]
    public void Closing_the_only_project_activates_a_remaining_tasklist_or_home()
    {
        var project = Guid.NewGuid();
        var child = Guid.NewGuid();
        var standalone = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(child, project, false),
            new(standalone, null, false)
        };

        var focus = TabSession.AfterProjectClosed(
            open,
            [project],
            new TabFocus(child, null),
            project);

        Assert.Equal(standalone, focus.DocumentId);

        var home = TabSession.AfterProjectClosed(
            [new OpenChecklistTab(child, project, false)],
            [project],
            new TabFocus(null, project),
            project);

        Assert.True(home.IsHome);
    }

    [Fact]
    public void Reopening_a_closed_tasklist_is_a_new_tab_and_a_duplicate_is_not()
    {
        var documentId = Guid.NewGuid();
        var project = Guid.NewGuid();
        var open = new List<OpenChecklistTab> { new(documentId, project, false) };

        Assert.False(TabSession.ShouldOpenNew(open, documentId));

        open.RemoveAll(tab => tab.DocumentId == documentId);
        Assert.True(TabSession.ShouldOpenNew(open, documentId));
        Assert.Equal(project, TabSession.ParentFor(alreadyOpen: false, existingParent: null, requestedParent: project));
    }

    [Fact]
    public void Deleting_a_project_releases_open_children_instead_of_orphaning_them()
    {
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();
        var child = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(child, project, false),
            new(kept, other, false)
        };

        var released = TabSession.ReleaseProject(open, project);

        Assert.Null(released[0].OpenedFromProjectId);
        Assert.Equal(child, released[0].DocumentId);
        Assert.Equal(other, released[1].OpenedFromProjectId);
        Assert.Equal(kept, released[1].DocumentId);
    }

    [Fact]
    public void Closing_a_background_tab_does_not_change_the_active_tab()
    {
        var project = Guid.NewGuid();
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(active, project, false),
            new(other, project, false)
        };

        var focus = TabSession.AfterChecklistClosed(
            open,
            [project],
            new TabFocus(active, null),
            other);

        Assert.Equal(active, focus.DocumentId);
    }

    [Fact]
    public void A_standalone_close_still_activates_the_neighboring_tab()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var open = new List<OpenChecklistTab>
        {
            new(first, null, true),
            new(second, null, false),
            new(third, null, false)
        };

        var focus = TabSession.AfterChecklistClosed(
            open,
            [],
            new TabFocus(second, null),
            second);

        Assert.Equal(third, focus.DocumentId);

        var last = TabSession.AfterChecklistClosed(
            open,
            [],
            new TabFocus(third, null),
            third);

        Assert.Equal(second, last.DocumentId);
    }

    [Fact]
    public void Unsaved_child_closes_are_prompted_before_anything_is_closed()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var saved = Guid.NewGuid();
        var plan = ProjectClose.Plan(
        [
            new TabCloseRequest(first, HasPath: false, IsDirty: true),
            new TabCloseRequest(second, HasPath: true, IsDirty: true),
            new TabCloseRequest(saved, HasPath: true, IsDirty: false)
        ]);

        Assert.Equal(TabCloseStepKind.Prompt, plan[0].Kind);
        Assert.Equal(first, plan[0].DocumentId);
        Assert.Equal(TabCloseStepKind.SaveExisting, plan[1].Kind);
        Assert.Equal(second, plan[1].DocumentId);
        Assert.Equal(
            [first, second, saved],
            plan.Where(step => step.Kind == TabCloseStepKind.Close).Select(step => step.DocumentId).ToArray());
        Assert.All(
            plan.TakeWhile(step => step.Kind != TabCloseStepKind.Close),
            step => Assert.NotEqual(TabCloseStepKind.Close, step.Kind));
        Assert.DoesNotContain(plan, step => step.Kind == TabCloseStepKind.Close && plan.IndexOf(step) < 2);
    }

    [Fact]
    public void Tasklist_order_appends_new_rows_and_keeps_a_dropped_order()
    {
        var directory = TempDirectory();
        var library = Library(directory);
        var project = library.Create(Draft("Ship"));
        var cedar = Sample("Cedar");
        var alpha = Sample("Alpha");
        var beta = Sample("Beta");
        cedar.ProjectId = project.Id;
        alpha.ProjectId = project.Id;
        beta.ProjectId = project.Id;
        library.Note(cedar, Path.Combine(directory, "c.json"));
        library.Note(alpha, Path.Combine(directory, "a.json"));
        library.Note(beta, Path.Combine(directory, "b.json"));

        Assert.Equal(
            [cedar.Id, alpha.Id, beta.Id],
            library.ForProject(project.Id).Select(record => record.DocumentId));

        library.Reorder(project.Id, [beta.Id, cedar.Id, alpha.Id]);
        library.Note(beta, Path.Combine(directory, "b.json"));

        var stored = Library(directory).ForProject(project.Id).Select(record => record.DocumentId);
        Assert.Equal([beta.Id, cedar.Id, alpha.Id], stored);
    }

    [Fact]
    public void Deadline_and_reminder_survive_reload_and_a_rename()
    {
        var directory = TempDirectory();
        var library = Library(directory);
        var project = library.Create(Draft("Ship"));
        project.Deadline = new DateOnly(2026, 12, 1);
        project.DeadlineMinute = 15 * 60;
        project.NotifyBeforeMinutes = 30;
        project.DeadlineSilent = true;
        project.ReminderOn = new DateTime(2026, 11, 20, 4, 30, 0, DateTimeKind.Utc);
        project.ReminderEveryMinutes = 1440;
        project.ReminderSilent = true;
        library.Remember(project);

        library.Update(project.Id, Draft("Renamed"));
        var stored = Library(directory).Find(project.Id);

        Assert.NotNull(stored);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(new DateOnly(2026, 12, 1), stored.Deadline);
        Assert.Equal(15 * 60, stored.DeadlineMinute);
        Assert.Equal(30, stored.NotifyBeforeMinutes);
        Assert.True(stored.DeadlineSilent);
        Assert.Equal(project.ReminderOn, stored.ReminderOn);
        Assert.Equal(1440, stored.ReminderEveryMinutes);
        Assert.True(stored.ReminderSilent);
    }

    [Fact]
    public void A_clean_child_close_does_not_invent_a_discard()
    {
        var id = Guid.NewGuid();
        var plan = ProjectClose.Plan([new TabCloseRequest(id, HasPath: false, IsDirty: false)]);

        var step = Assert.Single(plan);
        Assert.Equal(TabCloseStepKind.Close, step.Kind);
        Assert.Equal(id, step.DocumentId);
    }

    private static ProjectLibrary Library() => new(Path.Combine(TempDirectory(), "projects.json"));

    private static ProjectLibrary Library(string directory) =>
        new(Path.Combine(directory, "projects.json"));

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ProjectDraft Draft(
        string name,
        string description = "",
        string platform = "macOS",
        string framework = "Avalonia",
        string icon = "Folder",
        string color = "#3B82F6") =>
        new(name, description, platform, framework, icon, color);

    private static ChecklistDocument Sample(string title) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            WorkType = WorkType.Feature,
            CreatedOn = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
            UpdatedOn = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
            Items =
            [
                Check("Write the test", completed: true),
                Check("Open the screen", completed: false)
            ]
        };

    private static ChecklistItem Check(
        string title,
        bool completed,
        bool skipped = false,
        ChecklistPriority priority = ChecklistPriority.Required) =>
        new()
        {
            Title = title,
            Priority = priority,
            IsCompleted = completed && !skipped,
            IsSkipped = skipped
        };
}
