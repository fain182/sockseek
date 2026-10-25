using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class SoundCloudExtractor : IExtractor, IInputMatcher
    {
        private const string ApiBase = "https://api-v2.soundcloud.com";

        // api-v2 rejects /tracks requests with more ids than this.
        private const int TracksPerRequest = 50;

        private readonly SoundCloudSettings _soundCloud;

        public SoundCloudExtractor(SoundCloudSettings soundCloud) { _soundCloud = soundCloud; }

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && input.Contains("soundcloud.com");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;

            if (string.IsNullOrWhiteSpace(_soundCloud.AuthToken))
                throw new InvalidOperationException("A SoundCloud OAuth token is required. Set it with --soundcloud-token.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", _soundCloud.AuthToken);

            context.Log.Info("Loading playlist..");
            var playlist = await GetJson(httpClient, $"{ApiBase}/resolve?url={Uri.EscapeDataString(input.Trim())}");

            var kind = playlist.GetProperty("kind").GetString();
            if (kind != "playlist")
                throw new NotSupportedException($"Only SoundCloud playlists are supported, but the URL points to a {kind}.");

            var entries = playlist.GetProperty("tracks").EnumerateArray()
                .Select((track, index) => (Number: index + 1, Track: track))
                .ToList();

            if (extraction.Reverse)
                entries.Reverse();
            entries = entries.Skip(extraction.Offset).Take(extraction.MaxTracks).ToList();

            // The playlist response only embeds full metadata for the first few tracks; the rest are bare ids.
            var missingIds = entries.Where(e => !e.Track.TryGetProperty("title", out _))
                .Select(e => e.Track.GetProperty("id").GetInt64())
                .ToList();

            var details = new Dictionary<long, JsonElement>();
            foreach (var batch in missingIds.Chunk(TracksPerRequest))
            {
                var tracks = await GetJson(httpClient, $"{ApiBase}/tracks?ids={string.Join(',', batch)}");
                foreach (var track in tracks.EnumerateArray())
                    details[track.GetProperty("id").GetInt64()] = track;
            }

            var result = new JobList { ItemName = playlist.GetProperty("title").GetString(), EnablesIndexByDefault = true };
            foreach (var (number, entry) in entries)
            {
                var track = entry;
                if (!track.TryGetProperty("title", out _) && !details.TryGetValue(track.GetProperty("id").GetInt64(), out track))
                {
                    context.Log.Warn($"Skipping playlist track {number}: SoundCloud returned no details for it (removed or private).");
                    continue;
                }
                result.Jobs.Add(ToSongJob(track, number));
            }

            return result;
        }

        internal static SongJob ToSongJob(JsonElement track, int itemNumber)
        {
            string? publisherArtist = null;
            if (track.TryGetProperty("publisher_metadata", out var publisher) && publisher.ValueKind == JsonValueKind.Object
                && publisher.TryGetProperty("artist", out var artist))
                publisherArtist = artist.GetString();

            bool hasPublisherArtist = !string.IsNullOrWhiteSpace(publisherArtist);
            int durationMs = track.TryGetProperty("duration", out var duration) ? duration.GetInt32() : -1;

            var query = new SongQuery
            {
                Artist = hasPublisherArtist ? publisherArtist! : track.GetProperty("user").GetProperty("username").GetString() ?? "",
                Title = track.GetProperty("title").GetString() ?? "",
                URI = track.TryGetProperty("permalink_url", out var url) ? url.GetString() ?? "" : "",
                Length = durationMs >= 0 ? durationMs / 1000 : -1,
                ArtistMaybeWrong = !hasPublisherArtist,
            };

            return new SongJob(query) { ItemNumber = itemNumber };
        }

        private static async Task<JsonElement> GetJson(HttpClient httpClient, string url)
        {
            using var response = await httpClient.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"SoundCloud rejected the request ({(int)response.StatusCode}); check that the OAuth token is valid.");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }
    }
