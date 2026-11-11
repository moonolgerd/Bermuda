using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Bermuda.IpcContract;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Bermuda;

/// <summary>
/// Windows toast notifications through the Windows App SDK (<see cref="AppNotificationManager"/>),
/// which registers the app with Windows itself for both MSIX and unpackaged runs. Toasts can only
/// show local images, so every image source (https URL, data: URL or file path) is first
/// resolved to a file under %TEMP%\Bermuda\toast-images.
/// </summary>
internal static class NotificationService
{
    private const string ActionKey = "action";
    private const string IdKey = "id";
    private const string ReplyKey = "reply";
    private const string Group = "bermuda";
    private const int MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string ImageDir = Path.Combine(Path.GetTempPath(), "Bermuda", "toast-images");

    /// <summary>Raised (on a background thread) when a toast or one of its buttons is clicked.</summary>
    public static event Action<NotificationActivation>? Activated;

    public static void Initialize()
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, args) => Dispatch(args.Arguments, args.UserInput);

        try
        {
            if (UpdateService.IsPackaged())
                manager.Register();
            else
                manager.Register("Bermuda", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")));
        }
        catch (Exception ex)
        {
            // Toasts are a nicety; never block startup. ShowAsync reports the problem per call.
            _registrationError = ex.Message;
        }
    }

    private static string? _registrationError;

    /// <summary>
    /// Turns what Windows hands back on a click (the clicked element's arguments plus any reply text)
    /// into the event pushed to the web app. Shared by real clicks and <see cref="Simulate"/>.
    /// </summary>
    private static void Dispatch(IDictionary<string, string> arguments, IDictionary<string, string> userInput)
    {
        arguments.TryGetValue(IdKey, out var id);
        arguments.TryGetValue(ActionKey, out var action);
        userInput.TryGetValue(ReplyKey, out var reply);
        Activated?.Invoke(new NotificationActivation("notificationActivated", id ?? "", action ?? "", reply));
    }

    // The arguments each shown toast was built with, kept so a click can be simulated with exactly
    // what Windows would hand back (a button click carries only that button's arguments).
    private sealed record ShownToast(
        Dictionary<string, string> BodyArguments,
        Dictionary<string, Dictionary<string, string>> ButtonArguments,
        bool HasReply);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShownToast> Shown = new();

    /// <summary>
    /// Test hook: behaves as if the user clicked the toast body (empty <paramref name="action"/>) or one of
    /// its buttons, optionally with reply text, without needing Windows or a human.
    /// </summary>
    public static SimulateNotificationActivationResult Simulate(SimulateNotificationActivationRequest r)
    {
        if (!Shown.TryGetValue(r.NotificationId, out var toast))
            return new(false, $"No toast with id '{r.NotificationId}' was shown by this process.");

        var arguments = toast.BodyArguments;
        if (!string.IsNullOrEmpty(r.Action))
        {
            if (!toast.ButtonArguments.TryGetValue(r.Action, out arguments!))
                return new(false, $"Toast '{r.NotificationId}' has no button '{r.Action}'.");
        }

        var input = new Dictionary<string, string>();
        if (r.Reply is not null)
        {
            if (!toast.HasReply)
                return new(false, $"Toast '{r.NotificationId}' has no reply box.");
            input[ReplyKey] = r.Reply;
        }

        Dispatch(arguments, input);
        return new(true, "");
    }

    public static async Task<ShowNotificationResult> ShowAsync(ShowNotificationRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) && string.IsNullOrWhiteSpace(r.Body))
            return new(false, "Notification needs a title or a body.");

        if (_registrationError is not null)
            return new(false, $"Notification registration failed: {_registrationError}");

        try
        {
            // Show() succeeds even when Windows is going to swallow the toast, so ask first.
            var setting = AppNotificationManager.Default.Setting;
            if (setting != AppNotificationSetting.Enabled)
                return new(false, setting switch
                {
                    AppNotificationSetting.DisabledForApplication => "Notifications are turned off for Bermuda (Settings > System > Notifications).",
                    AppNotificationSetting.DisabledForUser => "Notifications are turned off for this user (Settings > System > Notifications).",
                    AppNotificationSetting.DisabledByGroupPolicy => "Notifications are disabled by group policy.",
                    AppNotificationSetting.DisabledByManifest => "Notifications are disabled by the app manifest.",
                    _ => $"Notifications are unavailable ({setting}).",
                });

            var id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id;
            var bodyArguments = new Dictionary<string, string> { [IdKey] = id };
            var builder = new AppNotificationBuilder();
            foreach (var (key, value) in bodyArguments)
                builder.AddArgument(key, value);
            builder
                .AddText(string.IsNullOrWhiteSpace(r.Title) ? "Bermuda" : r.Title)
                .AddText(r.Body ?? "");

            if (!string.IsNullOrWhiteSpace(r.Attribution))
                builder.SetAttributionText(r.Attribution);

            if (await ResolveImageAsync(r.Icon) is { } icon)
                builder.SetAppLogoOverride(icon, r.CircleIcon == true ? AppNotificationImageCrop.Circle : AppNotificationImageCrop.Default);
            if (await ResolveImageAsync(r.Image) is { } image)
                builder.SetInlineImage(image);
            if (await ResolveImageAsync(r.HeroImage) is { } hero)
                builder.SetHeroImage(hero);

            var hasReply = !string.IsNullOrWhiteSpace(r.ReplyPlaceholder);
            if (hasReply)
                builder.AddTextBox(ReplyKey, r.ReplyPlaceholder!, "");

            var buttonArguments = new Dictionary<string, Dictionary<string, string>>();
            foreach (var button in r.Buttons?.Take(5) ?? [])
            {
                // A button click carries only its own arguments, not the toast's, so it repeats the id.
                var args = buttonArguments[button.Id] = new() { [IdKey] = id, [ActionKey] = button.Id };
                var toastButton = new AppNotificationButton(button.Label);
                foreach (var (key, value) in args)
                    toastButton.AddArgument(key, value);
                if (await ResolveImageAsync(button.Icon) is { } buttonIcon)
                    toastButton.SetIcon(buttonIcon);
                if (hasReply)
                    toastButton.SetInputId(ReplyKey);
                builder.AddButton(toastButton);
            }

            // Urgent: the reminder scenario keeps the toast on screen until the user
            // dismisses it (plus the High priority applied below).
            if (r.Priority == NotificationPriority.Urgent)
                builder.SetScenario(AppNotificationScenario.Reminder);

            if (r.Silent == true)
                builder.MuteAudio();

            var notification = builder.BuildNotification();
            notification.Tag = ToTag(id);
            notification.Group = Group;
            if (r.Priority is NotificationPriority.High or NotificationPriority.Urgent)
                notification.Priority = AppNotificationPriority.High;
            if (r.ExpiresInSeconds is > 0)
                notification.Expiration = DateTimeOffset.Now.AddSeconds(r.ExpiresInSeconds.Value);

            AppNotificationManager.Default.Show(notification);
            Shown[id] = new ShownToast(bodyArguments, buttonArguments, hasReply);
            return new(true, "");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // Windows rejects tags of 64+ characters. A hash is deterministic, so reusing a long id
    // still replaces the earlier toast. The full id travels in the click arguments instead.
    private static string ToTag(string id) =>
        id.Length < 32 ? id : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..32];

    /// <summary>Removes the app's toasts from Action Center and unregisters on exit.</summary>
    public static void Clear()
    {
        try
        {
            AppNotificationManager.Default.RemoveAllAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            AppNotificationManager.Default.Unregister();
        }
        catch { /* best effort */ }
    }

    /// <summary>Resolves an https URL, data: URL or file path to a local file URI; null when absent or unusable.</summary>
    private static async Task<Uri?> ResolveImageAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        try
        {
            byte[] bytes;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0 || !source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                bytes = Convert.FromBase64String(source[(comma + 1)..]);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxImageBytes) return null;
                bytes = await response.Content.ReadAsByteArrayAsync();
            }
            else if (Path.IsPathRooted(source) && File.Exists(source))
            {
                return new Uri(source);
            }
            else
            {
                return null;
            }

            if (bytes.Length is 0 or > MaxImageBytes) return null;

            Directory.CreateDirectory(ImageDir);
            var path = Path.Combine(ImageDir, Convert.ToHexString(SHA256.HashData(bytes))[..24] + ImageExtension(bytes));
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            return new Uri(path);
        }
        catch
        {
            // A broken image should not stop the notification itself.
            return null;
        }
    }

    private static string ImageExtension(byte[] b) => b switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0xFF, 0xD8, ..] => ".jpg",
        [0x47, 0x49, 0x46, ..] => ".gif",
        _ => ".png",
    };
}
