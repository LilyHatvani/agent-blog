import { call } from './api';

/** The comments slot (docs/specs/embeds.md): a widget's script and the origins it may call, on every post page. */
export interface CommentsEmbed {
  set: boolean; script_url: string | null; script_origin: string | null; connect_origins: string[]; max_connect_origins: number;
}

export const integrations = {
  commentsEmbed: (blog: string) => call<CommentsEmbed>('GET', `/api/blogs/${blog}/embeds/comments`),
  saveCommentsEmbed: (blog: string, script_url: string, connect_origins: string[]) =>
    call<CommentsEmbed>('PUT', `/api/blogs/${blog}/embeds/comments`, { script_url, connect_origins }),
  clearCommentsEmbed: (blog: string) => call<CommentsEmbed>('DELETE', `/api/blogs/${blog}/embeds/comments`),
};
