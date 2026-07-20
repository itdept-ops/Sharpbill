import { PublicClientApplication } from "@azure/msal-browser";

let instance: PublicClientApplication | null = null;
let ready: Promise<void> | null = null;
let configuredClientId: string | null = null;

function getMsal(clientId: string): PublicClientApplication {
  if (!instance || configuredClientId !== clientId) {
    instance = new PublicClientApplication({
      auth: {
        clientId,
        authority: "https://login.microsoftonline.com/common", // multi-tenant + personal
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: "sessionStorage" },
    });
    configuredClientId = clientId;
    ready = instance.initialize();
  }
  return instance;
}

export async function microsoftLogin(nonce: string, clientId: string): Promise<string> {
  const msal = getMsal(clientId);
  await ready;
  const result = await msal.loginPopup({
    scopes: ["openid", "profile", "email"],
    prompt: "select_account",
    nonce,
  });
  return result.idToken; // the Microsoft ID token
}
