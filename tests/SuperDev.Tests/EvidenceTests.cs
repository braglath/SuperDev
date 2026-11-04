using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Tests;

public class EvidenceTests
{
    [Theory]
    [InlineData("/shots/Login.PNG", true, false)]
    [InlineData("/shots/login.jpeg", true, false)]
    [InlineData("/shots/walkthrough.MOV", false, true)]
    [InlineData("/shots/notes.txt", false, false)]
    [InlineData("/shots/photo.png.exe", false, false)]
    public void Classifies_screenshots_and_videos(string path, bool image, bool video)
    {
        Assert.Equal(image, EvidenceKind.IsImage(path));
        Assert.Equal(video, EvidenceKind.IsVideo(path));
        Assert.Equal(image || video, EvidenceKind.IsSupported(path));
    }

    [Fact]
    public void Uses_the_file_name_when_the_file_is_gone()
    {
        var path = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".png");

        Assert.False(EvidenceKind.FileExists(path));
        Assert.Equal(Path.GetFileName(path), EvidenceKind.DisplayName(path));
    }

    [Fact]
    public async Task Saves_the_path_and_marks_a_missing_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checklistPath = Path.Combine(directory, "checks.superdev.json");
        var shot = Path.Combine(directory, "login.png");
        var missing = Path.Combine(directory, "gone.mp4");
        await File.WriteAllBytesAsync(shot, [1, 2, 3]);

        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Evidence",
                WorkType = WorkType.Bug,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items =
                [
                    new ChecklistItem
                    {
                        Title = "Reproduce",
                        Category = "Testing",
                        Priority = ChecklistPriority.Required,
                        Evidence =
                        [
                            new EvidenceFile { Path = shot },
                            new EvidenceFile { Path = "  " },
                            new EvidenceFile { Path = missing }
                        ]
                    }
                ]
            };

            var files = new ChecklistFileService();
            await files.SaveAsync(checklistPath, document, replaceDifferentDocument: false);
            var loaded = await files.LoadAsync(checklistPath);

            Assert.Equal([shot, missing], loaded.Items[0].Evidence.Select(file => file.Path).ToArray());

            var text = ChecklistShare.ToPlainText(loaded);
            Assert.Contains(shot, text, StringComparison.Ordinal);
            Assert.Contains("missing: " + missing, text, StringComparison.Ordinal);

            var csv = ChecklistShare.ToCsv(loaded, "checks.superdev.json");
            Assert.Contains("Evidence", csv, StringComparison.Ordinal);
            Assert.Contains("missing: " + missing, csv, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
