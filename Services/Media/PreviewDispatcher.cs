namespace VirtmaAi.Services.Media;

public sealed class PreviewDispatcher : IPreviewDispatcher
{
    public PreviewKind Classify(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return PreviewKind.Unknown;

        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return PreviewKind.WebUrl;

        var ext = Path.GetExtension(pathOrUrl).ToLowerInvariant().TrimStart('.');
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" => PreviewKind.Image,
            "mp4" or "mov" or "mkv" or "webm" or "avi" => PreviewKind.Video,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => PreviewKind.Audio,
            "pdf" => PreviewKind.Pdf,
            "md" or "markdown" => PreviewKind.Markdown,
            "json" => PreviewKind.Json,
            "xml" or "xaml" => PreviewKind.Xml,
            "yaml" or "yml" => PreviewKind.Yaml,
            "html" or "htm" => PreviewKind.Html,
            "glb" or "gltf" or "obj" or "stl" or "fbx" => PreviewKind.Model3D,
            "txt" or "log" or "csv" or "tsv" or "ini" or "cfg" => PreviewKind.Text,
            "cs" or "js" or "ts" or "tsx" or "jsx" or "py" or "go" or "rs" or "java" or "kt" or "swift" or "rb" or "php" or "sql" or "sh" or "ps1" => PreviewKind.Code,
            _ => PreviewKind.Unknown
        };
    }
}
