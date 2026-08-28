using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Configuration source wrapping a single JSON file (typically
/// <c>appsettings.runtime.json</c>) where secret values are persisted with an
/// <c>enc:v1:&lt;base64&gt;</c> prefix. The companion provider transparently decrypts
/// them via the supplied <see cref="ISecretProtector"/> while loading; everything else
/// in the chain (binders, <c>IOptions</c>, etc.) sees plain configuration strings.
/// </summary>
public sealed class EncryptingJsonConfigurationSource : JsonConfigurationSource
{
    public ISecretProtector Protector { get; }

    public EncryptingJsonConfigurationSource(string path, ISecretProtector protector, bool optional, bool reloadOnChange)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must not be empty.", nameof(path));
        Path = path;
        Optional = optional;
        ReloadOnChange = reloadOnChange;
        Protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        // EnsureDefaults wires up the FileProvider/Optional defaults that JsonConfigurationSource
        // relies on internally — must be called before constructing the provider or any reload
        // attempt will throw a NullReferenceException on the FileProvider field.
        //
        // JsonConfigurationSource resolves Path relative to the builder's FileProvider, rooted
        // at ContentRoot. The runtime overrides path is typically absolute (e.g.
        // C:\ProgramData\NodePilot\appsettings.runtime.json), so point FileProvider at the
        // file's own directory and reduce Path to just the filename, or the loader can't find it.
        if (FileProvider is null && !string.IsNullOrEmpty(Path) && System.IO.Path.IsPathRooted(Path))
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                // PhysicalFileProvider requires the directory to exist at construction time.
                // For first-time installs the override file's directory may not exist yet;
                // create it so the watcher can attach (file itself stays optional/absent).
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                FileProvider = new PhysicalFileProvider(directory);
                Path = System.IO.Path.GetFileName(Path);
            }
        }

        EnsureDefaults(builder);
        return new EncryptingJsonConfigurationProvider(this);
    }
}
