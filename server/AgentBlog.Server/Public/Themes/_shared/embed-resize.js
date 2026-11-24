// Agent Blog: sizes a comments frame (docs/specs/embeds.md). The blog's own script, the only one a post page with the
// comments slot runs. The frame is another site's page in a sandboxed, cross-origin iframe; it may tell this page how
// tall it is, and nothing else: a message counts only when it comes from that frame, from exactly the origin the server
// put on it, and is exactly { type: 'resize', height: <number> }. The height is clamped to whole pixels in 100–5000.

var AGENT_BLOG_EMBED_MIN = 100;
var AGENT_BLOG_EMBED_MAX = 5000;

/** The height to give the frame for a message, or null to ignore it. */
function agentBlogEmbedHeight(data, origin, allowed) {
  if (typeof allowed !== 'string' || allowed.length === 0 || origin !== allowed) return null;
  if (data === null || typeof data !== 'object' || Array.isArray(data)) return null;
  var keys = Object.keys(data);
  if (keys.length !== 2 || data.type !== 'resize' || typeof data.height !== 'number' || !isFinite(data.height)) return null;
  return Math.round(Math.min(AGENT_BLOG_EMBED_MAX, Math.max(AGENT_BLOG_EMBED_MIN, data.height)));
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  var agentBlogFrames = document.querySelectorAll('iframe[data-embed="comments"]');
  for (var i = 0; i < agentBlogFrames.length; i++) {
    // Through the CSSOM: the page's CSP allows no style attributes in markup.
    agentBlogFrames[i].style.width = '100%';
    agentBlogFrames[i].style.border = '0';
  }
  window.addEventListener('message', function (event) {
    for (var j = 0; j < agentBlogFrames.length; j++) {
      var frame = agentBlogFrames[j];
      if (event.source !== frame.contentWindow) continue;
      var height = agentBlogEmbedHeight(event.data, event.origin, frame.getAttribute('data-origin'));
      if (height !== null) frame.style.height = height + 'px';
    }
  });
}
