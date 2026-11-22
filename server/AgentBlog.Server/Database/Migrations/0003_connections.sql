-- 0003 — agent connections over MCP OAuth (docs/specs/connections.md). The OAuth tables themselves come from
-- AgentKit.McpOAuth's own script, run by the Migrator (decision 0007), never copied here.

-- Which client the grant was made to (claude-ai, claude-code, a metadata document URL), for the owner's list.
ALTER TABLE connection ADD COLUMN client_id text CHECK (length(client_id) <= 500);

CREATE INDEX connection_blog ON connection (blog_id) WHERE revoked_at IS NULL;
