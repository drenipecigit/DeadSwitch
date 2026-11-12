using System.Text.RegularExpressions;

namespace DeadSwitch.Tests;

// JavaScript does not run in these tests. They read the files as text, so they catch mistakes such as
// innerHTML, but they do not prove that the page works and a determined author can get around them.
// The decisions in dashboard-logic.js are run in DashboardLogicTests. Only the owner's run in a browser
// shows how the page really behaves.
public class DashboardJavaScriptTests
{
    private const string PageScript = "/dashboard.js";
    private const string LogicScript = "/dashboard-logic.js";

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

    // The only properties the page script may set. Each of them keeps a value as plain text or only
    // shows or hides something. Any other property, such as src, style or onclick, is refused.
    private static readonly string[] SafeProperties =
    {
        "textContent",
        "title",
        "className",
        "hidden",
    };

    // The logic file must not be able to change the page, so that the tests in Jint cover all of it.
    private static readonly string[] PageCode =
    {
        "document",
        "window",
        "fetch",
        "setTimeout",
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

    [Theory]
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
    public async Task NoScriptHasCodeThatRunsText(string path)
    {
        string script = await ReadFileAsync(path);

        Assert.False(HasCodeThatRunsText(script));
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

    [Theory]
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
    public async Task NoScriptCreatesLinks(string path)
    {
        string script = await ReadFileAsync(path);

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
        string script = await ReadFileAsync(PageScript);

        Assert.False(ContainsAny(script, WriteCode));
        Assert.Single(Regex.Matches(script, @"\bfetch\("));
        Assert.Contains("fetch(\"/services/overview\"", script);
    }

    [Theory]
    [InlineData("cell.innerHTML = service.name;", "innerHTML")]
    [InlineData("image.src = service.url;", "src")]
    [InlineData("cell.onclick = service.name;", "onclick")]
    [InlineData("cell.style = service.name;", "style")]
    [InlineData("cell.innerText += service.name;", "innerText")]
    [InlineData("cell.textContent = service.name;", "textContent")]
    public void TheSearchFindsEveryPropertyThatIsSet(string code, string property)
    {
        Assert.Equal(new List<string> { property }, FindSetProperties(code));
    }

    [Theory]
    [InlineData("if (service.name === other.name) {")]
    [InlineData("if (service.securityGrade !== null) {")]
    [InlineData("if (list.length >= 0) {")]
    [InlineData("const name = service.name;")]
    public void TheSearchLeavesComparisonsAndVariablesAlone(string code)
    {
        Assert.Empty(FindSetProperties(code));
    }

    // Together with the forbidden words above, this means that a value can reach the page only as text.
    [Fact]
    public async Task ThePageScriptOnlySetsSafeProperties()
    {
        string script = await ReadFileAsync(PageScript);

        foreach (string property in FindSetProperties(script))
        {
            Assert.Contains(property, SafeProperties);
        }
    }

    [Fact]
    public async Task TheLogicFileDoesNotTouchThePage()
    {
        string script = await ReadFileAsync(LogicScript);

        Assert.False(ContainsAny(script, PageCode));
        Assert.Empty(FindSetProperties(script));
    }

    // The two helpers are the only way text reaches the page, and both write it through textContent.
    [Theory]
    [InlineData("element.textContent = text;")]
    [InlineData("createText(\"span\", \"card-name\", service.name)")]
    [InlineData("createText(\"span\", \"card-url\", service.url)")]
    [InlineData("appendDetail(row, service.failureReason)")]
    [InlineData("appendDetail(row, certificate.text)")]
    [InlineData("details.title = certificate.title;")]
    [InlineData("createText(\"span\", parts[i].className, String(parts[i].count))")]
    [InlineData("createText(\"span\", \"summary-label\", \" \" + parts[i].label)")]
    [InlineData("createText(\"span\", \"summary-separator\", \" | \")")]
    public async Task TheValuesFromTheServerAreWrittenAsText(string code)
    {
        string script = await ReadFileAsync(PageScript);

        Assert.Contains(code, script);
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
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
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
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
    [InlineData("/dashboard.css")]
    public async Task EveryFileIsPlainAscii(string path)
    {
        string text = await ReadFileAsync(path);

        foreach (char character in text)
        {
            Assert.True(char.IsAscii(character), $"The character {(int)character} is not ASCII.");
        }
    }

    // The summary is built from the parts that the logic tests prove, and from nothing else. It must be
    // emptied first: the page updates every ten seconds, and the old text would otherwise stay in front.
    [Fact]
    public async Task TheSummaryIsEmptiedAndBuiltFromTheParts()
    {
        string script = await ReadFileAsync(PageScript);

        Assert.Contains("summary.removeChild(summary.firstChild);", script);
        Assert.Contains("const parts = getSummaryParts(services);", script);
        Assert.Contains("showSummary(services);", script);
    }

    // Each class the logic gives a number must have a colour of its own in the style sheet. This proves
    // that the rule is there, not how the page looks: only a browser shows that.
    [Theory]
    [InlineData(".count-up {\n  color: var(--up);\n}")]
    [InlineData(".count-down {\n  color: var(--down);\n}")]
    [InlineData(".count-pending {\n  color: var(--pending);\n}")]
    [InlineData(".count-unknown {\n  color: var(--unknown);\n}")]
    public async Task EveryNumberClassHasItsColour(string rule)
    {
        string styles = await ReadStylesAsync();

        Assert.Contains(rule, styles);
    }

    // On a narrow screen the address moves below, so that the grade stays on the line of the name.
    // This proves that the rule is there, not how the page looks: only a browser shows that.
    [Fact]
    public async Task ANarrowScreenMovesTheAddressBelowTheNameLine()
    {
        string styles = await ReadStylesAsync();

        string expected =
            "@media (max-width: 600px) {\n" +
            "  .card-name {\n" +
            "    flex: 1 1 0;\n" +
            "    min-width: 0;\n" +
            "  }\n" +
            "\n" +
            "  .card-url {\n" +
            "    order: 1;\n" +
            "    flex-basis: 100%;\n" +
            "  }\n" +
            "}";
        Assert.Contains(expected, styles);
    }

    // A renamed field in the API would otherwise only be noticed as an empty field in the browser.
    [Theory]
    [InlineData("service.name")]
    [InlineData("service.url")]
    [InlineData("service.currentStatus")]
    [InlineData("service.lastKnownStatus")]
    [InlineData("service.lastCheckedAt")]
    [InlineData("service.isEnabled")]
    [InlineData("service.responseTimeMs")]
    [InlineData("service.failureReason")]
    [InlineData("service.securityGrade")]
    [InlineData("service.missingSecurityHeaders")]
    [InlineData("service.securityNotAssessedReason")]
    [InlineData("service.certificateNotAfterUtc")]
    [InlineData("service.certificateExpiryText")]
    [InlineData("service.certificateNotAssessedReason")]
    [InlineData("service.redirectFinding")]
    [InlineData("service.redirectTargetHost")]
    public async Task TheScriptsReadEveryFieldOfTheOverview(string field)
    {
        string pageScript = await ReadFileAsync(PageScript);
        string logicScript = await ReadFileAsync(LogicScript);

        Assert.Contains(field, pageScript + logicScript);
    }

    [Theory]
    [InlineData("update-time")]
    [InlineData("update-error")]
    [InlineData("no-services")]
    [InlineData("summary")]
    [InlineData("service-area")]
    [InlineData("service-cards")]
    [InlineData("grade-explanation")]
    public async Task ThePageHasEveryElementTheScriptLooksFor(string id)
    {
        string script = await ReadFileAsync(PageScript);
        string page = await ReadFileAsync("/");

        Assert.Contains($"getElementById(\"{id}\")", script);
        Assert.Contains($"id=\"{id}\"", page);
    }

    [Fact]
    public async Task TheMeaningOfTheGradeIsWrittenThroughTextContent()
    {
        string script = await ReadFileAsync(PageScript);

        Assert.Contains("\"Grade A means that all five security headers are present. Their values are not assessed.\"", script);
        Assert.Contains("getElementById(\"grade-explanation\").textContent = GRADE_EXPLANATION;", script);
    }

    // Every time on the page must come from formatLocalTime, which the logic tests prove. The browser's own
    // formatting follows the browser's language and would give another format on another machine.
    [Theory]
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
    public async Task NoScriptUsesTheBrowsersOwnTimeFormat(string path)
    {
        string script = await ReadFileAsync(path);

        Assert.DoesNotContain("toLocale", script);
        Assert.DoesNotContain("Intl", script);
    }

    // The order is proven in the logic tests. This shows that the cards are built from the sorted list.
    [Fact]
    public async Task TheCardsAreBuiltFromTheSortedList()
    {
        string script = await ReadFileAsync(PageScript);

        Assert.Contains("const sorted = sortServices(services);", script);
        Assert.Contains("list.appendChild(createCard(sorted[i]));", script);
    }

    // The limits around expiry are tested on the server. If the script used the number it would have
    // to make its own decisions about them.
    [Theory]
    [InlineData(PageScript)]
    [InlineData(LogicScript)]
    public async Task NoScriptUsesTheNumberOfDays(string path)
    {
        string script = await ReadFileAsync(path);

        Assert.DoesNotContain("certificateDaysLeft", script);
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

    // Finds "something.property =" and "something.property +=". The (?!=) leaves == and === alone.
    private static List<string> FindSetProperties(string code)
    {
        List<string> properties = new List<string>();

        foreach (Match match in Regex.Matches(code, @"\.(\w+)\s*\+?=(?!=)"))
        {
            properties.Add(match.Groups[1].Value);
        }

        return properties;
    }

    // The same text whether the file was checked out with Windows or Unix line endings.
    private static async Task<string> ReadStylesAsync()
    {
        string styles = await ReadFileAsync("/dashboard.css");
        return styles.Replace("\r\n", "\n");
    }

    private static async Task<string> ReadFileAsync(string path)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        return await client.GetStringAsync(path);
    }
}
