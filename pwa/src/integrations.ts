import { call } from './api';

/** The comments slot (docs/specs/embeds.md): a widget's script and the origins it may call, on every post page. */
export interface CommentsEmbed {
  set: boolean; script_url: string | null; script_origin: string | null; connect_origins: string[]; max_connect_origins: number;
}

/** A trusted sign-in provider (docs/specs/auth.md § A trusted provider): any OpenID Connect issuer. The secret never comes back. */
export interface OidcSetting {
  set: boolean; issuer: string | null; client_id: string | null; display_name: string | null; secret_set: boolean;
  redirect_uri: string | null; available: boolean; reason: string | null; max_display_name: number;
}

export const integrations = {
  oidc: (blog: string) => call<OidcSetting>('GET', `/api/blogs/${blog}/sign-in/oidc`),
  saveOidc: (blog: string, body: { issuer: string; client_id: string; client_secret?: string; display_name: string }) =>
    call<OidcSetting>('PUT', `/api/blogs/${blog}/sign-in/oidc`, body),
  removeOidc: (blog: string) => call<OidcSetting>('DELETE', `/api/blogs/${blog}/sign-in/oidc`),
  commentsEmbed: (blog: string) => call<CommentsEmbed>('GET', `/api/blogs/${blog}/embeds/comments`),
  saveCommentsEmbed: (blog: string, script_url: string, connect_origins: string[]) =>
    call<CommentsEmbed>('PUT', `/api/blogs/${blog}/embeds/comments`, { script_url, connect_origins }),
  clearCommentsEmbed: (blog: string) => call<CommentsEmbed>('DELETE', `/api/blogs/${blog}/embeds/comments`),
};
