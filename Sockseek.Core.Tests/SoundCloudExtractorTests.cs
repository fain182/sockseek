using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Settings;

namespace Tests.Core;

[TestClass]
public class SoundCloudExtractorTests
{
    [TestMethod]
    public void Track_WithPublisherArtist_UsesItAsArtist()
    {
        var job = SoundCloudExtractor.ToSongJob(Track("""
            {"id": 1, "title": "World On Fire", "duration": 199889, "permalink_url": "https://soundcloud.com/a/b",
             "user": {"username": "uploader"}, "publisher_metadata": {"artist": "The Royal Concept"}}
            """), 3);

        Assert.AreEqual("The Royal Concept", job.Query.Artist);
        Assert.AreEqual("World On Fire", job.Query.Title);
        Assert.AreEqual(199, job.Query.Length);
        Assert.AreEqual("https://soundcloud.com/a/b", job.Query.URI);
        Assert.IsFalse(job.Query.ArtistMaybeWrong);
        Assert.AreEqual(3, job.ItemNumber);
    }

    [TestMethod]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}, "publisher_metadata": null}""")]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}, "publisher_metadata": {"artist": ""}}""")]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}}""")]
    public void Track_WithoutPublisherArtist_FallsBackToUploader(string json)
    {
        var job = SoundCloudExtractor.ToSongJob(Track(json), 1);

        Assert.AreEqual("uploader", job.Query.Artist);
        Assert.IsTrue(job.Query.ArtistMaybeWrong);
        Assert.AreEqual(-1, job.Query.Length);
    }

    [TestMethod]
    public async Task MissingAuthToken_ReportsHowToSetIt()
    {
        var extractor = new SoundCloudExtractor(new SoundCloudSettings());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => extractor.GetTracks("https://soundcloud.com/artist/sets/playlist", new ExtractionSettings()));

        StringAssert.Contains(error.Message, "--soundcloud-token");
    }

    [TestMethod]
    public void SoundCloudUrl_SelectsSoundCloudExtractor()
    {
        var (type, extractor) = ExtractorRegistry.GetMatchingExtractor(
            "https://soundcloud.com/artist/sets/playlist", InputType.None, new DownloadSettings());

        Assert.AreEqual(InputType.SoundCloud, type);
        Assert.IsInstanceOfType<SoundCloudExtractor>(extractor);
    }

    private static JsonElement Track(string json) => JsonDocument.Parse(json).RootElement;
}
