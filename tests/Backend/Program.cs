using Cove.ClearAllMetadata;
using Cove.Core.Entities;

var video = new Video {
    Id = 42, Title = "Wrong scene", Code = "WRONG", Details = "Wrong details", Director = "Wrong director",
    Date = new DateOnly(2020, 1, 1), StudioId = 8, Organized = true, ImageBlobId = "wrong-cover",
    PrimaryFileId = 9, Captions = "keep", IsVr = true, ParentVideoId = 3, ClipStartSec = 12,
    MaxDuration = 120, MinPath = "/media/keep.mp4",
    Urls = [new() { Url = "https://wrong.example" }], RemoteIds = [new() { RemoteId = "wrong" }],
    VideoTags = [new() { TagId = 5 }], VideoPerformers = [new() { PerformerId = 6 }],
    Files = [new() { Id = 9 }], VideoGalleries = [new() { GalleryId = 7 }],
    PlayHistory = [new() { Id = 1 }]
};
ClearAllMetadataExtension.Reset(video);
if (video.Title != null || video.Code != null || video.Details != null || video.Director != null || video.Date != null || video.StudioId != null || video.ImageBlobId != null || video.Organized || video.Urls.Count != 0 || video.RemoteIds.Count != 0 || video.VideoTags.Count != 0 || video.VideoPerformers.Count != 0)
    throw new Exception("Misidentification was not fully cleared.");
if (video.Id != 42 || video.PrimaryFileId != 9 || video.Files.Single().Id != 9 || video.Captions != "keep" || !video.IsVr || video.ParentVideoId != 3 || video.ClipStartSec != 12 || video.MaxDuration != 120 || video.MinPath != "/media/keep.mp4" || video.VideoGalleries.Single().GalleryId != 7 || video.PlayHistory.Single().Id != 1)
    throw new Exception("Reset damaged preserved media or user data.");
ClearAllMetadataExtension.Reset(video);
Console.WriteLine("PASS: complete reset, media preservation, repeated reset.");
