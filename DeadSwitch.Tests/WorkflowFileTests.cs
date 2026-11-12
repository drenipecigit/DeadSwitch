namespace DeadSwitch.Tests;

// Reads the GitHub Actions workflow as plain text, line by line. It does not understand YAML, so a
// wrong indentation is not caught. What it does catch is a rule from the project being broken by a
// later edit: wider permissions, a pull request trigger, an action that is not pinned, and so on.
public class WorkflowFileTests
{
    private const string SolutionFileName = "DeadSwitch.slnx";
    private const int CommitShaLength = 40;

    [Fact]
    public void PermissionsAtTopLevelOnlyAllowReadingTheCode()
    {
        string[] lines = ReadWorkflowLines();

        // "permissions:" without indentation is the top level. A second one, for example on the job,
        // could give the job more rights than the top level says.
        int permissionLines = 0;
        int topLevelIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("permissions:"))
            {
                permissionLines++;
            }

            if (lines[i] == "permissions:")
            {
                topLevelIndex = i;
            }
        }

        Assert.Equal(1, permissionLines);
        Assert.NotEqual(-1, topLevelIndex);

        // The block must hold exactly one line: "contents: read". The line after it must not be
        // indented, or it would be another permission.
        Assert.True(topLevelIndex + 2 < lines.Length);
        Assert.Equal("  contents: read", lines[topLevelIndex + 1]);
        Assert.False(lines[topLevelIndex + 2].StartsWith(" "));
    }

    // Also catches pull_request_target, which runs with more rights.
    [Fact]
    public void ThereIsNoPullRequestTrigger()
    {
        string text = ReadWorkflowText();

        Assert.DoesNotContain("pull_request", text);
    }

    // A tag such as v4 can be moved to other code. A full commit SHA cannot.
    [Fact]
    public void EveryActionIsPinnedToAFullCommitShaWithAVersionComment()
    {
        string[] lines = ReadWorkflowLines();
        int actionLines = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("- "))
            {
                trimmed = trimmed.Substring(2);
            }

            if (!trimmed.StartsWith("uses:"))
            {
                continue;
            }

            actionLines++;
            string value = trimmed.Substring("uses:".Length).Trim();

            int atIndex = value.IndexOf('@');
            Assert.True(atIndex > 0, "No @ in: " + line);

            string afterAt = value.Substring(atIndex + 1);
            Assert.True(afterAt.Length > CommitShaLength, "Too short after @ in: " + line);

            string sha = afterAt.Substring(0, CommitShaLength);
            Assert.True(IsLowerCaseHex(sha), "Not a full commit SHA in: " + line);

            string comment = afterAt.Substring(CommitShaLength);
            Assert.True(comment.StartsWith(" # v"), "No version comment in: " + line);
        }

        // Without this the test would pass for a file with no actions at all.
        Assert.True(actionLines > 0);
    }

    [Fact]
    public void CheckoutDoesNotKeepTheToken()
    {
        string[] lines = ReadWorkflowLines();

        Assert.True(ContainsTrimmedLine(lines, "persist-credentials: false"));
    }

    [Fact]
    public void TheJobHasATimeout()
    {
        string[] lines = ReadWorkflowLines();

        bool found = false;
        foreach (string line in lines)
        {
            if (line.Trim().StartsWith("timeout-minutes:"))
            {
                found = true;
            }
        }

        Assert.True(found);
    }

    // "ubuntu-latest" changes to a new version without any change in the repository.
    [Fact]
    public void TheRunnerVersionIsFixed()
    {
        string[] lines = ReadWorkflowLines();
        string text = ReadWorkflowText();

        Assert.True(ContainsTrimmedLine(lines, "runs-on: ubuntu-24.04"));
        Assert.DoesNotContain("-latest", text);
    }

    [Fact]
    public void TheSdkIsTheSameAsOnTheDevelopmentMachine()
    {
        string[] lines = ReadWorkflowLines();

        Assert.True(ContainsTrimmedLine(lines, "dotnet-version: '10.0.301'"));
    }

    // The tests need no secret. A secret in the workflow would also be readable by every step.
    [Fact]
    public void NoSecretIsUsed()
    {
        string text = ReadWorkflowText();

        Assert.DoesNotContain("secrets.", text);
    }

    // The tests must pass in the runner's own time zone. Setting one to make them green would hide a bug.
    [Fact]
    public void NoTimeZoneIsSet()
    {
        string text = ReadWorkflowText();

        Assert.DoesNotContain("TZ", text);
    }

    private static bool ContainsTrimmedLine(string[] lines, string expected)
    {
        foreach (string line in lines)
        {
            if (line.Trim() == expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLowerCaseHex(string text)
    {
        foreach (char character in text)
        {
            bool isDigit = character >= '0' && character <= '9';
            bool isLetter = character >= 'a' && character <= 'f';
            if (!isDigit && !isLetter)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ReadWorkflowLines()
    {
        return File.ReadAllLines(GetWorkflowPath());
    }

    private static string ReadWorkflowText()
    {
        return File.ReadAllText(GetWorkflowPath());
    }

    private static string GetWorkflowPath()
    {
        return Path.Combine(FindRepositoryRoot(), ".github", "workflows", "build-and-test.yml");
    }

    // The tests run from bin/Debug/net10.0, so the folder with the solution file is searched for
    // upwards. This works both on the development machine and in a fresh checkout on GitHub.
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder != null)
        {
            if (File.Exists(Path.Combine(folder.FullName, SolutionFileName)))
            {
                return folder.FullName;
            }

            folder = folder.Parent;
        }

        throw new InvalidOperationException("The folder with " + SolutionFileName + " was not found.");
    }
}
