namespace VirtmaAi.Services.Media;

public interface IPreviewDispatcher
{
    PreviewKind Classify(string pathOrUrl);
}

public enum PreviewKind
{
    Unknown,
    Image,
    Video,
    Audio,
    Pdf,
    Text,
    Markdown,
    Code,
    Json,
    Xml,
    Yaml,
    Html,
    WebUrl,
    Model3D
}
