using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.Extensions.FileProviders;

namespace RIS.Server.Services;

/// <summary>Chapter of the user manual (wwwroot/manual/{language}/NN-slug.md).</summary>
public sealed record ManualChapter(string Slug, string Title, string Html, string Text);

public sealed record ManualSearchHit(ManualChapter Chapter, string Snippet);

/// <summary>
/// The user manual shipped with the application, in Polish and English. Markdown files are read from the web root
/// (also present in the repository), rendered once per language and cached. Links between chapters ("05-x.md#a")
/// and images ("img/x.png") are rewritten to /help/05-x#a and /manual/img/{language}/x.png.
/// </summary>
public sealed partial class ManualService(IWebHostEnvironment environment)
{
    public static readonly string[] Languages = ["pl", "en"];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tables, auto identifiers on headings, task lists, footnotes ...
        .Build();

    private readonly ConcurrentDictionary<string, IReadOnlyList<ManualChapter>> _chapters = new();

    /// <summary>Language of the manual for the UI culture (Polish, otherwise English).</summary>
    public static string LanguageOf(CultureInfo culture) => culture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";

    public IReadOnlyList<ManualChapter> Chapters(string language) => _chapters.GetOrAdd(language, Load);

    public ManualChapter? Find(string language, string? slug)
    {
        var chapters = Chapters(language);
        return string.IsNullOrEmpty(slug) ? chapters.FirstOrDefault() : chapters.FirstOrDefault(c => c.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Chapters containing all words of the query, with a snippet around the first one.</summary>
    public IReadOnlyList<ManualSearchHit> Search(string language, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        return Chapters(language)
            .Where(c => words.All(w => c.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase) || c.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .Select(c => new ManualSearchHit(c, Snippet(c.Text, words[0])))
            .ToList();
    }

    private IReadOnlyList<ManualChapter> Load(string language)
    {
        var folder = environment.WebRootFileProvider.GetDirectoryContents($"manual/{language}");
        if (!folder.Exists) return [];
        return folder.Where(f => !f.IsDirectory && f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => Render(language, f))
            .ToList();
    }

    private static ManualChapter Render(string language, IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream());
        var markdown = reader.ReadToEnd();
        var slug = Path.GetFileNameWithoutExtension(file.Name);
        var title = TitlePattern().Match(markdown) is { Success: true } m ? m.Groups[1].Value.Trim() : slug;

        var html = Markdown.ToHtml(markdown, Pipeline);
        html = ChapterLink().Replace(html, l => $"href=\"/help/{l.Groups[1].Value}{l.Groups[2].Value}\"");
        html = ImageLink().Replace(html, i => $"src=\"/manual/img/{language}/{i.Groups[1].Value}\"");
        html = html.Replace("<table>", "<div class=\"ris-manual-table\"><table>").Replace("</table>", "</table></div>");
        var text = Markdown.ToPlainText(markdown, Pipeline);
        return new ManualChapter(slug, title, html, text);
    }

    private static string Snippet(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return text.Length > 160 ? text[..160] + "…" : text;
        var start = Math.Max(0, index - 70);
        var length = Math.Min(text.Length - start, 180);
        return (start > 0 ? "…" : "") + text.Substring(start, length).ReplaceLineEndings(" ") + (start + length < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"href=""(\d\d-[a-z0-9-]+)\.md(#[^""]*)?""")]
    private static partial Regex ChapterLink();

    [GeneratedRegex(@"src=""img/([^""]+)""")]
    private static partial Regex ImageLink();
}
