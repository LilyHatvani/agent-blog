import { useState } from 'preact/hooks';
import { api } from '../api';
import { Icon } from '../components/Icon';
import { Editor } from '../components/Editor';

/** A new draft written in the app: a revision with the writer's name on it. Publishing is a step of its own. */
export function NewPost({ blog }: { blog: string }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <>
      <a class="back" href="#/posts"><Icon name="back" /> Posts</a>
      <h1>New post</h1>
      <Editor start={{ title: '', excerpt: '', body_md: '' }} saveLabel="Save draft" busy={busy} error={error}
        onCancel={() => { location.hash = '#/posts'; }}
        onSave={(_, all) => {
          setBusy(true);
          setError(null);
          api.createPost(blog, all)
            .then((r) => { location.hash = `#/posts/${r.post.id}`; })
            .catch((e) => setError(e.message))
            .finally(() => setBusy(false));
        }}>
        <p class="quiet small">It stays a draft, seen only in this app, until it is published.</p>
      </Editor>
    </>
  );
}
