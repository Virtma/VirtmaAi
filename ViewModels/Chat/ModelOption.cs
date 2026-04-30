namespace VirtmaAi.ViewModels.Chat;

public sealed record ModelOption(string ProviderId, string ProviderDisplayName, string ModelId)
{
    public string Key => ProviderId + "|" + ModelId;

    /// <summary>
    /// Human-readable "Provider \u2022 Name" label shown in pickers.
    /// When the ModelId is a file path (e.g. a .gguf file), only the file name
    /// without extension is shown so the picker stays readable.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var name = ModelId.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                       || (ModelId.Contains(System.IO.Path.DirectorySeparatorChar) &&
                           ModelId.Contains('.'))
                ? System.IO.Path.GetFileNameWithoutExtension(ModelId)
                : ModelId;
            return $"{ProviderDisplayName} \u2022 {name}";
        }
    }

    public override string ToString() => DisplayLabel;
}
