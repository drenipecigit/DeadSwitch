using System.Text.RegularExpressions;

namespace DeadSwitch.Tests;

// JavaScript does not run in these tests. They read the files as text, so they catch mistakes such as
// innerHTML, but they do not prove that the page works and a determined author can get around them.
// Only the owner's run in a browser shows how the page really behaves.
public class DashboardJavaScriptTests
{
    // Each of these turns text into code or HTML. A name from a user must never reach one of them.
    private static readonly string[] ForbiddenInScript =
    {
        "innerHTML",
        "outerHTML",
        "insertAdjacentHTML",
        "document.write",
        "setHTMLUnsafe",
        "createContextualFragment",
        "DOMParser",
        "srcdoc",
        "new Function",
        "setAttribute",
    };

    // A monitored URL must never become something that can be clicked.
    private static readonly string[] LinkCode =
    {
        "href",
        "createElement(\"a\")",
        "createElement('a')",
        "window.open",
        "location",
    };

    // The dashboard only reads in this phase.
    private static readonly string[] WriteCode =
    {
        "POST",
        "PUT",
        "DELETE",
        "PATCH",
        "method",
        "XMLHttpRequest",
        "sendBeacon",
    };

    private static readonly string[] ControlsThatChangeData =
    {
        "<form",
        "<button",
        "<input",
        "<select",
        "<textarea",
        "<a ",
    };

    private static readonly string[] OutsideResources =
    {
        "http://",
        "https://",
        "@import",
        "url(",
    };

    // A test that can never fail proves nothing, so the search itself is tested on bad code first.
    [Theory]
    [InlineData("cell.innerHTML = service.name;")]
    [InlineData("cell.INNERHTML = service.name;")]
    [InlineData("cell.outerHTML = service.name;")]
    [InlineData("row.insertAdjacentHTML('beforeend', service.name);")]
    [InlineData("document.write(service.name);")]
    [InlineData("document.writeln(service.name);")]
    [InlineData("eval(service.name);")]
    [InlineData("window.eval(service.name);")]
    [InlineData("window['eval'](service.name);")]
    [InlineData("cell.setHTMLUnsafe(service.name);")]
    [InlineData("range.createContextualFragment(service.name);")]
    [InlineData("new DOMParser().parseFromString(service.name, 'text/html');")]
    [InlineData("frame.srcdoc = service.name;")]
    [InlineData("new Function(service.name);")]
    [InlineData("cell.setAttribute('onclick', service.name);")]
    public void TheSearchFindsCodeThatRunsText(string badCode)
    {
        Assert.True(HasCodeThatRunsText(badCode));
    }

    // The word boundary around eval matters: a plain search would stop an ordinary word.
    [Theory]
    [InlineData("cell.textContent = service.name;")]
    [InlineData("// Retrieval of the medieval list.")]
    public void TheSearchLeavesSafeCodeAlone(string safeCode)
    {
        Assert.False(HasCodeThatRunsText(safeCode));
    }

    [Fact]
    public async Task TheScriptHasNoCodeThatRunsText()
    {
        string script = await ReadFileAsync("/dashboard.js");

        Assert.False(HasCodeThatRunsText(script));
    }

    [Fact]
    public async Task TheScriptWritesTextOnlyThroughTextContent()
    {
        string script = await ReadFileAsync("/dashboard.js");

        Assert.Contains(".textContent = ", script);
    }

    [Theory]
    [InlineData("href")]
    [InlineData("createElement(\"a\")")]
    [InlineData("createElement('a')")]
    [InlineData("window.open")]
    [InlineData("location")]
    public void TheSearchFindsLinkCode(string badCode)
    {
        Assert.True(ContainsAny(badCode, LinkCode));
    }

    [Fact]
    public async Task TheScriptCreatesNoLinks()
    {
        string script = await ReadFileAsync("/dashboard.js");

        Assert.False(ContainsAny(script, LinkCode));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("method: 'put'")]
    [InlineData("navigator.sendBeacon('/services');")]
    [InlineData("new XMLHttpRequest();")]
    public void TheSearchFindsWriteCode(string badCode)
    {
        Assert.True(ContainsAny(badCode, WriteCode));
    }

    [Fact]
    public async Task TheScriptOnlyReads()
    {
        string script = await ReadFileAsync("/dashboard.js");

        Assert.False(ContainsAny(script, WriteCode));
        Assert.Single(Regex.Matches(script, @"\bfetch\("));
        Assert.Contains("fetch(\"/services/overview\"", script);
    }

    [Fact]
    public async Task ThePageHasNoControlsThatChangeData()
    {
        string page = await ReadFileAsync("/");

        Assert.False(ContainsAny(page, ControlsThatChangeData));
    }

    // The policy would stop all of these anyway. The test keeps them from being written at all.
    [Fact]
    public async Task ThePageHasNoInlineCodeOrStyle()
    {
        string page = await ReadFileAsync("/");

        Assert.False(Regex.IsMatch(page, @"<script(?![^>]*\ssrc=)", RegexOptions.IgnoreCase), "A script without src.");
        Assert.False(Regex.IsMatch(page, @"<style", RegexOptions.IgnoreCase), "A style block.");
        Assert.False(Regex.IsMatch(page, @"\sstyle\s*=", RegexOptions.IgnoreCase), "A style attribute.");
        Assert.False(Regex.IsMatch(page, @"\son\w+\s*=", RegexOptions.IgnoreCase), "An event attribute.");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard.js")]
    [InlineData("/dashboard.css")]
    public async Task NoFileLoadsAnythingFromOutside(string path)
    {
        string text = await ReadFileAsync(path);

        Assert.False(ContainsAny(text, OutsideResources));
    }

    // The files are served without a character set. Plain ASCII reads the same in every one of them,
    // and an invisible character cannot hide in the code.
    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard.js")]
    [InlineData("/dashboard.css")]
    public async Task EveryFileIsPlainAscii(string path)
    {
        string text = await ReadFileAsync(path);

        foreach (char character in text)
        {
            Assert.True(char.IsAscii(character), $"The character {(int)character} is not ASCII.");
        }
    }

    // A renamed field in the API would otherwise only be noticed as an empty column in the browser.
    [Theory]
    [InlineData("service.name")]
    [InlineData("service.url")]
    [InlineData("service.currentStatus")]
    [InlineData("service.lastKnownStatus")]
    [InlineData("service.lastCheckedAt")]
    [InlineData("service.isEnabled")]
    [InlineData("service.responseTimeMs")]
    [InlineData("service.failureReason")]
    public async Task TheScriptReadsEveryFieldOfTheOverview(string field)
    {
        string script = await ReadFileAsync("/dashboard.js");

        Assert.Contains(field, script);
    }

    [Theory]
    [InlineData("update-time")]
    [InlineData("update-error")]
    [InlineData("no-services")]
    [InlineData("table-frame")]
    [InlineData("service-rows")]
    public async Task ThePageHasEveryElementTheScriptLooksFor(string id)
    {
        string script = await ReadFileAsync("/dashboard.js");
        string page = await ReadFileAsync("/");

        Assert.Contains($"getElementById(\"{id}\")", script);
        Assert.Contains($"id=\"{id}\"", page);
    }

    private static bool HasCodeThatRunsText(string code)
    {
        if (ContainsAny(code, ForbiddenInScript))
        {
            return true;
        }

        return Regex.IsMatch(code, @"\beval\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsAny(string text, string[] searchedFor)
    {
        foreach (string word in searchedFor)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> ReadFileAsync(string path)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        return await client.GetStringAsync(path);
    }
}
