using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Tests;

public class SkippedCheckTests
{
    [Fact]
    public void Skipping_a_critical_check_removes_it_from_progress_and_from_completion()
    {
        var done = Check("Done", ChecklistPriority.Required, completed: true);
        var open = Check("Still open", ChecklistPriority.Conditional, completed: false);
        var critical = Check("Must ship", ChecklistPriority.Critical, completed: false);
        var required = Check("Write the notes", ChecklistPriority.Required, completed: true);
        var items = new List<ChecklistItem> { done, open, critical, required };

        var before = ChecklistCounts.Measure(items);
        Assert.Equal(2, before.Completed);
        Assert.Equal(4, before.Total);
        Assert.Equal(0, before.Skipped);
        Assert.Equal(1, ChecklistCounts.IncompleteRequired(items));
        Assert.False(ChecklistCounts.AllCountedComplete(items));

        critical.MarkSkipped(true);
        required.MarkSkipped(true);

        Assert.False(critical.IsCompleted);
        Assert.False(required.IsCompleted);
        Assert.True(critical.IsSkipped);
        Assert.False(critical.BlocksWorkspaceCompletion);
        Assert.False(required.BlocksWorkspaceCompletion);

        var after = ChecklistCounts.Measure(items);
        Assert.Equal(1, after.Completed);
        Assert.Equal(2, after.Total);
        Assert.Equal(2, after.Skipped);
        Assert.Equal("1 / 2 completed · 2 skipped", after.Label);
        Assert.Equal(0, ChecklistCounts.IncompleteRequired(items));
        Assert.False(ChecklistCounts.AllCountedComplete(items));

        open.MarkCompleted(true);
        Assert.True(ChecklistCounts.AllCountedComplete(items));
        Assert.Equal(0, ChecklistCounts.IncompleteRequired(items));
    }

    [Fact]
    public void Completing_a_skipped_check_counts_it_again()
    {
        var item = Check("Must ship", ChecklistPriority.Critical, completed: false);
        item.MarkSkipped(true);

        item.MarkCompleted(true);

        Assert.True(item.IsCompleted);
        Assert.False(item.IsSkipped);
        var progress = ChecklistCounts.Measure([item]);
        Assert.Equal(1, progress.Completed);
        Assert.Equal(1, progress.Total);
        Assert.Equal(0, progress.Skipped);
    }

    [Fact]
    public void A_skipped_check_is_not_counted_as_completed()
    {
        var item = Check("Must ship", ChecklistPriority.Required, completed: true);
        item.MarkSkipped(true);

        var progress = ChecklistCounts.Measure([item]);
        Assert.Equal(0, progress.Completed);
        Assert.Equal(0, progress.Total);
        Assert.Equal(1, progress.Skipped);
        Assert.False(item.IsCompleted);
    }

    [Fact]
    public async Task Skipped_checks_round_trip_and_a_finished_flag_does_not_survive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checks.superdev.json");

        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Skip",
                WorkType = WorkType.Feature,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items =
                [
                    Check("Keep", ChecklistPriority.Required, completed: true),
                    new ChecklistItem
                    {
                        Title = "Set aside",
                        Category = "Testing",
                        Priority = ChecklistPriority.Critical,
                        IsCompleted = true,
                        IsSkipped = true
                    }
                ]
            };

            var files = new ChecklistFileService();
            await files.SaveAsync(path, document, replaceDifferentDocument: false);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"IsSkipped\": true", json, StringComparison.Ordinal);
            Assert.Equal(1, Count(json, "\"IsSkipped\""));

            var loaded = await files.LoadAsync(path);
            Assert.True(loaded.Items[0].IsCompleted);
            Assert.False(loaded.Items[0].IsSkipped);
            Assert.True(loaded.Items[1].IsSkipped);
            Assert.False(loaded.Items[1].IsCompleted);
            Assert.False(loaded.Items[1].BlocksWorkspaceCompletion);

            var text = ChecklistShare.ToPlainText(loaded);
            Assert.Contains("Progress: 1 of 1 complete, 1 skipped", text, StringComparison.Ordinal);
            Assert.Contains("- [-] Set aside — Testing · Critical · Skipped", text, StringComparison.Ordinal);

            var csv = ChecklistShare.ToCsv(loaded, "checks.superdev.json");
            Assert.Contains("1 of 1 complete, 1 skipped", csv, StringComparison.Ordinal);
            Assert.Contains("Skipped", csv, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ChecklistItem Check(string title, ChecklistPriority priority, bool completed) =>
        new()
        {
            Title = title,
            Category = "Development",
            Priority = priority,
            IsCompleted = completed
        };

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
