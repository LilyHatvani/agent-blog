-- Tighten old_address.target (docs/specs/old-addresses.md): the old CHECK '^/([^/]|$)' let '/\host' through, which a
-- browser reads as '//host' — an open redirect an export restore could plant. Refuse a backslash or control character
-- anywhere (and a backslash as the second character). No legitimate target carries either; rows that would break the
-- new rule are removed first so the constraint can be added.

DELETE FROM old_address WHERE target ~ '\\' OR target ~ '[[:cntrl:]]';

ALTER TABLE old_address DROP CONSTRAINT IF EXISTS old_address_target_check;
ALTER TABLE old_address ADD CONSTRAINT old_address_target_check
    CHECK (target ~ '^/([^/\\]|$)' AND target !~ '\\' AND target !~ '[[:cntrl:]]' AND length(target) <= 500 AND target !~ '\s');
