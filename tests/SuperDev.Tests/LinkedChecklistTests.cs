using System.Text.RegularExpressions;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Tests;

public class LinkedChecklistTests
{
    [Fact]
    public void Required_link_blocks_the_check_and_an_optional_link_does_not()
    {
        var item = new ChecklistItem
        {
            Title = "Ship",
            Subtasks =
            [
                new ChecklistSubtask
                {
                    Title = "Release notes",
                    IsCompleted = true
                },
                new ChecklistSubtask
                {
                    Title = "Optional review",
                    LinkedDocumentId = Guid.NewGuid(),
                    LinkOptional = true
                },
                new ChecklistSubtask
                {
                    Title = "Required review",
                    LinkedDocumentId = Guid.NewGuid()
                }
            ]
        };

        Assert.False(item.CanComplete);

        item.Subtasks[2].IsCompleted = true;
        Assert.True(item.CanComplete);

        item.Subtasks[0].IsCompleted = false;
        Assert.False(item.CanComplete);
    }

    [Fact]
    public async Task Older_links_stay_required_and_optional_links_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checks.superdev.json");

        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Links",
                WorkType = WorkType.Bug,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items =
                [
                    new ChecklistItem
                    {
                        Title = "Finish",
                        Category = "Development",
                        IsCompleted = true,
                        Subtasks =
                        [
                            new ChecklistSubtask
                            {
                                Title = "Must finish",
                                LinkedDocumentId = Guid.NewGuid(),
                                IsCompleted = true
                            },
                            new ChecklistSubtask
                            {
                                Title = "Can wait",
                                LinkedDocumentId = Guid.NewGuid(),
                                LinkOptional = true
                            }
                        ]
                    }
                ]
            };

            var files = new ChecklistFileService();
            await files.SaveAsync(path, document, replaceDifferentDocument: false);

            var json = await File.ReadAllTextAsync(path);
            Assert.Equal(1, Regex.Count(json, "\"LinkOptional\""));
            Assert.Contains("\"LinkOptional\": true", json, StringComparison.Ordinal);

            var loaded = await files.LoadAsync(path);
            Assert.False(loaded.Items[0].Subtasks[0].LinkOptional);
            Assert.True(loaded.Items[0].Subtasks[1].LinkOptional);
            Assert.True(loaded.Items[0].IsCompleted);

            var legacy = Regex.Replace(json, @",\s*""LinkOptional"":\s*true", "");
            await File.WriteAllTextAsync(path, legacy);
            var reopened = await files.LoadAsync(path);
            Assert.False(reopened.Items[0].Subtasks[0].LinkOptional);
            Assert.False(reopened.Items[0].Subtasks[1].LinkOptional);
            Assert.False(reopened.Items[0].IsCompleted);
            Assert.False(reopened.Items[0].CanComplete);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
