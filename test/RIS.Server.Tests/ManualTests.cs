using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed partial class ManualTests
{
    private RisServerFactory _factory = null!;

    [TestInitialize]
    public void Initialize() => _factory = new RisServerFactory();

    [TestCleanup]
    public async Task Cleanup() => await _factory.DisposeAsync();

    private ManualService Manual => _factory.Services.GetRequiredService<ManualService>();

    [TestMethod]
    public void BothLanguages_HaveTheSameChapters_WithTitles()
    {
        var pl = Manual.Chapters("pl").Select(c => c.Slug).ToList();
        var en = Manual.Chapters("en").Select(c => c.Slug).ToList();
        Assert.IsTrue(pl.Count >= 9, "Manual chapters missing: " + string.Join(", ", pl));
        CollectionAssert.AreEqual(pl, en, "Polish and English manuals must have the same chapter files.");
        foreach (var language in ManualService.Languages)
            foreach (var chapter in Manual.Chapters(language))
                Assert.AreNotEqual(chapter.Slug, chapter.Title, $"{language}/{chapter.Slug} has no '# Title' line.");
    }

    [TestMethod]
    public void Links_PointToExistingChaptersAnchorsAndImages()
    {
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        foreach (var language in ManualService.Languages)
        {
            var chapters = Manual.Chapters(language).ToDictionary(c => c.Slug);
            foreach (var chapter in chapters.Values)
            {
                foreach (Match image in ImageSource().Matches(chapter.Html))
                    Assert.IsTrue(webRoot.GetFileInfo(image.Groups[1].Value).Exists, $"{language}/{chapter.Slug}: missing image {image.Groups[1].Value}");

                foreach (Match link in HelpLink().Matches(chapter.Html))
                {
                    var target = link.Groups[1].Value;
                    Assert.IsTrue(chapters.TryGetValue(target, out var targetChapter), $"{language}/{chapter.Slug}: link to missing chapter {target}");
                    if (link.Groups[2].Success)
                        StringAssert.Contains(targetChapter!.Html, $"id=\"{link.Groups[2].Value}\"", $"{language}/{chapter.Slug}: missing anchor {target}#{link.Groups[2].Value}");
                }

                foreach (Match anchor in LocalAnchor().Matches(chapter.Html))
                    StringAssert.Contains(chapter.Html, $"id=\"{anchor.Groups[1].Value}\"", $"{language}/{chapter.Slug}: missing anchor #{anchor.Groups[1].Value}");

                Assert.IsFalse(chapter.Html.Contains(".md\"", StringComparison.Ordinal), $"{language}/{chapter.Slug}: unresolved .md link");
            }
        }
    }

    [TestMethod]
    public void Images_ExistInBothLanguages()
    {
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        var pl = webRoot.GetDirectoryContents("manual/img/pl").Select(f => f.Name).Order().ToList();
        var en = webRoot.GetDirectoryContents("manual/img/en").Select(f => f.Name).Order().ToList();
        Assert.IsTrue(pl.Count > 0);
        CollectionAssert.AreEqual(pl, en, "Each screenshot must exist in both languages.");
    }

    [TestMethod]
    public async Task HelpPage_RendersChapter_InUserLanguage_AndServesImages()
    {
        var client = _factory.Client("Viewer");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/help/09-faq"));
        StringAssert.Contains(page, "FAQ – najczęstsze pytania");
        StringAssert.Contains(page, "href=\"/help/07-maintenance\"");

        var english = _factory.Client("Viewer");
        english.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        StringAssert.Contains(WebUtility.HtmlDecode(await english.GetStringAsync("/help")), "What's new compared to RIS 4.x");

        var image = await client.GetAsync("/manual/img/en/dashboard.png");
        Assert.AreEqual(HttpStatusCode.OK, image.StatusCode);
        Assert.AreEqual("image/png", image.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public void Search_FindsChaptersWithAllWords()
    {
        var hits = Manual.Search("en", "AADSTS7000215 secret");
        Assert.IsTrue(hits.Any(h => h.Chapter.Slug == "08-troubleshooting"));
        Assert.AreEqual(0, Manual.Search("pl", "zzzz-nie-istnieje").Count);
    }

    [GeneratedRegex(@"src=""/(manual/img/[^""]+)""")]
    private static partial Regex ImageSource();

    [GeneratedRegex(@"href=""/help/([a-z0-9-]+)(?:#([^""]+))?""")]
    private static partial Regex HelpLink();

    [GeneratedRegex(@"href=""#([^""]+)""")]
    private static partial Regex LocalAnchor();
}
