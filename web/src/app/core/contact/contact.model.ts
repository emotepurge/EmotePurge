/** GET /api/contact/config. Mirrors ContactEndpoints' anonymous shape check on the Api. */
export interface ContactConfigResponse {
  available: boolean;
  /** Public by design — null exactly when `available` is false. */
  turnstileSiteKey: string | null;
}

/** POST /api/contact's body. `website` is the hidden honeypot — always sent, normally empty. */
export interface ContactSubmitRequest {
  name?: string;
  email: string;
  message: string;
  turnstileToken: string;
  website?: string;
}
