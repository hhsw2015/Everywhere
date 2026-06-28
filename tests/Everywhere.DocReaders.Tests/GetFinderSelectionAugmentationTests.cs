using Everywhere.Mcp.Tools;

namespace Everywhere.DocReaders.Tests;

public class GetFinderSelectionAugmentationTests
{
    [TestCase("paper.pdf", false, "pdf", "application/pdf")]
    [TestCase("note.docx", false, "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [TestCase("sheet.xlsx", false, "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [TestCase("deck.pptx", false, "pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [TestCase("book.epub", false, "epub", "application/epub+zip")]
    [TestCase("page.html", false, "html", "text/html")]
    [TestCase("page.htm", false, "html", "text/html")]
    [TestCase("readme.md", false, "text", "text/markdown")]
    [TestCase("plain.txt", false, "text", "text/plain")]
    [TestCase("photo.png", false, "image", "image/png")]
    [TestCase("photo.JPG", false, "image", "image/jpeg")]
    [TestCase("Downloads", true, "folder", "inode/directory")]
    [TestCase("strange.bin", false, "unknown", "application/octet-stream")]
    public void KindHintAndMime_MatchExtension(string name, bool isDir, string kind, string mime)
    {
        Assert.That(GetFinderSelectionTool.KindHintFromExtension(name, isDir), Is.EqualTo(kind));
        Assert.That(GetFinderSelectionTool.MimeFromExtension(name, isDir), Is.EqualTo(mime));
    }
}
