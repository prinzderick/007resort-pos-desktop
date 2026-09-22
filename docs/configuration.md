# POS configuration

The POS reads `appsettings.json` (next to the executable) and then environment variables prefixed
with `OTUEKE_` (double underscore `__` separates sections). **No secrets live in configuration.**
Device credentials issued by the API at registration are stored in the Windows-protected store
(DPAPI / Credential Manager), never in files in this repository or on disk in clear.

## Template

```json
{
  "Pos": {
    "ApiBaseUrl": "http://localhost:5080",
    "DeviceRegistration": {
      "DeviceName": "Main Reception 1",
      "DeviceId": null
    }
  }
}
```

| Key | Env var | Default | Notes |
|---|---|---|---|
| `Pos:ApiBaseUrl` | `OTUEKE_Pos__ApiBaseUrl` | `http://localhost:5080` | The **on-site** Otueke API |
| `Pos:DeviceRegistration:DeviceName` | `OTUEKE_Pos__DeviceRegistration__DeviceName` | empty | Suggested name at enrolment |
| `Pos:DeviceRegistration:DeviceId` | `OTUEKE_Pos__DeviceRegistration__DeviceId` | `null` | Set after registration by an administrator |

Everything else a terminal does — which screens it shows, what it can sell, which staff may sign
in — is returned by the API based on the registered device, its facility/operating point and the
signed-in staff member's role and permissions.
