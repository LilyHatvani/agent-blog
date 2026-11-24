import { call } from './api';

/** The comments slot (docs/specs/embeds.md): the widget's page for a post, framed below every post. */
export interface CommentsEmbed {
  set: boolean; url_template: string | null; frame_origin: string | null;
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
  saveCommentsEmbed: (blog: string, url_template: string) =>
    call<CommentsEmbed>('PUT', `/api/blogs/${blog}/embeds/comments`, { url_template }),
  clearCommentsEmbed: (blog: string) => call<CommentsEmbed>('DELETE', `/api/blogs/${blog}/embeds/comments`),
};
