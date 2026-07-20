import { PublicClientApplication } from "@azure/msal-browser";

let instance: PublicClientApplication | null = null;
let ready: Promise<void> | null = null;

function getMsal(): PublicClientApplication {
  if (!instance) {
    instance = new PublicClientApplication({
      auth: {
        clientId: import.meta.env.VITE_AZURE_CLIENT_ID,
        authority: "https://login.microsoftonline.com/common", // multi-tenant + personal
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: "sessionStorage" },
    });
    ready = instance.initialize();
  }
  return instance;
}

export async function microsoftLogin(nonce: string): Promise<string> {
  const msal = getMsal();
  await ready;
  const result = await msal.loginPopup({
    scopes: ["openid", "profile", "email"],
    prompt: "select_account",
    nonce,
  });
  return result.idToken; // the Microsoft ID token
}
