using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Tests;

public class ChecklistDescriptionTests
{
    [Fact]
    public async Task Description_is_optional_and_round_trips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superdev-description-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checks.superdev.json");

        try
        {
            var document = new ChecklistDocument
            {
                Id = Guid.NewGuid(),
                Title = "Login",
                WorkType = WorkType.Bug,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow,
                Items = []
            };

            var files = new ChecklistFileService();
            await files.SaveAsync(path, document, replaceDifferentDocument: false);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("\"Description\"", json, StringComparison.Ordinal);

            document.Description = "Crash when rotating.\n\nOnly on iPad.";
            await files.SaveAsync(path, document, replaceDifferentDocument: false);

            var loaded = await files.LoadAsync(path);
            Assert.Equal("Crash when rotating.\n\nOnly on iPad.", loaded.Description);

            var text = ChecklistShare.ToPlainText(loaded);
            Assert.Contains("Crash when rotating.", text, StringComparison.Ordinal);
            Assert.Contains("Only on iPad.", text, StringComparison.Ordinal);

            var csv = ChecklistShare.ToCsv(loaded, "checks.superdev.json");
            Assert.Contains("Description:", csv, StringComparison.Ordinal);
            Assert.Contains("Only on iPad.", csv, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
