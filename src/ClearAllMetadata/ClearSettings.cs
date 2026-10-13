namespace Cove.ClearAllMetadata;

public sealed record ClearSettings
{
    public bool Title { get; init; } = true;
    public bool Code { get; init; } = true;
    public bool Details { get; init; } = true;
    public bool Director { get; init; } = true;
    public bool Date { get; init; } = true;
    public bool Studio { get; init; } = true;
    public bool Performers { get; init; } = true;
    public bool Tags { get; init; } = true;
    public bool Urls { get; init; } = true;
    public bool RemoteIds { get; init; } = true;
    public bool Cover { get; init; } = true;
    public bool Organized { get; init; } = true;
    public bool Thumbnails { get; init; }
    public bool SpriteSheets { get; init; }
    public bool Phash { get; init; }
    public bool VideoPreviews { get; init; }
    public bool Md5 { get; init; }

    public string[] ProvenanceKeys() => new (bool Clear, string Key)[] {
        (Title, "title"), (Code, "code"), (Details, "details"), (Director, "director"),
        (Date, "date"), (Studio, "studio"), (Performers, "performers"), (Tags, "tags"),
        (Urls, "urls"), (RemoteIds, "remote_ids"), (Cover, "image_url"), (Organized, "organized")
    }.Where(item => item.Clear).Select(item => item.Key).ToArray();
}
