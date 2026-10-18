using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Notely.Core;

namespace Notely.Windows;

internal sealed class Settings
{
    public string Model { get; set; } = Models[0].Id;
    public double FontSize { get; set; } = 17;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 300;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public string OutputDevice { get; set; } = "";
    public string InputDevice { get; set; } = "";
    public static readonly (string Id, string Title)[] Models =
    [
        ("anthropic/claude-haiku-4.5", "Claude Haiku 4.5"),
        ("google/gemini-2.5-flash", "Gemini 2.5 Flash"),
        ("anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5")
    ];
    public static Settings Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.File("settings.json"))) ?? new();
            result.FontSize = double.IsFinite(result.FontSize) ? Math.Clamp(result.FontSize, 11, 32) : 17;
            result.Width = double.IsFinite(result.Width) ? Math.Clamp(result.Width, 420, 4000) : 800;
            result.Height = double.IsFinite(result.Height) ? Math.Clamp(result.Height, 180, 2000) : 300;
            if (!double.IsFinite(result.Left)) result.Left = -1;
            if (!double.IsFinite(result.Top)) result.Top = -1;
            if (!Models.Any(x => x.Id == result.Model)) result.Model = Models[0].Id;
            result.InputDevice ??= ""; result.OutputDevice ??= "";
            return result;
        }
        catch (Exception) { return new(); }
    }
    public void Save() => Paths.WriteAtomic("settings.json", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class Paths
{
    public static string DirectoryPath { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notely");
    internal static void UseTestDirectory(string path) => DirectoryPath = path;
    public static string File(string name) => Path.Combine(DirectoryPath, name);
    public static void Ensure()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!System.IO.File.Exists(File("profile.md"))) System.IO.File.WriteAllText(File("profile.md"), Prompts.ProfileTemplate, Encoding.UTF8);
        if (!System.IO.File.Exists(File("keyterms.txt"))) System.IO.File.WriteAllText(File("keyterms.txt"), Prompts.Keyterms, Encoding.UTF8);
    }
    public static void WriteAtomic(string name, string text)
    {
        var temp = File(name + ".tmp");
        System.IO.File.WriteAllText(temp, text, Encoding.UTF8);
        System.IO.File.Move(temp, File(name), true);
    }
    public static string Profile()
    {
        try { return Prompts.CleanProfile(System.IO.File.ReadAllText(File("profile.md"), Encoding.UTF8)); }
        catch (IOException) { return ""; }
    }
    public static string[] Keyterms()
    {
        try { return Prompts.ParseKeyterms(System.IO.File.ReadAllText(File("keyterms.txt"), Encoding.UTF8)); }
        catch (IOException) { return Prompts.ParseKeyterms(Prompts.Keyterms); }
    }
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

internal sealed record ApiKeys(string Deepgram, string OpenRouter);
internal static class Secrets
{
    public static ApiKeys? Load()
    {
        try
        {
            var encrypted = System.IO.File.ReadAllBytes(Paths.File("keys.dat"));
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var keys = JsonSerializer.Deserialize<ApiKeys>(bytes);
                return !string.IsNullOrWhiteSpace(keys?.Deepgram) && !string.IsNullOrWhiteSpace(keys.OpenRouter) ? keys : null;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception) { return null; }
    }
    public static void Save(ApiKeys keys)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(keys);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var temp = Paths.File("keys.dat.tmp");
            System.IO.File.WriteAllBytes(temp, encrypted);
            System.IO.File.Move(temp, Paths.File("keys.dat"), true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class Log
{
    private static readonly object gate = new();
    // Never write keys, request bodies, screenshots, transcripts or answers to diagnostics.
    public static void Write(string component, string message)
    {
        lock (gate)
        {
            try
            {
                var path = Paths.File("notely.log");
                if (System.IO.File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    System.IO.File.Move(path, Paths.File("notely.previous.log"), true);
                System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{component}] {message}\n");
            }
            catch (IOException) { }
        }
    }
}
