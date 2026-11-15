using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
namespace JobFlow.Infrastructure;
public sealed class SecretStore
{
    private readonly string directory;
    private readonly IDataProtector protector;
    public SecretStore(string? root = null)
    {
        directory = Path.Combine(root ?? Storage.Root, "secrets"); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keys = new DirectoryInfo(Path.Combine(directory, "keys")); keys.Create();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        protector = DataProtectionProvider.Create(keys, builder =>
        {
            builder.SetApplicationName("JobFlow");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        }).CreateProtector("integration-credentials-v1");
    }
    public bool Exists(Guid id) => File.Exists(Path.Combine(directory, id.ToString("N") + ".protected"));
    public async Task SaveAsync(Guid id, Dictionary<string, string> secrets, CancellationToken ct)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".protected");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, protector.Protect(JsonSerializer.Serialize(secrets)), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }
    public async Task<Dictionary<string, string>> ReadAsync(Guid id, CancellationToken ct)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".protected");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(await File.ReadAllTextAsync(path, ct))) ?? [];
    }
}
