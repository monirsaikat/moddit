namespace Modit.TestSupport;

/// <summary>
/// Best-effort cleanup of a test's temp folder. The engine host can still hold a file for a moment after the
/// client is disposed (and antivirus scanners on CI do the same), so deletion retries briefly and never fails a test.
/// </summary>
public static class TestDirectory
{
    public static void Delete(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
