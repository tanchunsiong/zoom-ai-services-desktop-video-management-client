# Security notes

- API key and secret are serialized only into a Windows Credential Manager generic credential scoped to the signed-in Windows user/machine. They are not present in repository files, settings JSON, queue JSON, errors, or UI logs.
- JWTs are generated in memory with HS256 and a one-hour expiry. The `iss` claim is the API key; signing uses the API secret.
- Requests go directly to `https://api.zoom.us/v2/aiservices/...` over HTTPS.
- Only extracted audio is uploaded. Source video remains local.
- Temporary extracted audio is deleted after every terminal outcome. For crash recovery, a startup janitor and age-based purge should be added before production release.
- Output captions and transcript JSON contain customer content and are intentionally stored beside the source by default. Users should choose an encrypted output location where required.
- This desktop architecture places Zoom Build credentials on the endpoint. For multi-customer distribution, replace direct credentials with short-lived tokens or calls to the Z Transcribe wrapper service so the vendor secret never ships to customers.

