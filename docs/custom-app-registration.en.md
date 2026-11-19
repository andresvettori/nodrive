# Use your own Entra app ID

NoDrive signs in with a public client app registered in Microsoft Entra. A default app ID is built in. If you distribute NoDrive or want your own consent screen, register your own:

1. Microsoft Entra admin center > App registrations > **New registration**.
2. Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**.
3. Redirect URI: platform **Mobile and desktop applications**, value `http://localhost`.
4. API permissions (Microsoft Graph, delegated): `Files.ReadWrite` and `offline_access`.
5. Authentication: enable **Allow public client flows**.
6. Copy the **Application (client) ID** into `clientId` in `%LOCALAPPDATA%\NoDrive\config.json`, then restart NoDrive and sign in again.

A personal Microsoft account cannot create app registrations by itself: create a free Entra tenant first.

---
Website: <https://andresvettori.github.io/nodrive/>
