namespace NoDrive.Graph;

/// <summary>
/// Microsoft Graph v1.0 endpoints used by NoDrive. Kept as constants so the transfer layer can be
/// swapped when pre-authenticated URLs retire (see memory_bank/research/graph-onedrive.md).
/// </summary>
public static class GraphEndpoints
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";
    public const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    public const string DriveRoot = "/me/drive/root";
    public const string Delta = "/me/drive/root/delta";
    public const string SocketIoSubscription = "/me/drive/root/subscriptions/socketIo";

    /// <summary>
    /// Default public-client application (client) ID, registered 2026-11-13 in the developer's Entra
    /// tenant (AzureADandPersonalMicrosoftAccount, public client flows, redirect http://localhost).
    /// A client ID is not a secret; config.json may override it so a user gets their own throttling budget.
    /// </summary>
    public const string DefaultClientId = "6711e7c6-ec09-42df-a5bb-bfb449119fdc";

    /// <summary>Least-privilege delegated scopes for a personal OneDrive.</summary>
    public static readonly string[] Scopes = ["Files.ReadWrite", "offline_access"];

    /// <summary>Upload-session fragments must be multiples of this size (327,680 bytes).</summary>
    public const int UploadFragmentAlignment = 320 * 1024;
}
