// Run manually on Windows only. Never touches the user's actual documents.
using System.IO;
using Sunrise.FileManager.Services.CloudDrive;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("CrossPaneFolderMoveSmoke: " + message);
    }

    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sunrise_FolderMove_R02_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            string nested = Path.Combine(source, "보고서", "2026");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(destination);
            byte[] original = Enumerable.Range(0, 2048).Select(n => (byte)(n % 251)).ToArray();
            string originalFile = Path.Combine(nested, "원본.pdf");
            await File.WriteAllBytesAsync(originalFile, original);

            var target = new CrossPaneCopyTarget(destination, null, null, null);
            var request = new CrossPaneCopySource(source, null, null);
            var result = await CrossPaneVerifiedMoveService.MoveAsync([request], target, null, CancellationToken.None);
            string movedFile = Path.Combine(destination, "source", "보고서", "2026", "원본.pdf");
            Check(result.Succeeded == 1 && result.FailedOnly == 0 && !result.Canceled, "directory transfer result");
            Check(!Directory.Exists(source) && File.Exists(movedFile), "publish and retire");
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(movedFile)), "exact bytes preserved");

            // Existing target means no overwrite and no loss of the fresh source.
            Directory.CreateDirectory(source);
            string collisionOriginal = Path.Combine(source, "새파일.txt");
            await File.WriteAllTextAsync(collisionOriginal, "keep-original");
            var collision = await CrossPaneVerifiedMoveService.MoveAsync([request], target, null, CancellationToken.None);
            Check(collision.Skipped == 1 && collision.Succeeded == 0, "collision reports skipped");
            Check(File.Exists(collisionOriginal), "collision preserves source");
            Check(File.Exists(movedFile), "collision preserves target");

            // Cancellation before starting does not modify either location.
            string independent = Path.Combine(root, "cancel-source");
            Directory.CreateDirectory(independent);
            await File.WriteAllTextAsync(Path.Combine(independent, "one.txt"), "must-remain");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var canceled = await CrossPaneVerifiedMoveService.MoveAsync(
                [new CrossPaneCopySource(independent, null, null)], target, null, cancel.Token);
            Check(canceled.Canceled && Directory.Exists(independent), "cancellation preserves source");
            Check(!Directory.Exists(Path.Combine(destination, "cancel-source")), "cancellation creates no target");

            // Parent + child selection is rejected before a single filesystem change.
            bool overlapRejected = false;
            try
            {
                await CrossPaneVerifiedMoveService.MoveAsync(
                    [new CrossPaneCopySource(source, null, null),
                     new CrossPaneCopySource(collisionOriginal, null, null)], target, null, CancellationToken.None);
            }
            catch (ArgumentException) { overlapRejected = true; }
            Check(overlapRejected && File.Exists(collisionOriginal), "nested selection preflight");

            Console.WriteLine("PASS: local folder move, bytes, no-overwrite, cancel, nested selection");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            // Own GUID temporary test root only. No real user documents involved.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
